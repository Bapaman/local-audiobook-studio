import type { JobSnapshot, VoxCpmGenerationInfo, VoxCpmGenerationOptions } from '../../api/types';

export const voxDefaults = (): VoxCpmGenerationOptions => ({ seed: 42, timesteps: 10, cfg: 2, tempoPercent: 100, useReference: true, referenceClipId: null });
export function validVoxOptions(value: unknown): value is VoxCpmGenerationOptions {
  if (!value || typeof value !== 'object') return false;
  const o = value as VoxCpmGenerationOptions;
  return Number.isInteger(o.seed) && o.seed >= 0 && o.seed <= 2147483647
    && Number.isInteger(o.timesteps) && o.timesteps >= 4 && o.timesteps <= 20
    && Number.isFinite(o.cfg) && o.cfg >= 1 && o.cfg <= 3
    && Number.isInteger(o.tempoPercent) && o.tempoPercent >= 80 && o.tempoPercent <= 120
    && typeof o.useReference === 'boolean' && (o.referenceClipId === null || typeof o.referenceClipId === 'string' && /^[a-f0-9]{32}$/.test(o.referenceClipId))
    && (o.useReference || o.referenceClipId === null);
}
type Draft = { modelId: string; options: VoxCpmGenerationOptions; selectionKnown: boolean };
export const draftKey = (projectId: string) => `voxcpm-generation-draft:${projectId}`;
export function loadVoxDraft(projectId: string): Draft {
  try {
    const draft: unknown = JSON.parse(localStorage.getItem(draftKey(projectId)) ?? 'null');
    if (draft && typeof draft === 'object' && 'modelId' in draft && typeof draft.modelId === 'string'
      && (draft.modelId === '' || /^voxcpm:[a-f0-9]{32}$/.test(draft.modelId)) && 'options' in draft && validVoxOptions(draft.options)) {
      const { seed, timesteps, cfg, tempoPercent, useReference, referenceClipId } = draft.options;
      return { modelId: draft.modelId, options: { seed, timesteps, cfg, tempoPercent, useReference, referenceClipId }, selectionKnown: true };
    }
  } catch { /* A missing or unreadable draft uses defaults. */ }
  return { modelId: '', options: voxDefaults(), selectionKnown: false };
}
export function saveVoxDraft(projectId: string, modelId: string, options: VoxCpmGenerationOptions) {
  try {
    if (!/^voxcpm:[a-f0-9]{32}$/.test(modelId)) {
      localStorage.setItem(draftKey(projectId), JSON.stringify({ modelId: '', options: voxDefaults() })); return;
    }
    if (validVoxOptions(options)) localStorage.setItem(draftKey(projectId), JSON.stringify({ modelId, options }));
  } catch { /* Generation remains usable if browser storage is unavailable. */ }
}

export function latestSuccessfulVoxSettings(jobs: readonly JobSnapshot[], projectId: string, modelIds: ReadonlySet<string>): VoxCpmGenerationInfo | undefined {
  const job = jobs.filter(job => job.projectId === projectId && job.state === 'completed' && job.completedUnits > 0 && !!job.audioResults?.length
    && job.voxCpmGeneration && /^[a-f0-9]{32}$/.test(job.voxCpmGeneration.modelId) && modelIds.has(job.voxCpmGeneration.modelId)
    && Number.isSafeInteger(job.voxCpmGeneration.globalStep) && job.voxCpmGeneration.globalStep > 0 && validVoxOptions(job.voxCpmGeneration.options))
    .sort((a, b) => b.createdUtc.localeCompare(a.createdUtc) || b.id.localeCompare(a.id))[0];
  if (!job?.voxCpmGeneration) return undefined;
  const { seed, timesteps, cfg, tempoPercent, useReference, referenceClipId } = job.voxCpmGeneration.options;
  return { ...job.voxCpmGeneration, options: { seed, timesteps, cfg, tempoPercent, useReference, referenceClipId } };
}
