import { useEffect, useRef, useState } from 'react';
import { ApiError, StudioClient } from '../../api/client';
import type { SnapshotResponse, TrainingControl, TrainingReadiness, TrainingResponse } from '../../api/types';

export const trainingActive = (value: TrainingResponse | null) => !!value && ['queued', 'preparing', 'training', 'stop_requested'].includes(value.snapshot.state);
type Send = (key: string, signal: AbortSignal) => Promise<TrainingResponse>;
type Scope = { id: string; active: boolean; abort: AbortController; value: TrainingResponse | null; pending: boolean; pollSoon?: () => void; retry?: { signature: string; key: string; send: Send } };

export function useTraining(current: SnapshotResponse, sharedClient?: StudioClient, installProject?: (value: SnapshotResponse) => void) {
  const [ownedClient] = useState(() => sharedClient ?? new StudioClient());
  const client = sharedClient ?? ownedClient;
  const scope = useRef<Scope | undefined>(undefined);
  const [training, setTraining] = useState<TrainingResponse | null>(null);
  const [readiness, setReadiness] = useState<TrainingReadiness>();
  const [loading, setLoading] = useState(true);
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  function install(owner: Scope, value: TrainingResponse | null) {
    if (!owner.active || scope.current !== owner) return;
    if (value && value.snapshot.projectId !== owner.id) return;
    if (owner.value && (!value || (value.snapshot.id === owner.value.snapshot.id && value.snapshot.revision < owner.value.snapshot.revision))) return;
    owner.value = value; setTraining(value);
  }
  useEffect(() => {
    const owner: Scope = { id: current.snapshot.id, active: true, abort: new AbortController(), value: null, pending: false };
    scope.current = owner;
    setTraining(null); setReadiness(undefined); setLoading(true); setPending(false); setError('');
    let timer: ReturnType<typeof setTimeout> | undefined;
    let reading = false;
    function schedule(delay: number) { clearTimeout(timer); timer = setTimeout(() => void refresh(), delay); }
    owner.pollSoon = () => { if (owner.active) schedule(2000); };
    async function refresh() {
      if (!owner.active || reading) return;
      reading = true;
      try {
        await client.bootstrap();
        if (!owner.active) return;
        const [runtime, value] = await Promise.all([client.trainingReadiness(owner.abort.signal), client.training(owner.id, owner.abort.signal)]);
        if (!owner.active) return;
        setReadiness({ ...runtime, ready: runtime.ready === true && runtime.diagnosticCode === null && runtime.experimental === true && runtime.cpuThreads === 2 });
        install(owner, value);
      } catch (failure) {
        if (owner.active) setError(failure instanceof ApiError ? failure.message : new ApiError('network').message);
      } finally {
        reading = false;
        if (owner.active) {
          setLoading(false);
          schedule(trainingActive(owner.value) || owner.pending ? 2000 : 15000);
        }
      }
    }
    void refresh();
    return () => { owner.active = false; owner.abort.abort(); clearTimeout(timer); };
  }, [client, current.snapshot.id]);

  async function command(signature: string, send: Send, stopTarget?: TrainingResponse) {
    const owner = scope.current;
    if (!owner?.active || owner.id !== current.snapshot.id || owner.pending) return;
    owner.pending = true; setPending(true); setError('');
    if (owner.retry?.signature !== signature) owner.retry = { signature, key: crypto.randomUUID(), send };
    try {
      let result: TrainingResponse;
      try { result = await owner.retry.send(owner.retry.key, owner.abort.signal); }
      catch (failure) {
        const next = failure instanceof ApiError && failure.code === 'training.conflict' ? failure.currentTraining : undefined;
        // One bounded STOP retry, never resume/restart or a different execution.
        // A network failure still requires a user retry with the original request.
        if (!stopTarget?.snapshot.executionId || !next || next.snapshot.executionId !== stopTarget.snapshot.executionId
          || next.snapshot.id !== stopTarget.snapshot.id || next.snapshot.sourceRecordingId !== stopTarget.snapshot.sourceRecordingId
          || !trainingActive(next) || next.snapshot.state === 'stop_requested' || !owner.active) throw failure;
        install(owner, next);
        result = await client.controlTraining(owner.id, next, 'stop', owner.retry.key, owner.abort.signal);
      }
      install(owner, result);
      owner.retry = undefined;
    } catch (failure) {
      if (!owner.active) return;
      const error = failure instanceof ApiError ? failure : new ApiError('request.invalid');
      if (error.code !== 'network') owner.retry = undefined;
      if (error.current) installProject?.(error.current);
      if (error.currentTraining) install(owner, error.currentTraining);
      else if (error.code === 'training.conflict' || error.current) {
        try { install(owner, await client.training(owner.id, owner.abort.signal)); } catch { /* Preserve the actionable original conflict. */ }
      }
      if (owner.active) setError(error.current && !installProject ? 'Проект изменён из другой вкладки. Обновите проект перед новым запуском обучения.' : error.message);
    } finally { owner.pending = false; if (owner.active) { setPending(false); owner.pollSoon?.(); } }
  }
  const value = training?.snapshot.projectId === current.snapshot.id ? training : null;
  return { training: value, readiness, loading, pending, error, active: trainingActive(value),
    start: () => command(`start:${current.etag}`, (key, signal) => client.startTraining(current.snapshot.id, current.etag, key, signal)),
    control: (intent: TrainingControl) => value ? command(`${value.snapshot.id}:${value.snapshot.executionId ?? 'legacy'}:${intent}`, (key, signal) => client.controlTraining(current.snapshot.id, value, intent, key, signal), intent === 'stop' ? value : undefined) : Promise.resolve(),
  };
}
