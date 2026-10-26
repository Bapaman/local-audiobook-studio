import { useEffect, useRef, useState } from 'react';
import type { Segment } from '../../api/types';

export function SegmentTable({ projectId, segments, refreshVersions, busy, onSave }: { projectId: string; segments: Segment[]; refreshVersions: Record<string, number>; busy: boolean; onSave: (id: string, spokenText: string) => Promise<void> }) {
  return <><h3>Фрагменты для озвучивания <span className="count">{segments.length}</span></h3><p className="subtle">Изменяйте только произносимый текст — исходник останется сохранённым.</p><ol className="segment-list">{segments.map((segment, index) => <SegmentEditor key={segment.id} projectId={projectId} segment={segment} refreshVersion={refreshVersions[segment.id] ?? 0} number={index + 1} busy={busy} onSave={onSave} />)}</ol></>;
}
function SegmentEditor({ projectId, segment, refreshVersion, number, busy, onSave }: { projectId: string; segment: Segment; refreshVersion: number; number: number; busy: boolean; onSave: (id: string, spokenText: string) => Promise<void> }) {
  const [spoken, setSpoken] = useState(segment.spokenText);
  const previousServerText = useRef(segment.spokenText);
  const previousRefresh = useRef(refreshVersion);
  useEffect(() => {
    const previous = previousServerText.current;
    const targetedRefresh = previousRefresh.current !== refreshVersion;
    // A saved/conflicting editor follows the authoritative response; unrelated dirty drafts survive.
    setSpoken(draft => targetedRefresh || draft === previous ? segment.spokenText : draft);
    previousServerText.current = segment.spokenText;
    previousRefresh.current = refreshVersion;
  }, [segment.spokenText, refreshVersion]);
  return <li className="segment"><div className="section-heading"><h4>Фрагмент {number}</h4><span className="state-badge">{segment.outputArtifactId ? 'Аудио сохранено' : 'Подготовлен'}</span></div><div className="segment-versions"><div><h5>Исходный</h5><p>{segment.originalText}</p></div><div><h5>Нормализованный</h5><p>{segment.normalizedText}</p></div></div><form onSubmit={event => { event.preventDefault(); void onSave(segment.id, spoken); }}><label htmlFor={`spoken-${segment.id}`}>Произносимый текст {number}</label><textarea id={`spoken-${segment.id}`} rows={3} required maxLength={320} disabled={busy} value={spoken} onChange={event => setSpoken(event.target.value)} /><p className="subtle">До 320 символов UTF-16 на фрагмент</p><button disabled={busy || !spoken.trim() || spoken === segment.spokenText} aria-label={`Сохранить фрагмент ${number}`}>Сохранить фрагмент {number}</button><span className="save-state">{spoken === segment.spokenText ? 'Сохранено' : 'Есть несохранённая правка'}</span></form>
    {segment.outputArtifactId && <audio aria-label={`Аудио фрагмента ${number}`} controls preload="metadata" src={`/api/projects/${projectId}/artifacts/${segment.outputArtifactId}`} />}</li>;
}
