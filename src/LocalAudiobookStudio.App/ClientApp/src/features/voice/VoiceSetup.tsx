import { useState } from 'react';
import type { ProjectSnapshot } from '../../api/types';
import type { Mutation } from '../project/ProjectWorkspacePage';
import { VoiceAnchorSelector } from './VoiceAnchorSelector';
import { multipart } from '../../api/client';

export function VoiceSetup({ snapshot, busy, mutate }: { snapshot: ProjectSnapshot; busy: boolean; mutate: Mutation }) {
  const [rights, setRights] = useState(false); const [file, setFile] = useState<File>();
  const recording = snapshot.voiceRecording; const anchor = snapshot.voiceAnchor;
  if (snapshot.hasAcceptedVoice && !anchor) return <><h2>01 · Ваш голос</h2><p>Голос сохранён в старом формате. Данные выбора фрагмента недоступны. Для другой пары «голос — текст» создайте новый проект.</p></>;
  const url = (id: string) => `/api/projects/${snapshot.id}/artifacts/${id}`;
  return <>
    <div className="section-heading"><h2>01 · Ваш голос</h2><span className="state-badge">{anchor ? 'Подтверждён' : recording ? 'Запись сохранена' : 'Нужна запись'}</span></div>
    {!recording && !anchor ? <><p>Добавьте спокойную, чистую запись длительностью 5–20 минут. Полная запись останется исходным материалом для проекта.</p><p className="format-note">Этап 1: WAV — PCM16, моно, 48 кГц, до 1 ГиБ. M4A и MP3 будут доступны с модулем FFmpeg.</p>
      <form onSubmit={event => { event.preventDefault(); if (!file || !rights) return; void mutate('/voice/recording', multipart({ fileName: file.name, rightsConfirmed: rights }, file)); }}>
        <label htmlFor="voice-file">Запись голоса WAV</label><input id="voice-file" type="file" accept=".wav,audio/wav" disabled={busy} onChange={event => setFile(event.target.files?.[0])} />
        <label className="check-label"><input type="checkbox" checked={rights} disabled={busy} onChange={event => setRights(event.target.checked)} /><span>Это мой голос, и я имею право использовать эту запись.</span></label>
        <button className="primary" disabled={busy || !rights || !file}>Сохранить запись</button>
      </form></> : <>
      {recording && <p>Исходная запись сохранена · {(recording.durationMilliseconds / 60000).toLocaleString('ru-RU', { maximumFractionDigits: 2 })} мин. Она не заменяется при выборе фрагмента.</p>}
      {recording?.hasClippingWarning && <p className="notice warning">В записи есть перегрузка звука. Выберите фрагмент без искажений.</p>}
      {anchor ? <div className="anchor-summary"><h3>Голосовой фрагмент подтверждён</h3><p>{(anchor.startMilliseconds / 1000).toLocaleString('ru-RU')}–{(anchor.endMilliseconds / 1000).toLocaleString('ru-RU')} с · {((anchor.endMilliseconds - anchor.startMilliseconds) / 1000).toLocaleString('ru-RU')} секунд</p><audio controls preload="metadata" aria-label="Подтверждённый голосовой фрагмент" src={url(anchor.artifactId)} /><h3>Подтверждённый текст</h3><p className="text-content confirmed-transcript">{anchor.transcript}</p><p className="subtle">Пара «голос — текст» сохранена. Для другой пары создайте новый проект.</p></div>
        : recording && <VoiceAnchorSelector durationMilliseconds={recording.durationMilliseconds} mediaUrl={url(recording.artifactId)} busy={busy} onConfirm={command => void mutate('/voice/anchor', command)} />}
    </>}
  </>;
}
