import { useEffect, useMemo, useState } from 'react';
import { StudioClient } from '../../api/client';
import type { SavedVoiceSelection, SnapshotResponse, TrainingReadiness, TrainingSnapshot, VoxCpmGenerationInfo, VoxCpmGenerationOptions, VoxCpmTrainingSnapshot } from '../../api/types';
import { JobProgress } from './JobProgress';
import { terminal, type GenerationState } from './useProjectEvents';
import { VoxCpmControls, useVoxCpmReferences } from './VoxCpmControls';
import { latestSuccessfulVoxSettings, loadVoxDraft, saveVoxDraft, validVoxOptions } from './voxCpmOptions';

type Props = { current: SnapshotResponse; generation: GenerationState; busy: boolean; training?: TrainingSnapshot | null; trainingReadiness?: TrainingReadiness; voxTraining?: VoxCpmTrainingSnapshot | null; client?: StudioClient };
export function GenerationPanel(props: Props) {
  return <GenerationPanelContent key={props.current.snapshot.id} {...props} />;
}
function GenerationPanelContent({ current, generation, busy, training, trainingReadiness, voxTraining, client }: Props) {
  const { snapshot } = current;
  const api = useMemo(() => client ?? new StudioClient(), [client]);
  const [draft] = useState(() => loadVoxDraft(snapshot.id));
  const [chosen, setChosen] = useState(() => snapshot.segments.map(segment => segment.id));
  const ids = chosen.filter(id => snapshot.segments.some(segment => segment.id === id));
  const [preview, setPreview] = useState(snapshot.segments[0]?.id ?? '');
  const previewId = snapshot.segments.some(segment => segment.id === preview) ? preview : snapshot.segments[0]?.id;
  const [selectedModelId, setSelectedModelId] = useState(draft.modelId);
  const [options, setOptions] = useState<VoxCpmGenerationOptions>(draft.options);
  const [selectionKnown, setSelectionKnown] = useState(draft.selectionKnown);
  const [restored, setRestored] = useState(false);
  const [modelConfirmed, setModelConfirmed] = useState(false);
  useEffect(() => { if (selectionKnown) saveVoxDraft(snapshot.id, selectedModelId, options); }, [snapshot.id, selectedModelId, options, selectionKnown]);
  const versions = training?.projectId === snapshot.id && training.sourceRecordingId === snapshot.voiceRecording?.artifactId ? training.models : [];
  const voxVersions = voxTraining?.projectId === snapshot.id ? voxTraining.models : [];
  const isVox = selectedModelId.startsWith('voxcpm:');
  const selectedVox = isVox ? voxVersions.find(model => `voxcpm:${model.id}` === selectedModelId) : undefined;
  const references = useVoxCpmReferences(api, snapshot.id, selectedVox?.id);
  const referenceCatalog = references?.value;
  const referenceReady = !options.useReference || !!referenceCatalog?.clips.some(clip => clip.id === (options.referenceClipId ?? referenceCatalog.defaultClipId));
  const selectedModel = isVox ? selectedVox : versions.find(model => model.id === selectedModelId && model.selectable === true);
  const active = generation.jobs.some(job => !terminal(job));
  useEffect(() => {
    if (selectionKnown || active || busy || generation.pending) return;
    const previous = latestSuccessfulVoxSettings(generation.jobs, snapshot.id, new Set(voxVersions.map(model => model.id)));
    if (!previous) return;
    setSelectedModelId(`voxcpm:${previous.modelId}`); setOptions(previous.options); setSelectionKnown(true); setRestored(true);
  }, [selectionKnown, active, busy, generation.pending, generation.jobs, snapshot.id, voxVersions]);
  const ready = (isVox ? generation.voxReadiness?.ready === true : generation.readiness?.ready) && !!snapshot.voiceAnchor && !!snapshot.textDocument;
  const savedReady = !selectedModelId || (!!selectedModel && modelConfirmed && (isVox ? validVoxOptions(options) && referenceReady : trainingReadiness?.ready === true));
  const disabled = busy || generation.pending || active || !ready || !savedReady;
  const choiceDisabled = busy || generation.pending || active;
  const voice: SavedVoiceSelection | undefined = selectedModelId ? isVox
    ? { savedVoxCpmModelId: selectedModelId.slice('voxcpm:'.length), experimentalConfirmed: modelConfirmed, voxCpmOptions: { ...options } }
    : { savedModelId: selectedModelId, experimentalConfirmed: modelConfirmed } : undefined;
  function reuse(info: VoxCpmGenerationInfo) {
    if (!validVoxOptions(info.options)) return;
    setSelectedModelId(`voxcpm:${info.modelId}`); setOptions({ ...info.options }); setModelConfirmed(false); setSelectionKnown(true); setRestored(false);
  }
  const bookReady = snapshot.segments.length > 0 && snapshot.segments.length <= 256 && snapshot.segments.every(segment => !!segment.outputArtifactId);
  return <section className="panel generation-panel" aria-labelledby="generation-heading">
    <div className="section-heading"><h2 id="generation-heading">Озвучивание</h2><span className="state-badge">Локально</span></div>
    {isVox ? <><p className="subtle">VoxCPM2 · сохранённая модель · CPU · экспериментальный режим</p>
      {!generation.voxReadiness ? <p role="status">Проверяем движок синтеза VoxCPM2…</p> : generation.voxReadiness.ready ? <p className="subtle">Движок синтеза VoxCPM2 готов. Готовность повторно проверяется при запуске.</p> : <p className="notice warning">Локальный движок синтеза VoxCPM2 пока недоступен. Проверьте его настройку; другое озвучивание автоматически не запускается.</p>}
      <p className="subtle">Для запуска требуется не менее 16 ГиБ свободной оперативной памяти. Лимит процесса — 13 ГиБ, резерв для системы — 3 ГиБ; проверка памяти выполняется перед синтезом.</p></>
      : !generation.readiness ? <p role="status">Проверяем движок озвучивания…</p> : !generation.readiness.ready ? <p className="notice">Движок озвучивания не настроен. Генерация станет доступна после установки и проверки локальной модели.</p> : <p className="subtle">Движок подключён · {generation.readiness.profileId === 'q8-f16-cpu' ? 'CPU' : 'CPU + GPU'}{generation.readiness.experimental ? ' · экспериментальный режим' : ''}</p>}
    <p>Озвучивается сохранённый произносимый текст. Сначала сохраните правки и подтвердите голосовой фрагмент.</p>
    {isVox && <p className="subtle">Для запуска пока также нужен подтверждённый короткий образец голоса 10–20 секунд в подготовке проекта, даже при использовании клипа из корпуса.</p>}
    <label htmlFor="generation-voice">Голос для озвучивания</label><select id="generation-voice" value={selectedModelId} disabled={choiceDisabled} onChange={event => { setSelectedModelId(event.target.value); setModelConfirmed(false); setOptions(previous => ({ ...previous, referenceClipId: null })); setSelectionKnown(true); setRestored(false); }}>
      <option value="">Короткий подтверждённый образец</option>
      {versions.map((model, index) => <option key={model.id} value={model.id} disabled={model.selectable !== true}>Версия {index + 1} · шаг {model.globalStep} · эпоха {model.completedEpoch}{model.selectable === true ? '' : ' · недоступна для озвучивания'}</option>)}
      {voxVersions.length > 0 && <optgroup label="VoxCPM2 — обученный голос">{voxVersions.map(model => <option key={model.id} value={`voxcpm:${model.id}`}>VoxCPM2 · шаг {model.globalStep} · проходов {model.completedEpochs} · {model.id.slice(0, 8)}</option>)}</optgroup>}
      {selectedModelId && !(isVox ? selectedVox : versions.some(model => model.id === selectedModelId)) && <option value={selectedModelId} disabled>Выбранная версия недоступна</option>}
    </select>
    {restored && <p className="subtle" role="status">Восстановлены настройки последней успешной пробы. Новое озвучивание запустится только по вашей команде.</p>}
    {!!selectedModelId && <>
      <p className="notice">{isVox ? 'Сохранённая модель VoxCPM2 использует обученные изменения и, по вашему выбору, опорный фрагмент из той же подтверждённой записи, чтобы закрепить тембр. Сначала прослушайте пробу: сходство голоса пока не подтверждено.' : 'Сначала текст озвучивается по короткому образцу, затем сохранённая модель RVC преобразует голос. Всё ещё нужен подтверждённый фрагмент 10–20 секунд и его точный текст. Качество результата не проверено; начните с пробы.'}</p>
      <label className="check-label"><input type="checkbox" checked={modelConfirmed} disabled={choiceDisabled} onChange={event => setModelConfirmed(event.target.checked)} />Подтверждаю экспериментальное применение сохранённой модели: качество голоса не гарантируется.</label>
      {!selectedModel && <p className="notice warning">Выбранная версия недоступна для озвучивания. Выберите другую версию или короткий образец.</p>}
      {!isVox && trainingReadiness?.ready !== true && <p className="notice warning">Для сохранённой модели нужен готовый локальный движок обучения и преобразования голоса.</p>}
    </>}
    {isVox && <VoxCpmControls projectId={snapshot.id} modelId={selectedModelId.slice('voxcpm:'.length)} options={options} onChange={value => { setOptions(value); setSelectionKnown(true); setRestored(false); }} disabled={choiceDisabled} references={references} />}
    {snapshot.segments.length > 0 && <>
      <label htmlFor="preview-segment">Фрагмент для пробы</label><select id="preview-segment" value={previewId ?? ''} onChange={event => setPreview(event.target.value)} disabled={busy || generation.pending}>
        {snapshot.segments.map((segment, index) => <option key={segment.id} value={segment.id}>Фрагмент {index + 1}</option>)}
      </select>
      <fieldset className="generation-selection"><legend>Фрагменты для озвучивания</legend>{snapshot.segments.map((segment, index) => <label key={segment.id} className="check-row"><input type="checkbox" checked={ids.includes(segment.id)} disabled={busy || generation.pending} onChange={event => setChosen(previous => event.target.checked ? [...previous, segment.id] : previous.filter(id => id !== segment.id))} />Фрагмент {index + 1}</label>)}</fieldset>
    </>}
    <div className="generation-actions"><button disabled={disabled || !previewId} onClick={() => void generation.enqueue('preview', [previewId!], current.etag, voice)}>Проба голоса</button>
      <button disabled={disabled || !ids.length} onClick={() => void generation.enqueue('generation', ids, current.etag, voice)}>Озвучить выбранные фрагменты</button></div>
    {active && <p className="subtle">Сначала завершите или отмените текущее задание. Пауза действует после текущего фрагмента.</p>}
    <p className="subtle">Вкладку можно закрыть: задание продолжится, пока работает локальная студия. Готовые фрагменты сохраняются отдельно.</p>
    {bookReady && !busy && !generation.pending && !active ? <p><a className="book-download" href={`/api/projects/${snapshot.id}/book.wav?revision=${snapshot.revision}`} download="audiobook.wav">Скачать всю книгу · WAV</a><span className="subtle"> — все фрагменты в порядке текста, без дополнительной обработки.</span></p>
      : <p className="subtle">{snapshot.segments.length > 256 ? 'Для одного WAV поддерживается до 256 фрагментов.' : bookReady ? 'Скачивание книги станет доступно после завершения текущего задания.' : 'Для скачивания всей книги сначала озвучьте все фрагменты.'}</p>}
    {generation.error && <div className="notice error" role="alert">{generation.error}</div>}
    {generation.jobs.length > 0 && <><p className="subtle">История показывает последние 32 завершённых задания и все незавершённые задания. Параметры ниже сохранены вместе с каждым заданием.</p><div className="job-list">{generation.jobs.map(job => <JobProgress key={job.id} job={job} pending={generation.pending || busy} control={generation.control} reuse={reuse} reuseDisabled={choiceDisabled} />)}</div></>}
    {generation.jobs.length > 0 && !generation.connected && <p className="subtle">Подключаем обновления прогресса. Закрытие вкладки не отменяет задание.</p>}
  </section>;
}
