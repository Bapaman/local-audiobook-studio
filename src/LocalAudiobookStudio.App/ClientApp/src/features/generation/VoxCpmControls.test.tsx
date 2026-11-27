import { act, render, renderHook, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { StudioClient } from '../../api/client';
import type { JobSnapshot, SnapshotResponse, VoxCpmTrainingSnapshot } from '../../api/types';
import { GenerationPanel } from './GenerationPanel';
import { JobProgress } from './JobProgress';
import { useProjectEvents } from './useProjectEvents';
import { TestEventSource } from '../../test-events';

const model = 'a'.repeat(32), otherModel = 'b'.repeat(32), clip = 'c'.repeat(32), otherClip = 'd'.repeat(32);
const defaults = { seed: 42, timesteps: 10, cfg: 2, tempoPercent: 100, useReference: true, referenceClipId: null };
const current: SnapshotResponse = { etag: '"2"', snapshot: { id: 'book1', displayName: 'Книга', revision: 2,
  voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 1, durationMilliseconds: 392034, hasClippingWarning: false },
  voiceAnchor: { artifactId: 'anchor1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Образец.' },
  textDocument: { sourceKind: 'Paste', displayName: 'Текст', encoding: 'utf-8', sha256: 'hash', byteCount: 1, originalText: 'Фраза.', normalizedText: 'Фраза.' },
  segments: [{ id: 's1', order: 0, originalText: 'Фраза.', normalizedText: 'Фраза.', spokenText: 'Фраза.', contentHash: 'hash', status: 'pending' }] } };
const vox: VoxCpmTrainingSnapshot = { id: 'train1', projectId: 'book1', revision: 2, state: 'paused', phase: 'paused',
  corpusVersionId: 'version1', corpusManifestSha256: 'hash', corpusClipCount: 2, retainedDurationSeconds: 24, globalStep: 29, completedEpochs: 1,
  nextRetainedOrdinal: 1, nextClipId: clip, targetCompletedEpochs: 2, executionId: 'execution1', canResume: true, qualityEstablished: false,
  diagnosticCode: null, createdUtc: '', updatedUtc: '', models: [model, otherModel].map(id => ({ id, trainingId: 'train1', corpusVersionId: 'version1', manifestSha256: 'hash', globalStep: 29, completedEpochs: 1, createdUtc: '' })) };
const runtime = { ready: true, experimental: true, diagnosticCode: null, profileId: 'voxcpm2-saved-adapter-cpu-v1' };
const catalog = (id = model, idClip = clip) => ({ modelId: id, corpusVersionId: 'version1', defaultClipId: idClip, clips: [{ id: idClip, trainingOrdinal: 0, seconds: 12, text: 'Подтверждённый текст.' }] });
const generation = () => ({ jobs: [] as JobSnapshot[], readiness: { ...runtime, profileId: 'q8-f16-cpu' }, voxReadiness: runtime, pending: false, error: '', connected: true, enqueue: vi.fn().mockResolvedValue(undefined), control: vi.fn().mockResolvedValue(undefined) });
const successfulJob = (change: Partial<JobSnapshot> = {}): JobSnapshot => ({ id: 'accepted1', projectId: 'book1', kind: 'preview', state: 'completed', requestedState: 'run', phase: 'completed',
  completedUnits: 1, totalUnits: 1, revision: 4, createdUtc: '2026-10-29T00:00:00Z', updatedUtc: '2026-10-29T00:01:00Z', completedUtc: '2026-10-29T00:01:00Z', diagnosticCode: null,
  inputHash: 'hash', effectiveProfileId: 'voxcpm2-saved-adapter-controls-cpu-v1',
  voxCpmGeneration: { modelId: model, globalStep: 29, options: { ...defaults, seed: 321, cfg: 2.4, referenceClipId: clip }, referenceClipId: clip, referenceTrainingOrdinal: 0, referenceSeconds: 12 },
  audioResults: [{ segmentId: 's1', order: 0, artifactId: 'wave1', seconds: 15, spokenText: 'Фраза.' }], ...change });
