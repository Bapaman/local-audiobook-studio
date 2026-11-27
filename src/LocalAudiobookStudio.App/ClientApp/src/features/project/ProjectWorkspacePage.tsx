import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, type Ref } from 'react';
import { bookWavHref, chaptersZipHref, maxWavFragments } from '../../api/bookDownload';
import type { StudioClient } from '../../api/client';
import type { SnapshotResponse, TrainingReadiness, TrainingSnapshot, TtsCorpusVersion, VoxCpmTrainingSnapshot } from '../../api/types';
import { VoiceSetup } from '../voice/VoiceSetup';
import { TextImport } from '../text/TextImport';
import { ProjectTextList } from '../text/ProjectTextList';
import { SegmentTable, type FragmentFocus, type SegmentTableHandle } from '../text/SegmentTable';
import { GenerationPanel, type GenerationSelectionHandle } from '../generation/GenerationPanel';
import { terminal, type GenerationState } from '../generation/useProjectEvents';
import { TrainingPanel } from '../training/TrainingPanel';
import { TtsCorpusPanel } from '../ttsCorpus/TtsCorpusPanel';
import { VoxCpmTrainingPanel } from '../voxcpmTraining/VoxCpmTrainingPanel';

export type Mutation = (suffix: string, body: unknown, method?: string) => Promise<void>;
type Props = { current: SnapshotResponse; generation: GenerationState; segmentRefresh: Record<string, number>; busy: boolean; headingRef: Ref<HTMLHeadingElement>; mutate: Mutation; onBack: () => void; client?: StudioClient; installProject?: (value: SnapshotResponse) => void; onDraftsChange?: (dirty: boolean) => void; voiceMessages?: boolean };
const screens = [
  { id: 'generation', label: 'Озвучивание', heading: 'generation-heading' },
  { id: 'texts', label: 'Тексты', heading: 'text-heading' },
  { id: 'voice', label: 'Голос', heading: 'voice-heading' },
  { id: 'training', label: 'Дообучение', heading: 'voice-training-heading' },
] as const;
type Screen = typeof screens[number]['id'];

