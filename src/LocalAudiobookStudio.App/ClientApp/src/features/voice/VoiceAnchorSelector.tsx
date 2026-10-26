import { useRef, useState } from 'react';
import type { ConfirmAnchor } from '../../api/types';

export function VoiceAnchorSelector({ durationMilliseconds, mediaUrl, busy, onConfirm }: { durationMilliseconds: number; mediaUrl: string; busy: boolean; onConfirm: (command: ConfirmAnchor) => void }) {
  const [start, setStart] = useState('0'); const [end, setEnd] = useState('10'); const [transcript, setTranscript] = useState(''); const [listened, setListened] = useState(false); const [playError, setPlayError] = useState('');
  const audio = useRef<HTMLAudioElement>(null); const playingInterval = useRef(false);
  const startMilliseconds = Math.round(Number(start) * 1000); const endMilliseconds = Math.round(Number(end) * 1000);
  const length = endMilliseconds - startMilliseconds;
  const valid = start !== '' && end !== '' && Number.isFinite(length) && startMilliseconds >= 0 && endMilliseconds <= durationMilliseconds && length >= 10000 && length <= 20000;
  function rangeChanged(value: string, change: (value: string) => void) { change(value); setListened(false); playingInterval.current = false; audio.current?.pause(); }
  return <div className="anchor-selector"><h3>Выберите голосовой фрагмент</h3><p>Найдите 10–20 секунд чистой речи. Запишите ровно те слова, которые слышны в этом фрагменте, включая «ё» и ударения.</p>
    <audio ref={audio} controls preload="metadata" aria-label="Прослушивание записи" src={mediaUrl} onTimeUpdate={() => { if (playingInterval.current && audio.current && audio.current.currentTime >= endMilliseconds / 1000) { audio.current.pause(); playingInterval.current = false; } }} />
    <form onSubmit={event => { event.preventDefault(); if (valid && listened && transcript.trim()) onConfirm({ startMilliseconds, endMilliseconds, transcript, pairListenedAndConfirmed: true }); }}>
      <div className="range-fields"><div><label htmlFor="anchor-start">Начало, секунды</label><input id="anchor-start" type="number" min={0} max={durationMilliseconds / 1000} step="0.001" required disabled={busy} value={start} onChange={event => rangeChanged(event.target.value, setStart)} /></div><div><label htmlFor="anchor-end">Конец, секунды</label><input id="anchor-end" type="number" min={0} max={durationMilliseconds / 1000} step="0.001" required disabled={busy} value={end} onChange={event => rangeChanged(event.target.value, setEnd)} /></div></div>
      <p className={valid ? 'range-status' : 'range-status invalid'} role="status">{valid ? `Выбрано ${(length / 1000).toLocaleString('ru-RU')} с` : 'Нужен диапазон 10–20 секунд внутри записи.'}</p>
      <button type="button" disabled={busy || !valid} onClick={() => { if (!audio.current) return; setPlayError(''); audio.current.currentTime = startMilliseconds / 1000; playingInterval.current = true; void audio.current.play().catch(() => { playingInterval.current = false; setPlayError('Не удалось воспроизвести запись. Попробуйте встроенные кнопки аудиоплеера.'); }); }}>Прослушать выбранный фрагмент</button>
      {playError && <p role="alert" className="notice error">{playError}</p>}
      <label htmlFor="anchor-transcript">Точный текст фрагмента</label><textarea id="anchor-transcript" rows={3} required maxLength={250000} disabled={busy} value={transcript} onChange={event => { setTranscript(event.target.value); setListened(false); }} />
      <label className="check-label"><input type="checkbox" disabled={busy || !valid} checked={listened} onChange={event => setListened(event.target.checked)} /><span>Прослушал и подтверждаю</span></label><p className="subtle">Подтверждаю, что текст точно соответствует выбранному фрагменту.</p>
      <button className="primary" disabled={busy || !valid || !listened || !transcript.trim()}>Подтвердить голосовой фрагмент</button>
    </form>
  </div>;
}