function serveReferences() {
  vi.stubGlobal('fetch', vi.fn(async (path: string) => new Response(JSON.stringify(catalog(path.includes(otherModel) ? otherModel : model, path.includes(otherModel) ? otherClip : clip)))));
}
async function select(user = userEvent.setup()) {
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), `voxcpm:${model}`);
  await user.click(screen.getByText('Расширенные настройки'));
  await screen.findByRole('option', { name: /Подтверждённый текст/ });
  return user;
}
beforeEach(() => { localStorage.clear(); serveReferences(); });
afterEach(() => { localStorage.clear(); });

it.each([
  ['worker.memory.insufficient', 'Для озвучивания нужно не менее 16 ГиБ свободной оперативной памяти. Освободите память перед новой пробой.'],
  ['worker.memory.reserve', 'Озвучивание остановлено, чтобы оставить память системе. Готовые результаты сохранены.'],
  ['worker.memory.policy', 'Не удалось подтвердить ограничения памяти и процессора. Озвучивание остановлено.'],
])('shows the actionable synthesis resource error %s in job history', (diagnosticCode, message) => {
  const job: JobSnapshot = { id: 'failed1', projectId: 'book1', kind: 'preview', state: 'failed', requestedState: 'run', phase: 'failed',
    completedUnits: 0, totalUnits: 1, revision: 3, createdUtc: '', updatedUtc: '', completedUtc: '', diagnosticCode,
    inputHash: 'hash', effectiveProfileId: 'voxcpm2-saved-adapter-controls-cpu-v1' };
  render(<JobProgress job={job} pending={false} control={vi.fn()} />);
  expect(screen.getByText(message)).toBeVisible();
});

it('shows defaults, approved audio, edits engine/export controls and sends captured options', async () => {
  const state = generation(); render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  const user = await select();
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(42);
  expect(screen.getByLabelText('Шаги синтеза')).toHaveValue(10);
  expect(screen.getByLabelText('CFG')).toHaveValue(2);
  expect(screen.getByLabelText('Темп экспортируемого аудио, %')).toHaveValue(100);
  expect(screen.getByLabelText('Аудио опорного клипа')).toHaveAttribute('src', `/api/projects/book1/voxcpm-generation/models/${model}/references/${clip}/audio`);
  for (const [label, value] of [['Seed — вариация', '123'], ['Шаги синтеза', '12'], ['CFG', '2.5'], ['Темп экспортируемого аудио, %', '90']]) {
    await user.clear(screen.getByLabelText(label)); await user.type(screen.getByLabelText(label), value);
  }
  await user.selectOptions(screen.getByLabelText('Опорный клип'), clip);
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение/));
  await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
  expect(state.enqueue).toHaveBeenCalledWith('preview', ['s1'], '"2"', { savedVoxCpmModelId: model, experimentalConfirmed: true,
    voxCpmOptions: { seed: 123, timesteps: 12, cfg: 2.5, tempoPercent: 90, useReference: true, referenceClipId: clip } });
  expect(screen.getByText(/приблизительно сохраняет высоту голоса/)).toHaveTextContent(/экспортируемый WAV/);
});

it('resets defaults, creates crypto seed, persists valid project drafts but never consent', async () => {
  vi.spyOn(crypto, 'getRandomValues').mockImplementation(array => { (array as Uint32Array)[0] = 123456; return array; });
  const state = generation(); const view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  const user = await select(); await user.click(screen.getByRole('button', { name: 'Новая вариация' }));
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(123456);
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение/));
  view.unmount(); render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(123456);
  expect(screen.getByLabelText(/Подтверждаю экспериментальное применение/)).not.toBeChecked();
  await user.click(screen.getByText('Расширенные настройки'));
  await user.click(screen.getByRole('button', { name: 'Сбросить настройки' }));
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(42);
  await user.clear(screen.getByLabelText('Шаги синтеза')); await user.type(screen.getByLabelText('Шаги синтеза'), '21');
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение/));
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
});

