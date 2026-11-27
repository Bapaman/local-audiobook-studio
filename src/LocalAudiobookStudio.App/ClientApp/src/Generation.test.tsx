import { act, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import App from './App';
import { ApiError, StudioClient } from './api/client';
import type { ProjectSnapshot } from './api/types';
import { TestEventSource } from './test-events';
import { useProjectEvents } from './features/generation/useProjectEvents';

const bootstrap = { version: '1.0', antiforgeryRequestToken: 'csrf', capabilities: { generation: false, training: false } };
const segment = { id: 's1', order: 0, originalText: 'Первая история.', normalizedText: 'Первая история.', spokenText: 'Первая история.', contentHash: 'hash', status: 'pending', outputArtifactId: null };
const project = { id: 'book1', displayName: 'Моя книга', revision: 2,
  voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 2880044, durationMilliseconds: 30000, hasClippingWarning: false },
  voiceAnchor: { artifactId: 'voice1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Точный текст.' },
  textDocument: { sourceKind: 'Paste', displayName: 'Текст книги', encoding: 'utf-8', sha256: 'hash', byteCount: 25, originalText: 'Первая история.', normalizedText: 'Первая история.' },
  segments: [segment, { ...segment, id: 's2', order: 1, spokenText: 'Вторая история.' }, { ...segment, id: 's3', order: 2, spokenText: 'Третья история.' }] };
const job = { id: 'job1', projectId: 'book1', kind: 'generation', state: 'running', requestedState: 'run', phase: 'synthesizing', completedUnits: 0, totalUnits: 2, revision: 3,
  createdUtc: '2026-10-11T00:00:00Z', updatedUtc: '2026-10-11T00:00:01Z', completedUtc: null, diagnosticCode: null, inputHash: 'hash', effectiveProfileId: 'q8-f16-cpu' };
const savedTraining = { id: 'training1', projectId: 'book1', revision: 5, state: 'completed', phase: 'completed', sourceRecordingId: 'record1', globalStep: 250, completedEpoch: 2, epochSize: 125,
  createdUtc: '2026-10-11T00:00:00Z', updatedUtc: '2026-10-11T00:05:00Z', diagnosticCode: null, canResume: true,
  models: [{ id: 'legacy1', globalStep: 1, completedEpoch: 0, createdUtc: '2026-10-11T00:01:00Z', qualityEstablished: false },
    { id: 'saved1', globalStep: 125, completedEpoch: 1, createdUtc: '2026-10-11T00:03:00Z', qualityEstablished: false, selectable: true },
    { id: 'saved2', globalStep: 250, completedEpoch: 2, createdUtc: '2026-10-11T00:05:00Z', qualityEstablished: false, selectable: true }] };
