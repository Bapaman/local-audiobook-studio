import { useEffect, useRef, useState } from 'react';
import { ApiError, StudioClient } from '../../api/client';
import type { TtsCorpusVersion, VoxCpmTrainingResponse } from '../../api/types';

const active = (value: VoxCpmTrainingResponse | null) => !!value && ['queued', 'loading', 'training', 'checkpointing', 'stop_requested'].includes(value.snapshot.state);
type Intent = { key: string; expectedId?: string; send: (key: string, signal: AbortSignal) => Promise<VoxCpmTrainingResponse> };
type Scope = { id: string; alive: boolean; abort: AbortController; epoch: number; value: VoxCpmTrainingResponse | null; pending: boolean; uncertain?: Intent; pollSoon?: () => void };

export function useVoxCpmTraining(projectId: string, projectEtag: string, versions: TtsCorpusVersion[], onProjectConflict: () => void, sharedClient?: StudioClient) {
  const [client] = useState(() => sharedClient ?? new StudioClient());
  const scope = useRef<Scope | undefined>(undefined);
  const [value, setValue] = useState<VoxCpmTrainingResponse | null>(null);
  const [loading, setLoading] = useState(true), [pending, setPending] = useState(false), [uncertain, setUncertain] = useState(false);
  const [readError, setReadError] = useState(''), [commandError, setCommandError] = useState('');
  const owns = (owner: Scope) => owner.alive && scope.current === owner;
  function install(owner: Scope, next: VoxCpmTrainingResponse | null) {
    if (!owns(owner) || next && next.snapshot.projectId !== owner.id) return;
    const previous = owner.value;
    if (previous && (!next || (next.snapshot.id === previous.snapshot.id && next.snapshot.revision < previous.snapshot.revision)
      || Date.parse(next.snapshot.createdUtc) < Date.parse(previous.snapshot.createdUtc))) return;
    owner.value = next; setValue(next);
  }
  useEffect(() => {
    const owner: Scope = { id: projectId, alive: true, abort: new AbortController(), epoch: 0, value: null, pending: false };
    scope.current = owner; setValue(null); setLoading(true); setPending(false); setUncertain(false); setReadError(''); setCommandError('');
    let timer: ReturnType<typeof setTimeout> | undefined, reading = false;
    const schedule = (delay: number) => { clearTimeout(timer); timer = setTimeout(() => void refresh(), delay); };
    owner.pollSoon = () => { if (owns(owner)) schedule(2000); };
    async function refresh() {
      if (!owns(owner) || reading) return; reading = true; const epoch = owner.epoch;
      try {
        await client.bootstrap(); if (!owns(owner)) return;
        const next = await client.voxCpmTraining(owner.id, owner.abort.signal);
        if (owns(owner) && epoch === owner.epoch && !owner.pending) { install(owner, next); setReadError(''); }
      } catch (error) { if (owns(owner) && epoch === owner.epoch) setReadError(error instanceof ApiError ? error.message : new ApiError('network').message); }
      finally { reading = false; if (owns(owner)) { setLoading(false); schedule(active(owner.value) || owner.pending || owner.uncertain ? 2000 : 15000); } }
    }
    void refresh();
    return () => { owner.alive = false; owner.abort.abort(); clearTimeout(timer); };
  }, [client, projectId]);

  async function issue(intent: Intent, retry = false) {
    const owner = scope.current;
    if (!owner || !owns(owner) || owner.id !== projectId || owner.pending || (owner.uncertain && (!retry || owner.uncertain !== intent))) return;
    owner.pending = true; owner.epoch++; setPending(true); setCommandError('');
    try {
      await client.bootstrap(); if (!owns(owner)) return;
      const next = await intent.send(intent.key, owner.abort.signal); if (!owns(owner)) return;
      if (next.snapshot.projectId !== owner.id || intent.expectedId && next.snapshot.id !== intent.expectedId) throw new ApiError('request.invalid');
      owner.epoch++; owner.uncertain = undefined; setUncertain(false); install(owner, next);
    } catch (error) {
      if (!owns(owner)) return;
      const problem = error instanceof ApiError ? error : new ApiError('network');
      owner.epoch++;
      owner.uncertain = problem.code === 'network' ? intent : undefined; setUncertain(!!owner.uncertain);
      const next = problem.currentVoxCpmTraining;
      if (next?.snapshot.projectId === owner.id && (!intent.expectedId || next.snapshot.id === intent.expectedId)) install(owner, next);
      if (problem.current?.snapshot.id === owner.id || problem.code === 'project.conflict') onProjectConflict();
      setCommandError(problem.message);
    } finally {
      owner.pending = false;
      if (owns(owner)) { setPending(false); owner.pollSoon?.(); }
    }
  }
  const current = value?.snapshot.projectId === projectId ? value : null;
  return { snapshot: current?.snapshot ?? null, etag: current?.etag,
    loading, pending, uncertain, active: active(current), busy: active(current) || pending || uncertain, error: commandError || readError,
    start: (versionId: string, consent: boolean) => consent === true && versions.some(v => v.id === versionId)
      ? issue({ key: crypto.randomUUID(), send: (key, signal) => client.startVoxCpmTraining(projectId, projectEtag, versionId, consent, key, signal) }) : Promise.resolve(),
    stop: () => current ? issue({ key: crypto.randomUUID(), expectedId: current.snapshot.id, send: (key, signal) => client.controlVoxCpmTraining(projectId, current, 'stop', key, signal) }) : Promise.resolve(),
    resume: () => current ? issue({ key: crypto.randomUUID(), expectedId: current.snapshot.id, send: (key, signal) => client.controlVoxCpmTraining(projectId, current, 'resume', key, signal) }) : Promise.resolve(),
    retryUncertain: () => scope.current?.uncertain ? issue(scope.current.uncertain, true) : Promise.resolve(),
  };
}
