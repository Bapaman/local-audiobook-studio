import { useEffect, useImperativeHandle, useLayoutEffect, useMemo, useRef, useState, type Ref } from 'react';
import type { Segment } from '../../api/types';

const fragmentsPerPage = 12;
const maxFragmentLength = 320;
const stressMark = '\u0301';
const graphemes = new Intl.Segmenter('ru', { granularity: 'grapheme' });
const russianVowel = /^[аеёиоуыэюя]$/iu;
type FragmentFilter = 'all' | 'missing' | 'ready' | 'unreviewed' | 'corrections' | 'drafts';
type Draft = { text: string; refreshVersion: number };
type CorrectionBaseline = { artifactId: string | null; note: string | null; needsNewAudio: boolean };
type CorrectionDraft = { segmentId: string; expected: CorrectionBaseline; text: string };
type CorrectionEditor = {
  draft?: string;
  stale: boolean;
  edit?: (text?: string) => void;
  rebase: () => void;
  save: (note: string | null, rejectCurrent?: boolean) => void;
};
type Comparison = { previous: HTMLAudioElement; current: HTMLAudioElement; playing: HTMLAudioElement };
type ComparisonStage = 'previous' | 'current' | 'error';
export type FragmentFocus = 'spoken' | 'audio';
export type SegmentTableHandle = { openFragment: (id: string, focus?: FragmentFocus) => void };
type Props = {
  ref?: Ref<SegmentTableHandle>;
  projectId: string;
  segments: Segment[];
  refreshVersions: Record<string, number>;
  busy: boolean;
  onSave: (id: string, spokenText: string) => Promise<void>;
  onDirtyChange?: (dirty: boolean) => void;
  onPrepareFragment?: (id: string) => void;
  onReview?: (id: string, artifactId: string, reviewed: boolean) => Promise<void>;
  onCorrection?: (id: string, note: string | null, expected: CorrectionBaseline, rejectCurrent?: boolean) => Promise<void>;
};

function correctionBaseline(segment: Segment): CorrectionBaseline {
  return {
    artifactId: segment.outputArtifactId ?? segment.previousAudio?.artifactId ?? null,
    note: segment.correction?.note ?? null,
    needsNewAudio: segment.correction?.needsNewAudio === true,
  };
}

function searchText(value: string) {
  return value.normalize('NFC').replaceAll('\u0301', '').toLocaleLowerCase('ru-RU')
    .replaceAll('ё', 'е').replace(/\s+/g, ' ').trim();
}