it('ignores invalid stored settings and keeps drafts isolated across projects', async () => {
  localStorage.setItem('voxcpm-generation-draft:book1', JSON.stringify({ modelId: `voxcpm:${model}`, options: { ...defaults, cfg: 999 } }));
  const state = generation(); const view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('');
  const user = await select(); await user.clear(screen.getByLabelText('Seed — вариация')); await user.type(screen.getByLabelText('Seed — вариация'), '77');
  view.rerender(<GenerationPanel current={{ ...current, snapshot: { ...current.snapshot, id: 'book2' } }} generation={state} busy={false} voxTraining={{ ...vox, projectId: 'book2' }} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('');
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), `voxcpm:${model}`);
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(42);
});

it('rejects a malformed stored model and admits only the six documented draft controls', async () => {
  localStorage.setItem('voxcpm-generation-draft:book1', JSON.stringify({ modelId: 'voxcpm:malformed', options: defaults }));
  const state = generation(); let view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(''); view.unmount();
  localStorage.setItem('voxcpm-generation-draft:book1', JSON.stringify({ modelId: `voxcpm:${model}`, options: { ...defaults, unrecognizedControl: 9 } }));
  view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await screen.findByRole('option', { name: /Подтверждённый текст/ });
  const user = userEvent.setup(); await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение/)); await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
  expect(state.enqueue).toHaveBeenCalledWith('preview', ['s1'], '"2"', { savedVoxCpmModelId: model, experimentalConfirmed: true, voxCpmOptions: defaults });
});

it('retains a saved selection while model history loads and honors switching back to the short sample on reload', async () => {
  localStorage.setItem('voxcpm-generation-draft:book1', JSON.stringify({ modelId: `voxcpm:${model}`, options: { ...defaults, seed: 88 } }));
  const state = generation(); let view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={null} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(`voxcpm:${model}`);
  view.rerender(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await screen.findByRole('option', { name: /Подтверждённый текст/ }); expect(screen.getByLabelText('Seed — вариация')).toHaveValue(88);
  await userEvent.setup().selectOptions(screen.getByLabelText('Голос для озвучивания'), ''); view.unmount();
  view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('');
});

it('isolates stale reference responses, clears explicit clip on model change and permits reference off', async () => {
  let resolve!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => path.includes(otherModel)
    ? new Response(JSON.stringify(catalog(otherModel, otherClip)))
    : new Promise<Response>(done => { resolve = done; signal = init?.signal as AbortSignal; }));
  const state = generation(); render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  const user = userEvent.setup(); await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), `voxcpm:${model}`);
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), `voxcpm:${otherModel}`);
  await user.click(screen.getByText('Расширенные настройки'));
  await screen.findByRole('option', { name: /Подтверждённый текст/ }); expect(signal?.aborted).toBe(true);
  await act(async () => { resolve(new Response(JSON.stringify(catalog()))); });
  expect(screen.queryByRole('option', { name: clip })).not.toBeInTheDocument();
  expect(screen.getByLabelText('Опорный клип')).toHaveValue('');
  await user.selectOptions(screen.getByLabelText('Опорный клип'), otherClip);
  await user.click(screen.getByLabelText('Использовать опорный клип'));
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение/)); await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
  expect(state.enqueue).toHaveBeenLastCalledWith('preview', ['s1'], '"2"', { savedVoxCpmModelId: otherModel, experimentalConfirmed: true, voxCpmOptions: { ...defaults, useReference: false } });
});

it('blocks missing approved reference without a fallback and freezes settings while retaining cancel', async () => {
  vi.stubGlobal('fetch', async () => new Response(JSON.stringify({ ...catalog(), clips: [], defaultClipId: null })));
  const state = generation(); const view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  const user = userEvent.setup(); await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), `voxcpm:${model}`);
  await screen.findByText(/Подходящий подтверждённый клип недоступен/);
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение/));
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
  expect(state.enqueue).not.toHaveBeenCalled();
  await user.click(screen.getByText('Расширенные настройки'));
  await user.click(screen.getByLabelText('Использовать опорный клип'));
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeEnabled();
  const job: JobSnapshot = { id: 'active1', projectId: 'book1', kind: 'preview', state: 'running', requestedState: 'run', phase: 'synthesizing', completedUnits: 0, totalUnits: 1, revision: 1,
    createdUtc: '', updatedUtc: '', completedUtc: null, diagnosticCode: null, inputHash: 'hash', effectiveProfileId: 'voxcpm2-saved-adapter-controls-cpu-v1' };
  view.rerender(<GenerationPanel current={current} generation={{ ...state, jobs: [job] }} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Seed — вариация')).toBeDisabled(); expect(screen.getByLabelText('Голос для озвучивания')).toBeDisabled();
  expect(screen.getByLabelText('Исходный · 100%')).toBeDisabled();
  await user.click(screen.getByRole('button', { name: 'Отменить задание' })); expect(state.control).toHaveBeenCalledWith(job, 'cancel');
});

