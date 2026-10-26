import { useEffect, useState } from 'react';
import { ApiError, type StudioClient } from '../../api/client';
import type { SnapshotResponse, TrainingReadiness, TrainingSnapshot, TrainingState } from '../../api/types';
import { useTraining } from './useTraining';

const states: Record<TrainingState, string> = { queued: 'В очереди', preparing: 'Подготовка записи', training: 'Обучение', stop_requested: 'Остановка запрошена', paused: 'Остановлено', completed: 'Эпоха завершена', interrupted: 'Прервано при закрытии студии', failed: 'Обучение остановлено с ошибкой' };
const phases: Record<string, string> = { queued: 'Ожидание вычислений', preparing: 'Подготовка записи', prepare: 'Подготовка записи', workflow_preparing: 'Подготовка полной записи', prepared: 'Запись подготовлена', features: 'Извлечение признаков', input_verified: 'Извлечение признаков', corpus_saved: 'Набор подготовлен', admission: 'Проверка обучающих данных', native_training: 'Обучение модели', training: 'Обучение модели', workflow_training: 'Запуск обучения', session_preparing: 'Загрузка данных', session_batch_ready: 'Загрузка данных и модели', session_state_ready: 'Модель загружена', session_update_started: 'Обучение модели', session_update_complete: 'Обучение модели', session_save_started: 'Сохранение модели', session_save_complete: 'Сохранение модели', completed: 'Эпоха завершена', paused: 'Остановлено', idle: 'Ожидание команды' };
const diagnostic = (code: string | null | undefined) => code && /^[a-zA-Z0-9_.-]{1,100}$/.test(code) ? code : null;

export function TrainingPanel({ current, busy, onActiveChange, onSnapshotChange, client, installProject }: { current: SnapshotResponse; busy: boolean; onActiveChange: (active: boolean) => void; onSnapshotChange?: (snapshot: TrainingSnapshot | null, readiness: TrainingReadiness | undefined) => void; client?: StudioClient; installProject?: (value: SnapshotResponse) => void }) {
  const state = useTraining(current, client, installProject);
  const [confirmed, setConfirmed] = useState(false);
  const snapshot = state.training?.snapshot;
  useEffect(() => { onActiveChange(state.active || state.pending); }, [state.active, state.pending, onActiveChange]);
  useEffect(() => () => onActiveChange(false), [onActiveChange]);
  useEffect(() => { onSnapshotChange?.(snapshot ?? null, state.readiness); }, [snapshot, state.readiness, onSnapshotChange]);
  const sourceMatches = snapshot?.sourceRecordingId === current.snapshot.voiceRecording?.artifactId;
  const ready = state.readiness?.ready === true;
  const disabled = busy || state.loading || state.pending;
  const resume = snapshot?.canResume === true && sourceMatches && ['paused', 'completed', 'interrupted', 'failed'].includes(snapshot.state);
  const repeatPreparation = snapshot && !state.active && !snapshot.canResume && snapshot.models.length === 0;
  return <section className="panel training-panel" aria-labelledby="training-heading">
    <div className="section-heading"><div><p className="eyebrow">ПОСТОЯННЫЙ ГОЛОС · ЭКСПЕРИМЕНТ</p><h2 id="training-heading">Обучение на полной записи</h2></div><span className="state-badge">CPU · 2 потока</span></div>
    <p>Обучение сохраняет версии личной модели на этом компьютере. Каждый запуск выполняет обучение до следующей полной эпохи. Продолжение использует ту же исходную запись.</p>
    {!state.readiness ? <p role="status">Проверяем локальный движок обучения…</p> : !ready ? <p className="notice warning">Локальный движок обучения не готов.{diagnostic(state.readiness.diagnosticCode) && <> Диагностика: <code>{diagnostic(state.readiness.diagnosticCode)}</code>.</>}</p> : <p className="subtle">Локальный движок обучения готов · экспериментальный режим.</p>}
    {!current.snapshot.voiceRecording && <p className="notice warning">Сначала сохраните исходную запись голоса в этом проекте.</p>}
    <label className="check-label"><input type="checkbox" checked={confirmed} disabled={disabled || state.active} onChange={event => setConfirmed(event.target.checked)} />Подтверждаю экспериментальное обучение: сохранённая версия не гарантирует качество голоса.</label>
    {!snapshot && <button className="primary" disabled={disabled || !ready || !confirmed || !current.snapshot.voiceRecording} onClick={() => void state.start()}>Обучить голос</button>}
    {snapshot && <article className="training-progress" aria-label="Прогресс обучения">
      <div className="section-heading"><h3>{states[snapshot.state]}</h3><span className="state-badge">{phases[snapshot.phase] ?? 'Локальная обработка'}</span></div>
      <dl className="training-metrics"><div><dt>Глобальный шаг:</dt><dd>{snapshot.globalStep}</dd></div><div><dt>Завершённые эпохи:</dt><dd>{snapshot.completedEpoch}</dd></div><div><dt>Шагов в эпохе:</dt><dd>{snapshot.epochSize}</dd></div></dl>
      {diagnostic(snapshot.diagnosticCode) && <p className="notice warning">{['training.time_budget', 'training.checkpoint_budget'].includes(snapshot.diagnosticCode!) && <>{new ApiError(snapshot.diagnosticCode!).message} </>}Диагностика: <code>{diagnostic(snapshot.diagnosticCode)}</code></p>}
      {state.active && <><button disabled={state.pending || snapshot.state === 'stop_requested'} onClick={() => void state.control('stop')}>Остановить обучение</button><p className="subtle">Остановка выполняется безопасно после текущей операции. Закрытие вкладки не останавливает обучение.</p></>}
      {!state.active && <><button disabled={disabled || !ready || !confirmed || !resume} onClick={() => void state.control('resume')}>{snapshot.state === 'completed' ? 'Обучить следующую эпоху' : 'Продолжить обучение'}</button>{!resume && <p className="subtle">Продолжение недоступно: нужна принятая версия модели и та же исходная запись.</p>}</>}
      {repeatPreparation && <><button disabled={disabled || !ready || !confirmed || !sourceMatches} onClick={() => void state.start()}>Повторить подготовку</button><p className="subtle">Принятая версия модели ещё не сохранена. Повторная подготовка заново обработает ту же исходную запись и запустит обучение.</p></>}
    </article>}
    {snapshot && snapshot.models.length > 0 && <div className="training-versions"><h3>Сохранённые версии модели</h3><ol>{snapshot.models.map((model, index) => <li key={model.id}><strong>Версия {index + 1}</strong> · шаг {model.globalStep} · эпоха {model.completedEpoch}<p className="subtle">Качество голоса ещё не подтверждено.{model.selectable !== true && ' Эта старая версия недоступна для озвучивания.'}</p></li>)}</ol></div>}
    {state.error && <p className="notice error" role="alert">{state.error}</p>}
    <p className="subtle">Для другой исходной записи создайте новый проект и новую модель. После перезапуска студии обучение продолжается только по вашей команде.</p>
    <p className="notice training-boundary">Озвучивание нового текста использует короткий подтверждённый образец и его точный текст. Доступную сохранённую версию можно выбрать в разделе «Озвучивание»: она экспериментально преобразует голос после озвучивания текста.</p>
  </section>;
}
