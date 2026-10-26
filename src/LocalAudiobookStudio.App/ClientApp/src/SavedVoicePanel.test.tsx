import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { expect, it, vi } from 'vitest';
import type { SnapshotResponse, TrainingSnapshot } from './api/types';
import { GenerationPanel } from './features/generation/GenerationPanel';

const current: SnapshotResponse = { etag: '"2"', snapshot: { id: 'book1', displayName: 'Книга', revision: 2,
  voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 1, durationMilliseconds: 360000, hasClippingWarning: false },
  voiceAnchor: { artifactId: 'anchor1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Точный текст.' },
  textDocument: { sourceKind: 'Paste', displayName: 'Книга', encoding: 'utf-8', sha256: 'hash', byteCount: 1, originalText: 'История.', normalizedText: 'История.' },
  segments: [{ id: 's1', order: 0, originalText: 'История.', normalizedText: 'История.', spokenText: 'История.', contentHash: 'hash', status: 'pending' }] } };
const training: TrainingSnapshot = { id: 'training1', projectId: 'book1', revision: 5, state: 'completed', phase: 'completed', sourceRecordingId: 'record1', globalStep: 125,
  completedEpoch: 1, epochSize: 125, createdUtc: '2026-10-11T00:00:00Z', updatedUtc: '2026-10-11T00:03:00Z', diagnosticCode: null, canResume: true,
  models: [{ id: 'saved1', globalStep: 125, completedEpoch: 1, createdUtc: '2026-10-11T00:03:00Z', qualityEstablished: false, selectable: true }] };

it('blocks the selected saved model if its binding disappears without silently falling back', async () => {
  const enqueue = vi.fn().mockResolvedValue(undefined); const user = userEvent.setup();
  const generation = { jobs: [], readiness: { ready: true, experimental: true, diagnosticCode: null, profileId: 'q8-f16-cpu' }, pending: false, error: '', connected: true, enqueue, control: vi.fn().mockResolvedValue(undefined) };
  const readiness = { ready: true, experimental: true as const, diagnosticCode: null, cpuThreads: 2 };
  const { rerender } = render(<GenerationPanel current={current} generation={generation} busy={false} training={training} trainingReadiness={readiness} />);
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'saved1');
  await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeEnabled();
  rerender(<GenerationPanel current={current} generation={generation} busy={false} training={{ ...training, models: [] }} trainingReadiness={readiness} />);
  expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('saved1');
  expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeDisabled();
  await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
  expect(enqueue).not.toHaveBeenCalled();
  await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), '');
  await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
  expect(enqueue).toHaveBeenCalledWith('preview', ['s1'], '"2"', undefined);
});
