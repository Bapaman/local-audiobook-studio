import { describe, expect, it, vi } from 'vitest';
import { ApiError, StudioClient } from './client';
import type { VoxCpmTrainingResponse } from './types';

const saved: VoxCpmTrainingResponse = { etag: '"3"', snapshot: { id: 'train1', projectId: 'book1', revision: 3, state: 'paused', phase: 'paused',
  corpusVersionId: 'v1', corpusManifestSha256: 'hash', corpusClipCount: 3, retainedDurationSeconds: 392, globalStep: 1, completedEpochs: 0,
  nextRetainedOrdinal: 1, nextClipId: 'c2', targetCompletedEpochs: 1, executionId: 'e1', models: [], canResume: true, qualityEstablished: false,
  diagnosticCode: null, createdUtc: '2026-10-19T00:00:00Z', updatedUtc: '2026-10-19T00:00:01Z' } };
describe('VoxCPM training transport', () => {
  it('sends explicit approved corpus and consent with project ETag, then controls with training ETag', async () => {
    const calls: { path: string; init?: RequestInit }[] = [];
    vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => { calls.push({ path, init }); return new Response(JSON.stringify(saved.snapshot), { headers: { ETag: '"3"' } }); });
    const client = new StudioClient();
    await client.startVoxCpmTraining('book/id', '"2"', 'v2', true, 'key-start');
    await client.controlVoxCpmTraining('book/id', saved, 'resume', 'key-resume');
    expect(calls[0].path).toBe('/api/projects/book%2Fid/voxcpm-training');
    expect(JSON.parse(calls[0].init!.body as string)).toEqual({ corpusVersionId: 'v2', experimentalConfirmed: true });
    expect(new Headers(calls[0].init?.headers).get('If-Match')).toBe('"2"');
    expect(calls[1].path).toBe('/api/projects/book%2Fid/voxcpm-training/train1/resume');
    expect(new Headers(calls[1].init?.headers).get('If-Match')).toBe('"3"');
  });
  it.each([[412, 'voxcpm_training', '"9"', true], [409, 'voxcpm_training', '"9"', false], [412, 'training', '"9"', false], [412, 'voxcpm_training', '', false]] as const)
    ('only types its own 412 + ETag (%s/%s)', async (status, conflictType, etag, typed) => {
      vi.stubGlobal('fetch', async () => new Response(JSON.stringify({ code: 'voxcpm_training.conflict', conflictType, currentSnapshot: saved.snapshot, detail: 'C:/private' }), { status, headers: etag ? { ETag: etag } : {} }));
      const error = await new StudioClient().controlVoxCpmTraining('book1', saved, 'stop', 'key').catch(e => e);
      expect(error).toBeInstanceOf(ApiError); expect(error.message).not.toContain('private');
      expect(error.currentVoxCpmTraining).toEqual(typed ? { snapshot: saved.snapshot, etag: '"9"' } : undefined);
    });
  it('permits null history but requires ETags on nonempty reads and writes', async () => {
    let body: unknown = null; vi.stubGlobal('fetch', async () => new Response(JSON.stringify(body)));
    const client = new StudioClient(); expect(await client.voxCpmTraining('book1')).toBeNull(); body = saved.snapshot;
    await expect(client.voxCpmTraining('book1')).rejects.toMatchObject({ code: 'request.invalid' });
    await expect(client.startVoxCpmTraining('book1', '"2"', 'v1', true, 'key')).rejects.toMatchObject({ code: 'request.invalid' });
  });
});
