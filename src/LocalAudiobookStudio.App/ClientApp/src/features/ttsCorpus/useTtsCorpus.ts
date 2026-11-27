import { useEffect, useRef, useState } from 'react';
import { ApiError, StudioClient } from '../../api/client';
import type { SnapshotResponse, TtsCorpusClipUpdate, TtsCorpusResponse } from '../../api/types';

export const corpusActive = (value: TtsCorpusResponse | null) => !!value && ['queued', 'preparing', 'transcribing', 'stop_requested'].includes(value.snapshot.state);
export const corpusCheckingResume = (value: TtsCorpusResponse | null) => value?.snapshot.diagnosticCode === 'tts_corpus.interrupted.checking';
type Send = (key: string, signal: AbortSignal) => Promise<TtsCorpusResponse>;
type Scope = { id: string; active: boolean; abort: AbortController; value: TtsCorpusResponse | null; pending: boolean; epoch: number; pollSoon?: () => void; retry?: { signature: string; key: string; send: Send } };

export function useTtsCorpus(current: SnapshotResponse, sharedClient?: StudioClient, installProject?: (value: SnapshotResponse) => void) {
  const [ownedClient] = useState(() => sharedClient ?? new StudioClient());
  const client = sharedClient ?? ownedClient;
  const scope = useRef<Scope | undefined>(undefined);
  const [corpus, setCorpus] = useState<TtsCorpusResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [pending, setPending] = useState(false);
  const [readError, setReadError] = useState('');
  const [mutationError, setMutationError] = useState('');

  function install(owner: Scope, next: TtsCorpusResponse | null) {
    if (!owner.active || scope.current !== owner || (next && next.snapshot.projectId !== owner.id)) return false;
    if (owner.value && (!next || (next.snapshot.id === owner.value.snapshot.id && next.snapshot.revision < owner.value.snapshot.revision))) return false;
    owner.value = next; setCorpus(next); return true;
  }
  useEffect(() => {
    const owner: Scope = { id: current.snapshot.id, active: true, abort: new AbortController(), value: null, pending: false, epoch: 0 };
    scope.current = owner;
    setCorpus(null); setLoading(true); setPending(false); setReadError(''); setMutationError('');
    let timer: ReturnType<typeof setTimeout> | undefined;
    let reading = false;
    function schedule(delay: number) { clearTimeout(timer); timer = setTimeout(() => void refresh(), delay); }
    owner.pollSoon = () => { if (owner.active) schedule(2000); };
    async function refresh() {
      if (!owner.active || reading) return;
      reading = true;
      const epoch = owner.epoch;
      try {
        await client.bootstrap();
        if (!owner.active) return;
        const next = await client.ttsCorpus(owner.id, owner.abort.signal);
        if (!owner.active) return;
        // A read begun before a command cannot replace its result, even if a new corpus reset its revision.
        if (epoch === owner.epoch) install(owner, next);
        setReadError('');
      } catch (failure) {
        if (owner.active) setReadError(failure instanceof ApiError ? failure.message : new ApiError('network').message);
      } finally {
        reading = false;
        if (owner.active) { setLoading(false); schedule(corpusActive(owner.value) || corpusCheckingResume(owner.value) || owner.pending ? 2000 : 15000); }
      }
    }
    void refresh();
    return () => { owner.active = false; owner.abort.abort(); clearTimeout(timer); };
  }, [client, current.snapshot.id]);

  async function command(signature: string, send: Send, expectedCorpusId?: string): Promise<TtsCorpusResponse | undefined> {
    const owner = scope.current;
    if (!owner?.active || owner.id !== current.snapshot.id || owner.pending) return;
    owner.pending = true; owner.epoch++; setPending(true); setReadError(''); setMutationError('');
    if (owner.retry?.signature !== signature) owner.retry = { signature, key: crypto.randomUUID(), send };
    try {
      const result = await owner.retry.send(owner.retry.key, owner.abort.signal);
      owner.epoch++;
      owner.retry = undefined;
      if (install(owner, result)) return result;
    } catch (failure) {
      if (!owner.active) return;
      const problem = failure instanceof ApiError ? failure : new ApiError('request.invalid');
      // An expired receipt must not turn the same user intent into a fresh operation.
      if (!['network', 'tts_corpus.idempotency.expired'].includes(problem.code)) owner.retry = undefined;
      if (problem.current?.snapshot.id === owner.id) installProject?.(problem.current);
      const next = problem.currentCorpus;
      if (next?.snapshot.projectId === owner.id && (!expectedCorpusId || next.snapshot.id === expectedCorpusId)) install(owner, next);
      else if (problem.code === 'tts_corpus.conflict' || problem.current) {
        try { install(owner, await client.ttsCorpus(owner.id, owner.abort.signal)); } catch { /* Keep the original actionable conflict. */ }
      }
      if (owner.active) setMutationError(problem.current && !installProject ? 'Проект изменён из другой вкладки. Обновите проект перед подготовкой корпуса.' : problem.message);
    } finally {
      owner.pending = false;
      if (owner.active) { setPending(false); owner.pollSoon?.(); }
    }
  }
  const value = corpus?.snapshot.projectId === current.snapshot.id ? corpus : null;
  const identity = value ? [current.snapshot.id, value.snapshot.id, value.snapshot.sourceRecordingId, value.snapshot.executionId ?? null] : [];
  return { corpus: value, loading, pending, error: mutationError || readError, active: corpusActive(value),
    audioUrl: (clipId: string) => value ? client.ttsCorpusAudioUrl(current.snapshot.id, value.snapshot.id, clipId) : '',
    start: () => command(JSON.stringify(['start', current.snapshot.id, current.snapshot.voiceRecording?.artifactId, current.etag, value?.snapshot.id ?? null]), (key, signal) => client.startTtsCorpus(current.snapshot.id, current.etag, key, signal)),
    control: (intent: 'stop' | 'resume') => value ? command(JSON.stringify([...identity, intent]), (key, signal) => client.controlTtsCorpus(current.snapshot.id, value, intent, key, signal), value.snapshot.id) : Promise.resolve(undefined),
    updateClip: (clipId: string, body: TtsCorpusClipUpdate) => value ? command(JSON.stringify([...identity, 'clip', clipId, body]), (key, signal) => client.updateTtsCorpusClip(current.snapshot.id, value, clipId, body, key, signal), value.snapshot.id) : Promise.resolve(undefined),
    approve: () => value ? command(JSON.stringify([...identity, 'approve', value.snapshot.revision]), (key, signal) => client.approveTtsCorpus(current.snapshot.id, value, key, signal), value.snapshot.id) : Promise.resolve(undefined),
  };
}