it('aborts references on project switch and never installs their late reply into the new project', async () => {
  let resolve!: (value: Response) => void; let signal: AbortSignal | undefined;
  vi.stubGlobal('fetch', async (_path: string, init?: RequestInit) => new Promise<Response>(done => { resolve = done; signal = init?.signal as AbortSignal; }));
  const state = generation(); const view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await userEvent.setup().selectOptions(screen.getByLabelText('Голос для озвучивания'), `voxcpm:${model}`);
  view.rerender(<GenerationPanel current={{ ...current, snapshot: { ...current.snapshot, id: 'book2' } }} generation={state} busy={false} voxTraining={{ ...vox, projectId: 'book2' }} />);
  expect(signal?.aborted).toBe(true); await act(async () => { resolve(new Response(JSON.stringify(catalog()))); });
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('');
  expect(screen.queryByLabelText('Аудио опорного клипа')).not.toBeInTheDocument();
});

it('uses actual inference readiness independently and preserves ready GGUF refresh when it fails', async () => {
  let failed = false;
  vi.stubGlobal('fetch', async (path: string) => {
    if (path === '/api/voxcpm-inference/readiness') { if (failed) throw new Error('offline'); return new Response(JSON.stringify(runtime)); }
    return new Response(JSON.stringify(path.endsWith('/jobs') ? [] : path === '/api/runtime/readiness' ? { ...runtime, profileId: 'q8-f16-cpu' } : { ...current.snapshot, revision: failed ? 3 : 2 }), { headers: { ETag: '"2"' } });
  });
  const install = vi.fn(); const { result } = renderHook(() => useProjectEvents(newClient, 'book1', install));
  await waitFor(() => expect(result.current.voxReadiness?.ready).toBe(true));
  failed = true; await act(async () => { TestEventSource.instances.at(-1)!.emit('job'); });
  await waitFor(() => expect(install).toHaveBeenLastCalledWith(expect.objectContaining({ snapshot: expect.objectContaining({ revision: 3 }) })));
  expect(result.current.readiness?.ready).toBe(true); expect(result.current.voxReadiness?.ready).toBe(false);
});
const newClient = new StudioClient();

