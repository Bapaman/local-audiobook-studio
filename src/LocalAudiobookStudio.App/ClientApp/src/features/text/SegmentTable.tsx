import { useCallback, useEffect, useRef, useState } from 'react';
import type { Segment } from '../../api/types';

export function SegmentTable({ projectId, segments, refreshVersions, busy, onSave, onDirtyChange }: { projectId: string; segments: Segment[]; refreshVersions: Record<string, number>; busy: boolean; onSave: (id: string, spokenText: string) => Promise<void>; onDirtyChange?: (dirty: boolean) => void }) {
  const dirtyRows = useRef(new Set<string>());
  const reportDirty = useCallback((id: string, dirty: boolean) => {
    const previous = dirtyRows.current.size > 0;
    if (dirty) dirtyRows.current.add(id); else dirtyRows.current.delete(id);
    const next = dirtyRows.current.size > 0;
    if (next !== previous) onDirtyChange?.(next);
  }, [onDirtyChange]);
  useEffect(() => () => { onDirtyChange?.(false); }, [onDirtyChange]);
  return <><h3>Фрагменты для озвучивания <span className="count">{segments.length}</span></h3><p className="subtle">Изменяйте только произносимый текст — исходник останется сохранённым.</p><ol className="segment-list">{segments.map((segment, index) => <SegmentEditor key={segment.id} projectId={projectId} segment={segment} refreshVersion={refreshVersions[segment.id] ?? 0} number={index + 1} busy={busy} onSave={onSave} onDirtyChange={reportDirty} />)}</ol></>;
}
function SegmentEditor({ projectId, segment, refreshVersion, number, busy, onSave, onDirtyChange }: { projectId: string; segment: Segment; refreshVersion: number; number: number; busy: boolean; onSave: (id: string, spokenText: string) => Promise<void>; onDirtyChange: (id: string, dirty: boolean) => void }) {
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
  const dirty = spoken !== segment.spokenText;
  useEffect(() => { onDirtyChange(segment.id, dirty); }, [segment.id, dirty, onDirtyChange]);
  useEffect(() => () => { onDirtyChange(segment.id, false); }, [segment.id, onDirtyChange]);
  return <li className="segment"><div className="section-heading"><h4>Фрагмент {number}</h4><span className="state-badge">{segment.outputArtifactId ? 'Аудио сохранено' : 'Подготовлен'}</span></div><div className="segment-versions"><div><h5>Исходный</h5><p>{segment.originalText}</p></div><div><h5>Нормализованный</h5><p>{segment.normalizedText}</p></div></div><form onSubmit={event => { event.preventDefault(); void onSave(segment.id, spoken); }}><label htmlFor={`spoken-${segment.id}`}>Произносимый текст {number}</label><textarea id={`spoken-${segment.id}`} rows={3} required maxLength={320} disabled={busy} value={spoken} onChange={event => setSpoken(event.target.value)} /><p className="subtle">До 320 символов UTF-16 на фрагмент</p><button disabled={busy || !spoken.trim() || !dirty} aria-label={`Сохранить фрагмент ${number}`}>Сохранить фрагмент {number}</button>{dirty && <button type="button" className="quiet" disabled={busy} aria-label={`Отменить правку фрагмента ${number}`} onClick={() => setSpoken(segment.spokenText)}>Отменить правку</button>}<span className="save-state">{dirty ? 'Есть несохранённая правка' : 'Сохранено'}</span></form>
    {segment.outputArtifactId && <audio aria-label={`Аудио фрагмента ${number}`} controls preload="metadata" src={`/api/projects/${projectId}/artifacts/${segment.outputArtifactId}`} />}</li>;
}
