import { act, render, renderHook, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, expect, it, vi } from 'vitest';
import { StudioClient } from '../../api/client';
import type { JobSnapshot, SnapshotResponse, VoxCpmTrainingSnapshot } from '../../api/types';
import { GenerationPanel } from './GenerationPanel';
import { useProjectEvents } from './useProjectEvents';

const current: SnapshotResponse = { etag: '"2"', snapshot: { id: 'book1', displayName: 'Книга', revision: 2,
  voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 1, durationMilliseconds: 392034, hasClippingWarning: false },
  voiceAnchor: { artifactId: 'anchor1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Пример.' },
  textDocument: { sourceKind: 'Paste', displayName: 'Текст', encoding: 'utf-8', sha256: 'hash', byteCount: 1, originalText: 'Новая фраза.', normalizedText: 'Новая фраза.' },
  segments: [{ id: 's1', order: 0, originalText: 'Новая фраза.', normalizedText: 'Новая фраза.', spokenText: 'Новая фраза.', contentHash: 'hash', status: 'pending' }] } };
const vox: VoxCpmTrainingSnapshot = { id: 'new-line', projectId: 'book1', revision: 155, state: 'paused', phase: 'paused',
  corpusVersionId: 'corpus2', corpusManifestSha256: 'hash', corpusClipCount: 28, retainedDurationSeconds: 392.034,
  globalStep: 29, completedEpochs: 1, nextRetainedOrdinal: 1, nextClipId: 'clip2', targetCompletedEpochs: 2, executionId: 'execution',
  models: [{ id: 'accepted29', trainingId: 'historical-line', corpusVersionId: 'corpus1', manifestSha256: 'hash', globalStep: 29, completedEpochs: 1, createdUtc: '2026-10-20T02:57:00Z' }],
  canResume: true, qualityEstablished: false, diagnosticCode: null, createdUtc: '2026-10-20T00:00:00Z', updatedUtc: '2026-10-20T02:57:00Z' };
const options = { seed: 42, timesteps: 10, cfg: 2, tempoPercent: 100, useReference: true, referenceClipId: null };
beforeEach(() => vi.stubGlobal('fetch', async () => new Response(JSON.stringify({ modelId: 'accepted29', corpusVersionId: 'corpus1', defaultClipId: 'c'.repeat(32), clips: [{ id: 'c'.repeat(32), trainingOrdinal: 0, seconds: 12, text: 'Опорный голос.' }] }))));
const generation = () => ({ jobs: [], readiness: { ready: false, experimental: true, diagnosticCode: 'worker.runtime.not_ready', profileId: 'q8-f16-cpu' }, voxReadiness: { ready: true, experimental: true, diagnosticCode: null, profileId: 'voxcpm2-saved-adapter-cpu-v1' }, pending: false, error: '', connected: true, enqueue: vi.fn().mockResolvedValue(undefined), control: vi.fn().mockResolvedValue(undefined) });

it.each(['voxcpm2-saved-adapter-cpu-v1', 'voxcpm2-saved-adapter-reference-cpu-v1', 'voxcpm2-saved-adapter-controls-cpu-v1'])('explains inference-only unavailability for queued %s while training is ready and offers cancel', async effectiveProfileId => {
  const state = generation(); const job: JobSnapshot = { id: 'queued-vox', projectId: 'book1', kind: 'preview', state: 'queued', requestedState: 'run', phase: 'queued', completedUnits: 0, totalUnits: 1, revision: 2,
    createdUtc: '2026-10-20T00:00:00Z', updatedUtc: '2026-10-20T00:00:01Z', completedUtc: null, diagnosticCode: 'worker.runtime.not_ready', inputHash: 'hash', effectiveProfileId };
  render(<GenerationPanel current={current} generation={{ ...state, jobs: [job] }} busy={false} voxTraining={vox} />);
  expect(screen.getByText(/Движок выбранной модели VoxCPM2 сейчас недоступен/)).toBeInTheDocument();
  await userEvent.setup().click(screen.getByRole('button', { name: 'Отменить задание' }));
  expect(state.control).toHaveBeenCalledWith(job, 'cancel'); expect(state.enqueue).not.toHaveBeenCalled();
});