const savedRoutes: Record<string, Responder> = {
  'GET /api/projects/book1/training': () => ({ body: savedTraining, etag: '"5"' }),
  'GET /api/training/readiness': () => ({ body: { ready: true, experimental: true, diagnosticCode: null, cpuThreads: 2 } }),
};
type Reply = { body: unknown; status?: number; etag?: string };
type Responder = (init?: RequestInit) => Reply | Promise<Reply>;
function serve(overrides: Record<string, Responder> = {}, snapshot: unknown = project) {
  const calls: { path: string; init?: RequestInit }[] = [];
  const routes: Record<string, Responder> = {
    'GET /api/bootstrap': () => ({ body: bootstrap }), 'GET /api/projects': () => ({ body: [] }),
    'POST /api/projects': () => ({ body: snapshot, status: 201, etag: '"2"' }),
    'GET /api/projects/book1': () => ({ body: snapshot, etag: '"2"' }),
    'GET /api/projects/book1/jobs': () => ({ body: [] }),
    'GET /api/projects/book1/training': () => ({ body: null }),
    'GET /api/tts-corpus/readiness': () => ({ body: { ready: false, diagnosticCode: 'tts_corpus.runtime.not_ready', provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }),
    'GET /api/voxcpm-training/readiness': () => ({ body: { ready: false, diagnosticCode: 'voxcpm_training.runtime.not_ready', provider: 'voxcpm2-lora-cpu-v1', cpuThreads: 2 } }),
    'GET /api/projects/book1/voxcpm-training': () => ({ body: null }),
    'GET /api/projects/book1/tts-corpus': () => ({ body: null }),
    'GET /api/training/readiness': () => ({ body: { ready: false, experimental: true, diagnosticCode: 'training.runtime.not_ready', cpuThreads: 2 } }),
    'GET /api/runtime/readiness': () => ({ body: { ready: true, experimental: true, diagnosticCode: null, profileId: 'q8-f16-cpu' } }), ...overrides,
  };
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    calls.push({ path, init }); const route = routes[`${init?.method ?? 'GET'} ${path}`];
    if (!route) throw new Error(`Unexpected request ${path}`);
    const reply = await route(init);
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? 200, headers: { 'Content-Type': 'application/json', ...(reply.etag ? { ETag: reply.etag } : {}) } });
  });
  return calls;
}
async function open(overrides: Record<string, Responder> = {}, snapshot: unknown = project) {
  const calls = serve(overrides, snapshot); const user = userEvent.setup(); render(<App />);
  await screen.findByText('Пока нет голосов'); await user.type(screen.getByLabelText('Название голоса'), 'Моя книга');
  await user.click(screen.getByRole('button', { name: 'Добавить голос' }));
  await screen.findByRole('heading', { name: 'Моя книга' });
  return { user, calls };
}
const emit = async (type = 'job', source = TestEventSource.instances.at(-1)!) => { await act(async () => { source.emit(type); }); };
const deferred = <T,>() => { let resolve!: (value: T) => void; const promise = new Promise<T>(done => { resolve = done; }); return { promise, resolve }; };

