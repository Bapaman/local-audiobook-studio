import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ProjectWorkspacePage } from '../project/ProjectWorkspacePage';

const project = { id: 'book1', displayName: 'Книга', revision: 2,
  voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 2880044, durationMilliseconds: 300000, hasClippingWarning: false },
  voiceAnchor: { artifactId: 'voice1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Образец.' },
  textDocument: { sourceKind: 'Paste', displayName: 'Текст', encoding: 'utf-8', sha256: 'hash', byteCount: 20, originalText: 'История.', normalizedText: 'История.' },
  segments: [{ id: 's1', order: 0, originalText: 'История.', normalizedText: 'История.', spokenText: 'История.', contentHash: 'hash', status: 'pending' }] };
const training = { id: 'train1', projectId: 'book1', revision: 3, state: 'training', phase: 'native_training', sourceRecordingId: 'record1', globalStep: 41, completedEpoch: 1, epochSize: 32,
  createdUtc: '2026-10-14T10:00:00Z', updatedUtc: '2026-10-14T10:00:01Z', diagnosticCode: null, canResume: false, models: [] };
type Reply = { body: unknown; status?: number; etag?: string };
type Responder = (init?: RequestInit) => Reply | Promise<Reply>;
function serve(overrides: Record<string, Responder> = {}) {
  const calls: { path: string; init?: RequestInit }[] = [];
  const routes: Record<string, Responder> = {
    'GET /api/bootstrap': () => ({ body: { version: '1', antiforgeryRequestToken: 'csrf', capabilities: { training: true, generation: true } } }),
    'GET /api/training/readiness': () => ({ body: { ready: true, experimental: true, diagnosticCode: null, cpuThreads: 2 } }),
    'GET /api/projects/book1/training': () => ({ body: null }), ...overrides,
    'GET /api/tts-corpus/readiness': () => ({ body: { ready: false, diagnosticCode: 'tts_corpus.runtime.not_ready', provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }),
    'GET /api/projects/book1/tts-corpus': () => ({ body: null }),
    'GET /api/voxcpm-training/readiness': () => ({ body: { ready: false, diagnosticCode: 'voxcpm_training.runtime.not_ready', provider: 'voxcpm2-lora-cpu-v1', cpuThreads: 2 } }),
    'GET /api/projects/book1/voxcpm-training': () => ({ body: null }),
  };
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    calls.push({ path, init });
    const route = routes[`${init?.method ?? 'GET'} ${path}`];
    if (!route) throw new Error(`Unexpected request ${path}`);
    const reply = await route(init);
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? 200, headers: { 'Content-Type': 'application/json', ...(reply.etag ? { ETag: reply.etag } : {}) } });
  });
  return calls;
}
function open(snapshot = project) {
  return render(<ProjectWorkspacePage current={{ snapshot, etag: '"2"' }} generation={{ jobs: [], readiness: { ready: true, experimental: true, diagnosticCode: null, profileId: 'q8-f16-cpu' }, pending: false, error: '', connected: true, enqueue: async () => {}, control: async () => {} }} segmentRefresh={{}} busy={false} headingRef={null} mutate={async () => {}} onBack={() => {}} />);
}
const panel = () => screen.getByRole('region', { name: 'Обучение на полной записи' });
afterEach(() => vi.useRealTimers());

