import { useCallback, useState, type Ref } from 'react';
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
export function ProjectWorkspacePage({ current, generation, segmentRefresh, busy, headingRef, mutate, onBack, client, installProject }: { current: SnapshotResponse; generation: GenerationState; segmentRefresh: Record<string, number>; busy: boolean; headingRef: Ref<HTMLHeadingElement>; mutate: Mutation; onBack: () => void; client?: StudioClient; installProject?: (value: SnapshotResponse) => void }) {
  const snapshot = current.snapshot;
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
      <section className="panel"><TextImport key={snapshot.id} snapshot={snapshot} busy={editingBlocked} mutate={mutate} /></section></div>
    {snapshot.textDocument && <section className="panel text-preview"><div className="section-heading"><h2>Текст и фрагменты</h2><span className="state-badge">Сохранено</span></div><p>Исходный текст сохраняется дословно. Нормализованный текст подготовлен для разбиения; произносимый текст можно править отдельно.</p>
      <div className="text-versions"><div><h3>Исходный текст</h3><div className="text-content" role="region" aria-label="Исходный текст" tabIndex={0}>{snapshot.textDocument.originalText}</div></div><div><h3>Нормализованный текст</h3><div className="text-content" role="region" aria-label="Нормализованный текст" tabIndex={0}>{snapshot.textDocument.normalizedText}</div></div></div>
      <SegmentTable key={snapshot.id} projectId={snapshot.id} segments={snapshot.segments} refreshVersions={segmentRefresh} busy={editingBlocked} onSave={(id, spokenText) => mutate(`/segments/${id}`, { spokenText }, 'PATCH')} />
    </section>}
    {snapshot.hasAcceptedText && !snapshot.textDocument && <section className="panel text-preview"><h2>Сохранённые фрагменты</h2><ol className="segment-list">{snapshot.segments.map((segment, index) => <li className="segment" key={segment.id}><h3>Фрагмент {index + 1}</h3><h4>Исходный</h4><p>{segment.originalText}</p><h4>Нормализованный</h4><p>{segment.normalizedText}</p><label htmlFor={`legacy-${segment.id}`}>Произносимый текст {index + 1}</label><textarea id={`legacy-${segment.id}`} readOnly value={segment.spokenText} /></li>)}</ol></section>}
    <TtsCorpusPanel key={`corpus-${snapshot.id}`} current={current} busy={busy || voxActive || trainingActive || generation.pending || generationActive} onActiveChange={setCorpusActive} client={client} installProject={installProject} onVersionsChange={installVersions} />
    <VoxCpmTrainingPanel key={`vox-${snapshot.id}`} projectId={snapshot.id} projectEtag={current.etag} versions={corpusVersions.projectId === snapshot.id ? corpusVersions.versions : []} externalBusy={busy || corpusActive || trainingActive || generation.pending || generationActive} onBusyChange={setVoxActive} onProjectConflict={refreshProject} onSnapshotChange={installVoxTraining} client={client} />
    <GenerationPanel key={`generation-${snapshot.id}`} current={current} generation={generation} busy={busy || voxActive || trainingActive || corpusActive} training={training.snapshot} trainingReadiness={training.readiness} voxTraining={voxTraining.snapshot} client={client} />
    <TrainingPanel key={`training-${snapshot.id}`} current={current} busy={busy || voxActive || corpusActive || generation.pending || generationActive} onActiveChange={setTrainingActive} onSnapshotChange={installTraining} client={client} installProject={installProject} />
  </>;
}