it('retains captured history/audio after refresh and reuses captured options/model without consent', async () => {
  const captured = { ...defaults, seed: 99, tempoPercent: 85, referenceClipId: clip };
  const state = generation(); state.jobs = [{ id: 'job1', projectId: 'book1', kind: 'preview', state: 'completed', requestedState: 'run', phase: 'completed', completedUnits: 33, totalUnits: 33, revision: 2,
    createdUtc: '', updatedUtc: '', completedUtc: '', diagnosticCode: null, inputHash: 'hash', effectiveProfileId: 'voxcpm2-saved-adapter-controls-cpu-v1',
    voxCpmGeneration: { modelId: model, globalStep: 29, options: captured, referenceClipId: clip, referenceTrainingOrdinal: 0, referenceSeconds: 12 },
    audioResults: [{ segmentId: 's1', order: 0, artifactId: 'audio1', seconds: 4.2, spokenText: 'Исторический текст.' }] }];
  state.jobs.unshift(successfulJob({ kind: 'generation', createdUtc: '2026-10-10T00:00:00Z' }));
  const view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  const user = userEvent.setup();
  await user.click(screen.getByText('История заданий', { exact: true }));
  const history = await screen.findByRole('article', { name: 'Пробное задание' });
  expect(within(history).getByText(/Seed: 99/)).toHaveTextContent(/85%/);
  expect(within(history).getByLabelText('Аудио результата 1')).toHaveAttribute('src', '/api/projects/book1/artifacts/audio1');
  expect(within(history).getByRole('link', { name: /Скачать WAV/ })).toHaveAttribute('href', '/api/projects/book1/artifacts/audio1');
  expect(within(history).getByText(/первые 32/)).toBeInTheDocument();
  await user.click(within(history).getByRole('button', { name: 'Использовать эти настройки' }));
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(`voxcpm:${model}`);
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(99);
  expect(screen.getByLabelText(/Подтверждаю экспериментальное применение/)).not.toBeChecked();
  view.rerender(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  expect(within(history).getByText(/Seed: 99/)).toBeInTheDocument();
});

it('keeps all six options in uncertain retry identity and wire, changing any option gets a new key', async () => {
  const calls: RequestInit[] = [];
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    if (init?.method === 'POST') { calls.push(init); throw new Error('lost'); }
    return new Response(JSON.stringify(path.endsWith('/bootstrap') ? { version: '1', antiforgeryRequestToken: 'csrf', capabilities: {} }
      : path.endsWith('/readiness') ? { ...runtime, profileId: path.includes('voxcpm') ? runtime.profileId : 'q8-f16-cpu' }
        : path.endsWith('/jobs') ? [] : current.snapshot), { headers: { ETag: '"2"' } });
  });
  const client = new StudioClient(); await client.bootstrap(); const install = vi.fn();
  const { result } = renderHook(() => useProjectEvents(client, 'book1', install)); await waitFor(() => expect(result.current.voxReadiness).toBeDefined());
  const send = async (options: typeof defaults) => { await act(async () => { await result.current.enqueue('preview', ['s1'], '"2"', { savedVoxCpmModelId: model, experimentalConfirmed: true, voxCpmOptions: options }); }); };
  await send(defaults); await send({ ...defaults });
  const first = new Headers(calls[0].headers).get('Idempotency-Key'); expect(new Headers(calls[1].headers).get('Idempotency-Key')).toBe(first);
  for (const options of [{ ...defaults, seed: 43 }, { ...defaults, timesteps: 11 }, { ...defaults, cfg: 2.1 }, { ...defaults, tempoPercent: 99 }, { ...defaults, useReference: false }, { ...defaults, referenceClipId: clip }]) await send(options as typeof defaults);
  expect(new Set(calls.slice(1).map(call => new Headers(call.headers).get('Idempotency-Key'))).size).toBe(7);
  expect(JSON.parse(calls[0].body as string).voxCpmOptions).toEqual(defaults);
});

