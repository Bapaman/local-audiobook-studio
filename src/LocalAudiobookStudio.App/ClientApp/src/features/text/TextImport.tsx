import { useEffect, useRef, useState } from 'react';
import type { ProjectSnapshot } from '../../api/types';
import type { Mutation } from '../project/ProjectWorkspacePage';
import { multipart } from '../../api/client';

export function TextImport({ snapshot, busy, mutate, dirty = false }: { snapshot: ProjectSnapshot; busy: boolean; mutate: Mutation; dirty?: boolean }) {
  const [mode, setMode] = useState('paste'); const [text, setText] = useState(''); const [file, setFile] = useState<File>();
  const [displayName, setDisplayName] = useState(''); const [appendOpen, setAppendOpen] = useState(false); const [fileVersion, setFileVersion] = useState(0);
  const intent = useRef<string | undefined>(undefined);
  const append = !!snapshot.textDocument;
  const hadDocument = useRef(append);
  const blocked = busy || dirty;
  const atLimit = (snapshot.textDocuments?.length ?? (append ? 1 : 0)) >= 32;
  useEffect(() => {
    if (!hadDocument.current && append) { setText(''); setFile(undefined); setFileVersion(version => version + 1); }
    hadDocument.current = append;
  }, [append]);
  useEffect(() => {
    if (!intent.current || !snapshot.textDocuments?.some(document => document.id === intent.current)) return;
    // Mutation resolves even when App handled an API error. Only the snapshot acknowledges a committed append.
    intent.current = undefined; setText(''); setFile(undefined); setDisplayName(''); setAppendOpen(false); setFileVersion(version => version + 1);
  }, [snapshot.textDocuments]);
  const changed = () => { intent.current = undefined; };
  function submit() {
    if (blocked || (append && atLimit)) return;
    const title = append ? displayName.trim() || (mode === 'file' ? file?.name : undefined) || 'Текст книги' : mode === 'file' ? file?.name || 'Текст книги' : 'Текст книги';
    if (mode === 'file' && !file || mode === 'paste' && !text.trim()) return;
    if (append) intent.current ??= crypto.randomUUID().replaceAll('-', '');
    const metadata = { ...(append ? { textId: intent.current } : {}), displayName: title };
    const suffix = `/text/${append ? 'append/' : ''}${mode === 'paste' ? 'paste' : 'file'}`;
    void mutate(suffix, mode === 'paste' ? { ...metadata, text } : multipart(metadata, file!));
  }
  if (snapshot.hasAcceptedText && !snapshot.textDocument) return <><h2>02 · Текст книги</h2><p>Текст сохранён в старом формате. Фрагменты доступны ниже только для чтения. Для редактирования создайте новый проект и импортируйте исходник.</p></>;
  return <><div className="section-heading"><h2>02 · Текст книги</h2><span className="state-badge">{snapshot.textDocument ? 'Сохранён' : 'Нужен текст'}</span></div>
    {snapshot.textDocument && <><p>Источник «{snapshot.textDocument.displayName}» сохранён. Исходный текст, нормализованную версию и фрагменты можно просмотреть ниже.</p><p className="subtle">Добавьте другой текст в этот проект, чтобы использовать сохранённый голос. Предыдущие тексты и аудио сохранятся.</p>
      <button type="button" disabled={blocked || atLimit} onClick={() => setAppendOpen(value => !value)}>Добавить новый текст</button>
      {atLimit && <p className="subtle">В проекте уже 32 текста. Для следующего текста создайте новый проект.</p>}
      {dirty && <p className="subtle">Сохраните или отмените правки фрагментов перед добавлением текста.</p>}</>}
    {(!append || appendOpen) && <><p>Вставьте текст или выберите TXT. Слова с «ё» и знаками ударения сохраняются.</p><form onSubmit={event => { event.preventDefault(); submit(); }}>
        {append && <><label htmlFor="new-text-name">Название нового текста</label><input id="new-text-name" disabled={blocked} maxLength={120} value={displayName} onChange={event => { changed(); setDisplayName(event.target.value); }} placeholder="Необязательно" /></>}
        <fieldset className="input-choice" disabled={blocked}><legend>Способ добавления</legend><label><input type="radio" name="text-source" checked={mode === 'paste'} onChange={() => { changed(); setMode('paste'); }} /> Вставить текст</label><label><input type="radio" name="text-source" checked={mode === 'file'} onChange={() => { changed(); setMode('file'); }} /> Файл TXT</label></fieldset>
        {mode === 'paste' ? <><label htmlFor="book-text">Текст книги</label><textarea id="book-text" rows={7} disabled={blocked} required maxLength={8000000} value={text} onChange={event => { changed(); setText(event.target.value); }} placeholder="Добавьте текст вашей истории…" /></> : <><label htmlFor="text-file">Текстовый файл TXT</label><input key={fileVersion} id="text-file" type="file" accept=".txt,text/plain" disabled={blocked} onChange={event => { changed(); setFile(event.target.files?.[0]); }} /></>}
        <p className="subtle">TXT до 16 МиБ · Вставка до 8 000 000 символов UTF-16</p>
        {append && <p className="subtle">В проекте до 32 текстов, 10 000 фрагментов и 8 000 000 символов UTF-16 суммарно. Голос используется без нового обучения.</p>}
        <button className="primary" disabled={blocked || append && atLimit || (mode === 'paste' ? !text.trim() : !file)}>{append ? 'Добавить текст' : 'Импортировать текст'}</button>
      </form></>}
  </>;
}
