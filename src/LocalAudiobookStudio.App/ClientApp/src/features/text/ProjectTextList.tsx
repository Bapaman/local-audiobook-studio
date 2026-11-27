import type { ProjectTextDocument } from '../../api/types';

type Props = {
  documents: readonly ProjectTextDocument[];
  selectedId: string;
  audioSegmentIds: ReadonlySet<string>;
  reviewedSegmentIds: ReadonlySet<string>;
  correctionSegmentIds: ReadonlySet<string>;
  disabled: boolean;
  onSelect: (id: string) => void;
};

export function ProjectTextList({ documents, selectedId, audioSegmentIds, reviewedSegmentIds, correctionSegmentIds, disabled, onSelect }: Props) {
  return <ol className="project-text-list" aria-label="Список текстов голоса">
    {documents.map(({ id, document, segmentIds }) => {
      const total = segmentIds.length;
      const ready = segmentIds.filter(segmentId => audioSegmentIds.has(segmentId)).length;
      const reviewed = segmentIds.filter(segmentId => reviewedSegmentIds.has(segmentId)).length;
      const corrections = segmentIds.filter(segmentId => correctionSegmentIds.has(segmentId)).length;
      const complete = total > 0 && ready === total;
      const status = complete ? `Готово · ${ready} из ${total}` : total ? `Озвучено ${ready} из ${total}` : 'Нет фрагментов';
      const titleId = `text-list-title-${id}`;
      const statusId = `text-list-status-${id}`;
      const reviewId = `text-list-review-${id}`;
      const correctionId = `text-list-correction-${id}`;
      const description = [statusId, ready ? reviewId : '', corrections ? correctionId : ''].filter(Boolean).join(' ');
      return <li key={id}>
        <button type="button" className="project-text-row" aria-current={id === selectedId || undefined}
          aria-labelledby={titleId} aria-describedby={description} disabled={disabled} onClick={() => onSelect(id)}>
          <span id={titleId} className="project-text-title">{document.displayName}</span>
          <span className="project-text-progress">
            <span id={statusId} className={complete ? 'project-text-status complete' : 'project-text-status'}>{status}</span>
            {ready > 0 && <span id={reviewId} className="project-text-status">Проверено {reviewed} из {ready}</span>}
            {corrections > 0 && <span id={correctionId} className="project-text-status">Замечаний · {corrections}</span>}
          </span>
        </button>
      </li>;
    })}
  </ol>;
}
