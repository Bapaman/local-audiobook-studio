import { describe, expect, it, vi } from 'vitest';
import { ApiError, StudioClient } from './client';
import type { TtsCorpusResponse } from './types';

const corpus: TtsCorpusResponse = { etag: '"3"', snapshot: { id: 'corpus1', projectId: 'book1', revision: 3, state: 'completed', phase: 'completed', sourceRecordingId: 'record1',
  sourceDurationSeconds: 300, retainedDurationSeconds: 0, totalClipCount: 0, completedClipCount: 0, clips: [], versions: [], canResume: false,
  diagnosticCode: null, createdUtc: '2026-10-16T00:00:00Z', updatedUtc: '2026-10-16T00:00:00Z', executionId: 'execution1' } };

describe('Corpus transport boundary', () => {
  it.each([
    { status: 412, conflictType: 'tts_corpus', etag: '"9"', typed: true },
    { status: 409, conflictType: 'tts_corpus', etag: '"9"', typed: false },
    { status: 412, conflictType: 'training', etag: '"9"', typed: false },
    { status: 412, conflictType: 'tts_corpus', etag: '', typed: false },
  ])('accepts corpus conflict snapshots only for the typed 412 with ETag ($status/$conflictType/$etag)', async ({ status, conflictType, etag, typed }) => {
    vi.stubGlobal('fetch', async () => new Response(JSON.stringify({ code: 'tts_corpus.conflict', conflictType, currentSnapshot: { ...corpus.snapshot, revision: 9 }, detail: 'C:/private/audio' }), { status, headers: etag ? { ETag: etag } : {} }));
    const problem = await new StudioClient().controlTtsCorpus('book1', corpus, 'stop', 'operation-key').catch(error => error);
    expect(problem).toBeInstanceOf(ApiError); expect(problem.message).not.toContain('private');
    if (typed) expect(problem.currentCorpus).toEqual({ snapshot: { ...corpus.snapshot, revision: 9 }, etag: '"9"' });
    else expect(problem.currentCorpus).toBeUndefined();
  });
  it('requires an ETag for a nonempty read and mutation response, but permits an absent corpus', async () => {
    let body: unknown = null;
    vi.stubGlobal('fetch', async () => new Response(JSON.stringify(body), { status: 200 }));
    const client = new StudioClient(); expect(await client.ttsCorpus('book1')).toBeNull(); body = corpus.snapshot;
    await expect(client.ttsCorpus('book1')).rejects.toMatchObject({ code: 'request.invalid' });
    await expect(client.updateTtsCorpusClip('book1', corpus, 'clip1', { text: 'Текст.', excluded: false, reviewed: false }, 'key')).rejects.toMatchObject({ code: 'request.invalid' });
  });
  it('preserves abort cancellation instead of reporting it as network uncertainty', async () => {
    const abort = new AbortController(); const cancelled = new DOMException('Canceled', 'AbortError');
    vi.stubGlobal('fetch', async () => { abort.abort(); throw cancelled; });
    await expect(new StudioClient().ttsCorpus('book1', abort.signal)).rejects.toBe(cancelled);
  });
  it('builds same-origin audio routes from encoded opaque IDs only', () => {
    expect(new StudioClient().ttsCorpusAudioUrl('book/id', 'corpus?id', 'clip#id')).toBe('/api/projects/book%2Fid/tts-corpus/corpus%3Fid/clips/clip%23id/audio');
  });
});
