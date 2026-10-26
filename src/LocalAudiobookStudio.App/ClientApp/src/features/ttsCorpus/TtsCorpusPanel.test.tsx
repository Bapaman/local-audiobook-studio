import { act, fireEvent, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ProjectWorkspacePage } from '../project/ProjectWorkspacePage';
import { useTtsCorpus } from './useTtsCorpus';
import type { SnapshotResponse } from '../../api/types';

const project = { id: 'book1', displayName: 'Книга', revision: 2,
  voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 2880044, durationMilliseconds: 30000, hasClippingWarning: false },
  voiceAnchor: { artifactId: 'voice1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Точный текст.' },
  textDocument: { sourceKind: 'Paste', displayName: 'Текст книги', encoding: 'utf-8', sha256: 'hash', byteCount: 20, originalText: 'История.', normalizedText: 'История.' },
  segments: [{ id: 's1', order: 0, originalText: 'История.', normalizedText: 'История.', spokenText: 'История.', contentHash: 'hash', status: 'pending' }] };
const clip = { id: 'clip1', index: 0, startSample: 0, endSample: 160000, text: 'Точный текст.', excluded: false, reviewed: true,
  warningCodes: ['tts_corpus.transcript.sparse'], words: [{ text: 'Точный', start: 0.1, end: null }] };
const corpus = { id: 'corpus1', projectId: 'book1', revision: 3, state: 'completed', phase: 'completed', sourceRecordingId: 'record1',
  sourceDurationSeconds: 300, retainedDurationSeconds: 10, totalClipCount: 1, completedClipCount: 1, clips: [clip], versions: [], canResume: false,
  diagnosticCode: null, createdUtc: '2026-10-16T10:00:00Z', updatedUtc: '2026-10-16T10:00:01Z', executionId: 'execution-a' };
type Reply = { body: unknown; status?: number; etag?: string };
type Responder = (init?: RequestInit) => Reply | Promise<Reply>;
const read = '/api/projects/book1/tts-corpus';
const edit = `${read}/corpus1/clips/clip1`;
function serve(overrides: Record<string, Responder> = {}) {
  const calls: { path: string; init?: RequestInit }[] = [];
  const routes: Record<string, Responder> = {
    'GET /api/bootstrap': () => ({ body: { version: '1', antiforgeryRequestToken: 'csrf', capabilities: { generation: true, training: true } } }),
    'GET /api/training/readiness': () => ({ body: { ready: true, experimental: true, diagnosticCode: null, cpuThreads: 2 } }),
    'GET /api/projects/book1/training': () => ({ body: null }),
    'GET /api/voxcpm-training/readiness': () => ({ body: { ready: false, diagnosticCode: 'voxcpm_training.runtime.not_ready', provider: 'voxcpm2-lora-cpu-v1', cpuThreads: 2 } }),
    'GET /api/projects/book1/voxcpm-training': () => ({ body: null }),
    'GET /api/tts-corpus/readiness': () => ({ body: { ready: true, diagnosticCode: null, provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }),
    [`GET ${read}`]: () => ({ body: corpus, etag: '"3"' }), ...overrides,
  };
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    calls.push({ path, init });
    const route = routes[`${init?.method ?? 'GET'} ${path}`];
    if (!route) throw new Error(`Unexpected request ${init?.method} ${path}`);
    const reply = await route(init);
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? 200, headers: { 'Content-Type': 'application/json', ...(reply.etag ? { ETag: reply.etag } : {}) } });
  });
  return calls;
}
function open(busy = false, snapshot = project) {
  return render(<ProjectWorkspacePage current={{ snapshot, etag: '"2"' }} generation={{ jobs: [], readiness: { ready: true, experimental: true, diagnosticCode: null, profileId: 'q8-f16-cpu' }, pending: false, error: '', connected: true, enqueue: async () => {}, control: async () => {} }} segmentRefresh={{}} busy={busy} headingRef={null} mutate={async () => {}} onBack={() => {}} />);
}
const panel = () => screen.getByRole('region', { name: 'Корпус речи для VoxCPM2' });
const writes = (calls: ReturnType<typeof serve>) => calls.filter(call => ['POST', 'PUT'].includes(call.init?.method ?? 'GET'));
afterEach(() => vi.useRealTimers());