it('recovers successful server settings after storage loss and delayed model load without consent or generation', async () => {
  const state = generation(); const accepted = successfulJob();
  const jobs = [successfulJob({ id: 'failed2', state: 'failed', createdUtc: '2026-11-01T00:00:00Z' }), accepted];
  const view = render(<GenerationPanel current={current} generation={{ ...state, jobs }} busy={false} voxTraining={null} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('');
  view.rerender(<GenerationPanel current={current} generation={{ ...state, jobs }} busy={false} voxTraining={vox} />);
  await waitFor(() => expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(`voxcpm:${model}`));
  expect(screen.getByText(/Восстановлены настройки последней успешной пробы/)).toBeVisible();
  expect(screen.getByLabelText(/Подтверждаю экспериментальное применение/)).not.toBeChecked();
  expect(state.enqueue).not.toHaveBeenCalled();
  await userEvent.setup().click(screen.getByText('Расширенные настройки'));
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(321);
  expect(screen.getByLabelText('CFG')).toHaveValue(2.4);
  view.unmount(); localStorage.clear();
  render(<GenerationPanel current={current} generation={{ ...state, jobs }} busy={false} voxTraining={vox} />);
  await waitFor(() => expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(`voxcpm:${model}`));
  expect(state.enqueue).not.toHaveBeenCalled();
});

it('keeps explicit draft and short-sample choices ahead of successful history across remounts', async () => {
  localStorage.setItem('voxcpm-generation-draft:book1', JSON.stringify({ modelId: `voxcpm:${otherModel}`, options: { ...defaults, seed: 77 } }));
  const state = { ...generation(), jobs: [successfulJob()] };
  const view = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(`voxcpm:${otherModel}`);
  await userEvent.setup().selectOptions(screen.getByLabelText('Голос для озвучивания'), ''); view.unmount();
  render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await waitFor(() => expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(''));
  expect(state.enqueue).not.toHaveBeenCalled();
});

it('never replaces manual edits with late successful history or a foreign or unavailable voice', async () => {
  const state = generation();
  const invalid = successfulJob({ id: 'foreign', projectId: 'book2' });
  const absent = successfulJob({ id: 'absent', voxCpmGeneration: { ...successfulJob().voxCpmGeneration!, modelId: 'e'.repeat(32) } });
  const malformed = successfulJob({ id: 'bad-options', voxCpmGeneration: { ...successfulJob().voxCpmGeneration!, options: { ...defaults, cfg: 999 } } });
  const view = render(<GenerationPanel current={current} generation={{ ...state, jobs: [invalid, absent, malformed] }} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('');
  const user = userEvent.setup(); await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), `voxcpm:${otherModel}`);
  await user.click(screen.getByText('Расширенные настройки'));
  await user.clear(screen.getByLabelText('Seed — вариация')); await user.type(screen.getByLabelText('Seed — вариация'), '88');
  view.rerender(<GenerationPanel current={current} generation={{ ...state, jobs: [successfulJob()] }} busy={false} voxTraining={vox} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue(`voxcpm:${otherModel}`);
  expect(screen.getByLabelText('Seed — вариация')).toHaveValue(88);
});

it('starts with simple tempo choices and keeps every other captured option while expert controls stay available', async () => {
  const options = { ...defaults, seed: 123, cfg: 2.5, referenceClipId: clip };
  localStorage.setItem('voxcpm-generation-draft:book1', JSON.stringify({ modelId: `voxcpm:${model}`, options }));
  const state = generation(); render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await screen.findByLabelText('Аудио опорного клипа');
  expect(screen.getByLabelText('Seed — вариация')).not.toBeVisible();
  const user = userEvent.setup(); await user.click(screen.getByLabelText('Чуть медленнее · 90%'));
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение/));
  await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
  expect(state.enqueue).toHaveBeenCalledWith('preview', ['s1'], '"2"', { savedVoxCpmModelId: model, experimentalConfirmed: true, voxCpmOptions: { ...options, tempoPercent: 90 } });
  await user.click(screen.getByText('Расширенные настройки'));
  expect(screen.getByRole('spinbutton', { name: 'Seed — вариация' })).toHaveValue(123);
  expect(screen.getByLabelText('Темп экспортируемого аудио, %')).toHaveValue(90);
});

it('retains all 32 terminal jobs alongside active jobs', async () => {
  const jobs = Array.from({ length: 32 }, (_, i) => successfulJob({ id: `saved${i}`, createdUtc: new Date(Date.UTC(2026, 8, i + 1)).toISOString() }));
  const active = successfulJob({ id: 'running', state: 'running', completedUnits: 0, completedUtc: null, createdUtc: '2026-11-01T00:00:00Z', audioResults: null });
  vi.stubGlobal('fetch', async (path: string) => new Response(JSON.stringify(path.endsWith('/readiness') ? { ...runtime, profileId: path.includes('voxcpm') ? runtime.profileId : 'q8-f16-cpu' }
    : path.endsWith('/jobs') ? [active, ...jobs] : current.snapshot), { headers: { ETag: '"2"' } }));
  const client = new StudioClient(), install = vi.fn();
  const { result } = renderHook(() => useProjectEvents(client, 'book1', install));
  await waitFor(() => expect(result.current.voxReadiness).toBeDefined());
  expect(result.current.jobs.filter(job => job.state === 'completed')).toHaveLength(32);
  expect(result.current.jobs).toHaveLength(33);
});
