import { useState } from 'react';
import type { ProjectSnapshot } from '../../api/types';
import type { Mutation } from '../project/ProjectWorkspacePage';
import { multipart } from '../../api/client';

export function TextImport({ snapshot, busy, mutate }: { snapshot: ProjectSnapshot; busy: boolean; mutate: Mutation }) {
  const [mode, setMode] = useState('paste'); const [text, setText] = useState(''); const [file, setFile] = useState<File>();
  if (snapshot.hasAcceptedText && !snapshot.textDocument) return <><h2>02 · Текст книги</h2><p>Текст сохранён в старом формате. Фрагменты доступны ниже только для чтения. Для редактирования создайте новый проект и импортируйте исходник.</p></>;
  return <><div className="section-heading"><h2>02 · Текст книги</h2><span className="state-badge">{snapshot.textDocument ? 'Сохранён' : 'Нужен текст'}</span></div>
    {snapshot.textDocument ? <><p>Источник «{snapshot.textDocument.displayName}» сохранён. Исходный текст, нормализованную версию и фрагменты можно просмотреть ниже.</p><p className="subtle">Для замены исходника создайте новый проект. Произносимый текст редактируется по фрагментам.</p></>
      : <><p>Вставьте текст или выберите TXT. Слова с «ё» и знаками ударения сохраняются.</p><form onSubmit={event => { event.preventDefault(); if (mode === 'paste') void mutate('/text/paste', { displayName: 'Текст книги', text }); else if (file) void mutate('/text/file', multipart({ displayName: file.name }, file)); }}>
        <fieldset className="input-choice" disabled={busy}><legend>Способ добавления</legend><label><input type="radio" name="text-source" checked={mode === 'paste'} onChange={() => setMode('paste')} /> Вставить текст</label><label><input type="radio" name="text-source" checked={mode === 'file'} onChange={() => setMode('file')} /> Файл TXT</label></fieldset>
        {mode === 'paste' ? <><label htmlFor="book-text">Текст книги</label><textarea id="book-text" rows={7} disabled={busy} required maxLength={8000000} value={text} onChange={event => setText(event.target.value)} placeholder="Добавьте текст вашей истории…" /></> : <><label htmlFor="text-file">Текстовый файл TXT</label><input id="text-file" type="file" accept=".txt,text/plain" disabled={busy} onChange={event => setFile(event.target.files?.[0])} /></>}
        <p className="subtle">TXT до 16 МиБ · Вставка до 8 000 000 символов UTF-16</p><button className="primary" disabled={busy || (mode === 'paste' ? !text.trim() : !file)}>Импортировать текст</button>
      </form></>}
  </>;
}
