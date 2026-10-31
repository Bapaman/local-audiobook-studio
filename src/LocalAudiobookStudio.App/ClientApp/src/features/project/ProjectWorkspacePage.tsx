import { useCallback, useEffect, useRef, useState, type Ref } from 'react';
import type { StudioClient } from '../../api/client';
import type { SnapshotResponse, TrainingReadiness, TrainingSnapshot, TtsCorpusVersion, VoxCpmTrainingReadiness, VoxCpmTrainingSnapshot } from '../../api/types';
import { VoiceSetup } from '../voice/VoiceSetup';
import { TextImport } from '../text/TextImport';
import { SegmentTable } from '../text/SegmentTable';
import { GenerationPanel } from '../generation/GenerationPanel';
import { terminal, type GenerationState } from '../generation/useProjectEvents';
import { TrainingPanel } from '../training/TrainingPanel';
import { TtsCorpusPanel } from '../ttsCorpus/TtsCorpusPanel';
import { VoxCpmTrainingPanel } from '../voxcpmTraining/VoxCpmTrainingPanel';

export type Mutation = (suffix: string, body: unknown, method?: string) => Promise<void>;
type Props = { current: SnapshotResponse; generation: GenerationState; segmentRefresh: Record<string, number>; busy: boolean; headingRef: Ref<HTMLHeadingElement>; mutate: Mutation; onBack: () => void; client?: StudioClient; installProject?: (value: SnapshotResponse) => void };
export function ProjectWorkspacePage(props: Props) { return <ProjectWorkspaceContent key={props.current.snapshot.id} {...props} />; }
function ProjectWorkspaceContent({ current, generation, segmentRefresh, busy, headingRef, mutate, onBack, client, installProject }: Props) {
  const snapshot = current.snapshot;
  const documents = snapshot.textDocuments ?? [];
  const [selectedId, setSelectedId] = useState(documents[0]?.id ?? '');
  const [dirty, setDirty] = useState(false);
  const previousDocumentIds = useRef(documents.map(document => document.id));
  const appendedId = useRef<string | undefined>(undefined);
  useEffect(() => {
    const ids = snapshot.textDocuments?.map(document => document.id) ?? [];
    const previous = previousDocumentIds.current;
    if (ids.length > previous.length && previous.every((id, index) => ids[index] === id)) appendedId.current = ids.at(-1);
    previousDocumentIds.current = ids;
    // An externally appended text must not unmount an editor with an unsaved draft.
    if (!dirty && appendedId.current) { setSelectedId(appendedId.current); appendedId.current = undefined; }
  }, [snapshot.textDocuments, dirty]);
  const selected = documents.find(document => document.id === selectedId) ?? documents[0];
  const segmentIds = selected ? new Set(selected.segmentIds) : undefined;
  const selectedCurrent = selected ? { ...current, snapshot: { ...snapshot, textDocument: selected.document, segments: snapshot.segments.filter(segment => segmentIds!.has(segment.id)) } } : current;
  const selectedSnapshot = selectedCurrent.snapshot;
  const [trainingActive, setTrainingActive] = useState(false);
  const [corpusActive, setCorpusActive] = useState(false);
  const [voxActive, setVoxActive] = useState(false);
  const [corpusVersions, setCorpusVersions] = useState<{ projectId: string; versions: TtsCorpusVersion[] }>({ projectId: snapshot.id, versions: [] });
  const installVersions = useCallback((versions: TtsCorpusVersion[]) => setCorpusVersions({ projectId: snapshot.id, versions }), [snapshot.id]);
  const refreshProject = useCallback(() => { if (client && installProject) void client.snapshot(`/api/projects/${encodeURIComponent(snapshot.id)}`).then(installProject).catch(() => {}); }, [client, installProject, snapshot.id]);
  const [training, setTraining] = useState<{ snapshot: TrainingSnapshot | null; readiness?: TrainingReadiness }>({ snapshot: null });
  const installTraining = useCallback((snapshot: TrainingSnapshot | null, readiness: TrainingReadiness | undefined) => setTraining({ snapshot, readiness }), []);
  const [voxTraining, setVoxTraining] = useState<{ snapshot: VoxCpmTrainingSnapshot | null; readiness?: VoxCpmTrainingReadiness }>({ snapshot: null });
  const installVoxTraining = useCallback((snapshot: VoxCpmTrainingSnapshot | null, readiness: VoxCpmTrainingReadiness | undefined) => setVoxTraining({ snapshot, readiness }), []);
  const generationActive = generation.jobs.some(job => !terminal(job));
  const editingBlocked = busy || trainingActive || corpusActive || voxActive || generation.pending || generationActive;
  return <>
    <button className="back-link" disabled={busy} onClick={onBack}>← Все проекты</button>
    <section className="workspace-heading"><div><p className="eyebrow">ПОДГОТОВКА АУДИОКНИГИ</p><h1 ref={headingRef} tabIndex={-1}>{snapshot.displayName}</h1></div><span className="revision">Версия {snapshot.revision}</span></section>
    <ol className="steps" aria-label="Подготовка проекта"><li><span>01</span> Голос <small>{snapshot.voiceAnchor || snapshot.hasAcceptedVoice ? 'Подтверждён' : 'Подготовка'}</small></li><li><span>02</span> Текст <small>{snapshot.textDocument || snapshot.hasAcceptedText ? 'Сохранён' : 'Подготовка'}</small></li><li><span>03</span> Озвучивание <small>{generation.jobs.some(job => !terminal(job)) ? 'В работе' : 'Фрагменты'}</small></li></ol>
    <div className="setup-grid"><section className="panel"><VoiceSetup key={snapshot.id} snapshot={snapshot} busy={editingBlocked} mutate={mutate} /></section>
      <section className="panel"><TextImport key={snapshot.id} snapshot={selectedSnapshot} busy={editingBlocked} dirty={dirty} mutate={mutate} /></section></div>
    {snapshot.textDocument && <section className="panel text-preview"><div className="section-heading"><h2>Текст и фрагменты</h2><span className="state-badge">Сохранено</span></div><p>Исходный текст сохраняется дословно. Нормализованный текст подготовлен для разбиения; произносимый текст можно править отдельно.</p>
      {documents.length > 1 && <><label htmlFor="text-document">Текст для озвучивания</label><select id="text-document" value={selected?.id ?? ''} disabled={editingBlocked || dirty} onChange={event => setSelectedId(event.target.value)}>{documents.map(document => <option key={document.id} value={document.id}>{document.order + 1} · {document.document.displayName}</option>)}</select></>}
      {dirty && <p className="subtle" role="status">Сохраните или отмените правки фрагментов, чтобы выбрать другой текст или добавить новый.</p>}
      <div className="text-versions"><div><h3>Исходный текст</h3><div className="text-content" role="region" aria-label="Исходный текст" tabIndex={0}>{selectedSnapshot.textDocument!.originalText}</div></div><div><h3>Нормализованный текст</h3><div className="text-content" role="region" aria-label="Нормализованный текст" tabIndex={0}>{selectedSnapshot.textDocument!.normalizedText}</div></div></div>
      <SegmentTable key={selected && selected.id !== documents[0]?.id ? selected.id : snapshot.id} projectId={snapshot.id} segments={selectedSnapshot.segments} refreshVersions={segmentRefresh} busy={editingBlocked} onDirtyChange={setDirty} onSave={(id, spokenText) => mutate(`/segments/${id}`, { spokenText }, 'PATCH')} />
    </section>}
    {snapshot.hasAcceptedText && !snapshot.textDocument && <section className="panel text-preview"><h2>Сохранённые фрагменты</h2><ol className="segment-list">{snapshot.segments.map((segment, index) => <li className="segment" key={segment.id}><h3>Фрагмент {index + 1}</h3><h4>Исходный</h4><p>{segment.originalText}</p><h4>Нормализованный</h4><p>{segment.normalizedText}</p><label htmlFor={`legacy-${segment.id}`}>Произносимый текст {index + 1}</label><textarea id={`legacy-${segment.id}`} readOnly value={segment.spokenText} /></li>)}</ol></section>}
    <TtsCorpusPanel key={`corpus-${snapshot.id}`} current={current} busy={busy || voxActive || trainingActive || generation.pending || generationActive} onActiveChange={setCorpusActive} client={client} installProject={installProject} onVersionsChange={installVersions} />
    <VoxCpmTrainingPanel key={`vox-${snapshot.id}`} projectId={snapshot.id} projectEtag={current.etag} versions={corpusVersions.projectId === snapshot.id ? corpusVersions.versions : []} externalBusy={busy || corpusActive || trainingActive || generation.pending || generationActive} onBusyChange={setVoxActive} onProjectConflict={refreshProject} onSnapshotChange={installVoxTraining} client={client} />
    <GenerationPanel key={`generation-${snapshot.id}`} current={selectedCurrent} textDocumentId={selected?.id} generation={generation} busy={busy || dirty || voxActive || trainingActive || corpusActive} training={training.snapshot} trainingReadiness={training.readiness} voxTraining={voxTraining.snapshot} client={client} />
    <TrainingPanel key={`training-${snapshot.id}`} current={current} busy={busy || voxActive || corpusActive || generation.pending || generationActive} onActiveChange={setTrainingActive} onSnapshotChange={installTraining} client={client} installProject={installProject} />
  </>;
}
