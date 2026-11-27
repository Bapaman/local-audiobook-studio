import { useState, type Ref } from 'react';
import type { ProjectListItem } from '../../api/types';

export function ProjectListPage({ projects, busy, headingRef, onCreate, onOpen }: { projects: ProjectListItem[]; busy: boolean; headingRef: Ref<HTMLHeadingElement>; onCreate: (name: string) => void; onOpen: (id: string) => void }) {
  const [name, setName] = useState('');
  return <>
    <section className="hero"><h1 ref={headingRef} tabIndex={-1}>Голоса</h1><p className="lead">Выберите голос для озвучивания или добавьте свой.</p></section>
    <div className="catalog-layout">
      <section className="panel"><h2>Новый голос</h2>
        <form onSubmit={event => { event.preventDefault(); if (name.trim()) onCreate(name); }}>
          <label htmlFor="project-name">Название голоса</label><input id="project-name" autoComplete="off" maxLength={120} required value={name} onChange={event => setName(event.target.value)} placeholder="Например, «Мой голос»" />
          <button className="primary" disabled={busy || !name.trim()}>Добавить голос</button>
        </form>
      </section>
      <section className="catalog"><h2>Мои голоса</h2>
        {projects.length === 0 ? <div className="empty-state"><h3>Пока нет голосов</h3></div>
          : <ul className="project-list">{projects.map(project => <li key={project.id} className="project-card">
            <div><h3>{project.displayName}</h3><p className="project-state">{project.hasAnchor ? 'Образец сохранён' : project.hasRecording ? 'Нужно подтвердить образец' : 'Нужно добавить запись'}</p></div>
            <button disabled={busy} aria-label={`Открыть голос «${project.displayName}»`} onClick={() => onOpen(project.id)}>Открыть</button>
          </li>)}</ul>}
      </section>
    </div>
  </>;
}
