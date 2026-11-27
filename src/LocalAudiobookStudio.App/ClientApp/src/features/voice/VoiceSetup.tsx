import { useState } from 'react';
import type { ProjectSnapshot } from '../../api/types';
import type { Mutation } from '../project/ProjectWorkspacePage';
import { VoiceAnchorSelector } from './VoiceAnchorSelector';
import { multipart } from '../../api/client';

export function VoiceSetup({ snapshot, busy, mutate, voiceMessages }: { snapshot: ProjectSnapshot; busy: boolean; mutate: Mutation; voiceMessages?: boolean }) {
  const [rights, setRights] = useState(false);
  const [file, setFile] = useState<File>();
  const [saving, setSaving] = useState(false);
  const recording = snapshot.voiceRecording; const anchor = snapshot.voiceAnchor;
  if (snapshot.hasAcceptedVoice && !anchor) return <><h2 id="voice-heading" tabIndex={-1}>Ваш голос</h2><p>Голос сохранён в старом формате. Чтобы озвучивать им новые тексты, вернитесь в «Все голоса», добавьте новый голос и подтвердите образец.</p></>;
  const url = (id: string) => `/api/projects/${snapshot.id}/artifacts/${id}`;
  const duration = recording && (recording.durationMilliseconds < 60000
    ? `${(recording.durationMilliseconds / 1000).toLocaleString('ru-RU')} с`
    : `${(recording.durationMilliseconds / 60000).toLocaleString('ru-RU', { maximumFractionDigits: 2 })} мин`);
  return <>
    <div className="section-heading"><h2 id="voice-heading" tabIndex={-1}>Ваш голос</h2><span className="state-badge">{anchor ? 'Подтверждён' : recording ? 'Запись сохранена' : 'Нужна запись'}</span></div>
    {!recording && !anchor ? <><p>Добавьте запись с чистой речью длительностью от 5 секунд до 20 минут.</p>
      <p id="voice-formats" className="format-note">{voiceMessages
        ? 'Подойдут голосовые сообщения OGG, OGA и OPUS, записи M4A и MP3, а также WAV (PCM16, моно, 48 кГц). До 1 ГиБ.'
        : 'WAV (PCM16, моно, 48 кГц), до 1 ГиБ.'}</p>
      <form onSubmit={async event => {
        event.preventDefault();
        if (busy || saving || !file || !rights) return;
        setSaving(true);
        try { await mutate('/voice/recording', multipart({ fileName: file.name, rightsConfirmed: rights }, file)); }
        finally { setSaving(false); }
      }}>
        <label htmlFor="voice-file">{voiceMessages ? 'Файл записи голоса' : 'Запись голоса WAV'}</label>
        <input id="voice-file" type="file" aria-describedby="voice-formats" accept={voiceMessages ? '.wav,.ogg,.oga,.opus,.m4a,.mp3' : '.wav,audio/wav'}
          disabled={busy || saving} onChange={event => setFile(event.target.files?.[0])} />
        <label className="check-label"><input type="checkbox" checked={rights} disabled={busy || saving} onChange={event => setRights(event.target.checked)} /><span>Это мой голос, и я имею право использовать эту запись.</span></label>
        <button className="primary" disabled={busy || saving || !rights || !file}>Сохранить запись</button>
        {saving && <p role="status">Сохраняем запись голоса…</p>}
      </form></> : <>
      {recording && <p>Исходная запись сохранена · {duration}</p>}
      {recording?.hasClippingWarning && <p className="notice warning">В записи есть перегрузка звука. Выберите фрагмент без искажений.</p>}
      {anchor ? <div className="anchor-summary"><h3>Голосовой фрагмент подтверждён</h3><p>{(anchor.startMilliseconds / 1000).toLocaleString('ru-RU')}–{(anchor.endMilliseconds / 1000).toLocaleString('ru-RU')} с · {((anchor.endMilliseconds - anchor.startMilliseconds) / 1000).toLocaleString('ru-RU')} секунд</p><audio controls preload="metadata" aria-label="Подтверждённый голосовой фрагмент" src={url(anchor.artifactId)} /><h3>Подтверждённый текст</h3><p className="text-content confirmed-transcript">{anchor.transcript}</p></div>
        : recording && <VoiceAnchorSelector durationMilliseconds={recording.durationMilliseconds} mediaUrl={url(recording.artifactId)} busy={busy} onConfirm={command => void mutate('/voice/anchor', command)} />}
    </>}
  </>;
}
