import { useEffect, useState } from 'react';
import { ApiError, type StudioClient } from '../../api/client';
import type { SnapshotResponse, TtsCorpusClip, TtsCorpusState, TtsCorpusVersion } from '../../api/types';
import { corpusCheckingResume, useTtsCorpus } from './useTtsCorpus';

const states: Record<TtsCorpusState, string> = { queued: 'В очереди', preparing: 'Разделение записи', transcribing: 'Распознавание речи', stop_requested: 'Остановка запрошена', paused: 'Остановлено', completed: 'Черновик подготовлен', interrupted: 'Прервано при закрытии студии', failed: 'Подготовка остановлена с ошибкой' };
const phases: Record<string, string> = { queued: 'Ожидание вычислений', preparing: 'Разделение по паузам', prepare: 'Разделение по паузам', transcribing: 'Локальное распознавание', completed: 'Готов к проверке', paused: 'Остановлено', interrupted: 'Прервано' };
const safeCode = (code: string | null | undefined) => code && /^tts_corpus\.[a-zA-Z0-9_.-]{1,90}$/.test(code) ? code : null;
const duration = (seconds: number) => `${new Intl.NumberFormat('ru-RU', { maximumFractionDigits: 2 }).format(seconds)} с`;
const validText = (text: string) => { const normalized = text.trim().replace(/\s+/g, ' '); return normalized.length > 0 && normalized.length <= 4000; };
type Buffer = { text: string; excluded: boolean; dirty: boolean };
const bufferFor = (clip: TtsCorpusClip): Buffer => ({ text: clip.text, excluded: clip.excluded, dirty: false });

