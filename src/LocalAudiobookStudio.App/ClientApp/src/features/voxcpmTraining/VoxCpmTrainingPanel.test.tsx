import { act, fireEvent, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { VoxCpmTrainingPanel } from './VoxCpmTrainingPanel';
import { useVoxCpmTraining } from './useVoxCpmTraining';
import { ProjectWorkspacePage } from '../project/ProjectWorkspacePage';
import type { VoxCpmTrainingSnapshot, TtsCorpusVersion, ProjectSnapshot } from '../../api/types';

const versions: TtsCorpusVersion[] = [{ id: 'v1', sha256: 'hash1', clipCount: 2, retainedDurationSeconds: 300, createdUtc: '2026-10-18T00:00:00Z' },
  { id: 'v2', sha256: 'hash2', clipCount: 3, retainedDurationSeconds: 392, createdUtc: '2026-10-19T00:00:00Z' }];
const saved: VoxCpmTrainingSnapshot = { id: 'train1', projectId: 'book1', revision: 3, state: 'paused', phase: 'paused', corpusVersionId: 'v2', corpusManifestSha256: 'hash2',
  corpusClipCount: 3, retainedDurationSeconds: 392, globalStep: 1, completedEpochs: 0, nextRetainedOrdinal: 1, nextClipId: 'c2', targetCompletedEpochs: 1, executionId: 'e1',
  models: [{ id: 'm1', trainingId: 'train1', corpusVersionId: 'v2', manifestSha256: 'model-hash', globalStep: 1, completedEpochs: 0, createdUtc: '2026-10-19T00:00:01Z' }],
  canResume: true, qualityEstablished: false, diagnosticCode: null, createdUtc: '2026-10-19T00:00:00Z', updatedUtc: '2026-10-19T00:00:01Z' };
type Reply = { body: unknown; etag?: string; status?: number };
type Handler = (init?: RequestInit) => Reply | Promise<Reply>;
const read = '/api/projects/book1/voxcpm-training';
const ready = { ready: true, diagnosticCode: null, provider: 'voxcpm2-lora-cpu-v1', cpuThreads: 2 };
function serve(overrides: Record<string, Handler> = {}) {
  const calls: { path: string; init?: RequestInit }[] = [];
  const routes: Record<string, Handler> = { 'GET /api/bootstrap': () => ({ body: { version: '1', antiforgeryRequestToken: 'csrf', capabilities: { generation: true, training: true } } }),
    'GET /api/voxcpm-training/readiness': () => ({ body: ready }), [`GET ${read}`]: () => ({ body: saved, etag: '"3"' }),
    'GET /api/training/readiness': () => ({ body: { ready: true, experimental: true, diagnosticCode: null, cpuThreads: 2 } }), 'GET /api/projects/book1/training': () => ({ body: null }),
    'GET /api/tts-corpus/readiness': () => ({ body: { ready: true, diagnosticCode: null, provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }), 'GET /api/projects/book1/tts-corpus': () => ({ body: null }), ...overrides };
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    calls.push({ path, init }); const route = routes[`${init?.method ?? 'GET'} ${path}`]; if (!route) throw new Error(`Unexpected ${path}`);
    const reply = await route(init); return new Response(JSON.stringify(reply.body), { status: reply.status ?? 200, headers: reply.etag ? { ETag: reply.etag } : {} });
  }); return calls;
}
function open(projectId = 'book1', projectEtag = '"2"', onBusyChange = vi.fn()) {
  return render(<VoxCpmTrainingPanel projectId={projectId} projectEtag={projectEtag} versions={versions} externalBusy={false} onBusyChange={onBusyChange} onProjectConflict={() => {}} />);
}
const panel = () => screen.getByRole('region', { name: 'Дообучение VoxCPM2' });
const posts = (calls: ReturnType<typeof serve>) => calls.filter(c => c.init?.method === 'POST');
afterEach(() => vi.useRealTimers());
describe('Resumable VoxCPM2 training', () => {
  it('approved_version_id_and_exact_consent_are_sent', async () => {
    const calls = serve({ [`GET ${read}`]: () => ({ body: null }), [`POST ${read}`]: () => ({ body: { ...saved, state: 'queued', revision: 1, globalStep: 0, models: [] }, etag: '"1"' }) });
    open(); const button = await screen.findByRole('button', { name: 'Начать проход' }); await waitFor(() => expect(screen.getByLabelText('Версия корпуса для обучения')).toHaveValue('v2'));
    expect(button).toBeDisabled(); fireEvent.click(screen.getByRole('checkbox', { name: /понимаю, что качество/ })); fireEvent.click(button);
    await within(panel()).findByText('В очереди'); expect(posts(calls)).toHaveLength(1);
    expect(JSON.parse(posts(calls)[0].init!.body as string)).toEqual({ corpusVersionId: 'v2', experimentalConfirmed: true }); expect(panel()).not.toHaveTextContent('Голос обучен');
  });
  it('saved_steps_and_epochs_are_not_quality_percent', async () => {
    serve(); open(); await within(panel()).findByText(/Принятые шаги: 1/);
    expect(panel()).toHaveTextContent('Полных проходов: 0'); expect(panel()).toHaveTextContent('Качество голоса ещё не оценивалось'); expect(panel()).not.toHaveTextContent('%');
    expect(within(panel()).getByRole('button', { name: 'Продолжить обучение' })).toBeInTheDocument(); expect(within(panel()).queryByRole('radio')).not.toBeInTheDocument();
  });
  it('old_models_remain_without_ready_runtime_and_delayed_readiness_does_not_block_history', async () => {
    let resolve!: (value: Reply) => void; const pending = new Promise<Reply>(done => { resolve = done; });
    serve({ 'GET /api/voxcpm-training/readiness': () => pending }); const view = open();
    await within(panel()).findByText(/Сохранение.*m1/); expect(screen.getByRole('button', { name: 'Продолжить обучение' })).toBeDisabled();
    await act(async () => { resolve({ body: { ...ready, ready: false, diagnosticCode: 'voxcpm_training.runtime.not_ready' } }); });
    expect(panel()).toHaveTextContent(/Сохранение.*m1/); expect(panel()).toHaveTextContent(/не готов/); view.unmount();
  });
  it('completed_offers_another_pass_without_promising_quality', async () => {
    serve({ [`GET ${read}`]: () => ({ body: { ...saved, state: 'completed', globalStep: 3, completedEpochs: 1, nextRetainedOrdinal: 0 }, etag: '"3"' }) }); open();
    expect(await screen.findByRole('button', { name: 'Ещё один проход' })).toBeDisabled(); expect(panel()).toHaveTextContent('Качество голоса ещё не оценивалось');
  });
  it('uncertain_command_retries_only_on_user_click_with_same_key_body_etag', async () => {
    let attempts = 0; const calls = serve({ [`GET ${read}`]: () => ({ body: null }), [`POST ${read}`]: () => {
      if (++attempts === 1) throw new Error('Lost response'); return { body: { ...saved, revision: 1, state: 'queued' }, etag: '"1"' };
    } });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    fireEvent.click(screen.getByRole('checkbox', { name: /понимаю, что качество/ }));
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Начать проход' })); });
    expect(posts(calls)).toHaveLength(1); await act(async () => { await vi.advanceTimersByTimeAsync(45000); }); expect(posts(calls)).toHaveLength(1);
    view.rerender(<VoxCpmTrainingPanel projectId="book1" projectEtag={'"9"'} versions={versions} externalBusy={false} onBusyChange={() => {}} onProjectConflict={() => {}} />);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Повторить тот же запрос' })); });
    const sent = posts(calls); expect(sent).toHaveLength(2); expect(sent[0].init?.body).toBe(sent[1].init?.body);
    expect(sent.map(c => new Headers(c.init?.headers).get('If-Match'))).toEqual(['"2"', '"2"']); expect(new Headers(sent[0].init?.headers).get('Idempotency-Key')).toBe(new Headers(sent[1].init?.headers).get('Idempotency-Key')); view.unmount();
  });
  it('expired_key_does_not_auto_start', async () => {
    const calls = serve({ [`GET ${read}`]: () => ({ body: null }), [`POST ${read}`]: () => ({ body: { code: 'voxcpm_training.idempotency.expired' }, status: 409 }) });
    vi.useFakeTimers(); const view = open(); await act(async () => {}); fireEvent.click(screen.getByRole('checkbox', { name: /понимаю, что качество/ }));
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Начать проход' })); await vi.advanceTimersByTimeAsync(45000); });
    expect(posts(calls)).toHaveLength(1); expect(screen.queryByRole('button', { name: 'Повторить тот же запрос' })).not.toBeInTheDocument(); expect(panel()).toHaveTextContent(/устарел/); view.unmount();
  });
  it('late_precommand_poll_and_old_project_response_cannot_replace_new_snapshot', async () => {
    let resolve!: (value: Reply) => void; const delayed = new Promise<Reply>(done => { resolve = done; });
    serve({ [`GET ${read}`]: () => delayed, [`POST ${read}`]: () => ({ body: { ...saved, id: 'new-train', revision: 1, state: 'queued' }, etag: '"1"' }),
      'GET /api/projects/book2/voxcpm-training': () => ({ body: { ...saved, projectId: 'book2', id: 'other-train' }, etag: '"3"' }) });
    const { result, rerender, unmount } = renderHook(({ id }) => useVoxCpmTraining(id, '"2"', versions, () => {}), { initialProps: { id: 'book1' } });
    await act(async () => {}); await act(async () => { await result.current.start('v2', true); }); expect(result.current.snapshot?.id).toBe('new-train');
    await act(async () => { resolve({ body: saved, etag: '"3"' }); }); expect(result.current.snapshot?.id).toBe('new-train');
    rerender({ id: 'book2' }); await waitFor(() => expect(result.current.snapshot?.id).toBe('other-train'));
    await act(async () => { resolve({ body: saved, etag: '"3"' }); }); expect(result.current.snapshot?.id).toBe('other-train'); unmount();
  });
  it('active_training_blocks_conflicting_workspace_actions_and_clears_busy_on_unmount', async () => {
    vi.useFakeTimers();
    let running = true;
    serve({ [`GET ${read}`]: () => ({ body: { ...saved, state: running ? 'training' : 'paused', canResume: !running, revision: running ? 3 : 4 }, etag: running ? '"3"' : '"4"' }) });
    const busy = vi.fn(); const standalone = open('book1', '"2"', busy); await act(async () => {}); expect(busy).toHaveBeenLastCalledWith(true); standalone.unmount(); expect(busy).toHaveBeenLastCalledWith(false);
    const project: ProjectSnapshot = { id: 'book1', displayName: 'Книга', revision: 2, voiceRecording: null, voiceAnchor: null, textDocument: null, segments: [] };
    const draw = (snapshot: ProjectSnapshot) => <ProjectWorkspacePage current={{ snapshot, etag: '"2"' }} generation={{ jobs: [], readiness: { ready: true, experimental: true, diagnosticCode: null, profileId: 'q8-f16-cpu' }, pending: false, error: '', connected: true, enqueue: async () => {}, control: async () => {} }} segmentRefresh={{}} busy={false} headingRef={null} mutate={async () => {}} onBack={() => {}} />;
    const workspace = render(draw(project));
    await act(async () => {}); expect(screen.getByLabelText('Запись голоса WAV')).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: /экспериментальную подготовку/ })).toBeDisabled(); expect(screen.getByRole('checkbox', { name: /Подтверждаю экспериментальное обучение/ })).toBeDisabled();
    workspace.rerender(draw({ ...project,
      voiceRecording: { artifactId: 'r1', sha256: 'hash', byteLength: 44, durationMilliseconds: 392000, hasClippingWarning: false },
      voiceAnchor: { artifactId: 'a1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Образец.' },
      textDocument: { sourceKind: 'Paste', displayName: 'Текст', encoding: 'utf-8', sha256: 'hash', byteCount: 10, originalText: 'Текст.', normalizedText: 'Текст.' },
      segments: [{ id: 's1', order: 0, originalText: 'Текст.', normalizedText: 'Текст.', spokenText: 'Текст.', contentHash: 'hash', status: 'pending' }] }));
    expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
    running = false; await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeEnabled(); workspace.unmount();
  });
  it('aborts_old_project_read_and_ignores_its_late_response', async () => {
    let resolve!: (value: Reply) => void; const delayed = new Promise<Reply>(done => { resolve = done; });
    const calls = serve({ [`GET ${read}`]: () => delayed, 'GET /api/projects/book2/voxcpm-training': () => ({ body: { ...saved, projectId: 'book2', id: 'other' }, etag: '"3"' }) });
    const hook = renderHook(({ id }) => useVoxCpmTraining(id, '"2"', versions, () => {}), { initialProps: { id: 'book1' } });
    await act(async () => {}); hook.rerender({ id: 'book2' }); await waitFor(() => expect(hook.result.current.snapshot?.id).toBe('other'));
    expect(calls.find(c => c.path === read)?.init?.signal?.aborted).toBe(true);
    await act(async () => { resolve({ body: { ...saved, state: 'training' }, etag: '"3"' }); }); expect(hook.result.current.snapshot?.id).toBe('other'); hook.unmount();
  });
});