it('explicitly selects a historical Vox model independently of GGUF and sends only Vox consent', async () => {
  const state = generation(); const user = userEvent.setup();
  render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'voxcpm:accepted29');
  expect(screen.getByText(/опорный фрагмент из той же подтверждённой записи/)).toBeInTheDocument();
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
  await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
  expect(state.enqueue).toHaveBeenCalledWith('preview', ['s1'], '"2"', { savedVoxCpmModelId: 'accepted29', experimentalConfirmed: true, voxCpmOptions: options });
});
it('keeps a missing selected Vox model blocked with no fallback and resets consent on provider change', async () => {
  const state = generation(); const user = userEvent.setup();
  const { rerender } = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'voxcpm:accepted29');
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
  rerender(<GenerationPanel current={current} generation={state} busy={false} voxTraining={{ ...vox, models: [] }} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('voxcpm:accepted29');
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled(); expect(state.enqueue).not.toHaveBeenCalled();
  rerender(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), '');
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'voxcpm:accepted29');
  expect(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/)).not.toBeChecked();
});
it.each(['busy', 'unavailable', 'other-project'])('blocks Vox when %s without auto-command', async reason => {
  const state = generation(); const user = userEvent.setup();
  const { rerender } = render(<GenerationPanel current={current} generation={state} busy={false} voxTraining={vox} />);
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'voxcpm:accepted29');
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
  rerender(<GenerationPanel current={current} generation={reason === 'unavailable' ? { ...state, voxReadiness: { ...state.voxReadiness, ready: false } } : state} busy={reason === 'busy'} voxTraining={reason === 'other-project' ? { ...vox, projectId: 'book2' } : vox} />);
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled(); expect(state.enqueue).not.toHaveBeenCalled();
});
it('uses distinct uncertain request keys and wire fields for RVC and Vox', async () => {
  const calls: RequestInit[] = [];
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    if (init?.method === 'POST') { calls.push(init); throw new Error('lost'); }
    const value = path.endsWith('/bootstrap') ? { version: '1', antiforgeryRequestToken: 'csrf', capabilities: { generation: true, training: false } }
      : path.endsWith('/readiness') ? { ready: false, diagnosticCode: 'worker.runtime.not_ready', experimental: true, profileId: 'q8-f16-cpu' }
        : path.endsWith('/jobs') ? [] : current.snapshot;
    return new Response(JSON.stringify(value), { headers: { ETag: '"2"' } });
  });
  const client = new StudioClient(); await client.bootstrap(); const install = vi.fn();
  const { result } = renderHook(() => useProjectEvents(client, 'book1', install));
  await waitFor(() => expect(result.current.readiness).toBeDefined());
  await act(async () => { await result.current.enqueue('preview', ['s1'], '"2"', { savedModelId: 'same-id', experimentalConfirmed: true }); });
  await act(async () => { await result.current.enqueue('preview', ['s1'], '"2"', { savedVoxCpmModelId: 'same-id', experimentalConfirmed: true }); });
  expect(calls).toHaveLength(2);
  expect(JSON.parse(calls[1].body as string)).toEqual({ kind: 'preview', segmentIds: ['s1'], savedVoxCpmModelId: 'same-id', experimentalConfirmed: true });
  expect(new Headers(calls[0].headers).get('Idempotency-Key')).not.toBe(new Headers(calls[1].headers).get('Idempotency-Key'));
});
it('aborts an in-flight generation request on project switch without installing a stale reply', async () => {
  let signal: AbortSignal | undefined;
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    if (init?.method === 'POST') { signal = init.signal as AbortSignal; return new Promise<Response>((_resolve, reject) => signal?.addEventListener('abort', () => reject(new DOMException('Abort', 'AbortError')))); }
    const value = path.endsWith('/bootstrap') ? { version: '1', antiforgeryRequestToken: 'csrf', capabilities: {} }
      : path.endsWith('/readiness') ? { ready: false, diagnosticCode: 'worker.runtime.not_ready', experimental: true, profileId: 'q8-f16-cpu' }
        : path.endsWith('/jobs') ? [] : { ...current.snapshot, id: path.includes('book2') ? 'book2' : 'book1' };
    return new Response(JSON.stringify(value), { headers: { ETag: '"2"' } });
  });
  const client = new StudioClient(); await client.bootstrap(); const install = vi.fn();
  const { result, rerender } = renderHook(({ id }) => useProjectEvents(client, id, install), { initialProps: { id: 'book1' } });
  await waitFor(() => expect(result.current.readiness).toBeDefined());
  let pending!: Promise<void>; act(() => { pending = result.current.enqueue('preview', ['s1'], '"2"', { savedVoxCpmModelId: 'accepted29', experimentalConfirmed: true }); });
  await waitFor(() => expect(signal).toBeDefined()); rerender({ id: 'book2' });
  expect(signal?.aborted).toBe(true); await act(async () => { await pending; }); expect(result.current.jobs).toEqual([]);
});
