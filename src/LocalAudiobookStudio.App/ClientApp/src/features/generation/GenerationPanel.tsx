import { useEffect, useImperativeHandle, useMemo, useRef, useState, type ReactNode, type Ref } from 'react';
import { bookWavHref, chaptersZipHref, maxWavFragments } from '../../api/bookDownload';
import { StudioClient } from '../../api/client';
import type { JobSnapshot, SavedVoiceSelection, Segment, SnapshotResponse, TrainingReadiness, TrainingSnapshot, VoxCpmGenerationInfo, VoxCpmGenerationOptions, VoxCpmTrainingSnapshot } from '../../api/types';
import { JobProgress } from './JobProgress';
import { ReadyAudio } from './ReadyAudio';
import { generationHistoryLimit, terminal, type GenerationState } from './useProjectEvents';
import { VoxCpmControls, useVoxCpmReferences } from './VoxCpmControls';
import { latestSuccessfulVoxSettings, loadVoxDraft, saveVoxDraft, validVoxOptions } from './voxCpmOptions';

export type GenerationSelectionHandle = { prepareFragment: (id: string) => void };
type Props = { current: SnapshotResponse; textDocumentId?: string; generation: GenerationState; busy: boolean; training?: TrainingSnapshot | null; trainingReadiness?: TrainingReadiness; voxTraining?: VoxCpmTrainingSnapshot | null; client?: StudioClient; bookRemainder?: { segmentIds: string[]; fragmentCount: number }; onOpenFragment?: (id: string) => void; selectionRef?: Ref<GenerationSelectionHandle>; setup?: { message: string; label: string; onClick: () => void }; textPicker?: ReactNode; bookExport?: ReactNode };
export function GenerationPanel(props: Props) {
  return <GenerationPanelContent key={props.current.snapshot.id} {...props} />;
}
function GenerationPanelContent({ current, textDocumentId, generation, busy, training, trainingReadiness, voxTraining, client, bookRemainder, onOpenFragment, selectionRef, setup, textPicker, bookExport }: Props) {
  const { snapshot } = current;
  const api = useMemo(() => client ?? new StudioClient(), [client]);
  const [draft] = useState(() => loadVoxDraft(snapshot.id));
  const [selectedModelId, setSelectedModelId] = useState(draft.modelId);
  const [options, setOptions] = useState<VoxCpmGenerationOptions>(draft.options);
  const [selectionKnown, setSelectionKnown] = useState(draft.selectionKnown);
  const [restored, setRestored] = useState(false);
  const [modelConfirmed, setModelConfirmed] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  useEffect(() => { if (selectionKnown) saveVoxDraft(snapshot.id, selectedModelId, options); }, [snapshot.id, selectedModelId, options, selectionKnown]);
  const versions = training?.projectId === snapshot.id && training.sourceRecordingId === snapshot.voiceRecording?.artifactId ? training.models : [];
  const voxVersions = voxTraining?.projectId === snapshot.id ? voxTraining.models : [];
  const isVox = selectedModelId.startsWith('voxcpm:');
  const selectedVox = isVox ? voxVersions.find(model => `voxcpm:${model.id}` === selectedModelId) : undefined;
  const references = useVoxCpmReferences(api, snapshot.id, selectedVox?.id);
  const referenceCatalog = references?.value;
  const referenceReady = !options.useReference || !!referenceCatalog?.clips.some(clip => clip.id === (options.referenceClipId ?? referenceCatalog.defaultClipId));
  const selectedModel = isVox ? selectedVox : versions.find(model => model.id === selectedModelId && model.selectable === true);
  const activeJobs = generation.jobs.filter(job => !terminal(job));
  const active = activeJobs.length > 0;
  const [latestJob, ...historyJobs] = generation.jobs.filter(terminal);
  useEffect(() => {
    if (selectionKnown || active || busy || generation.pending) return;
    const previous = latestSuccessfulVoxSettings(generation.jobs, snapshot.id, new Set(voxVersions.map(model => model.id)));
    if (!previous) return;
    setSelectedModelId(`voxcpm:${previous.modelId}`); setOptions(previous.options); setSelectionKnown(true); setRestored(true);
  }, [selectionKnown, active, busy, generation.pending, generation.jobs, snapshot.id, voxVersions]);
  const ready = (isVox ? generation.voxReadiness?.ready === true : generation.readiness?.ready) && !!snapshot.voiceAnchor && !!snapshot.textDocument;
  const savedReady = !selectedModelId || (!!selectedModel && modelConfirmed && (isVox ? validVoxOptions(options) && referenceReady : trainingReadiness?.ready === true));
  const choiceDisabled = busy || generation.pending || submitting || active;
  const disabled = choiceDisabled || !ready || !savedReady;
  const voice: SavedVoiceSelection | undefined = selectedModelId ? isVox
    ? { savedVoxCpmModelId: selectedModelId.slice('voxcpm:'.length), experimentalConfirmed: modelConfirmed, voxCpmOptions: { ...options } }
    : { savedModelId: selectedModelId, experimentalConfirmed: modelConfirmed } : undefined;
  function reuse(info: VoxCpmGenerationInfo) {
    if (!validVoxOptions(info.options)) return;
    setSelectedModelId(`voxcpm:${info.modelId}`); setOptions({ ...info.options }); setModelConfirmed(false); setSelectionKnown(true); setRestored(false);
  }
  async function enqueue(kind: 'preview' | 'generation', ids: string[]) {
    setSubmitting(true);
    try { await generation.enqueue(kind, ids, current.etag, voice); }
    finally { setSubmitting(false); }
  }
  function renderJob(job: JobSnapshot) {
    return <JobProgress key={job.id} job={job} pending={generation.pending || busy}
      control={generation.control} reuse={reuse} reuseDisabled={choiceDisabled} />;
  }
  const audioFragments = [...snapshot.segments].sort((left, right) => left.order - right.order)
    .flatMap((segment, index) => segment.outputArtifactId
      ? [{ segmentId: segment.id, artifactId: segment.outputArtifactId, number: index + 1 }]
      : []);
  const audioComplete = audioFragments.length > 0 && audioFragments.length === snapshot.segments.length;
  const splitExport = snapshot.segments.length > maxWavFragments;
  const canDownload = audioComplete && !choiceDisabled;
  const archiveTextId = (snapshot.textDocuments?.length ?? 0) > 1 ? textDocumentId : undefined;
  const download = !canDownload ? undefined : splitExport ? {
    href: chaptersZipHref(snapshot.id, snapshot.revision, archiveTextId),
    fileName: 'chapters.zip',
    label: archiveTextId ? 'Скачать выбранный текст частями · ZIP' : 'Скачать всю книгу частями · ZIP',
    note: `ZIP содержит пронумерованные WAV-файлы. В каждом — до ${maxWavFragments} фрагментов.`,
  } : {
    href: bookWavHref(snapshot.id, snapshot.revision, textDocumentId),
    fileName: 'audiobook.wav',
    label: textDocumentId ? 'Скачать выбранный текст · WAV' : 'Скачать всю книгу · WAV',
  };
  const exportMessage = audioComplete ? 'Скачивание станет доступно, когда студия завершит работу.'
    : splitExport ? 'ZIP будет доступен после озвучивания всех фрагментов.'
      : 'WAV будет доступен после озвучивания всех фрагментов.';
  return <section className="panel generation-panel" aria-labelledby="generation-heading">
    <h2 id="generation-heading" tabIndex={-1}>Озвучивание</h2>
    {active && <section className="generation-queue" aria-labelledby="generation-queue-heading">
      <h3 id="generation-queue-heading" tabIndex={-1}>Очередь озвучивания</h3>
      <div className="job-list">{activeJobs.map(renderJob)}</div>
      <p className="subtle">Пауза действует после текущего фрагмента. Озвучивание продолжится при закрытии вкладки, пока работает студия.</p>
      {!generation.connected && <p className="subtle">Подключаем обновления прогресса.</p>}
    </section>}
    {textPicker}
    {audioFragments.length > 0 && <ReadyAudio key={`ready-audio-${textDocumentId ?? 'single-text'}`} projectId={snapshot.id}
      fragments={audioFragments} fragmentCount={snapshot.segments.length} download={download} onOpenFragment={onOpenFragment} />}
    {bookExport}
    {setup ? <div className="workspace-next">
      <p>{setup.message}</p>
      <button className="primary" type="button" onClick={setup.onClick}>{setup.label}</button>
    </div> : <>
      {isVox
        ? !generation.voxReadiness ? <p role="status">Проверяем движок синтеза VoxCPM2…</p>
          : !generation.voxReadiness.ready && <p className="notice warning">Локальный движок синтеза VoxCPM2 пока недоступен. Проверьте его настройку.</p>
        : !generation.readiness ? <p role="status">Проверяем движок озвучивания…</p>
          : !generation.readiness.ready && <p className="notice warning">Движок озвучивания не настроен. Генерация станет доступна после установки и проверки локальной модели.</p>}
      {!snapshot.voiceAnchor && <p>Подтвердите голосовой фрагмент перед озвучиванием.</p>}
      <label htmlFor="generation-voice">Голос для озвучивания</label><select id="generation-voice" value={selectedModelId} disabled={choiceDisabled} onChange={event => { setSelectedModelId(event.target.value); setModelConfirmed(false); setOptions(previous => ({ ...previous, referenceClipId: null })); setSelectionKnown(true); setRestored(false); }}>
        <option value="">Короткий подтверждённый образец</option>
        {versions.map((model, index) => <option key={model.id} value={model.id} disabled={model.selectable !== true}>Версия {index + 1} · шаг {model.globalStep} · эпоха {model.completedEpoch}{model.selectable === true ? '' : ' · недоступна для озвучивания'}</option>)}
        {voxVersions.length > 0 && <optgroup label="VoxCPM2 — обученный голос">{voxVersions.map(model => <option key={model.id} value={`voxcpm:${model.id}`}>VoxCPM2 · шаг {model.globalStep} · проходов {model.completedEpochs} · {model.id.slice(0, 8)}</option>)}</optgroup>}
        {selectedModelId && !(isVox ? selectedVox : versions.some(model => model.id === selectedModelId)) && <option value={selectedModelId} disabled>Выбранная версия недоступна</option>}
      </select>
      {restored && <p className="subtle" role="status">Восстановлены настройки последней успешной пробы. Новое озвучивание запустится только по вашей команде.</p>}
      {!!selectedModelId && <>
        <p className="notice">{isVox ? 'Сохранённая модель VoxCPM2 использует обученные изменения и, по вашему выбору, опорный фрагмент из той же подтверждённой записи, чтобы закрепить тембр. Сначала прослушайте пробу: сходство голоса пока не подтверждено.' : 'Сначала текст озвучивается по короткому образцу, затем сохранённая модель RVC преобразует голос. Начните с пробы, чтобы проверить звучание.'}</p>
        <label className="check-label"><input type="checkbox" checked={modelConfirmed} disabled={choiceDisabled} onChange={event => setModelConfirmed(event.target.checked)} />Подтверждаю экспериментальное применение сохранённой модели: качество голоса не гарантируется.</label>
        {!selectedModel && <p className="notice warning">Выбранная версия недоступна для озвучивания. Выберите другую версию или короткий образец.</p>}
        {!isVox && trainingReadiness?.ready !== true && <p className="notice warning">Для сохранённой модели нужен готовый локальный движок обучения и преобразования голоса.</p>}
      </>}
      {isVox && <VoxCpmControls projectId={snapshot.id} modelId={selectedModelId.slice('voxcpm:'.length)} options={options} onChange={value => { setOptions(value); setSelectionKnown(true); setRestored(false); }} disabled={choiceDisabled} references={references} />}
      <GenerationSelection key={textDocumentId ?? (snapshot.textDocument ? 'single-text' : 'no-text')} ref={selectionRef}
        segments={snapshot.segments} disabled={disabled} choiceDisabled={choiceDisabled} enqueue={enqueue} />
      {bookRemainder && bookRemainder.segmentIds.length > 0 && <div className="book-generation">
        <button type="button" disabled={disabled} aria-describedby="book-generation-count"
          onClick={() => void enqueue('generation', bookRemainder.segmentIds)}>Озвучить остаток книги</button>
        <p className="subtle" id="book-generation-count">Осталось озвучить {bookRemainder.segmentIds.length} из {bookRemainder.fragmentCount} фрагментов во всех текстах.</p>
      </div>}
    </>}
    {submitting && !active && <p className="notice" role="status">{isVox
      ? 'Проверяем сохранённую модель VoxCPM2. Дождитесь появления задания в очереди.'
      : 'Готовим задание к запуску.'}</p>}
    {active && !setup && <p className="subtle"><a href="#generation-queue-heading">К очереди озвучивания</a></p>}
    {!setup && !canDownload && audioFragments.length > 0 && <p className="subtle">{exportMessage}</p>}
    {generation.error && <div className="notice error" role="alert">{generation.error}</div>}
    {latestJob && <section className="latest-generation-job" aria-labelledby="latest-generation-heading">
      <h3 id="latest-generation-heading">Последнее задание</h3>
      {renderJob(latestJob)}
    </section>}
    {historyJobs.length > 0 && <details className="generation-history" open={historyOpen} onToggle={event => {
      const panel = event.currentTarget;
      if (!panel.open) panel.querySelectorAll('audio').forEach(media => { if (!media.paused) media.pause(); });
      setHistoryOpen(panel.open);
    }}>
      <summary><span>История заданий</span><span className="count">{historyJobs.length}</span></summary>
      {historyOpen && <>
        {historyJobs.length + 1 === generationHistoryLimit && <p className="subtle">Показаны последние {generationHistoryLimit} завершённых задания, включая последнее задание выше.</p>}
        <div className="job-list">{historyJobs.map(renderJob)}</div>
      </>}
    </details>}
  </section>;
}