export function SegmentTable({ ref, projectId, segments, refreshVersions, busy, onSave, onDirtyChange, onPrepareFragment, onReview, onCorrection }: Props) {
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<FragmentFilter>('all');
  const [page, setPage] = useState(1);
  const [controlToFocus, setControlToFocus] = useState<string>();
  const [drafts, setDrafts] = useState<ReadonlyMap<string, Draft>>(() => new Map());
  const [correctionDraft, setCorrectionDraft] = useState<CorrectionDraft>();
  const filterControl = useRef<HTMLSelectElement>(null);
  useImperativeHandle(ref, () => ({
    openFragment(id: string, focus: FragmentFocus = 'spoken') {
      const index = segments.findIndex(segment => segment.id === id);
      if (index < 0) return;
      setQuery('');
      setFilter('all');
      setPage(Math.floor(index / fragmentsPerPage) + 1);
      setControlToFocus(`${focus}-${id}`);
    },
  }), [segments]);
  useLayoutEffect(() => {
    if (!controlToFocus || busy) return;
    const control = document.getElementById(controlToFocus) ?? filterControl.current;
    control?.focus({ preventScroll: true });
    control?.scrollIntoView?.({ block: 'center' });
    setControlToFocus(undefined);
  }, [controlToFocus, busy]);
  const currentDrafts = useMemo(() => {
    if (drafts.size === 0) return drafts;
    const kept = new Map<string, Draft>();
    for (const segment of segments) {
      const draft = drafts.get(segment.id);
      if (draft && draft.refreshVersion === (refreshVersions[segment.id] ?? 0) && draft.text !== segment.spokenText)
        kept.set(segment.id, draft);
    }
    return kept.size === drafts.size ? drafts : kept;
  }, [segments, refreshVersions, drafts]);
  useEffect(() => {
    if (currentDrafts !== drafts) setDrafts(previous => previous === drafts ? currentDrafts : previous);
  }, [currentDrafts, drafts]);
  const correctionSegment = correctionDraft && segments.find(segment => segment.id === correctionDraft.segmentId);
  const savedCorrection = correctionSegment && correctionBaseline(correctionSegment);
  const correctionSaved = correctionDraft && savedCorrection && savedCorrection.artifactId === correctionDraft.expected.artifactId
    && savedCorrection.note !== correctionDraft.expected.note && savedCorrection.note === correctionDraft.text.trim()
    && savedCorrection.needsNewAudio === (correctionDraft.expected.note === null || correctionDraft.expected.needsNewAudio);
  useLayoutEffect(() => {
    if (!correctionSaved || !correctionDraft) return;
    setCorrectionDraft(undefined);
    setControlToFocus(`correction-action-${correctionDraft.segmentId}`);
  }, [correctionDraft, correctionSaved]);
  const activeCorrection = correctionSaved ? undefined : correctionDraft;
  const dirtyFragments = new Set(currentDrafts.keys());
  const correctionChanged = !!activeCorrection && activeCorrection.text !== (activeCorrection.expected.note ?? '');
  if (correctionChanged) dirtyFragments.add(activeCorrection.segmentId);
  const hasDrafts = dirtyFragments.size > 0;
  useEffect(() => { onDirtyChange?.(hasDrafts); }, [hasDrafts, onDirtyChange]);
  useEffect(() => () => { onDirtyChange?.(false); }, [onDirtyChange]);

  const indexed = useMemo(() => segments.map((segment, index) => ({
    segment, number: index + 1,
    text: [segment.originalText, segment.normalizedText, segment.spokenText].map(searchText).join('\n'),
  })), [segments]);
  const term = searchText(query);
  const queryNumber = /^(?:#\s*)?(\d+)$/.exec(term)?.[1];
  const withAudio = segments.filter(segment => segment.outputArtifactId);
  const reviewedCount = withAudio.filter(segment => segment.audioReviewed).length;
  const correctionCount = segments.filter(segment => segment.correction).length;
  const matches = indexed.filter(({ segment, number, text }) => {
    if (filter === 'ready' && !segment.outputArtifactId) return false;
    if (filter === 'missing' && segment.outputArtifactId) return false;
    if (filter === 'unreviewed' && (!segment.outputArtifactId || segment.audioReviewed || segment.correction?.needsNewAudio)) return false;
    if (filter === 'corrections' && !segment.correction) return false;
    if (filter === 'drafts' && !dirtyFragments.has(segment.id)) return false;
    return !term || (queryNumber !== undefined && number === Number(queryNumber)) || text.includes(term);
  });
  const pageCount = Math.max(1, Math.ceil(matches.length / fragmentsPerPage));
  const currentPage = Math.min(page, pageCount);
  useEffect(() => {
    if (page !== currentPage) setPage(currentPage);
  }, [page, currentPage]);
  const visible = matches.slice((currentPage - 1) * fragmentsPerPage, currentPage * fragmentsPerPage);

  function changeView(nextQuery: string, nextFilter: FragmentFilter) {
    setQuery(nextQuery);
    setFilter(nextFilter);
    setPage(1);
  }

  function updateDraft(segment: Segment, text: string) {
    setDrafts(previous => {
      const next = new Map(previous);
      if (text === segment.spokenText) next.delete(segment.id);
      else next.set(segment.id, { text, refreshVersion: refreshVersions[segment.id] ?? 0 });
      return next;
    });
  }

  function editCorrection(segment: Segment, text?: string) {
    if (text === undefined) {
      setCorrectionDraft(undefined);
      setControlToFocus(`correction-action-${segment.id}`);
      return;
    }
    if (activeCorrection?.segmentId !== segment.id) setControlToFocus(`correction-${segment.id}`);
    setCorrectionDraft(previous => previous?.segmentId === segment.id
      ? { ...previous, text } : { segmentId: segment.id, expected: correctionBaseline(segment), text });
  }

  return <>
    <div className="segment-browser">
      <div className="segment-filters">
        <label>Найти фрагмент
          <input type="search" placeholder="Слово, фраза или номер" value={query}
            onChange={event => changeView(event.currentTarget.value, filter)} />
        </label>
        <label>Фильтр фрагментов
          <select ref={filterControl} value={filter} onChange={event => changeView(query, event.currentTarget.value as FragmentFilter)}>
            <option value="all">Все</option><option value="missing">Без аудио</option>
            <option value="ready">С аудио</option>
            {onReview && <option value="unreviewed">Аудио к проверке</option>}
            {onCorrection && <option value="corrections">С замечаниями</option>}
            <option value="drafts">С правками</option>
          </select>
        </label>
      </div>
      <div className="segment-browser-actions">
        <p className="subtle" role="status">Найдено {matches.length} из {segments.length}</p>
        {onReview && withAudio.length > 0 && <p className="subtle" role="status">Проверено {reviewedCount} из {withAudio.length}</p>}
        {hasDrafts && <button type="button" className="quiet" onClick={() => changeView('', 'drafts')}>Показать правки · {dirtyFragments.size}</button>}
        {onCorrection && correctionCount > 0 && <button type="button" className="quiet"
          onClick={() => changeView('', 'corrections')}>Показать замечания · {correctionCount}</button>}
        {(query || filter !== 'all') && <button type="button" className="quiet" aria-label="Сбросить поиск и фильтр"
          onClick={() => changeView('', 'all')}>Сбросить</button>}
      </div>
      {pageCount > 1 && <nav className="segment-pagination" aria-label="Страницы фрагментов">
        <button type="button" aria-label="Предыдущая страница" disabled={currentPage === 1}
          onClick={() => setPage(currentPage - 1)}>Назад</button>
        <span className="subtle">Страница {currentPage} из {pageCount}</span>
        <button type="button" aria-label="Следующая страница" disabled={currentPage === pageCount}
          onClick={() => setPage(currentPage + 1)}>Далее</button>
      </nav>}
    </div>
    {visible.length ? <>
      <p className="subtle">До {maxFragmentLength} символов на фрагмент.</p>
      <ol className="segment-list" onPlayCapture={event => {
        const playing = event.target;
        if (!(playing instanceof HTMLAudioElement)) return;
        for (const player of event.currentTarget.querySelectorAll('audio')) {
          if (player !== playing && !player.paused) player.pause();
        }
      }}>{visible.map(({ segment, number }) => {
        const editing = activeCorrection?.segmentId === segment.id ? activeCorrection : undefined;
        const latest = correctionBaseline(segment);
        const expected = editing?.expected ?? latest;
        const correctionEditor: CorrectionEditor | undefined = onCorrection && {
          draft: editing?.text,
          stale: expected.artifactId !== latest.artifactId || expected.note !== latest.note || expected.needsNewAudio !== latest.needsNewAudio,
          edit: !correctionChanged || editing ? text => editCorrection(segment, text) : undefined,
          rebase: () => {
            if (!editing) return;
            setCorrectionDraft({ ...editing, expected: latest });
            setControlToFocus(`correction-${segment.id}`);
          },
          save: (note, rejectCurrent) => void onCorrection(segment.id, note, expected, rejectCurrent),
        };
        return <SegmentEditor key={segment.id} projectId={projectId} segment={segment} number={number}
          busy={busy} spoken={currentDrafts.get(segment.id)?.text ?? segment.spokenText}
          onSave={onSave} onChange={text => updateDraft(segment, text)} onPrepareFragment={onPrepareFragment}
          preparationBlocked={hasDrafts} onReview={onReview} correctionEditor={correctionEditor} />;
      })}</ol>
    </> : <p className="subtle">Фрагменты не найдены.</p>}
  </>;
}

function SegmentEditor({ projectId, segment, number, busy, spoken, onSave, onChange, onPrepareFragment, preparationBlocked, onReview,
  correctionEditor }: {
  projectId: string; segment: Segment; number: number; busy: boolean; spoken: string;
  onSave: Props['onSave']; onChange: (text: string) => void;
  onPrepareFragment: Props['onPrepareFragment']; preparationBlocked: boolean;
  onReview: Props['onReview'];
  correctionEditor?: CorrectionEditor;
}) {
  const audio = useRef<HTMLAudioElement>(null);
  const earlierAudio = useRef<HTMLAudioElement>(null);
  const earlierDetails = useRef<HTMLDetailsElement>(null);
  const comparison = useRef<Comparison>(undefined);
  const [comparisonStage, setComparisonStage] = useState<ComparisonStage>();
  const textField = useRef<HTMLTextAreaElement>(null);
  const pendingSelection = useRef<{ text: string; caret: number }>(undefined);
  const [stressHint, setStressHint] = useState('');
  const { outputArtifactId, audioReviewed, correction } = segment;
  const previous = segment.previousAudio;
  useLayoutEffect(() => {
    const players = [audio.current, earlierAudio.current];
    setComparisonStage(undefined);
    return () => {
      comparison.current = undefined;
      for (const player of players) if (player && !player.paused) player.pause();
    };
  }, [outputArtifactId, previous?.artifactId]);
  useLayoutEffect(() => {
    const pending = pendingSelection.current;
    pendingSelection.current = undefined;
    if (pending?.text !== spoken || !textField.current) return;
    textField.current.focus({ preventScroll: true });
    textField.current.setSelectionRange(pending.caret, pending.caret);
  }, [spoken]);

  function toggleStress() {
    const field = textField.current;
    if (!field || field.disabled) return;
    const { value, selectionStart, selectionEnd } = field;
    const selected = selectionStart !== selectionEnd;
    const vowel = graphemes.segment(value).containing(selected ? selectionStart : selectionStart - 1);
    const letter = vowel?.segment.replaceAll(stressMark, '');
    if (!vowel || !letter || !russianVowel.test(letter.normalize('NFC')) ||
        (selected && vowel.index !== selectionStart) || vowel.index + vowel.segment.length !== selectionEnd) {
      setStressHint('Выделите одну гласную или поставьте курсор сразу после неё.');
      field.focus({ preventScroll: true });
      return;
    }
    const replacement = letter === vowel.segment ? letter + stressMark : letter;
    const text = value.slice(0, vowel.index) + replacement + value.slice(selectionEnd);
    if (text.length > maxFragmentLength) {
      setStressHint('Для знака ударения нужно освободить место в тексте фрагмента.');
      field.focus({ preventScroll: true });
      return;
    }
    pendingSelection.current = { text, caret: vowel.index + replacement.length };
    setStressHint('');
    onChange(text);
  }

  function finishComparison() {
    comparison.current = undefined;
    setComparisonStage(undefined);
  }

  function stopComparison() {
    const playing = comparison.current?.playing;
    finishComparison();
    playing?.pause();
  }

  function playComparedRecording(sequence: Comparison, stage: 'previous' | 'current') {
    const media = sequence[stage];
    sequence.playing = media;
    setComparisonStage(stage);
    media.currentTime = 0;
    void media.play().catch(() => {
      if (comparison.current === sequence && sequence.playing === media) {
        comparison.current = undefined;
        setComparisonStage('error');
      }
    });
  }

  function toggleComparison() {
    if (comparison.current) { stopComparison(); return; }
    const before = earlierAudio.current;
    const after = audio.current;
    if (!before || !after || !earlierDetails.current) return;
    earlierDetails.current.open = true;
    const sequence = { previous: before, current: after, playing: before };
    comparison.current = sequence;
    playComparedRecording(sequence, 'previous');
  }

  function onRecordingPlayed(media: HTMLAudioElement) {
    if (comparison.current && comparison.current.playing !== media) finishComparison();
  }

  function onRecordingPaused(media: HTMLAudioElement) {
    if (comparison.current?.playing === media && media.paused && !media.ended) finishComparison();
  }

  function onRecordingEnded(media: HTMLAudioElement) {
    const sequence = comparison.current;
    if (!sequence || sequence.playing !== media || !media.ended) return;
    if (!earlierDetails.current?.open || sequence.current.closest('[hidden]')) { finishComparison(); return; }
    if (sequence.previous === media) playComparedRecording(sequence, 'current');
    else finishComparison();
  }

  const dirty = spoken !== segment.spokenText;
  const comparing = comparisonStage === 'previous' || comparisonStage === 'current';
  const comparisonLabel = comparing ? 'Остановить сравнение' : 'Сравнить записи';
  return <li className="segment">
    <div className="section-heading"><h3>Фрагмент {number}</h3>
      {correction ? <span className="state-badge">{correction.needsNewAudio ? 'Нужно исправить' : 'К повторной проверке'}</span>
        : outputArtifactId ? <span className="state-badge">Аудио сохранено</span>
        : previous && <span className="state-badge">Нужно озвучить</span>}
    </div>
    <form onSubmit={event => { event.preventDefault(); void onSave(segment.id, spoken); }}>
      <label htmlFor={`spoken-${segment.id}`}>Текст фрагмента {number}</label>
      <textarea ref={textField} id={`spoken-${segment.id}`} rows={3} required maxLength={maxFragmentLength} disabled={busy} value={spoken}
        aria-describedby={stressHint ? `stress-hint-${segment.id}` : undefined}
        onChange={event => { setStressHint(''); onChange(event.target.value); }} />
      <div className="segment-actions">
        <button disabled={busy || !spoken.trim() || !dirty} aria-label={`Сохранить фрагмент ${number}`}>Сохранить фрагмент {number}</button>
        <button type="button" className="quiet" disabled={busy} aria-label={`Ударение · фрагмент ${number}`}
          onClick={toggleStress}>Ударение</button>
        {onPrepareFragment && <button type="button" className="quiet" disabled={busy || preparationBlocked}
          aria-label={`К озвучиванию фрагмента ${number}`}
          onClick={() => onPrepareFragment(segment.id)}>К озвучиванию</button>}
        {dirty && <button type="button" className="quiet" disabled={busy} aria-label={`Отменить правку фрагмента ${number}`}
          onClick={() => { setStressHint(''); onChange(segment.spokenText); }}>Отменить правку</button>}
        {dirty && <span className="save-state">Есть несохранённая правка</span>}
      </div>
      {stressHint && <p role="status" id={`stress-hint-${segment.id}`} className="subtle">{stressHint}</p>}
      {dirty && outputArtifactId && <p className="subtle">После сохранения потребуется новое озвучивание.</p>}
    </form>
    {outputArtifactId && <>
      {previous && <div className="segment-actions segment-comparison">
        <button type="button" aria-label={`${comparisonLabel} · фрагмент ${number}`} onClick={toggleComparison}>{comparisonLabel}</button>
        {comparisonStage && <span className="subtle" role="status">
          {comparisonStage === 'previous' ? 'Играет предыдущая запись' :
            comparisonStage === 'current' ? 'Играет текущая запись' : 'Не удалось запустить запись. Запустите её в плеере.'}
        </span>}
      </div>}
      <audio ref={audio} id={`audio-${segment.id}`} tabIndex={0} aria-label={`Аудио фрагмента ${number}`} controls preload="metadata"
        src={`/api/projects/${projectId}/artifacts/${outputArtifactId}`}
        onPlay={event => onRecordingPlayed(event.currentTarget)} onPause={event => onRecordingPaused(event.currentTarget)}
        onEnded={event => onRecordingEnded(event.currentTarget)} />
      {onReview && <label className="check-label">
        <input type="checkbox" aria-label={`Проверено на слух · фрагмент ${number}`} checked={audioReviewed === true}
          aria-describedby={correction ? `correction-hint-${segment.id}` : undefined}
          disabled={busy || dirty || correctionEditor?.draft !== undefined}
          onChange={event => void onReview(segment.id, outputArtifactId, event.currentTarget.checked)} />
        Проверено на слух
      </label>}
    </>}
    {correctionEditor && (outputArtifactId || previous || correction || correctionEditor.draft !== undefined) &&
      <SegmentCorrection segment={segment} number={number} busy={busy} editor={correctionEditor} />}
    {previous && <details ref={earlierDetails} className="text-reference" onToggle={event => {
      if (event.currentTarget.open) return;
      if (comparison.current) stopComparison();
      else if (earlierAudio.current && !earlierAudio.current.paused) earlierAudio.current.pause();
    }}>
      <summary>Предыдущая запись</summary>
      <p>{previous.spokenText}</p>
      <audio ref={earlierAudio} aria-label={`Предыдущая запись фрагмента ${number}`} controls preload="none"
        src={`/api/projects/${projectId}/artifacts/${previous.artifactId}`}
        onPlay={event => onRecordingPlayed(event.currentTarget)} onPause={event => onRecordingPaused(event.currentTarget)}
        onEnded={event => onRecordingEnded(event.currentTarget)} />
    </details>}
    <details className="text-reference">
      <summary>Сверить фрагмент</summary>
      <div className="segment-versions"><div><h4>Исходный текст</h4><p>{segment.originalText}</p></div>
        {segment.originalText !== segment.normalizedText && <div><h4>Текст после подготовки</h4><p>{segment.normalizedText}</p></div>}
      </div>
    </details>
  </li>;
}

function SegmentCorrection({ segment, number, busy, editor }: {
  segment: Segment; number: number; busy: boolean; editor: CorrectionEditor;
}) {
  const { draft, stale, edit, rebase, save } = editor;
  const note = segment.correction?.note;
  const editLabel = note ? 'Изменить замечание' : 'Добавить замечание';
  const trimmed = draft?.trim();
  const canSave = !busy && !stale && !!trimmed && trimmed !== note && !!(segment.outputArtifactId || segment.previousAudio || note);
  return <div className="segment-correction">
    {note && <>
      <p className="correction-note">{note}</p>
      {segment.outputArtifactId && <p id={`correction-hint-${segment.id}`} className="subtle">Отметка «Проверено на слух» закрывает замечание.</p>}
    </>}
    {draft !== undefined ? <form onSubmit={event => { event.preventDefault(); if (canSave) save(trimmed); }}>
      <label htmlFor={`correction-${segment.id}`}>Замечание к фрагменту {number}</label>
      <input id={`correction-${segment.id}`} type="text" required maxLength={240} value={draft}
        disabled={busy} onChange={event => edit?.(event.currentTarget.value)} />
      {stale && <p className="subtle" role="status">Запись или замечание изменились. Сверьте их перед сохранением.</p>}
      <div className="segment-actions">
        <button disabled={!canSave}>Сохранить замечание</button>
        {stale && <button type="button" className="quiet" disabled={busy} onClick={rebase}>Продолжить с текущей записью</button>}
        <button type="button" className="quiet" disabled={busy} onClick={() => edit?.()}>Отмена</button>
      </div>
    </form> : <div className="segment-actions">
      <button id={`correction-action-${segment.id}`} type="button" className="quiet" disabled={busy || !edit} aria-label={`${editLabel} к фрагменту ${number}`}
        onClick={() => edit?.(note ?? '')}>{editLabel}</button>
      {note && !segment.correction?.needsNewAudio && <button type="button" className="quiet" disabled={busy}
        onClick={() => save(note, true)}>Вернуть на исправление</button>}
      {note && <button type="button" className="quiet" disabled={busy} onClick={() => save(null)}>Снять замечание</button>}
    </div>}
  </div>;
}