export function TtsCorpusPanel({ current, busy, onActiveChange, client, installProject, onVersionsChange }: { current: SnapshotResponse; busy: boolean; onActiveChange: (active: boolean) => void; client?: StudioClient; installProject?: (value: SnapshotResponse) => void; onVersionsChange?: (versions: TtsCorpusVersion[]) => void }) {
  const state = useTtsCorpus(current, client, installProject);
  const [experimental, setExperimental] = useState(false);
  const [checkingEngine, setCheckingEngine] = useState(false);
  const snapshot = state.corpus?.snapshot;
  const checkingResume = corpusCheckingResume(state.corpus);
  useEffect(() => { onVersionsChange?.(snapshot?.versions ?? []); }, [snapshot?.versions, onVersionsChange]);
  useEffect(() => { onActiveChange(state.active || state.pending); }, [state.active, state.pending, onActiveChange]);
  useEffect(() => () => onActiveChange(false), [onActiveChange]);
  const disabled = busy || state.loading || state.pending;
  const sourceMatches = !!snapshot && snapshot.sourceRecordingId === current.snapshot.voiceRecording?.artifactId;
  const canResume = !checkingResume && sourceMatches && snapshot?.canResume === true && ['paused', 'interrupted', 'failed'].includes(snapshot.state);
  const canStartFresh = !checkingResume && sourceMatches && snapshot?.canResume === false && ['paused', 'interrupted', 'failed'].includes(snapshot.state);
  async function prepare(run: () => Promise<unknown>) {
    setCheckingEngine(true);
    try { await run(); } finally { setCheckingEngine(false); }
  }
  return <section className="panel training-panel" aria-labelledby="tts-corpus-heading" hidden={!current.snapshot.voiceRecording && !snapshot}>
    <h2 id="tts-corpus-heading">Подготовка записи</h2>
    {!snapshot && <p>Студия разделит запись на фрагменты и распознает речь. Проверьте распознанный текст, чтобы перейти к дообучению.</p>}
    {state.loading && <p role="status">Загружаем состояние записи…</p>}
    {checkingEngine && <p className="training-check" role="status">Проверяем файлы распознавания перед запуском. Это может занять пару минут.</p>}
    {!current.snapshot.voiceRecording && <p className="notice warning">Сначала сохраните исходную запись в разделе «Голос».</p>}
    <label className="check-label"><input type="checkbox" checked={experimental} disabled={disabled || state.active} onChange={event => setExperimental(event.target.checked)} />Подтверждаю экспериментальную подготовку: распознанный текст и границы клипов нужно проверить на слух.</label>
    {!snapshot && <button className="primary" disabled={disabled || !experimental || !current.snapshot.voiceRecording} onClick={() => void prepare(state.start)}>Подготовить запись для обучения VoxCPM2</button>}
    {snapshot && <>
      <article className="training-progress" aria-label="Прогресс подготовки корпуса">
        <div className="section-heading"><h3>{states[snapshot.state] ?? 'Состояние корпуса обновляется'}</h3><span className="state-badge">{phases[snapshot.phase] ?? 'Локальная обработка'}</span></div>
        <p>Готовые клипы: {snapshot.completedClipCount}{snapshot.totalClipCount === null ? ' · общее число пока неизвестно' : ` из ${snapshot.totalClipCount}`}</p>
        <p>Сохранённая длительность: {duration(snapshot.retainedDurationSeconds)} · только включённые принятые клипы.</p>
        <p className="subtle">Исходная запись: {duration(snapshot.sourceDurationSeconds)}. Длительность обновится после сохранения исключений.</p>
        {checkingResume && <p className="training-check" role="status">Проверяем, можно ли продолжить подготовку записи.</p>}
        {!checkingResume && safeCode(snapshot.diagnosticCode) && <p className="notice warning">{new ApiError(snapshot.diagnosticCode!).message} Диагностика: <code>{safeCode(snapshot.diagnosticCode)}</code></p>}
        {state.active ? <><button disabled={state.pending || snapshot.state === 'stop_requested'} onClick={() => void state.control('stop')}>Остановить подготовку</button><p className="subtle">Остановка выполняется после текущего клипа. Закрытие вкладки не отменяет подготовку, пока работает студия.</p></> : <>
          <button disabled={disabled || !experimental || !canResume} onClick={() => void prepare(() => state.control('resume'))}>Продолжить подготовку</button>
          {!checkingResume && !canResume && snapshot.state !== 'completed' && <p className="subtle">Продолжение недоступно: нужны та же запись, план и локальный движок. Сохранённые клипы можно проверить.</p>}
          {canStartFresh && <><p className="subtle">При новой подготовке неподтверждённый черновик будет заменён. Подтверждённые версии сохранятся.</p><button disabled={disabled || !experimental} onClick={() => void prepare(state.start)}>Начать подготовку заново</button></>}
        </>}
      </article>
      {!sourceMatches && <p className="notice warning">Корпус относится к другой исходной записи. Правки и подтверждение недоступны.</p>}
      <CorpusEditor key={snapshot.id} snapshot={snapshot} state={state} disabled={disabled || state.active || !sourceMatches} />
      {snapshot.versions.length > 0 && <details className="training-versions workspace-extra"><summary>Подтверждённые версии записи ({snapshot.versions.length})</summary><ol>{snapshot.versions.map((version, index) => <li key={version.id}><strong>Версия корпуса {index + 1}</strong> · клипов: {version.clipCount} · {duration(version.retainedDurationSeconds)}<p><time dateTime={version.createdUtc}>{new Date(version.createdUtc).toLocaleString('ru-RU')}</time></p><p className="subtle">SHA-256: <code className="corpus-hash">{version.sha256}</code></p></li>)}</ol></details>}
    </>}
    {state.error && <p className="notice error" role="alert">{state.error}</p>}
  </section>;
}

