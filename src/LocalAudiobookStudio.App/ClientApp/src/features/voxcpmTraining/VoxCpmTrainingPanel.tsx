import { useEffect, useState } from 'react';
import { ApiError, type StudioClient } from '../../api/client';
import type { TtsCorpusVersion, VoxCpmTrainingState, VoxCpmTrainingSnapshot } from '../../api/types';
import { useVoxCpmTraining } from './useVoxCpmTraining';

const states: Record<VoxCpmTrainingState, string> = { queued: 'В очереди', loading: 'Загрузка модели', training: 'Обучение на текущем фрагменте', checkpointing: 'Проверка сохранения',
  stop_requested: 'Остановка запрошена', paused: 'Остановлено', completed: 'Заказанный проход завершён', interrupted: 'Прервано при закрытии студии', failed: 'Проход остановлен с ошибкой' };
const duration = (seconds: number) => `${Math.floor(seconds / 60)} мин ${Math.floor(seconds % 60)} с`;
export function VoxCpmTrainingPanel({ projectId, projectEtag, versions, externalBusy, onBusyChange, onProjectConflict, onSnapshotChange, client }:
  { projectId: string; projectEtag: string; versions: TtsCorpusVersion[]; externalBusy: boolean; onBusyChange: (busy: boolean) => void; onProjectConflict: () => void; onSnapshotChange?: (value: VoxCpmTrainingSnapshot | null) => void; client?: StudioClient }) {
  const state = useVoxCpmTraining(projectId, projectEtag, versions, onProjectConflict, client);
  const [selected, setSelected] = useState(versions.at(-1)?.id ?? ''); const [consent, setConsent] = useState(false);
  useEffect(() => { if (!versions.some(v => v.id === selected)) setSelected(versions.at(-1)?.id ?? ''); }, [versions, selected]);
  useEffect(() => { onBusyChange(state.busy); }, [state.busy, onBusyChange]);
  useEffect(() => () => onBusyChange(false), [onBusyChange]);
  useEffect(() => { onSnapshotChange?.(state.snapshot); }, [state.snapshot, onSnapshotChange]);
  const snapshot = state.snapshot;
  const disabled = externalBusy || state.loading || state.pending || state.uncertain || state.active;
  const canRun = !disabled && consent;
  const diagnostic = snapshot?.diagnosticCode && /^voxcpm_training\.[a-zA-Z0-9_.]{1,80}$/.test(snapshot.diagnosticCode) ? snapshot.diagnosticCode : null;
  return <section className="panel training-panel voxcpm-training" aria-labelledby="voxcpm-training-heading" hidden={!snapshot && !versions.length}>
    <h2 id="voxcpm-training-heading">Дообучение VoxCPM2</h2>
    {state.pending && !state.active && <p className="training-check" role="status">Проверяем файлы VoxCPM2 перед запуском. Это может занять пару минут.</p>}
    <label htmlFor="vox-corpus-version">Подтверждённая запись для обучения</label><select id="vox-corpus-version" value={selected} disabled={disabled || versions.length === 0} onChange={event => setSelected(event.target.value)}>
      {versions.length === 0 && <option value="">Сначала подтвердите корпус речи</option>}
      {versions.map((v, i) => <option key={v.id} value={v.id}>Версия {i + 1} · {v.clipCount} фрагм. · {duration(v.retainedDurationSeconds)}</option>)}
    </select>
    <label className="check-label"><input type="checkbox" checked={consent} disabled={disabled} onChange={event => setConsent(event.target.checked)} />Я понимаю, что качество пока не подтверждено, и запускаю экспериментальное обучение на своём голосе.</label>
    {!snapshot && <button className="primary" disabled={!canRun || !selected} onClick={() => void state.start(selected, consent)}>Начать проход</button>}
    {state.loading && <p role="status">Загружаем сохранённый прогресс…</p>}
    {snapshot && <>
      <article className="training-progress" aria-label="Прогресс дообучения VoxCPM2">
        <h3>{states[snapshot.state] ?? 'Состояние обновляется'}</h3>
        <p>Подтверждённая запись: {duration(snapshot.retainedDurationSeconds)} · фрагментов: {snapshot.corpusClipCount}.</p>
        <p>Принятые шаги: {snapshot.globalStep} · Полных проходов: {snapshot.completedEpochs} · Цель: {snapshot.targetCompletedEpochs}.</p>
        <p>Следующий фрагмент: {snapshot.nextRetainedOrdinal + 1} из {snapshot.corpusClipCount}.</p>
        {diagnostic && <p className="notice warning">{new ApiError(diagnostic).message} <code>{diagnostic}</code></p>}
        {state.active ? <><button disabled={state.pending || state.uncertain || snapshot.state === 'stop_requested'} onClick={() => void state.stop()}>Остановить</button><p className="subtle">Остановка — на безопасной границе. Закрытие вкладки не отменяет работу студии.</p></>
          : <div className="generation-actions"><button disabled={!canRun || !snapshot.canResume} onClick={() => void state.resume()}>{snapshot.state === 'completed' ? 'Ещё один проход' : 'Продолжить обучение'}</button>
            <button disabled={!canRun || !selected} onClick={() => void state.start(selected, consent)}>Новая линия обучения</button></div>}
      </article>
      {snapshot.models.length > 0 && <details className="training-versions workspace-extra"><summary>Сохранённые модели ({snapshot.models.length})</summary>
        <p className="subtle">Старые сохранения остаются в истории и доступны в выборе голоса. Перед озвучиванием студия проверит запись и выбранную модель.</p>
        <ol className="vox-model-history" tabIndex={0} aria-label="История сохранений VoxCPM2">{snapshot.models.map(m => <li key={m.id}>Сохранение {m.id.slice(0, 8)} · шаг {m.globalStep} · проходов {m.completedEpochs}<p className="subtle">Линия {m.trainingId.slice(0, 8)} · <time dateTime={m.createdUtc}>{new Date(m.createdUtc).toLocaleString('ru-RU')}</time></p></li>)}</ol>
      </details>}
    </>}
    {state.error && <p className="notice error" role="alert">{state.error}</p>}
    {state.uncertain && <><p className="notice warning">Ответ потерян. Повтор отправит ту же команду с прежним ключом; автоматически она не повторяется.</p><button disabled={state.pending} onClick={() => void state.retryUncertain()}>Повторить тот же запрос</button></>}
  </section>;
}
