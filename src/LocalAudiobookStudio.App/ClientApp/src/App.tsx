import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, StudioClient } from './api/client';
import type { Bootstrap, ProjectListItem, SnapshotResponse } from './api/types';
import { ProjectListPage } from './features/projects/ProjectListPage';
import { ProjectWorkspacePage } from './features/project/ProjectWorkspacePage';
import { useProjectEvents } from './features/generation/useProjectEvents';

export default function App() {
  const [client] = useState(() => new StudioClient());
  const [bootstrap, setBootstrap] = useState<Bootstrap>();
  const [projects, setProjects] = useState<ProjectListItem[]>([]);
  const [current, setCurrent] = useState<SnapshotResponse>();
  const [segmentRefresh, setSegmentRefresh] = useState<Record<string, number>>({});
  const [projectDrafts, setProjectDrafts] = useState(false);
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState('');
  const [closed, setClosed] = useState(false);
  const heading = useRef<HTMLHeadingElement>(null);
  const creation = useRef<{ name: string; key: string } | undefined>(undefined);
  const installProject = useCallback((value: SnapshotResponse) => {
    setCurrent(previous => previous?.snapshot.id === value.snapshot.id && previous.snapshot.revision <= value.snapshot.revision ? value : previous);
  }, []);
  const generation = useProjectEvents(client, current?.snapshot.id, installProject);

  useEffect(() => {
    let active = true;
    void (async () => {
      try { const info = await client.bootstrap(); const list = await client.list(); if (active) { setBootstrap(info); setProjects(list); } }
      catch (failure) { if (active) setError(failure instanceof ApiError ? failure.message : new ApiError('network').message); }
      finally { if (active) setBusy(false); }
    })();
    return () => { active = false; };
  }, [client]);
  useEffect(() => { heading.current?.focus(); }, [current?.snapshot.id, closed]);
  useEffect(() => {
    if (!projectDrafts) return;
    function warnBeforeUnload(event: BeforeUnloadEvent) { event.preventDefault(); }
    window.addEventListener('beforeunload', warnBeforeUnload);
    return () => window.removeEventListener('beforeunload', warnBeforeUnload);
  }, [projectDrafts]);

  async function action(work: () => Promise<void>, onConflictRefresh?: () => void, conflictMessage?: string) {
    setBusy(true); setError('');
    try { await work(); }
    catch (failure) {
      if (failure instanceof ApiError) { if (failure.current) { installProject(failure.current); onConflictRefresh?.(); } setError(failure.current ? conflictMessage ?? failure.message : failure.message); }
      else setError(new ApiError('network').message);
    }
    finally { setBusy(false); }
  }
  async function mutate(suffix: string, body: unknown, method = 'POST') {
    if (!current) return;
    const segmentId = /^\/segments\/([^/]+)$/.exec(suffix)?.[1];
    const refreshSavedEditor = () => { if (segmentId) setSegmentRefresh(previous => ({ ...previous, [segmentId]: (previous[segmentId] ?? 0) + 1 })); };
    await action(async () => {
      installProject(await client.snapshot(`/api/projects/${current.snapshot.id}${suffix}`, method, body, current.etag));
      refreshSavedEditor();
    }, refreshSavedEditor, segmentId ? 'Текст изменён из другой вкладки. Правка фрагмента не сохранена. Загружена текущая версия фрагмента. Проверьте её перед новой правкой.' : undefined);
  }

  return <>
    <a className="skip-link" href="#main">К содержимому</a>
    <header className="app-header">
      <div className="brand"><span className="brand-mark" aria-hidden="true">а</span><div>Моя аудиокнига<span className="brand-subtitle">Локальная студия</span></div></div>
      <div className="header-actions"><span className="local-badge">На вашем компьютере</span>
        {bootstrap && !closed && <button className="quiet" disabled={busy || projectDrafts}
          aria-describedby={projectDrafts ? 'project-draft-status' : undefined}
          onClick={() => void action(async () => { await client.exit(); setClosed(true); setCurrent(undefined); })}>Выйти из приложения</button>}
      </div>
    </header>
    <main id="main" aria-busy={busy}>
      {error && <div className="notice error" role="alert">{error}</div>}
      {closed ? <section className="hero"><h1 ref={heading} tabIndex={-1}>Студия закрывается</h1><p>Голоса и тексты сохранены. Для продолжения откройте приложение через ярлык.</p></section>
        : !bootstrap ? <section className="hero"><h1>Моя аудиокнига</h1>{busy && <p role="status">Подключаем локальную студию…</p>}</section>
        : current ? <ProjectWorkspacePage current={current} generation={generation} segmentRefresh={segmentRefresh}
          busy={busy} headingRef={heading} mutate={mutate} client={client} installProject={installProject} onDraftsChange={setProjectDrafts}
          voiceMessages={bootstrap.capabilities.voiceMessages}
          onBack={() => void action(async () => { setProjects(await client.list()); setCurrent(undefined); })} />
        : <ProjectListPage projects={projects} busy={busy} headingRef={heading} onCreate={name => void action(async () => {
          if (creation.current?.name !== name) creation.current = { name, key: crypto.randomUUID() };
          setCurrent(await client.snapshot('/api/projects', 'POST', { displayName: name }, undefined, creation.current.key)); creation.current = undefined;
        })} onOpen={id => void action(async () => {
          const latest = await client.snapshot(`/api/projects/${id}`);
          setCurrent(await client.snapshot(`/api/projects/${id}/open`, 'POST', undefined, latest.etag));
        })} />}
    </main>
    <footer className="app-footer">Подготовка и озвучивание фрагментов <span>Данные остаются на этом компьютере</span></footer>
  </>;
}
