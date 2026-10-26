import { useEffect, useRef, useState } from 'react';
import { ApiError, StudioClient } from '../../api/client';
import type { JobControl, JobKind, JobSnapshot, RuntimeReadiness, SavedVoiceSelection, SnapshotResponse } from '../../api/types';

export const terminal = (job: JobSnapshot) => ['completed', 'failed', 'canceled'].includes(job.state);
type Scope = { id: string; active: boolean; abort: AbortController; jobs: JobSnapshot[]; pending: boolean; retry?: { signature: string; key: string } };

export function useProjectEvents(client: StudioClient, projectId: string | undefined, installProject: (value: SnapshotResponse) => void) {
  const scope = useRef<Scope | undefined>(undefined);
  const [jobs, setJobs] = useState<JobSnapshot[]>([]);
  const [readiness, setReadiness] = useState<RuntimeReadiness>();
  const [voxReadiness, setVoxReadiness] = useState<RuntimeReadiness>();
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [connected, setConnected] = useState(false);

  function installJobs(owner: Scope, values: JobSnapshot[]) {
    if (!owner.active || scope.current !== owner) return;
    const next = new Map(values.filter(job => job.projectId === owner.id).map(job => [job.id, job]));
    // A command may commit after a list read begins; never roll its revision back.
    for (const previous of owner.jobs) {
      const incoming = next.get(previous.id);
      if (!incoming || incoming.revision < previous.revision) next.set(previous.id, previous);
    }
    owner.jobs = [...next.values()].sort((a, b) => b.createdUtc.localeCompare(a.createdUtc))
      .filter((job, index) => index < 32 || !terminal(job));
    setJobs(owner.jobs);
  }

  useEffect(() => {
    setJobs([]); setReadiness(undefined); setVoxReadiness(undefined); setError(''); setPending(false); setConnected(false);
    if (!projectId) { scope.current = undefined; return; }
    const owner: Scope = { id: projectId, active: true, abort: new AbortController(), jobs: [], pending: false }; scope.current = owner;
    let reading = false; let again = false;
    async function refresh() {
      again = true;
      if (reading || !owner.active) return;
      reading = true;
      try {
        while (again && owner.active) {
          again = false;
          const [project, latestJobs, runtime, vox] = await Promise.allSettled([
            client.snapshot(`/api/projects/${owner.id}`), client.jobs(owner.id), client.readiness(owner.abort.signal), client.voxCpmReadiness(owner.abort.signal),
          ]);
          if (!owner.active) return;
          if (project.status === 'fulfilled') installProject(project.value);
          if (latestJobs.status === 'fulfilled') installJobs(owner, latestJobs.value);
          if (runtime.status === 'fulfilled') {
            const value = runtime.value;
            setReadiness({ ...value, ready: value.ready === true && value.diagnosticCode === null && ['q8-f16-cpu', 'q8-f16-hybrid-16'].includes(value.profileId) });
          } else setReadiness({ ready: false, experimental: true, diagnosticCode: 'worker.runtime.not_ready', profileId: 'q8-f16-cpu' });
          if (vox.status === 'fulfilled') {
            const value = vox.value;
            setVoxReadiness({ ...value, ready: value.ready === true && value.diagnosticCode === null && ['voxcpm2-saved-adapter-cpu-v1', 'voxcpm2-saved-adapter-reference-cpu-v1', 'voxcpm2-saved-adapter-controls-cpu-v1'].includes(value.profileId) });
          } else setVoxReadiness({ ready: false, experimental: true, diagnosticCode: 'worker.runtime.not_ready', profileId: 'voxcpm2-saved-adapter-controls-cpu-v1' });
          const failed = [project, latestJobs, runtime].find(value => value.status === 'rejected');
          if (failed?.status === 'rejected') setError(failed.reason instanceof ApiError ? failed.reason.message : new ApiError('network').message);
        }
      } catch (failure) { if (owner.active) setError(failure instanceof ApiError ? failure.message : new ApiError('network').message); }
      finally { reading = false; }
    }
    const events = new EventSource(`/api/projects/${projectId}/events`);
    const hint = () => { if (owner.active) void refresh(); };
    for (const name of ['snapshot', 'job', 'reset']) events.addEventListener(name, hint);
    events.addEventListener('open', () => { if (owner.active) { setConnected(true); hint(); } });
    events.addEventListener('error', () => { if (owner.active) setConnected(false); });
    void refresh();
    return () => { owner.active = false; owner.abort.abort(); events.close(); };
  }, [client, projectId, installProject]);

  async function command(signature: string, send: (key: string, signal: AbortSignal) => Promise<{ snapshot: JobSnapshot }>) {
    const owner = scope.current;
    if (!owner?.active || owner.id !== projectId || owner.pending) return;
    owner.pending = true; setPending(true); setError('');
    if (owner.retry?.signature !== signature) owner.retry = { signature, key: crypto.randomUUID() };
    try {
      const result = await send(owner.retry.key, owner.abort.signal);
      if (owner.active) { installJobs(owner, [result.snapshot, ...owner.jobs.filter(job => job.id !== result.snapshot.id)]); owner.retry = undefined; }
    } catch (failure) {
      if (!owner.active) return;
      if (failure instanceof ApiError) {
        if (failure.currentJob) installJobs(owner, [failure.currentJob.snapshot, ...owner.jobs.filter(job => job.id !== failure.currentJob!.snapshot.id)]);
        if (failure.current) installProject(failure.current);
        if (failure.code !== 'network') owner.retry = undefined;
        setError(failure.message);
      } else { owner.retry = undefined; setError(new ApiError('request.invalid').message); }
    } finally { owner.pending = false; if (owner.active) setPending(false); }
  }
  return {
    jobs: jobs.filter(job => job.projectId === projectId), readiness, voxReadiness, pending, error, connected,
    enqueue: (kind: JobKind, ids: string[], etag: string, voice?: SavedVoiceSelection) => command(JSON.stringify([kind, ids, voice?.savedModelId ?? null, voice?.savedVoxCpmModelId ?? null, voice?.experimentalConfirmed ?? null,
      voice?.voxCpmOptions ? [voice.voxCpmOptions.seed, voice.voxCpmOptions.timesteps, voice.voxCpmOptions.cfg, voice.voxCpmOptions.tempoPercent, voice.voxCpmOptions.useReference, voice.voxCpmOptions.referenceClipId] : null]), (key, signal) => client.enqueue(projectId!, kind, ids, etag, key, voice, signal)),
    control: (job: JobSnapshot, intent: JobControl) => command(`${job.id}:${intent}`, (key, signal) => client.control(projectId!, job, intent, key, signal)),
  };
}
export type GenerationState = Omit<ReturnType<typeof useProjectEvents>, 'voxReadiness'> & { voxReadiness?: RuntimeReadiness };