describe('Local training controls', () => {
  it('does not reuse a network-uncertain STOP key or captured ETag for a new execution', async () => {
    let changed = false; let attempts = 0;
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: { ...training, executionId: changed ? 'execution-b' : 'execution-a', revision: changed ? 9 : 3 }, etag: changed ? '"9"' : '"3"' }),
      'POST /api/projects/book1/training/train1/stop': () => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...training, executionId: 'execution-b', state: 'stop_requested', revision: 10 }, etag: '"10"' }; },
    }); const view = open();
    await screen.findByRole('button', { name: 'Остановить обучение' }); vi.useFakeTimers();
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Остановить обучение' })); });
    expect(screen.getByRole('alert')).toHaveTextContent(/Нет связи/);
    changed = true; await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Остановить обучение' })); });
    const posts = calls.filter(c => c.init?.method === 'POST');
    expect(posts.map(c => new Headers(c.init?.headers).get('If-Match'))).toEqual(['"3"', '"9"']);
    expect(new Headers(posts[0].init?.headers).get('Idempotency-Key')).not.toBe(new Headers(posts[1].init?.headers).get('Idempotency-Key'));
    view.unmount();
  });
  it('retries STOP once only for progress in the same owned execution and keeps the command key', async () => {
    let attempts = 0;
    const active = { ...training, executionId: 'execution-a' };
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: active, etag: '"3"' }),
      'POST /api/projects/book1/training/train1/stop': () => ++attempts === 1
        ? { status: 412, etag: '"4"', body: { code: 'training.conflict', conflictType: 'training', currentSnapshot: { ...active, revision: 4, globalStep: 42 } } }
        : { status: 202, etag: '"5"', body: { ...active, revision: 5, state: 'stop_requested' } },
    }); open(); const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Остановить обучение' }));
    await screen.findByText('Остановка запрошена');
    const posts = calls.filter(c => c.init?.method === 'POST');
    expect(posts).toHaveLength(2);
    expect(posts.map(c => new Headers(c.init?.headers).get('If-Match'))).toEqual(['"3"', '"4"']);
    expect(new Headers(posts[0].init?.headers).get('Idempotency-Key')).toBe(new Headers(posts[1].init?.headers).get('Idempotency-Key'));
  });
  it('never retries STOP against a replacement execution even with the same training ID', async () => {
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: { ...training, executionId: 'execution-a' }, etag: '"3"' }),
      'POST /api/projects/book1/training/train1/stop': () => ({ status: 412, etag: '"4"', body: { code: 'training.conflict', conflictType: 'training', currentSnapshot: { ...training, executionId: 'execution-b', revision: 4 } } }),
    }); open(); const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Остановить обучение' }));
    await screen.findByRole('alert');
    expect(calls.filter(c => c.init?.method === 'POST')).toHaveLength(1);
  });
  it('disables training when local runtime is unready and shows its safe diagnostic', async () => {
    serve({ 'GET /api/training/readiness': () => ({ body: { ready: false, experimental: true, diagnosticCode: 'training.runtime.not_ready', cpuThreads: 2 } }) });
    open();
    expect(await screen.findByText(/training.runtime.not_ready/)).toBeInTheDocument();
    expect(within(panel()).getByRole('button', { name: 'Обучить голос' })).toBeDisabled();
  });
  it('requires the saved full recording before training', async () => {
    serve(); open({ ...project, voiceRecording: null } as unknown as typeof project);
    expect(await within(panel()).findByText(/Сначала сохраните исходную запись/)).toBeInTheDocument();
    expect(within(panel()).getByRole('button', { name: 'Обучить голос' })).toBeDisabled();
  });
  it('requires explicit acknowledgement and starts with project CAS, CSRF and idempotency', async () => {
    const calls = serve({ 'POST /api/projects/book1/training': () => ({ body: { ...training, state: 'queued', revision: 1 }, status: 202, etag: '"1"' }) });
    open(); const user = userEvent.setup();
    const consent = await within(panel()).findByRole('checkbox', { name: /экспериментальное обучение/ });
    const start = within(panel()).getByRole('button', { name: 'Обучить голос' });
    expect(start).toBeDisabled(); await user.click(consent); await user.click(start);
    expect(await screen.findByText('В очереди')).toBeInTheDocument();
    const call = calls.find(call => call.init?.method === 'POST')!;
    expect(JSON.parse(call.init?.body as string)).toEqual({ experimentalConfirmed: true });
    expect(new Headers(call.init?.headers).get('If-Match')).toBe('"2"');
    expect(new Headers(call.init?.headers).get('X-CSRF-TOKEN')).toBe('csrf');
    expect(new Headers(call.init?.headers).get('Idempotency-Key')).toBeTruthy();
  });
  it('shows actual optimizer progress and blocks text editing while active training owns the project', async () => {
    serve({ 'GET /api/projects/book1/training': () => ({ body: training, etag: '"3"' }) }); open();
    const progress = await screen.findByRole('article', { name: 'Прогресс обучения' });
    expect(progress).toHaveTextContent('Глобальный шаг:41');
    expect(progress).toHaveTextContent('Завершённые эпохи:1');
    expect(progress).toHaveTextContent('Шагов в эпохе:32');
    await waitFor(() => expect(screen.getByLabelText('Произносимый текст 1')).toBeDisabled());
    expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
    expect(screen.getByText(/Озвучивание нового текста использует короткий/)).toBeInTheDocument();
  });
  it('stops with training CAS then continues one next epoch from saved versions without quality claims', async () => {
    const saved = { ...training, revision: 4, state: 'completed', canResume: true, models: [{ id: 'model1', globalStep: 64, completedEpoch: 2, createdUtc: training.updatedUtc, qualityEstablished: false }] };
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: training, etag: '"3"' }),
      'POST /api/projects/book1/training/train1/stop': () => ({ body: saved, etag: '"4"' }),
      'POST /api/projects/book1/training/train1/resume': () => ({ body: { ...saved, state: 'queued', revision: 5 }, status: 202, etag: '"5"' }),
    }); open(); const user = userEvent.setup();
    await user.click(await screen.findByRole('button', { name: 'Остановить обучение' }));
    expect(await screen.findByText(/Качество голоса ещё не подтверждено/)).toBeInTheDocument();
    const next = screen.getByRole('button', { name: 'Обучить следующую эпоху' }); expect(next).toBeDisabled();
    await user.click(within(panel()).getByRole('checkbox', { name: /экспериментальное обучение/ })); await user.click(next);
    await screen.findByText('В очереди');
    const controls = calls.filter(call => call.init?.method === 'POST');
    expect(controls.map(call => new Headers(call.init?.headers).get('If-Match'))).toEqual(['"3"', '"4"']);
    expect(controls.every(call => !!new Headers(call.init?.headers).get('Idempotency-Key'))).toBe(true);
  });
  it('refreshes active progress within two seconds after starting from an inactive panel', async () => {
    let started = false;
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: started ? { ...training, revision: 8, globalStep: 57 } : null, ...(started ? { etag: '"8"' } : {}) }),
      'POST /api/projects/book1/training': () => { started = true; return { body: { ...training, state: 'queued', revision: 1 }, etag: '"1"' }; },
    }); const view = open();
    await screen.findByText(/Локальный движок обучения готов/);
    vi.useFakeTimers();
    fireEvent.click(within(panel()).getByRole('checkbox', { name: /экспериментальное обучение/ }));
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Обучить голос' })); });
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(screen.getByRole('article', { name: 'Прогресс обучения' })).toHaveTextContent('Глобальный шаг:57');
    const reads = calls.filter(call => call.path.endsWith('/training') && call.init?.method === 'GET');
    expect(reads).toHaveLength(2);
    expect(calls.filter(call => call.path === '/api/training/readiness')).toHaveLength(2);
    view.unmount(); expect(reads[1].init?.signal?.aborted).toBe(true);
    const count = calls.length; await act(async () => { await vi.advanceTimersByTimeAsync(30000); }); expect(calls).toHaveLength(count);
  });
  it('applies a training conflict snapshot without replaying the control or displaying private details', async () => {
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: training, etag: '"3"' }),
      'POST /api/projects/book1/training/train1/stop': () => ({ status: 412, etag: '"9"', body: { code: 'training.conflict', conflictType: 'training', currentSnapshot: { ...training, state: 'paused', revision: 9, canResume: true }, detail: 'C:/private/model' } }),
    }); open(); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Остановить обучение' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/из другой вкладки/);
    expect(screen.getByText('Остановлено')).toBeInTheDocument();
    expect(screen.queryByText(/private/)).not.toBeInTheDocument();
    expect(calls.filter(call => call.init?.method === 'POST')).toHaveLength(1);
  });
  it('refetches a conflict without a snapshot and offers no automatic restart', async () => {
    let conflicted = false;
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: conflicted ? { ...training, state: 'interrupted', revision: 9, canResume: false } : training, etag: conflicted ? '"9"' : '"3"' }),
      'POST /api/projects/book1/training/train1/stop': () => { conflicted = true; return { status: 412, body: { code: 'training.conflict' } }; },
    }); open(); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Остановить обучение' }));
    expect(await screen.findByText('Прервано при закрытии студии')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Продолжить обучение' })).toBeDisabled();
    expect(calls.filter(call => call.init?.method === 'POST')).toHaveLength(1);
  });
  it('keeps the idempotency key after a lost response and requires a user retry', async () => {
    let attempts = 0;
    const calls = serve({ 'POST /api/projects/book1/training': () => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...training, state: 'queued', revision: 1 }, etag: '"1"' }; } });
    open(); const user = userEvent.setup(); await user.click(await within(panel()).findByRole('checkbox', { name: /экспериментальное обучение/ }));
    await user.click(screen.getByRole('button', { name: 'Обучить голос' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Нет связи/); expect(attempts).toBe(1);
    await user.click(screen.getByRole('button', { name: 'Обучить голос' })); await screen.findByText('В очереди');
    const keys = calls.filter(call => call.init?.method === 'POST').map(call => new Headers(call.init?.headers).get('Idempotency-Key'));
    expect(keys).toHaveLength(2); expect(keys[0]).toBeTruthy(); expect(keys[1]).toBe(keys[0]);
  });
  it.each(['paused', 'interrupted', 'failed', 'completed'])('allows explicit fresh preparation after %s without an accepted model', async (state) => {
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: { ...training, state, canResume: false, models: [] }, etag: '"3"' }),
      'POST /api/projects/book1/training': () => ({ body: { ...training, state: 'queued', revision: 4 }, etag: '"4"' }),
    }); open(); const user = userEvent.setup();
    const repeat = await screen.findByRole('button', { name: 'Повторить подготовку' });
    expect(repeat).toBeDisabled();
    expect(screen.getByRole('button', { name: state === 'completed' ? 'Обучить следующую эпоху' : 'Продолжить обучение' })).toBeDisabled();
    await user.click(within(panel()).getByRole('checkbox', { name: /экспериментальное обучение/ }));
    await user.click(repeat); await screen.findByText('В очереди');
    const posts = calls.filter(call => call.init?.method === 'POST');
    expect(posts).toHaveLength(1); expect(posts[0].path).toBe('/api/projects/book1/training');
    expect(new Headers(posts[0].init?.headers).get('If-Match')).toBe('"2"');
    expect(new Headers(posts[0].init?.headers).get('Idempotency-Key')).toBeTruthy();
    expect(JSON.parse(posts[0].init?.body as string)).toEqual({ experimentalConfirmed: true });
  });
  it.each([
    ['training.time_budget', 'Достигнут лимит времени'],
    ['training.checkpoint_budget', 'Достигнут лимит сохранений'],
  ])('continues the incomplete epoch after %s with a readable budget diagnostic', async (diagnosticCode, message) => {
    const paused = { ...training, state: 'paused', diagnosticCode, canResume: true, models: [{ id: 'model1', globalStep: 41, completedEpoch: 1, createdUtc: training.updatedUtc, qualityEstablished: false }] };
    const calls = serve({
      'GET /api/projects/book1/training': () => ({ body: paused, etag: '"3"' }),
      'POST /api/projects/book1/training/train1/resume': () => ({ body: { ...paused, state: 'queued', revision: 4, diagnosticCode: null }, etag: '"4"' }),
    }); open(); const user = userEvent.setup();
    expect(await screen.findByText(new RegExp(message))).toBeInTheDocument();
    expect(screen.getByRole('article', { name: 'Прогресс обучения' })).toHaveTextContent('Завершённые эпохи:1');
    expect(screen.queryByRole('button', { name: 'Обучить следующую эпоху' })).not.toBeInTheDocument();
    await user.click(within(panel()).getByRole('checkbox', { name: /экспериментальное обучение/ }));
    await user.click(screen.getByRole('button', { name: 'Продолжить обучение' })); await screen.findByText('В очереди');
    const post = calls.find(call => call.init?.method === 'POST')!;
    expect(post.path).toBe('/api/projects/book1/training/train1/resume');
    expect(new Headers(post.init?.headers).get('If-Match')).toBe('"3"');
  });
});