describe('Persistent generation interface', () => {
  it('selects only fragments without audio and follows newly accepted audio before enqueueing', async () => {
    let snapshot: ProjectSnapshot = { ...project, segments: project.segments.map(item => ({
      ...item, outputArtifactId: item.id === 's1' ? 'audio1' : null,
    })) };
    const { user, calls } = await open({
      'GET /api/projects/book1': () => ({ body: snapshot, etag: `"${snapshot.revision}"` }),
      'POST /api/projects/book1/jobs': () => ({ body: { ...job, state: 'queued', totalUnits: 1 }, status: 202, etag: '"3"' }),
    }, snapshot);
    const fragmentSettings = screen.getByText('Выбор фрагментов');
    expect(fragmentSettings.closest('details')).not.toHaveAttribute('open');
    await user.click(fragmentSettings);
    const selection = screen.getByRole('group', { name: 'Фрагменты для озвучивания' });
    const first = within(selection).getByRole('checkbox', { name: 'Фрагмент 1' });
    const second = within(selection).getByRole('checkbox', { name: 'Фрагмент 2' });
    const third = within(selection).getByRole('checkbox', { name: 'Фрагмент 3' });
    const missing = within(selection).getByRole('button', { name: 'Выбрать без аудио' });

    await user.click(missing);
    expect(first).not.toBeChecked();
    expect(second).toBeChecked();
    expect(third).toBeChecked();
    expect(first).toHaveAccessibleDescription('Есть аудио');
    await user.click(within(selection).getByRole('button', { name: 'Выбрать все' }));
    expect(first).toBeChecked();
    await user.click(missing);
    expect(calls.filter(call => call.init?.method === 'POST' && call.path.endsWith('/jobs'))).toHaveLength(0);

    snapshot = { ...snapshot, revision: 3, segments: snapshot.segments.map(item =>
      item.id === 's3' ? { ...item, outputArtifactId: 'audio3' } : item) };
    await emit();
    expect(third).not.toBeChecked();
    expect(within(selection).getByText('Выбрано 1 из 3')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Озвучить выбранные фрагменты' }));
    const command = calls.find(call => call.init?.method === 'POST' && call.path.endsWith('/jobs'))!;
    expect(JSON.parse(command.init!.body as string)).toEqual({ kind: 'generation', segmentIds: ['s2'] });
    expect(new Headers(command.init?.headers).get('If-Match')).toBe('"3"');
  });

  it('keeps active controls visible, opens older jobs on demand, and shows a job after it finishes', async () => {
    const current = { ...job, textTitle: 'Текущая глава' };
    const previous = { ...job, id: 'previous', state: 'completed', textTitle: 'Последняя проба',
      createdUtc: '2026-10-10T15:00:00Z' };
    const older = { ...previous, id: 'older', textTitle: 'Первая проба', createdUtc: '2026-10-10T10:00:00Z',
      audioResults: [{ segmentId: 's1', order: 0, artifactId: 'archive-audio', seconds: 1, spokenText: 'Текст первой пробы.' }] };
    let jobs = [current, previous, older];
    const { user } = await open({ 'GET /api/projects/book1/jobs': () => ({ body: jobs }) });
    const queue = await screen.findByRole('region', { name: 'Очередь озвучивания' });
    expect(within(queue).getByText('Текст: Текущая глава')).toBeVisible();
    expect(within(queue).getByRole('button', { name: 'Пауза' })).toBeEnabled();
    expect(within(screen.getByRole('region', { name: 'Последнее задание' })).getByText('Текст: Последняя проба')).toBeVisible();
    const disclosure = screen.getByText('История заданий', { exact: true });
    expect(screen.queryByText('Текст: Первая проба')).not.toBeInTheDocument();
    await user.click(disclosure);
    expect(await screen.findByText('Текст: Первая проба')).toBeVisible();
    expect(screen.getByLabelText('Аудио результата 1')).toHaveAttribute('src', '/api/projects/book1/artifacts/archive-audio');
    await user.click(disclosure);
    await waitFor(() => expect(screen.queryByText('Текст: Первая проба')).not.toBeInTheDocument());
    jobs = [{ ...current, state: 'completed', completedUnits: 2, revision: 4 }, previous, older];
    await emit();
    expect(screen.queryByRole('region', { name: 'Очередь озвучивания' })).not.toBeInTheDocument();
    expect(within(screen.getByRole('region', { name: 'Последнее задание' })).getByText('Текст: Текущая глава')).toBeVisible();
  });
  it('explains unconfirmed decode-limit completion and waits for an explicit user command', async () => {
    const { calls } = await open({ 'GET /api/projects/book1/jobs': () => ({ body: [{ ...job, state: 'failed', phase: 'failed', diagnosticCode: 'worker.output.limit' }] }) });
    expect(await screen.findByText(/Не удалось подтвердить, что фрагмент прочитан до конца/)).toHaveTextContent(/Разделите его на более короткие фрагменты/);
    await emit();
    expect(calls.filter(call => call.init?.method === 'POST' && call.path !== '/api/projects')).toHaveLength(0);
    expect(screen.queryByLabelText('Аудио фрагмента 1')).not.toBeInTheDocument();
  });
  it('requires explicit consent for a selectable saved voice and sends that exact version', async () => {
    const { user, calls } = await open({ ...savedRoutes, 'POST /api/projects/book1/jobs': () => ({ body: { ...job, kind: 'preview', state: 'queued', totalUnits: 1 }, status: 202, etag: '"3"' }) });
    const choice = await screen.findByLabelText('Голос для озвучивания');
    await screen.findByRole('option', { name: /шаг 125/ });
    expect(choice).toHaveValue('');
    const preview = screen.getByRole('button', { name: 'Проба голоса' });
    await waitFor(() => expect(preview).toBeEnabled());
    expect(screen.getByRole('option', { name: /Версия 1.*недоступна/ })).toBeDisabled();
    await user.selectOptions(choice, 'saved1');
    expect(preview).toBeDisabled();
    expect(screen.getByText(/Сначала текст озвучивается.*затем.*RVC/)).toBeInTheDocument();
    await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
    await user.click(preview);
    const commands = calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST');
    expect(commands).toHaveLength(1);
    expect(JSON.parse(commands[0].init!.body as string)).toEqual({ kind: 'preview', segmentIds: ['s1'], savedModelId: 'saved1', experimentalConfirmed: true });
    expect(new Headers(commands[0].init?.headers).get('If-Match')).toBe('"2"');
  });
  it.each(['anchor', 'runtime'] as const)('cannot use a saved voice without its required %s', async missing => {
    const { user, calls } = await open({ ...savedRoutes,
      ...(missing === 'runtime' ? { 'GET /api/training/readiness': () => ({ body: { ready: false, experimental: true, diagnosticCode: 'training.runtime.not_ready', cpuThreads: 2 } }) } : {}),
    }, missing === 'anchor' ? { ...project, voiceAnchor: null } : project);
    if (missing === 'anchor') {
      expect(screen.getByRole('button', { name: 'Подтвердить образец' })).toBeVisible();
      expect(screen.queryByLabelText('Голос для озвучивания')).not.toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'Проба голоса' })).not.toBeInTheDocument();
      expect(calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST')).toHaveLength(0);
      return;
    }
    await screen.findByRole('option', { name: /шаг 125/ });
    await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'saved1');
    await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
    expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Озвучить выбранные фрагменты' })).toBeDisabled();
    expect(calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST')).toHaveLength(0);
  });
  it('uses a new command key when the selected saved version changes after network uncertainty', async () => {
    let attempts = 0;
    const { user, calls } = await open({ ...savedRoutes, 'POST /api/projects/book1/jobs': () => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...job, kind: 'preview', state: 'queued', totalUnits: 1 }, status: 202, etag: '"3"' }; } });
    const choice = await screen.findByLabelText('Голос для озвучивания');
    await screen.findByRole('option', { name: /шаг 125/ });
    await user.selectOptions(choice, 'saved1');
    await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
    await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
    await screen.findByRole('alert');
    expect(attempts).toBe(1);
    await user.selectOptions(choice, 'saved2');
    const confirmation = screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/);
    expect(confirmation).not.toBeChecked();
    expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
    await user.click(confirmation); await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
    const commands = calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST');
    expect(commands.map(call => JSON.parse(call.init!.body as string).savedModelId)).toEqual(['saved1', 'saved2']);
    const keys = commands.map(call => new Headers(call.init?.headers).get('Idempotency-Key'));
    expect(keys[0]).toBeTruthy(); expect(keys[1]).toBeTruthy(); expect(keys[1]).not.toBe(keys[0]);
  });
  it('keeps the chosen saved version after a project conflict and waits for another user command', async () => {
    const { user, calls } = await open({ ...savedRoutes, 'POST /api/projects/book1/jobs': () => ({ status: 412, etag: '"9"', body: { code: 'project.conflict', conflictType: 'project', currentSnapshot: { ...project, revision: 9 } } }) });
    await screen.findByRole('option', { name: /шаг 125/ });
    await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'saved1');
    const confirmation = screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/);
    await user.click(confirmation); await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
    await screen.findByRole('alert');
    expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('saved1');
    expect(confirmation).toBeChecked();
    expect(calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST')).toHaveLength(1);
    expect(screen.getByTitle('Версия 9')).toBeInTheDocument();
  });
  it('does not show selectable versions belonging to another recording', async () => {
    const { user, calls } = await open({ ...savedRoutes, 'GET /api/projects/book1/training': () => ({ body: { ...savedTraining, sourceRecordingId: 'other-recording' }, etag: '"5"' }) });
    const choice = await screen.findByLabelText('Голос для озвучивания');
    await user.click(screen.getByRole('button', { name: 'Дообучение' }));
    await user.click(screen.getByText('Прежний способ обучения · RVC'));
    await screen.findByRole('heading', { name: 'Сохранённые версии модели' });
    await user.click(screen.getByRole('button', { name: 'Озвучивание' }));
    expect(choice).toHaveValue('');
    expect(screen.queryByRole('option', { name: /шаг 125/ })).not.toBeInTheDocument();
    expect(calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST')).toHaveLength(0);
  });
  it('does not reuse an uncertain command key after its experimental consent value changes', async () => {
    const calls = serve({ 'POST /api/projects/book1/jobs': () => { throw new Error('Lost response'); } });
    const client = new StudioClient(); await client.bootstrap();
    const install = vi.fn();
    const { result } = renderHook(() => useProjectEvents(client, 'book1', install));
    await waitFor(() => expect(result.current.readiness?.ready).toBe(true));
    await act(async () => { await result.current.enqueue('preview', ['s1'], '"2"', { savedModelId: 'saved1', experimentalConfirmed: true }); });
    await act(async () => { await result.current.enqueue('preview', ['s1'], '"2"', { savedModelId: 'saved1', experimentalConfirmed: false }); });
    const commands = calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST');
    expect(commands.map(call => JSON.parse(call.init!.body as string).experimentalConfirmed)).toEqual([true, false]);
    expect(new Headers(commands[0].init?.headers).get('Idempotency-Key')).not.toBe(new Headers(commands[1].init?.headers).get('Idempotency-Key'));
  });
  it('offers one book download only after every current fragment has accepted audio', async () => {
    const full = { ...project, segments: project.segments.map((s, i) => ({ ...s, outputArtifactId: `audio${i}` })) };
    await open({}, full);
    expect(await screen.findByRole('link', { name: 'Скачать всю книгу · WAV' })).toHaveAttribute('href', '/api/projects/book1/book.wav?revision=2');
  });
  it('does not present an incomplete collection as a full audiobook', async () => {
    await open({}, { ...project, segments: [{ ...project.segments[0], outputArtifactId: 'audio1' }, ...project.segments.slice(1)] });
    expect(screen.queryByRole('link', { name: 'Скачать всю книгу · WAV' })).not.toBeInTheDocument();
    expect(await screen.findByText('WAV будет доступен после озвучивания всех фрагментов.')).toBeInTheDocument();
  });
  it('does not retain pruned terminal jobs forever after authoritative history refresh', async () => {
    let pruned = false;
    const history = Array.from({ length: 32 }, (_, i) => ({ ...job, id: `recent${i}`, state: 'completed', phase: 'completed', completedUnits: 2, createdUtc: `2026-10-11T00:01:${String(i).padStart(2, '0')}Z` }));
    const { user } = await open({ 'GET /api/projects/book1/jobs': () => ({ body: pruned ? history : [{ ...job, state: 'completed', completedUnits: 2 }] }) });
    await screen.findByRole('article', { name: 'Задание озвучивания' }); pruned = true; await emit();
    await user.click(await screen.findByText('История заданий', { exact: true }));
    expect(await screen.findByText(/последние 32 завершённых задания/)).toBeVisible();
    await waitFor(() => expect(screen.getAllByRole('article', { name: 'Задание озвучивания' })).toHaveLength(32));
  });
  it('reuses the request key after network uncertainty without automatically issuing another command', async () => {
    let attempts = 0;
    const { user, calls } = await open({ 'POST /api/projects/book1/jobs': () => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...job, kind: 'preview', totalUnits: 1, state: 'queued' }, status: 202, etag: '"3"' }; } });
    const preview = await screen.findByRole('button', { name: 'Проба голоса' }); await waitFor(() => expect(preview).toBeEnabled());
    await user.click(preview); await screen.findByRole('alert'); expect(attempts).toBe(1);
    await user.click(preview); await screen.findByRole('article', { name: 'Пробное задание' });
    const keys = calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST').map(call => new Headers(call.init?.headers).get('Idempotency-Key'));
    expect(keys).toHaveLength(2); expect(keys[0]).toBeTruthy(); expect(keys[1]).toBe(keys[0]);
  });
  it('keeps a job conflict separate from a project snapshot and ignores private error details', async () => {
    serve({ 'POST /api/projects/book1/jobs/job1/pause': () => ({ status: 412, etag: '"9"', body: { code: 'job.conflict', conflictType: 'job', currentSnapshot: { ...job, revision: 9 }, detail: 'C:/private/secret' } }) });
    const client = new StudioClient(); await client.bootstrap();
    try { await client.snapshot('/api/projects/book1/jobs/job1/pause', 'POST', undefined, '"3"', 'key1'); throw new Error('Expected conflict'); }
    catch (failure) {
      expect(failure).toBeInstanceOf(ApiError); const error = failure as ApiError;
      expect(error.current).toBeUndefined();
      expect(error).toMatchObject({ currentJob: { snapshot: { id: 'job1', revision: 9 }, etag: '"9"' } });
      expect(error.message).not.toContain('private');
    }
  });
  it('enqueues a saved preview then shows accepted audio after a durable hint', async () => {
    let finished = false;
    const { user, calls } = await open({
      'GET /api/projects/book1': () => ({ body: finished ? { ...project, revision: 4, segments: [{ ...segment, outputArtifactId: 'audio1', status: 'completed' }, ...project.segments.slice(1)] } : project, etag: finished ? '"4"' : '"2"' }),
      'GET /api/projects/book1/jobs': () => ({ body: finished ? [{ ...job, kind: 'preview', state: 'completed', phase: 'completed', completedUnits: 1, totalUnits: 1, revision: 5 }] : [] }),
      'POST /api/projects/book1/jobs': () => ({ body: { ...job, kind: 'preview', state: 'queued', revision: 1, totalUnits: 1 }, status: 202, etag: '"1"' }),
    });
    const preview = await screen.findByRole('button', { name: 'Проба голоса' }); await waitFor(() => expect(preview).toBeEnabled());
    await user.click(preview); const command = calls.find(call => call.path.endsWith('/jobs') && call.init?.method === 'POST')!;
    expect(JSON.parse(command.init!.body as string)).toEqual({ kind: 'preview', segmentIds: ['s1'] });
    expect(new Headers(command.init?.headers).get('If-Match')).toBe('"2"');
    expect(new Headers(command.init?.headers).get('X-CSRF-TOKEN')).toBe('csrf');
    expect(new Headers(command.init?.headers).get('Idempotency-Key')).toBeTruthy();
    finished = true; await emit();
    expect(await screen.findByLabelText('Аудио фрагмента 1')).toHaveAttribute('src', '/api/projects/book1/artifacts/audio1');
    expect(await screen.findByText('Готово: 1 из 1')).toBeInTheDocument();
  });
  it('preserves two dirty editors while a clean editor follows background snapshot changes', async () => {
    let changed = false;
    const { user } = await open({ 'GET /api/projects/book1': () => ({ body: changed ? { ...project, revision: 4, segments: project.segments.map(s => ({ ...s, spokenText: `Новый ${s.id}`, outputArtifactId: s.id === 's1' ? 'audio1' : null })) } : project, etag: changed ? '"4"' : '"2"' }) });
    await user.click(screen.getByRole('button', { name: 'Тексты' }));
    await user.clear(screen.getByLabelText('Текст фрагмента 1')); await user.type(screen.getByLabelText('Текст фрагмента 1'), 'Моя первая правка');
    await user.clear(screen.getByLabelText('Текст фрагмента 2')); await user.type(screen.getByLabelText('Текст фрагмента 2'), 'Моя вторая правка');
    changed = true; await emit('reset'); await screen.findByTitle('Версия 4');
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue('Моя первая правка');
    expect(screen.getByLabelText('Текст фрагмента 2')).toHaveValue('Моя вторая правка');
    expect(screen.getByLabelText('Текст фрагмента 3')).toHaveValue('Новый s3');
  });
  it('refreshes a stale job without replaying pause or replacing the project ETag', async () => {
    const paused = { ...job, state: 'paused', requestedState: 'pause', phase: 'paused', revision: 9 };
    const { user, calls } = await open({
      'GET /api/projects/book1/jobs': () => ({ body: [job] }),
      'POST /api/projects/book1/jobs/job1/pause': () => ({ status: 412, etag: '"9"', body: { code: 'job.conflict', conflictType: 'job', currentSnapshot: paused } }),
      'POST /api/projects/book1/jobs/job1/resume': () => ({ status: 202, etag: '"10"', body: { ...job, state: 'queued', revision: 10 } }),
    });
    await user.click(await screen.findByRole('button', { name: 'Пауза' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Задание изменено/);
    expect(screen.getByTitle('Версия 2')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Продолжить задание' }));
    const controls = calls.filter(call => /\/pause$|\/resume$/.test(call.path));
    expect(controls.map(call => new Headers(call.init?.headers).get('If-Match'))).toEqual(['"3"', '"9"']);
    expect(controls).toHaveLength(2);
  });
  it('disables synthesis without a configured runtime and never presents training as ready', async () => {
    await open({ 'GET /api/runtime/readiness': () => ({ body: { ready: false, experimental: false, diagnosticCode: 'worker.runtime.not_ready', profileId: 'q8-f16-cpu' } }) });
    expect(await screen.findByRole('button', { name: 'Проба голоса' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Озвучить выбранные фрагменты' })).toBeDisabled();
    expect(screen.queryByRole('button', { name: 'Обучить голос' })).not.toBeInTheDocument();
    expect(await screen.findByText(/Движок озвучивания не настроен/)).toBeInTheDocument();
  });
  it('ignores an older in-flight refresh after a saved edit returns a newer revision', async () => {
    const old = deferred<Reply>(); let reads = 0;
    const { user } = await open({
      'GET /api/projects/book1': () => ++reads === 1 ? { body: project, etag: '"2"' } : old.promise,
      'PATCH /api/projects/book1/segments/s1': () => ({ body: { ...project, revision: 5, segments: [{ ...segment, spokenText: 'Сохранённая правка' }, ...project.segments.slice(1)] }, etag: '"5"' }),
    });
    await waitFor(() => expect(reads).toBe(1)); await emit(); await waitFor(() => expect(reads).toBe(2));
    await user.click(screen.getByRole('button', { name: 'Тексты' }));
    await user.clear(screen.getByLabelText('Текст фрагмента 1')); await user.type(screen.getByLabelText('Текст фрагмента 1'), 'Сохранённая правка');
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' })); await screen.findByTitle('Версия 5');
    await act(async () => { old.resolve({ body: { ...project, revision: 3 }, etag: '"3"' }); });
    expect(screen.getByTitle('Версия 5')).toBeInTheDocument();
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue('Сохранённая правка');
  });
  it('retains pasted text, rights, range, transcript and confirmation across unrelated reset', async () => {
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
    const setup = { ...project, voiceAnchor: null, textDocument: null, segments: [] };
    const { user } = await open({}, setup);
    await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    await user.type(screen.getByLabelText('Текст книги'), 'Несохранённая книга');
    await user.click(screen.getByRole('button', { name: 'Голос' }));
    await user.clear(screen.getByLabelText('Начало, секунды')); await user.type(screen.getByLabelText('Начало, секунды'), '5');
    await user.clear(screen.getByLabelText('Конец, секунды')); await user.type(screen.getByLabelText('Конец, секунды'), '17');
    await user.type(screen.getByLabelText('Точный текст фрагмента'), 'Мой текст'); await user.click(screen.getByLabelText('Прослушал и подтверждаю'));
    await emit('reset');
    expect(screen.getByLabelText('Текст книги')).toHaveValue('Несохранённая книга');
    expect(screen.getByLabelText('Начало, секунды')).toHaveValue(5); expect(screen.getByLabelText('Конец, секунды')).toHaveValue(17);
    expect(screen.getByLabelText('Точный текст фрагмента')).toHaveValue('Мой текст'); expect(screen.getByLabelText('Прослушал и подтверждаю')).toBeChecked();
  });
  it('closes old subscriptions and ignores late project reads after leaving the workspace', async () => {
    const late = deferred<Reply>(); let reads = 0;
    const { user } = await open({ 'GET /api/projects/book1': () => ++reads === 1 ? { body: project, etag: '"2"' } : late.promise });
    await waitFor(() => expect(reads).toBe(1)); const source = TestEventSource.instances.at(-1)!;
    await emit('job', source); await waitFor(() => expect(reads).toBe(2));
    await user.click(screen.getByRole('button', { name: '← Все голоса' })); await screen.findByText('Пока нет голосов'); expect(source.closed).toBe(true);
    await act(async () => { late.resolve({ body: { ...project, revision: 10 }, etag: '"10"' }); source.emit('reset'); });
    expect(screen.queryByRole('heading', { name: 'Моя книга' })).not.toBeInTheDocument();
    expect(screen.getByText('Пока нет голосов')).toBeInTheDocument();
  });
});