describe('Russian corpus review', () => {
  it('offers an explicit fresh preparation for a partial nonresumable draft while keeping approved versions', async () => {
    const versions = [{ id: 'v1', sha256: 'immutable-sha', clipCount: 1, retainedDurationSeconds: 10, createdUtc: corpus.createdUtc }];
    const calls = serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'interrupted', totalClipCount: 3, canResume: false, versions }, etag: '"3"' }),
      [`POST ${read}`]: () => ({ body: { ...corpus, id: 'corpus2', revision: 1, state: 'queued', clips: [], completedClipCount: 0, versions }, etag: '"1"' }) });
    open(); const user = userEvent.setup();
    const start = await screen.findByRole('button', { name: 'Начать подготовку заново' });
    expect(start).toBeDisabled(); expect(panel()).toHaveTextContent(/неподтверждённый черновик будет заменён/i);
    expect(panel()).toHaveTextContent(/подтверждённые версии сохранятся/i);
    await user.click(screen.getByRole('checkbox', { name: /экспериментальную подготовку/ })); await user.click(start);
    await within(panel()).findByText('В очереди'); expect(panel()).toHaveTextContent('immutable-sha');
    expect(writes(calls)).toHaveLength(1); expect(writes(calls)[0].path).toBe(read);
    expect(new Headers(writes(calls)[0].init?.headers).get('If-Match')).toBe('"2"');
    expect(JSON.parse(writes(calls)[0].init!.body as string)).toEqual({ experimentalConfirmed: true });
  });
  it('uses a new start key for explicit replacement after a lost initial start is discovered by polling', async () => {
    let installed = false; let attempts = 0;
    const calls = serve({ [`GET ${read}`]: () => ({ body: installed ? { ...corpus, state: 'failed', canResume: false, totalClipCount: 3 } : null, ...(installed ? { etag: '"3"' } : {}) }),
      [`POST ${read}`]: () => { if (++attempts === 1) throw new Error('lost initial start response'); return { body: { ...corpus, id: 'corpus2', state: 'queued', revision: 1 }, etag: '"1"' }; } });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    fireEvent.click(screen.getByRole('checkbox', { name: /экспериментальную подготовку/ }));
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' })); });
    expect(writes(calls)).toHaveLength(1); installed = true;
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Начать подготовку заново' })); });
    const sent = writes(calls); expect(sent).toHaveLength(2);
    expect(new Headers(sent[0].init?.headers).get('Idempotency-Key')).not.toBe(new Headers(sent[1].init?.headers).get('Idempotency-Key'));
    expect(sent.map(call => new Headers(call.init?.headers).get('If-Match'))).toEqual(['"2"', '"2"']); view.unmount();
  });
  it.each(['completed', 'transcribing'])('installs and keeps polling %s corpus while readiness is pending without duplicate readiness checks', async state => {
    let resolve!: (reply: Reply) => void; let revision = 3;
    const readiness = new Promise<Reply>(done => { resolve = done; });
    const calls = serve({ 'GET /api/tts-corpus/readiness': () => readiness,
      [`GET ${read}`]: () => ({ body: { ...corpus, state, revision, retainedDurationSeconds: revision }, etag: `"${revision}"` }) });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    expect(screen.getByLabelText('Текст клипа 1')).toBeInTheDocument();
    if (state === 'completed') {
      expect(screen.getByLabelText('Текст клипа 1')).toBeEnabled();
      fireEvent.change(screen.getByLabelText('Текст клипа 1'), { target: { value: 'Правка.' } });
      expect(screen.getByRole('button', { name: 'Сохранить клип 1' })).toBeEnabled();
    }
    revision = 4;
    await act(async () => { await vi.advanceTimersByTimeAsync(state === 'transcribing' ? 2000 : 15000); });
    expect(panel()).toHaveTextContent('Сохранённая длительность: 4 с');
    expect(calls.filter(call => call.path === read)).toHaveLength(2);
    expect(calls.filter(call => call.path === '/api/tts-corpus/readiness')).toHaveLength(1);
    view.unmount(); expect(calls.find(call => call.path === '/api/tts-corpus/readiness')?.init?.signal?.aborted).toBe(true);
    const count = calls.length;
    await act(async () => { resolve({ body: { ready: true, diagnosticCode: null, provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }); await vi.advanceTimersByTimeAsync(30000); });
    expect(calls).toHaveLength(count);
  });
  it('ignores delayed readiness from a replaced project scope and aborts its pending check', async () => {
    let resolve!: (reply: Reply) => void; let checks = 0;
    const delayed = new Promise<Reply>(done => { resolve = done; });
    const calls = serve({ 'GET /api/tts-corpus/readiness': () => ++checks === 1 ? delayed : { body: { ready: true, diagnosticCode: null, provider: 'whisper-large-v3-turbo', cpuThreads: 2 } },
      'GET /api/projects/book2/tts-corpus': () => ({ body: { ...corpus, projectId: 'book2', id: 'other-corpus' }, etag: '"3"' }) });
    const first = { snapshot: project, etag: '"2"' } as SnapshotResponse;
    const view = renderHook(({ current }) => useTtsCorpus(current), { initialProps: { current: first } });
    await act(async () => {}); expect(view.result.current.corpus?.snapshot.id).toBe('corpus1');
    view.rerender({ current: { ...first, snapshot: { ...first.snapshot, id: 'book2' } } });
    await act(async () => {});
    expect(view.result.current.corpus?.snapshot.id).toBe('other-corpus'); expect(view.result.current.readiness?.ready).toBe(true);
    expect(calls.find(call => call.path === '/api/tts-corpus/readiness')?.init?.signal?.aborted).toBe(true);
    await act(async () => { resolve({ body: { ready: false, diagnosticCode: 'tts_corpus.runtime.not_ready', provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }); });
    expect(view.result.current.readiness?.ready).toBe(true); view.unmount();
  });
  it('keeps fresh preparation disabled while another project operation is busy', async () => {
    serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'interrupted', canResume: false }, etag: '"3"' }) }); open(true);
    expect(await screen.findByRole('button', { name: 'Начать подготовку заново' })).toBeDisabled();
    expect(screen.getByRole('checkbox', { name: /экспериментальную подготовку/ })).toBeDisabled();
  });
  it('clears a transient corpus read error after a successful poll', async () => {
    let fail = true;
    serve({ [`GET ${read}`]: () => { if (fail) throw new Error('read unavailable'); return { body: corpus, etag: '"3"' }; } });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    expect(screen.getByRole('alert')).toHaveTextContent(/Нет связи/);
    fail = false; await act(async () => { await vi.advanceTimersByTimeAsync(15000); });
    expect(screen.getByLabelText('Текст клипа 1')).toBeInTheDocument(); expect(screen.queryByRole('alert')).not.toBeInTheDocument(); view.unmount();
  });
  it.each(['network', 'tts_corpus.idempotency.expired', 'tts_corpus.conflict'])('preserves unresolved mutation %s feedback through a successful poll', async failure => {
    const calls = serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'transcribing' }, etag: '"3"' }),
      [`POST ${read}/corpus1/stop`]: () => { if (failure === 'network') throw new Error('response lost'); return { status: 409, body: { code: failure } }; } });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Остановить подготовку' })); });
    const message = screen.getByRole('alert').textContent;
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(screen.getByRole('alert').textContent).toBe(message); expect(writes(calls)).toHaveLength(1); view.unmount();
  });
  it('requires experimental consent and sends project CAS, CSRF and an idempotency key', async () => {
    const calls = serve({ [`GET ${read}`]: () => ({ body: null }), [`POST ${read}`]: () => ({ body: { ...corpus, state: 'queued', revision: 1, clips: [] }, status: 202, etag: '"1"' }) });
    open(); const user = userEvent.setup();
    const start = await screen.findByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' });
    expect(start).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: /экспериментальную подготовку/ })); await user.click(start);
    expect(await within(panel()).findByText('В очереди')).toBeInTheDocument();
    const call = writes(calls)[0]; expect(call.path).toBe(read);
    expect(JSON.parse(call.init!.body as string)).toEqual({ experimentalConfirmed: true });
    expect(new Headers(call.init?.headers).get('If-Match')).toBe('"2"');
    expect(new Headers(call.init?.headers).get('X-CSRF-TOKEN')).toBe('csrf');
    expect(new Headers(call.init?.headers).get('Idempotency-Key')).toBeTruthy();
    expect(panel()).toHaveTextContent(/ещё не обученная голосовая модель/);
  });
  it('explains an unavailable runtime and needs an accepted recording', async () => {
    serve({ [`GET ${read}`]: () => ({ body: null }), 'GET /api/tts-corpus/readiness': () => ({ body: { ready: false, diagnosticCode: 'tts_corpus.runtime.not_ready', provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }) });
    open(false, { ...project, voiceRecording: null } as unknown as typeof project);
    expect(await screen.findByText(/tts_corpus.runtime.not_ready/)).toBeInTheDocument();
    expect(within(panel()).getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' })).toBeDisabled();
    expect(panel()).toHaveTextContent(/Сначала сохраните исходную запись/);
  });
  it('saves edits as unreviewed before a separate explicit review, preserving immutable versions', async () => {
    const version = { id: 'version1', sha256: 'immutable-sha', clipCount: 1, retainedDurationSeconds: 10, createdUtc: corpus.createdUtc };
    let saved = { ...corpus, versions: [version] };
    const calls = serve({ [`GET ${read}`]: () => ({ body: saved, etag: `"${saved.revision}"` }),
      [`PUT ${edit}`]: init => { saved = { ...saved, revision: saved.revision + 1, clips: [{ ...clip, ...JSON.parse(init!.body as string) }] }; return { body: saved, etag: `"${saved.revision}"` }; } });
    open(); const user = userEvent.setup(); const text = await screen.findByLabelText('Текст клипа 1');
    await user.clear(text); await user.type(text, 'Исправленный текст.');
    expect(screen.getByRole('button', { name: 'Подтвердить клип 1' })).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: /Подтверждаю дословный текст и границы/ }));
    expect(screen.getByRole('button', { name: 'Подтвердить версию корпуса' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Сохранить клип 1' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Подтвердить клип 1' })).toBeEnabled());
    expect(JSON.parse(writes(calls)[0].init!.body as string)).toEqual({ text: 'Исправленный текст.', excluded: false, reviewed: false });
    expect(panel()).toHaveTextContent('immutable-sha');
    await user.click(screen.getByRole('button', { name: 'Подтвердить клип 1' }));
    await waitFor(() => expect(writes(calls)).toHaveLength(2));
    expect(JSON.parse(writes(calls)[1].init!.body as string)).toEqual({ text: 'Исправленный текст.', excluded: false, reviewed: true });
    expect(writes(calls).map(call => new Headers(call.init?.headers).get('If-Match'))).toEqual(['"3"', '"4"']);
    expect(within(panel()).getByLabelText('Аудио клипа 1')).toHaveAttribute('src', '/api/projects/book1/tts-corpus/corpus1/clips/clip1/audio');
    expect(panel()).toHaveTextContent(/Конец последнего слова неизвестен/);
    expect(panel()).toHaveTextContent(/приблизительные/);
  });
  it('saves exclusion without review and displays only server retained duration', async () => {
    const calls = serve({ [`PUT ${edit}`]: () => ({ body: { ...corpus, revision: 4, retainedDurationSeconds: 0, clips: [{ ...clip, excluded: true, reviewed: false }] }, etag: '"4"' }) });
    open(); const user = userEvent.setup(); await user.click(await screen.findByRole('checkbox', { name: 'Исключить клип 1' }));
    expect(panel()).toHaveTextContent('Сохранённая длительность: 10 с');
    expect(screen.getByRole('button', { name: 'Подтвердить клип 1' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Сохранить клип 1' }));
    await waitFor(() => expect(panel()).toHaveTextContent('Сохранённая длительность: 0 с'));
    expect(JSON.parse(writes(calls)[0].init!.body as string)).toEqual({ text: 'Точный текст.', excluded: true, reviewed: false });
    await user.click(screen.getByRole('checkbox', { name: /Подтверждаю дословный текст и границы/ }));
    expect(screen.getByRole('button', { name: 'Подтвердить версию корпуса' })).toBeDisabled();
  });
  it('requires final transcript and boundary confirmation for approval with corpus CAS', async () => {
    const calls = serve({ [`POST ${read}/corpus1/approve`]: () => ({ body: { ...corpus, revision: 4, versions: [{ id: 'v1', sha256: 'approved-sha', clipCount: 1, retainedDurationSeconds: 10, createdUtc: corpus.createdUtc }] }, etag: '"4"' }) });
    open(); const user = userEvent.setup(); const approve = await screen.findByRole('button', { name: 'Подтвердить версию корпуса' }); expect(approve).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: /Подтверждаю дословный текст и границы/ })); await user.click(approve);
    expect(await screen.findByText('approved-sha')).toBeInTheDocument();
    const call = writes(calls)[0]; expect(new Headers(call.init?.headers).get('If-Match')).toBe('"3"');
    expect(new Headers(call.init?.headers).get('X-CSRF-TOKEN')).toBe('csrf');
    expect(new Headers(call.init?.headers).get('Idempotency-Key')).toBeTruthy();
    expect(JSON.parse(call.init!.body as string)).toEqual({ transcriptAndBoundariesConfirmed: true });
    expect(screen.queryByRole('option', { name: /approved-sha/ })).not.toBeInTheDocument();
  });
  it.each([
    { name: 'empty', clips: [] }, { name: 'empty text', clips: [{ ...clip, text: '' }] },
    { name: 'unreviewed', clips: [{ ...clip, reviewed: false }] }, { name: 'too long', clips: [{ ...clip, text: 'я'.repeat(4001) }] },
  ])('keeps $name drafts editable and cannot approve them', async ({ clips }) => {
    serve({ [`GET ${read}`]: () => ({ body: { ...corpus, clips }, etag: '"3"' }) }); open(); const user = userEvent.setup();
    await user.click(await screen.findByRole('checkbox', { name: /Подтверждаю дословный текст и границы/ }));
    expect(screen.getByRole('button', { name: 'Подтвердить версию корпуса' })).toBeDisabled();
    if (clips.length) expect(screen.getByLabelText('Текст клипа 1')).toBeEnabled();
    expect(panel()).toHaveTextContent(/проверьте|недостаточно/i);
  });
  it('refreshes typed 412 without replay or private diagnostics', async () => {
    const active = { ...corpus, state: 'transcribing', phase: 'transcribing', completedClipCount: 1, totalClipCount: 3 };
    const calls = serve({ [`GET ${read}`]: () => ({ body: active, etag: '"3"' }),
      [`POST ${read}/corpus1/stop`]: () => ({ status: 412, etag: '"9"', body: { code: 'tts_corpus.conflict', conflictType: 'tts_corpus', currentSnapshot: { ...corpus, state: 'paused', revision: 9, canResume: true }, detail: 'C:/private/audio' } }) });
    open(); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Остановить подготовку' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/из другой вкладки/);
    expect(within(panel()).getByText('Остановлено')).toBeInTheDocument(); expect(writes(calls)).toHaveLength(1);
    expect(panel()).not.toHaveTextContent('private');
  });
  it('requires user retry after network uncertainty and preserves the original request', async () => {
    let attempts = 0;
    const calls = serve({ [`GET ${read}`]: () => ({ body: null }), [`POST ${read}`]: () => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...corpus, state: 'queued', revision: 1 }, etag: '"1"' }; } });
    open(); const user = userEvent.setup(); await user.click(await screen.findByRole('checkbox', { name: /экспериментальную подготовку/ }));
    await user.click(screen.getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Нет связи/); expect(writes(calls)).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' }));
    await within(panel()).findByText('В очереди'); const sent = writes(calls);
    expect(sent.map(call => new Headers(call.init?.headers).get('If-Match'))).toEqual(['"2"', '"2"']);
    expect(new Headers(sent[0].init?.headers).get('Idempotency-Key')).toBe(new Headers(sent[1].init?.headers).get('Idempotency-Key'));
    expect(sent[0].init?.body).toBe(sent[1].init?.body);
  });
  it('uses a new key when a network-uncertain clip edit changes', async () => {
    let attempts = 0;
    const calls = serve({ [`PUT ${edit}`]: init => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...corpus, revision: 4, clips: [{ ...clip, ...JSON.parse(init!.body as string) }] }, etag: '"4"' }; } });
    open(); const user = userEvent.setup(); const text = await screen.findByLabelText('Текст клипа 1');
    await user.clear(text); await user.type(text, 'Первая правка.'); await user.click(screen.getByRole('button', { name: 'Сохранить клип 1' }));
    await screen.findByRole('alert'); await user.clear(text); await user.type(text, 'Новая правка.'); await user.click(screen.getByRole('button', { name: 'Сохранить клип 1' }));
    await waitFor(() => expect(writes(calls)).toHaveLength(2));
    expect(new Headers(writes(calls)[0].init?.headers).get('Idempotency-Key')).not.toBe(new Headers(writes(calls)[1].init?.headers).get('Idempotency-Key'));
  });
  it('polls idle at 15s without replacing dirty text or accepting stale/foreign snapshots', async () => {
    let next = corpus;
    const calls = serve({ [`GET ${read}`]: () => ({ body: next, etag: `"${next.revision}"` }) }); vi.useFakeTimers(); const view = open();
    await act(async () => {});
    const text = screen.getByLabelText('Текст клипа 1'); fireEvent.change(text, { target: { value: 'Несохранённая правка.' } });
    next = { ...corpus, revision: 9, retainedDurationSeconds: 12, clips: [{ ...clip, text: 'Чужая правка.' }] };
    await act(async () => { await vi.advanceTimersByTimeAsync(14999); }); expect(calls.filter(call => call.path === read)).toHaveLength(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); }); expect(text).toHaveValue('Несохранённая правка.'); expect(panel()).toHaveTextContent('Сохранённая длительность: 12 с');
    next = { ...corpus, revision: 4, retainedDurationSeconds: 4 }; await act(async () => { await vi.advanceTimersByTimeAsync(15000); }); expect(panel()).toHaveTextContent('Сохранённая длительность: 12 с');
    next = { ...corpus, projectId: 'other', revision: 50, retainedDurationSeconds: 50 }; await act(async () => { await vi.advanceTimersByTimeAsync(15000); }); expect(panel()).toHaveTextContent('Сохранённая длительность: 12 с');
    view.unmount(); expect(calls.filter(call => call.path === read).at(-1)?.init?.signal?.aborted).toBe(true);
    const count = calls.length; await act(async () => { await vi.advanceTimersByTimeAsync(30000); }); expect(calls).toHaveLength(count);
  });
  it('polls active at 2s, displays real clip counts, blocks other work and cancels on unmount', async () => {
    let count = 1;
    const calls = serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'transcribing', phase: 'transcribing', revision: count + 2, completedClipCount: count, totalClipCount: 3 }, etag: `"${count + 2}"` }) });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    expect(panel()).toHaveTextContent('Готовые клипы: 1 из 3');
    expect(screen.getByLabelText('Произносимый текст 1')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Обучить голос' })).toBeDisabled();
    expect(screen.getByLabelText('Текст клипа 1')).toBeDisabled();
    count = 2; await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    expect(panel()).toHaveTextContent('Готовые клипы: 2 из 3');
    view.unmount(); const reads = calls.filter(call => call.path === read); expect(reads).toHaveLength(2); expect(reads[1].init?.signal?.aborted).toBe(true);
    const total = calls.length; await act(async () => { await vi.advanceTimersByTimeAsync(30000); }); expect(calls).toHaveLength(total);
  });
  it('blocks other work while a command is pending and clears blocking after failure', async () => {
    let reject!: (reason: unknown) => void;
    const pending = new Promise<Reply>((_, fail) => { reject = fail; });
    serve({ [`GET ${read}`]: () => ({ body: null }), [`POST ${read}`]: () => pending }); open(); const user = userEvent.setup();
    await user.click(await screen.findByRole('checkbox', { name: /экспериментальную подготовку/ }));
    await user.click(screen.getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' }));
    await waitFor(() => expect(screen.getByLabelText('Произносимый текст 1')).toBeDisabled());
    await act(async () => { reject(new Error('Lost response')); });
    await waitFor(() => expect(screen.getByLabelText('Произносимый текст 1')).toBeEnabled());
  });
  it('resumes only server-authorized matching source and uses corpus CAS', async () => {
    const calls = serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'paused', canResume: true }, etag: '"3"' }),
      [`POST ${read}/corpus1/resume`]: () => ({ body: { ...corpus, state: 'queued', revision: 4, executionId: 'execution-b' }, etag: '"4"' }) });
    open(); const user = userEvent.setup(); const resume = await screen.findByRole('button', { name: 'Продолжить подготовку' }); expect(resume).toBeDisabled();
    await user.click(screen.getByRole('checkbox', { name: /экспериментальную подготовку/ })); await user.click(resume);
    await within(panel()).findByText('В очереди'); expect(new Headers(writes(calls)[0].init?.headers).get('If-Match')).toBe('"3"');
  });
  it('keeps explicit final confirmation through unchanged idle polling', async () => {
    serve(); vi.useFakeTimers(); const view = open(); await act(async () => {});
    fireEvent.click(screen.getByRole('checkbox', { name: /Подтверждаю дословный текст и границы/ }));
    expect(screen.getByRole('button', { name: 'Подтвердить версию корпуса' })).toBeEnabled();
    await act(async () => { await vi.advanceTimersByTimeAsync(15000); });
    expect(screen.getByRole('checkbox', { name: /Подтверждаю дословный текст и границы/ })).toBeChecked();
    expect(screen.getByRole('button', { name: 'Подтвердить версию корпуса' })).toBeEnabled(); view.unmount();
  });
  it('refreshes a snapshot-less 412 and never automatically replays STOP', async () => {
    let conflicted = false;
    const calls = serve({ [`GET ${read}`]: () => ({ body: conflicted ? { ...corpus, state: 'interrupted', revision: 9 } : { ...corpus, state: 'transcribing' }, etag: conflicted ? '"9"' : '"3"' }),
      [`POST ${read}/corpus1/stop`]: () => { conflicted = true; return { status: 412, body: { code: 'tts_corpus.conflict', detail: 'C:/private' } }; } });
    open(); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Остановить подготовку' }));
    expect(await within(panel()).findByText('Прервано при закрытии студии')).toBeInTheDocument(); expect(writes(calls)).toHaveLength(1);
    expect(calls.filter(call => call.path === read)).toHaveLength(2); expect(panel()).not.toHaveTextContent('private');
  });
  it('does not reuse a network-uncertain STOP request for a replacement execution', async () => {
    let execution = 'execution-a'; let attempts = 0;
    const calls = serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'transcribing', executionId: execution, revision: execution === 'execution-a' ? 3 : 9 }, etag: execution === 'execution-a' ? '"3"' : '"9"' }),
      [`POST ${read}/corpus1/stop`]: () => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...corpus, revision: 10, state: 'stop_requested', executionId: execution }, etag: '"10"' }; } });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Остановить подготовку' })); });
    expect(writes(calls)).toHaveLength(1); execution = 'execution-b';
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); });
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Остановить подготовку' })); });
    const sent = writes(calls); expect(sent.map(call => new Headers(call.init?.headers).get('If-Match'))).toEqual(['"3"', '"9"']);
    expect(new Headers(sent[0].init?.headers).get('Idempotency-Key')).not.toBe(new Headers(sent[1].init?.headers).get('Idempotency-Key')); view.unmount();
  });
  it('reuses the captured ETag and key for an unchanged network-uncertain STOP despite polling progress', async () => {
    let revision = 3; let attempts = 0;
    const calls = serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'transcribing', revision }, etag: `"${revision}"` }),
      [`POST ${read}/corpus1/stop`]: () => { if (++attempts === 1) throw new Error('Lost response'); return { body: { ...corpus, revision: 10, state: 'stop_requested' }, etag: '"10"' }; } });
    vi.useFakeTimers(); const view = open(); await act(async () => {});
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Остановить подготовку' })); }); revision = 9;
    await act(async () => { await vi.advanceTimersByTimeAsync(2000); }); expect(writes(calls)).toHaveLength(1);
    await act(async () => { fireEvent.click(screen.getByRole('button', { name: 'Остановить подготовку' })); });
    const sent = writes(calls); expect(sent.map(call => new Headers(call.init?.headers).get('If-Match'))).toEqual(['"3"', '"3"']);
    expect(new Headers(sent[0].init?.headers).get('Idempotency-Key')).toBe(new Headers(sent[1].init?.headers).get('Idempotency-Key')); view.unmount();
  });
  it('disables corpus editing and resume while other project work is busy', async () => {
    serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'paused', canResume: true }, etag: '"3"' }) }); open(true);
    expect(await screen.findByLabelText('Текст клипа 1')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Продолжить подготовку' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Подтвердить версию корпуса' })).toBeDisabled();
  });
  it('never offers resume or approval for a different source recording', async () => {
    serve({ [`GET ${read}`]: () => ({ body: { ...corpus, state: 'paused', canResume: true, sourceRecordingId: 'other-recording' }, etag: '"3"' }) }); open();
    expect(await screen.findByLabelText('Текст клипа 1')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Продолжить подготовку' })).toBeDisabled();
    expect(panel()).toHaveTextContent(/другой исходной записи/);
  });
  it('keeps an expired operation key instead of silently converting it into new work', async () => {
    const calls = serve({ [`GET ${read}`]: () => ({ body: null }), [`POST ${read}`]: () => ({ status: 409, body: { code: 'tts_corpus.idempotency.expired' } }) });
    open(); const user = userEvent.setup(); await user.click(await screen.findByRole('checkbox', { name: /экспериментальную подготовку/ }));
    await user.click(screen.getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/старый запрос/); expect(writes(calls)).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' }));
    const sent = writes(calls); expect(sent).toHaveLength(2);
    expect(new Headers(sent[0].init?.headers).get('Idempotency-Key')).toBe(new Headers(sent[1].init?.headers).get('Idempotency-Key'));
  });
});
