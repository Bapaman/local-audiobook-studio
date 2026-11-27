import { useEffect, useRef, useState } from 'react';

type ReadyAudioFragment = { segmentId: string; artifactId: string; number: number };
type Props = {
  projectId: string;
  fragments: readonly ReadyAudioFragment[];
  fragmentCount: number;
  download?: { href: string; fileName: string; label: string; note?: string };
  onOpenFragment?: (id: string) => void;
};

export function ReadyAudio({ projectId, fragments, fragmentCount, download, onOpenFragment }: Props) {
  const audio = useRef<HTMLAudioElement>(null);
  const playback = useRef(0);
  const [selection, setSelection] = useState<ReadyAudioFragment>();
  const [failed, setFailed] = useState(false);
  const index = selection ? fragments.findIndex(fragment =>
    fragment.segmentId === selection.segmentId && fragment.artifactId === selection.artifactId) : -1;
  const current = fragments[index];
  const partial = fragments.length < fragmentCount;
  const selectionRemoved = !!selection && !current;

  useEffect(() => {
    const media = audio.current;
    return () => {
      playback.current++;
      if (media?.hasAttribute('src')) media.pause();
    };
  }, []);

  useEffect(() => {
    if (!selectionRemoved) return;
    playback.current++;
    const media = audio.current;
    media?.pause();
    media?.removeAttribute('src');
    media?.load();
    setSelection(undefined);
    setFailed(false);
  }, [selectionRemoved]);

  function play(fragment?: ReadyAudioFragment) {
    const media = audio.current;
    if (!media || !fragment) return;
    const request = ++playback.current;
    setSelection(fragment);
    setFailed(false);
    media.src = `/api/projects/${encodeURIComponent(projectId)}/artifacts/${encodeURIComponent(fragment.artifactId)}`;
    void media.play().catch(() => {
      if (playback.current === request) setFailed(true);
    });
  }

  return <section className="ready-audio" aria-labelledby="ready-audio-heading">
    <h3 id="ready-audio-heading">Готовое аудио</h3>
    {partial && <p className="subtle ready-audio-availability">Готово {fragments.length} из {fragmentCount}. Плеер пропускает фрагменты без аудио.</p>}
    {download?.note && <p className="subtle ready-audio-availability">{download.note}</p>}
    <div className="ready-audio-actions">
      <button type="button" onClick={() => play(fragments[0])}>{current ? 'Слушать с начала' : partial ? 'Прослушать готовые фрагменты' : 'Прослушать текст'}</button>
      {download && <a className="book-download" href={download.href} download={download.fileName}>{download.label}</a>}
    </div>
    {current && <div className="ready-audio-position">
      <p className="subtle" role="status">Фрагмент {current.number} из {fragmentCount}</p>
      {fragments.length > 1 && <select aria-label="Перейти к фрагменту" value={current.segmentId}
        onChange={event => play(fragments.find(fragment => fragment.segmentId === event.currentTarget.value))}>
        {fragments.map(fragment => <option key={fragment.segmentId} value={fragment.segmentId}>Фрагмент {fragment.number}</option>)}
      </select>}
      {onOpenFragment && <button type="button" className="quiet" onClick={() => {
        playback.current++;
        audio.current?.pause();
        onOpenFragment(current.segmentId);
      }}>Открыть фрагмент в редакторе</button>}
    </div>}
    <audio ref={audio} hidden={!current} aria-label="Аудио выбранного текста" controls preload="none"
      onEnded={() => { if (current) play(fragments[index + 1]); }}
      onError={() => { if (current) setFailed(true); }} />
    {current && failed && <p className="notice error" role="alert">Не удалось воспроизвести этот фрагмент.</p>}
  </section>;
}