function CorpusEditor({ snapshot, state, disabled }: { snapshot: NonNullable<ReturnType<typeof useTtsCorpus>['corpus']>['snapshot']; state: ReturnType<typeof useTtsCorpus>; disabled: boolean }) {
  const [buffers, setBuffers] = useState<Record<string, Buffer>>({});
  const [confirmed, setConfirmed] = useState(false);
  useEffect(() => {
    setBuffers(previous => Object.fromEntries(snapshot.clips.map(clip => [clip.id, previous[clip.id]?.dirty ? previous[clip.id] : bufferFor(clip)])));
  }, [snapshot]);
  useEffect(() => { setConfirmed(false); }, [snapshot.id, snapshot.revision]);
  function change(clip: TtsCorpusClip, value: Partial<Buffer>) {
    setBuffers(previous => {
      const next = { ...(previous[clip.id] ?? bufferFor(clip)), ...value };
      next.dirty = next.text !== clip.text || next.excluded !== clip.excluded;
      return { ...previous, [clip.id]: next };
    });
    setConfirmed(false);
  }
  async function save(clip: TtsCorpusClip, review: boolean) {
    const buffer = buffers[clip.id] ?? bufferFor(clip);
    const result = await state.updateClip(clip.id, { text: buffer.text, excluded: buffer.excluded, reviewed: review });
    const saved = result?.snapshot.clips.find(value => value.id === clip.id);
    if (saved) setBuffers(previous => ({ ...previous, [clip.id]: bufferFor(saved) }));
    setConfirmed(false);
  }
  const dirty = snapshot.clips.some(clip => buffers[clip.id]?.dirty);
  const included = snapshot.clips.filter(clip => !clip.excluded);
  const approvable = !dirty && included.length > 0 && included.every(clip => clip.reviewed && validText(clip.text));
  return <div>
    <h3>Проверка клипов</h3><p>Прослушайте каждый клип целиком. Исправьте дословный текст, сохраните правку и затем отдельно подтвердите клип. Изменение текста или исключения снимает проверку.</p>
    <p className="subtle">Метки слов приблизительные и не доказывают точность границ. Проверьте начало и конец речи на слух.</p>
    {snapshot.clips.length === 0 && <p className="notice warning">Пока недостаточно данных для подтверждения: нет готовых клипов.</p>}
    <ol className="segment-list">{snapshot.clips.map(clip => {
      const buffer = buffers[clip.id] ?? bufferFor(clip);
      const number = clip.index + 1;
      return <li className="segment" key={clip.id}>
        <div className="section-heading"><h4>Клип {number} · {duration((clip.endSample - clip.startSample) / 16000)}</h4><span className="state-badge">{buffer.dirty ? 'Есть несохранённые правки' : clip.excluded ? 'Исключён' : clip.reviewed ? 'Проверен' : 'Нужна проверка'}</span></div>
        <audio controls preload="none" aria-label={`Аудио клипа ${number}`} src={state.audioUrl(clip.id)} />
        {clip.words.some(word => word.end === null) && <p className="notice warning">Конец последнего слова неизвестен. Прослушайте конец клипа и убедитесь, что речь не обрезана.</p>}
        {clip.warningCodes.filter(code => safeCode(code)).map(code => <p className="notice warning" key={code}>Проверьте соответствие текста звуку и границы клипа. Диагностика: <code>{code}</code></p>)}
        <label htmlFor={`corpus-text-${clip.id}`}>Текст клипа {number}</label><textarea id={`corpus-text-${clip.id}`} disabled={disabled} value={buffer.text} onChange={event => change(clip, { text: event.target.value })} />
        {!validText(buffer.text) && !buffer.excluded && <p className="notice warning">Проверьте текст: нужен непустой дословный текст до 4000 символов после объединения пробелов. Черновик остаётся доступным для правки.</p>}
        <label className="check-label"><input type="checkbox" disabled={disabled} checked={buffer.excluded} onChange={event => change(clip, { excluded: event.target.checked })} />Исключить клип {number}</label>
        <div className="generation-actions"><button disabled={disabled || !buffer.dirty || (!buffer.excluded && !validText(buffer.text))} onClick={() => void save(clip, false)}>Сохранить клип {number}</button><button disabled={disabled || buffer.dirty || buffer.excluded || clip.reviewed || !validText(buffer.text)} onClick={() => void save(clip, true)}>Подтвердить клип {number}</button></div>
      </li>;
    })}</ol>
    {!approvable && snapshot.clips.length > 0 && <p className="notice warning">Перед подтверждением версии сохраните все правки и проверьте каждый включённый клип. Нужен хотя бы один включённый клип с текстом.</p>}
    <label className="check-label"><input type="checkbox" checked={confirmed} disabled={disabled} onChange={event => setConfirmed(event.target.checked)} />Подтверждаю дословный текст и границы всех включённых клипов после прослушивания.</label>
    <button className="primary" disabled={disabled || !confirmed || !approvable} onClick={() => void state.approve()}>Подтвердить версию корпуса</button>
  </div>;
}
