import { useState, type Ref } from 'react';
import type { ProjectListItem } from '../../api/types';

export function ProjectListPage({ projects, busy, headingRef, onCreate, onOpen }: { projects: ProjectListItem[]; busy: boolean; headingRef: Ref<HTMLHeadingElement>; onCreate: (name: string) => void; onOpen: (id: string) => void }) {
  const [name, setName] = useState('');
  return <>
    <section className="hero"><p className="eyebrow">ВАШ ГОЛОС. ВАШИ ИСТОРИИ.</p><h1 ref={headingRef} tabIndex={-1}>Начните с книги</h1><p className="lead">Сохраните запись своего голоса, выберите выразительный фрагмент и подготовьте текст будущей аудиокниги.</p></section>
    <div className="catalog-layout">
      <section className="panel"><h2>Новый проект</h2><p>Одна книга, одна исходная запись. Вы сможете продолжить подготовку в любое время.</p>
        <form onSubmit={event => { event.preventDefault(); if (name.trim()) onCreate(name); }}>
          <label htmlFor="project-name">Название проекта</label><input id="project-name" autoComplete="off" maxLength={120} required value={name} onChange={event => setName(event.target.value)} placeholder="Например, рассказы для семьи" />
          <button className="primary" disabled={busy || !name.trim()}>Создать проект</button>
        </form>
      </section>
      <section className="catalog"><h2>Ваши проекты <span className="count">{projects.length}</span></h2>
        {projects.length === 0 ? <div className="empty-state"><span aria-hidden="true" className="empty-symbol">▤</span><h3>Пока нет проектов</h3><p>Создайте первый — здесь появится ваша библиотека.</p></div>
          : <ul className="project-list">{projects.map(project => <li key={project.id} className="project-card"><h3>{project.displayName}</h3><p className="project-state">Голос: {project.hasAnchor ? 'фрагмент подтверждён' : project.hasRecording ? 'запись сохранена' : 'не добавлен'} · Текст: {project.hasText ? 'сохранён' : 'не добавлен'}</p><button disabled={busy} aria-label={`Продолжить «${project.displayName}»`} onClick={() => onOpen(project.id)}>Продолжить <span aria-hidden="true">→</span></button></li>)}</ul>}
      </section>
    </div>
  </>;
}