function GenerationSelection({ ref, segments, disabled, choiceDisabled, enqueue }: {
  ref?: Ref<GenerationSelectionHandle>; segments: Segment[]; disabled: boolean; choiceDisabled: boolean;
  enqueue: (kind: 'preview' | 'generation', ids: string[]) => Promise<void>;
}) {
  const selection = useRef<HTMLFieldSetElement>(null);
  const settings = useRef<HTMLDetailsElement>(null);
  const [chosen, setChosen] = useState<Set<string> | 'without-audio'>(() => new Set(segments.map(segment => segment.id)));
  const missingIds = segments.filter(segment => !segment.outputArtifactId).map(segment => segment.id);
  const selected = chosen === 'without-audio' ? new Set(missingIds) : chosen;
  const ids = segments.filter(segment => selected.has(segment.id)).map(segment => segment.id);
  const [preview, setPreview] = useState(segments[0]?.id ?? '');
  const previewId = segments.some(segment => segment.id === preview) ? preview : segments[0]?.id;
  useImperativeHandle(ref, () => ({
    prepareFragment(id: string) {
      if (choiceDisabled || !segments.some(segment => segment.id === id)) return;
      setChosen(new Set([id]));
      setPreview(id);
      if (settings.current) settings.current.open = true;
      selection.current?.focus({ preventScroll: true });
      selection.current?.scrollIntoView?.({ block: 'center' });
    },
  }), [choiceDisabled, segments]);

  function chooseSegment(id: string, checked: boolean) {
    const next = new Set(ids);
    if (checked) next.add(id);
    else next.delete(id);
    setChosen(next);
  }

  return <>
    <div className="generation-actions"><button disabled={disabled || !previewId} onClick={() => void enqueue('preview', [previewId!])}>Проба голоса</button>
      <button className="primary" disabled={disabled || !ids.length} onClick={() => void enqueue('generation', ids)}>Озвучить выбранные фрагменты</button></div>
    {segments.length > 0 && <details className="generation-fragments" ref={settings}>
      <summary>Выбор фрагментов <span className="subtle">· {ids.length} из {segments.length}</span></summary>
      <label htmlFor="preview-segment">Фрагмент для пробы</label><select id="preview-segment" value={previewId ?? ''} onChange={event => setPreview(event.target.value)} disabled={choiceDisabled}>
        {segments.map((segment, index) => <option key={segment.id} value={segment.id}>Фрагмент {index + 1}</option>)}
      </select>
      <fieldset ref={selection} tabIndex={-1} className="generation-selection" disabled={choiceDisabled}>
        <legend>Фрагменты для озвучивания</legend>
        <div className="generation-selection-toolbar">
          {missingIds.length > 0 && missingIds.length < segments.length && <button type="button" className="quiet" onClick={() => setChosen('without-audio')}>Выбрать без аудио</button>}
          {segments.length > 1 && <button type="button" className="quiet" disabled={ids.length === segments.length} onClick={() => setChosen(new Set(segments.map(segment => segment.id)))}>Выбрать все</button>}
          <span className="subtle" aria-live="polite">Выбрано {ids.length} из {segments.length}</span>
        </div>
        <div className="generation-selection-list">
          {segments.map((segment, index) => {
            const audioLabelId = segment.outputArtifactId ? `generation-audio-${segment.id}` : undefined;
            return <div className="generation-selection-item" key={segment.id}>
              <label className="check-row">
                <input type="checkbox" checked={selected.has(segment.id)} aria-describedby={audioLabelId}
                  onChange={event => chooseSegment(segment.id, event.target.checked)} />
                Фрагмент {index + 1}
              </label>
              {audioLabelId && <span className="subtle" id={audioLabelId}>Есть аудио</span>}
            </div>;
          })}
        </div>
      </fieldset>
    </details>}
  </>;
}