export function ProjectWorkspacePage(props: Props) { return <ProjectWorkspaceContent key={props.current.snapshot.id} {...props} />; }
function ProjectWorkspaceContent({ current, generation, segmentRefresh, busy, headingRef, mutate, onBack, client, installProject, onDraftsChange, voiceMessages }: Props) {
  const snapshot = current.snapshot;
  const documents = snapshot.textDocuments ?? [];
  const [selectedId, setSelectedId] = useState(documents[0]?.id ?? '');
  const [dirty, setDirty] = useState(false);
  const updateDraftStatus = useCallback((pending: boolean) => {
    setDirty(pending);
    onDraftsChange?.(pending);
  }, [onDraftsChange]);
  const [screen, setScreen] = useState<Screen>('generation');
  const workspace = useRef<HTMLDivElement>(null);
  const focusScreen = useRef(false);
  const [fragmentToOpen, setFragmentToOpen] = useState<{ documentId: string; segmentId: string; focus: FragmentFocus }>();
  const [fragmentToGenerate, setFragmentToGenerate] = useState<string>();
  const segmentTable = useRef<SegmentTableHandle>(null);
  const generationSelection = useRef<GenerationSelectionHandle>(null);
  const previousDocuments = useRef({ ids: documents.map(document => document.id), hasText: !!snapshot.textDocument });
  const appendedId = useRef<string | undefined>(undefined);
  useEffect(() => {
    const ids = snapshot.textDocuments?.map(document => document.id) ?? [];
    const previous = previousDocuments.current;
    // A project with one text may omit textDocuments. Its next text is still an append.
    if (previous.hasText && ids.length > previous.ids.length && previous.ids.every((id, index) => ids[index] === id))
      appendedId.current = ids.at(-1);
    previousDocuments.current = { ids, hasText: !!snapshot.textDocument };
    // An externally appended text must not unmount an editor with an unsaved draft.
    if (!dirty && appendedId.current) { setSelectedId(appendedId.current); appendedId.current = undefined; }
  }, [snapshot.textDocuments, snapshot.textDocument, dirty]);
  const selected = documents.find(document => document.id === selectedId) ?? documents[0];
  const canChooseFragments = !!snapshot.voiceAnchor && !!(selected?.document ?? snapshot.textDocument);
  useLayoutEffect(() => {
    if (!focusScreen.current) return;
    focusScreen.current = false;
    const heading = workspace.current?.querySelector<HTMLElement>(`#${screens.find(item => item.id === screen)?.heading}`);
    heading?.focus({ preventScroll: true });
    heading?.scrollIntoView?.({ block: 'start' });
  }, [screen]);
  useLayoutEffect(() => {
    if (!fragmentToOpen || screen !== 'texts') return;
    if ((selected?.id ?? snapshot.id) === fragmentToOpen.documentId) segmentTable.current?.openFragment(fragmentToOpen.segmentId, fragmentToOpen.focus);
    setFragmentToOpen(undefined);
  }, [selected?.id, snapshot.id, fragmentToOpen, screen]);
  useLayoutEffect(() => {
    if (!fragmentToGenerate || screen !== 'generation' || !canChooseFragments) return;
    generationSelection.current?.prepareFragment(fragmentToGenerate);
    setFragmentToGenerate(undefined);
  }, [fragmentToGenerate, screen, canChooseFragments]);
  const orderedSegments = useMemo(() => [...snapshot.segments].sort((left, right) => left.order - right.order), [snapshot.segments]);
  const selectedSegments = useMemo(() => {
    if (!selected) return orderedSegments;
    const ids = new Set(selected.segmentIds);
    return orderedSegments.filter(segment => ids.has(segment.id));
  }, [selected, orderedSegments]);
  const selectedCurrent = { ...current, snapshot: { ...snapshot, textDocument: selected?.document ?? snapshot.textDocument, segments: selectedSegments } };
  const selectedSnapshot = selectedCurrent.snapshot;
  const [trainingActive, setTrainingActive] = useState(false);
  const [corpusActive, setCorpusActive] = useState(false);
  const [voxActive, setVoxActive] = useState(false);
  const [corpusVersions, setCorpusVersions] = useState<{ projectId: string; versions: TtsCorpusVersion[] }>({ projectId: snapshot.id, versions: [] });
  const installVersions = useCallback((versions: TtsCorpusVersion[]) => setCorpusVersions({ projectId: snapshot.id, versions }), [snapshot.id]);
  const refreshProject = useCallback(() => { if (client && installProject) void client.snapshot(`/api/projects/${encodeURIComponent(snapshot.id)}`).then(installProject).catch(() => {}); }, [client, installProject, snapshot.id]);
  const [training, setTraining] = useState<{ snapshot: TrainingSnapshot | null; readiness?: TrainingReadiness }>({ snapshot: null });
  const installTraining = useCallback((snapshot: TrainingSnapshot | null, readiness: TrainingReadiness | undefined) => setTraining({ snapshot, readiness }), []);
  const [voxTraining, setVoxTraining] = useState<VoxCpmTrainingSnapshot | null>(null);
  const generationActive = generation.jobs.some(job => !terminal(job));
  const editingBlocked = busy || trainingActive || corpusActive || voxActive || generation.pending || generationActive;
  const switchingBlocked = editingBlocked || dirty;
  const audioSegments = orderedSegments.filter(segment => segment.outputArtifactId);
  const audioSegmentIds = new Set(audioSegments.map(segment => segment.id));
  const reviewedSegmentIds = new Set(audioSegments.filter(segment => segment.audioReviewed).map(segment => segment.id));
  const correctionSegmentIds = new Set(orderedSegments.filter(segment => segment.correction).map(segment => segment.id));
  const nextReview = audioSegments.find(segment => !segment.audioReviewed && !segment.correction?.needsNewAudio);
  const reviewDocument = nextReview && documents.find(document => document.segmentIds.includes(nextReview.id));
  const nextCorrection = orderedSegments.find(segment => segment.correction?.needsNewAudio);
  const correctionDocument = nextCorrection && documents.find(document => document.segmentIds.includes(nextCorrection.id));
  const projectFragmentCount = snapshot.segments.length;
  const readyTextCount = documents.filter(document => document.segmentIds.length > 0
    && document.segmentIds.every(id => audioSegmentIds.has(id))).length;
  const bookRemainder = documents.length > 1 ? {
    segmentIds: orderedSegments.filter(segment => !segment.outputArtifactId).map(segment => segment.id),
    fragmentCount: projectFragmentCount,
  } : undefined;
  const bookComplete = projectFragmentCount > 0 && audioSegmentIds.size === projectFragmentCount
    && readyTextCount === documents.length;
  const wholeWavSupported = projectFragmentCount <= maxWavFragments;
  const hasSplitChapters = documents.some(document => document.segmentIds.length > maxWavFragments);
  const canExportBook = bookComplete && !switchingBlocked;
  const projectExportMessage = !bookComplete
    ? wholeWavSupported ? 'Общий WAV доступен после озвучивания всех текстов.'
      : 'Архив глав доступен после озвучивания всех текстов.'
    : dirty ? 'Сохраните или отмените правки перед скачиванием книги.'
      : editingBlocked ? 'Экспорт книги будет доступен после текущей операции.' : undefined;
  const totalFragments = selectedSnapshot.segments.length;
  const audioFragments = selected ? selected.segmentIds.filter(id => audioSegmentIds.has(id)).length : audioSegmentIds.size;
  const trainingInProgress = corpusActive || voxActive || trainingActive;

  function openScreen(next: Screen) {
    if (next === screen) return;
    workspace.current?.querySelectorAll<HTMLAudioElement>(`#workspace-${screen} audio`).forEach(audio => {
      if (!audio.paused) audio.pause();
    });
    focusScreen.current = true;
    setScreen(next);
  }

  function openProjectFragment(documentId: string | undefined, segmentId: string | undefined, focus: FragmentFocus) {
    if (switchingBlocked || !documentId || !segmentId) return;
    setSelectedId(documentId);
    setFragmentToOpen({ documentId, segmentId, focus });
  }

  const setup = !selectedSnapshot.textDocument
    ? { message: snapshot.hasAcceptedText ? 'Чтобы озвучить текст в старом формате, добавьте голос заново и загрузите исходный текст.' : 'Добавьте текст, который хотите озвучить.',
      label: snapshot.hasAcceptedText ? 'Открыть тексты' : 'Добавить текст', onClick: () => openScreen('texts') }
    : !snapshot.voiceAnchor
      ? { message: snapshot.hasAcceptedVoice ? 'Для озвучивания добавьте новый голос в списке «Все голоса» и подтвердите образец.' : 'Для озвучивания нужен короткий образец вашего голоса.',
        label: snapshot.hasAcceptedVoice ? 'Открыть голос' : snapshot.voiceRecording ? 'Подтвердить образец' : 'Добавить запись голоса', onClick: () => openScreen('voice') }
      : undefined;

  return <div className="workspace" ref={workspace}>
    <button className="back-link" disabled={busy || dirty}
      aria-describedby={dirty ? 'project-draft-status' : undefined} onClick={onBack}>← Все голоса</button>
    <header className="workspace-heading">
      <h1 ref={headingRef} tabIndex={-1} title={`Версия ${snapshot.revision}`}>{snapshot.displayName}</h1>
    </header>
    <nav className="workspace-nav" aria-label="Разделы голоса">
      {screens.map(item => <button type="button" key={item.id} aria-controls={`workspace-${item.id}`}
        aria-current={screen === item.id ? 'page' : undefined}
        aria-describedby={item.id === 'training' && trainingInProgress ? 'workspace-training-status' : undefined}
        onClick={() => openScreen(item.id)}>
        {item.label}
        {item.id === 'training' && trainingInProgress && <small id="workspace-training-status" aria-hidden="true">В работе</small>}
      </button>)}
    </nav>

    {dirty && <div className="workspace-draft notice" id="project-draft-status" role="status">
      Сохраните или отмените правки, чтобы сменить главу, начать озвучивание или вернуться к списку голосов.
      {screen !== 'texts' && <button type="button" onClick={() => openScreen('texts')}>Вернуться к правкам</button>}
    </div>}

    <div id="workspace-texts" className="workspace-screen" hidden={screen !== 'texts'}>
      <section className="panel" aria-labelledby="text-heading">
        <div className="section-heading workspace-section-heading">
          <h2 id="text-heading" tabIndex={-1}>Тексты этого голоса</h2>
          {selectedSnapshot.textDocument && <button type="button" onClick={() => openScreen('generation')}>Перейти к озвучиванию</button>}
        </div>
        {selected && documents.length > 1
          ? <>
            <div className="project-book-progress">
              <strong>Вся книга · готово {readyTextCount} из {documents.length} текстов</strong>
              <span className="subtle">Озвучено {audioSegmentIds.size} из {projectFragmentCount} фрагментов</span>
            </div>
            {audioSegments.length > 0 && <div className="project-book-review">
              <p className="subtle" role="status">Проверено по книге {reviewedSegmentIds.size} из {audioSegments.length}</p>
              {reviewDocument && <button type="button" disabled={switchingBlocked}
                aria-describedby={dirty ? 'project-draft-status' : undefined}
                onClick={() => openProjectFragment(reviewDocument.id, nextReview?.id, 'audio')}>К следующей записи на проверку</button>}
            </div>}
            {correctionSegmentIds.size > 0 && <div className="project-book-review">
              <p className="subtle" role="status">Замечаний по книге · {correctionSegmentIds.size}</p>
              {correctionDocument && <button type="button" disabled={switchingBlocked}
                aria-describedby={dirty ? 'project-draft-status' : undefined}
                onClick={() => openProjectFragment(correctionDocument.id, nextCorrection?.id, 'spoken')}>К следующему исправлению</button>}
            </div>}
            <ProjectTextList documents={documents} selectedId={selected.id} audioSegmentIds={audioSegmentIds} reviewedSegmentIds={reviewedSegmentIds}
              correctionSegmentIds={correctionSegmentIds}
              disabled={switchingBlocked} onSelect={setSelectedId} />
          </>
          : selectedSnapshot.textDocument && <p>Источник «{selectedSnapshot.textDocument.displayName}» сохранён.</p>}
        <TextImport key={snapshot.id} snapshot={selectedSnapshot} busy={editingBlocked} dirty={dirty} mutate={mutate} />
      </section>
      {selectedSnapshot.textDocument && <section className="panel text-preview" aria-labelledby="text-preview-heading">
        <h2 id="text-preview-heading">Фрагменты текста <span className="count">{totalFragments}</span></h2>
        <p className="editor-hint">Выделите гласную или поставьте курсор после неё и нажмите «Ударение», чтобы поставить или снять знак. Сохраните правку и выберите «К озвучиванию».</p>
        <details className="text-reference" key={`reference-${selected?.id ?? snapshot.id}`}>
          <summary>Сверить с исходным текстом</summary>
          <p className="subtle">При загрузке студия приводит пробелы и переносы строк к единому виду. Здесь можно посмотреть результат подготовки.</p>
          <div className="text-versions">
            <div><h3>Исходный текст</h3><div className="text-content" role="region" aria-label="Исходный текст" tabIndex={0}>{selectedSnapshot.textDocument.originalText}</div></div>
            {selectedSnapshot.textDocument.originalText !== selectedSnapshot.textDocument.normalizedText && <div><h3>Текст после подготовки</h3><div className="text-content" role="region" aria-label="Текст после подготовки" tabIndex={0}>{selectedSnapshot.textDocument.normalizedText}</div></div>}
          </div>
        </details>
        <SegmentTable ref={segmentTable} key={selected && selected.id !== documents[0]?.id ? selected.id : snapshot.id} projectId={snapshot.id}
          segments={selectedSnapshot.segments} refreshVersions={segmentRefresh} busy={editingBlocked} onDirtyChange={updateDraftStatus}
          onPrepareFragment={id => {
            setFragmentToGenerate(id);
            openScreen('generation');
          }}
          onReview={(id, artifactId, reviewed) => mutate(`/segments/${id}/review`, { artifactId, reviewed }, 'PATCH')}
          onCorrection={(id, note, expected, rejectCurrent) => mutate(`/segments/${id}/correction`, { note, expected, rejectCurrent }, 'PATCH')}
          onSave={(id, spokenText) => mutate(`/segments/${id}`, { spokenText }, 'PATCH')} />
      </section>}
      {snapshot.hasAcceptedText && !selectedSnapshot.textDocument && <section className="panel text-preview" aria-labelledby="legacy-text-heading">
        <h2 id="legacy-text-heading">Сохранённые фрагменты</h2>
        <ol className="segment-list">
          {snapshot.segments.map((segment, index) => <li className="segment" key={segment.id}>
            <h3>Фрагмент {index + 1}</h3><h4>Исходный текст</h4><p>{segment.originalText}</p>
            <label htmlFor={`legacy-${segment.id}`}>Текст фрагмента {index + 1}</label>
            <textarea id={`legacy-${segment.id}`} readOnly value={segment.spokenText} />
          </li>)}
        </ol>
      </section>}
    </div>

    <div id="workspace-generation" className="workspace-screen" hidden={screen !== 'generation'}>
      <GenerationPanel key={`generation-${snapshot.id}`} current={selectedCurrent} textDocumentId={selected?.id} generation={generation}
        selectionRef={generationSelection} setup={setup}
        onOpenFragment={selectedSnapshot.textDocument && !editingBlocked ? id => {
          setFragmentToOpen({ documentId: selected?.id ?? snapshot.id, segmentId: id, focus: 'spoken' });
          openScreen('texts');
        } : undefined}
        busy={busy || dirty || voxActive || trainingActive || corpusActive} training={training.snapshot}
        trainingReadiness={training.readiness} voxTraining={voxTraining} client={client} bookRemainder={bookRemainder}
        textPicker={selectedSnapshot.textDocument && <div className="workspace-document">
          {documents.length > 1 ? <>
            <label htmlFor="text-document">Текст для озвучивания</label>
            <select id="text-document" value={selected?.id ?? ''} disabled={switchingBlocked} onChange={event => setSelectedId(event.target.value)}>
              {documents.map(document => <option key={document.id} value={document.id}>{document.order + 1} · {document.document.displayName}</option>)}
            </select>
          </> : <strong>{selectedSnapshot.textDocument.displayName}</strong>}
          {totalFragments > 0 && <span className="subtle">Озвучено {audioFragments} из {totalFragments}</span>}
        </div>}
        bookExport={documents.length > 1 && <div className="project-book-export">
          {canExportBook && wholeWavSupported && <a className="book-download" href={bookWavHref(snapshot.id, snapshot.revision)} download="audiobook.wav">Скачать всю книгу · WAV</a>}
          {canExportBook && <a className="book-download" href={chaptersZipHref(snapshot.id, snapshot.revision)} download="chapters.zip">Скачать главы · ZIP</a>}
          {projectExportMessage && <p className="subtle">{projectExportMessage}</p>}
          {!wholeWavSupported && <p className="subtle">Один WAV поддерживает до {maxWavFragments} фрагментов.{hasSplitChapters && ' Длинные главы в ZIP разделены на пронумерованные WAV-файлы.'}</p>}
        </div>} />
    </div>
    <div id="workspace-voice" className="workspace-screen" hidden={screen !== 'voice'}>
      <section className="panel" aria-labelledby="voice-heading">
        <VoiceSetup key={snapshot.id} snapshot={snapshot} busy={editingBlocked || dirty} mutate={mutate} voiceMessages={voiceMessages} />
        {snapshot.voiceRecording && <div className="generation-actions">
          <button type="button" onClick={() => openScreen('training')}>Перейти к дообучению</button>
          {snapshot.voiceAnchor && <button type="button" className="primary" onClick={() => openScreen('generation')}>Перейти к озвучиванию</button>}
        </div>}
      </section>
    </div>
    <div id="workspace-training" className="workspace-screen" hidden={screen !== 'training'}>
      <section className="workspace-training-heading" aria-labelledby="voice-training-heading">
        <h2 id="voice-training-heading" tabIndex={-1}>Дообучение голоса</h2>
        {!snapshot.voiceRecording && <div className="workspace-next">
          <p>Для дообучения нужна запись вашего голоса.</p>
          <button className="primary" type="button" onClick={() => openScreen('voice')}>Добавить запись голоса</button>
        </div>}
        {(generation.pending || generationActive) && <p className="notice warning">Дождитесь окончания озвучивания или остановите задание в разделе «Озвучивание».</p>}
        {!!voxTraining?.models.length && <button type="button" onClick={() => openScreen('generation')}>Перейти к озвучиванию</button>}
      </section>
      <TtsCorpusPanel key={`corpus-${snapshot.id}`} current={current}
        busy={busy || dirty || voxActive || trainingActive || generation.pending || generationActive}
        onActiveChange={setCorpusActive} client={client} installProject={installProject} onVersionsChange={installVersions} />
      <VoxCpmTrainingPanel key={`vox-${snapshot.id}`} projectId={snapshot.id}
        projectEtag={current.etag} versions={corpusVersions.projectId === snapshot.id ? corpusVersions.versions : []}
        externalBusy={busy || dirty || corpusActive || trainingActive || generation.pending || generationActive}
        onBusyChange={setVoxActive} onProjectConflict={refreshProject} onSnapshotChange={setVoxTraining} client={client} />
      <details className="panel workspace-extra" hidden={!training.snapshot && (!snapshot.voiceRecording || training.readiness?.ready !== true)}>
        <summary>Прежний способ обучения · RVC{trainingActive && <span className="state-badge">В работе</span>}</summary>
        <TrainingPanel key={`training-${snapshot.id}`} current={current} busy={busy || dirty || voxActive || corpusActive || generation.pending || generationActive}
          onActiveChange={setTrainingActive} onSnapshotChange={installTraining} client={client} installProject={installProject} />
      </details>
    </div>
  </div>;
}
