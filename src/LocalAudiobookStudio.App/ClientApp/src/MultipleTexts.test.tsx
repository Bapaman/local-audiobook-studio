import { act, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import App from './App';
import { TextImport } from './features/text/TextImport';
import { TestEventSource } from './test-events';
import type { JobSnapshot, ProjectSnapshot, Segment, TextDocument } from './api/types';

const firstId = '11111111111111111111111111111111';
const secondId = '22222222222222222222222222222222';
const thirdId = '33333333333333333333333333333333';
const document = (name: string, text: string): TextDocument => ({ sourceKind: 'Paste', displayName: name, encoding: 'utf-8', sha256: 'hash', byteCount: text.length, originalText: text, normalizedText: text });
const segment = (id: string, order: number, text: string, outputArtifactId: string | null = null): Segment => ({ id, order, originalText: text, normalizedText: text, spokenText: text, contentHash: 'hash', status: 'pending', outputArtifactId });
const first = document('Первая книга', 'Первая история.');
const second = document('Вторая книга', 'Вторая история.');
const single: ProjectSnapshot = { id: 'book1', displayName: 'Моя книга', revision: 2,
  voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 2880044, durationMilliseconds: 30000, hasClippingWarning: false },
  voiceAnchor: { artifactId: 'voice1', startMilliseconds: 0, endMilliseconds: 12000, transcript: 'Точный текст.' }, textDocument: first,
  segments: [segment('s1', 0, first.originalText)] };
const multiple = { ...single, revision: 3, segments: [...single.segments, segment('s2', 1, second.originalText, 'audio2')], textDocuments: [
  { id: firstId, order: 0, document: first, segmentIds: ['s1'] }, { id: secondId, order: 1, document: second, segmentIds: ['s2'] },
] };
const job: JobSnapshot = { id: 'job1', projectId: 'book1', kind: 'generation', state: 'completed', requestedState: 'run', phase: 'completed', completedUnits: 1, totalUnits: 1, revision: 1,
  createdUtc: '2026-11-01T00:00:00Z', updatedUtc: '2026-11-01T00:00:01Z', completedUtc: '2026-11-01T00:00:01Z', diagnosticCode: null, inputHash: 'hash', effectiveProfileId: 'q8-f16-cpu' };
type Reply = { body: unknown; etag?: string; status?: number };
type Responder = (init?: RequestInit) => Reply | Promise<Reply>;
async function open(initial: ProjectSnapshot = multiple, overrides: Record<string, Responder> = {}, active = false) {
  let current = initial;
  const calls: { path: string; init?: RequestInit }[] = [];
  const routes: Record<string, Responder> = {
    'GET /api/bootstrap': () => ({ body: { version: '1', antiforgeryRequestToken: 'csrf', capabilities: { generation: false, training: false } } }),
    'GET /api/projects': () => ({ body: [] }), 'POST /api/projects': () => ({ body: current, etag: `"${current.revision}"` }),
    'GET /api/projects/book1': () => ({ body: current, etag: `"${current.revision}"` }), 'GET /api/projects/book1/jobs': () => ({ body: [] }),
    'GET /api/projects/book1/training': () => ({ body: null }), 'GET /api/projects/book1/tts-corpus': () => ({ body: null }), 'GET /api/projects/book1/voxcpm-training': () => ({ body: null }),
    ...Object.fromEntries(['training', 'tts-corpus', 'voxcpm-training'].map(name => [`GET /api/${name}/readiness`, () => ({ body: { ready: false, diagnosticCode: `${name}.runtime.not_ready`, experimental: true, cpuThreads: 2 } })])),
    'GET /api/runtime/readiness': () => ({ body: { ready: true, experimental: true, diagnosticCode: null, profileId: 'q8-f16-cpu' } }),
    'GET /api/voxcpm-inference/readiness': () => ({ body: { ready: false, experimental: true, diagnosticCode: 'worker.runtime.not_ready', profileId: 'voxcpm2-saved-adapter-controls-cpu-v1' } }), ...overrides,
  };
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    calls.push({ path, init }); const handler = routes[`${init?.method ?? 'GET'} ${path}`];
    if (!handler) throw new Error(`Unexpected request ${path}`);
    const reply = await handler(init);
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? 200, headers: { 'Content-Type': 'application/json', ...(reply.etag ? { ETag: reply.etag } : {}) } });
  });
  const user = userEvent.setup(); render(<App />); await screen.findByText('Пока нет проектов');
  await user.type(screen.getByLabelText('Название проекта'), 'Моя книга'); await user.click(screen.getByRole('button', { name: 'Создать проект' }));
  await screen.findByRole('heading', { name: 'Моя книга' });
  if (!active) await waitFor(() => expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeEnabled());
  return { user, calls, update: async (value: ProjectSnapshot) => { current = value; await act(async () => { TestEventSource.instances.at(-1)!.emit('snapshot'); }); await screen.findByText(`Версия ${value.revision}`); } };
}
const appendCalls = (calls: { path: string; init?: RequestInit }[]) => calls.filter(call => call.path.includes('/text/append/'));

describe('Multiple saved texts', () => {
  it('starts a blank append after the initial text import is accepted', async () => {
    const { user } = await open({ ...single, revision: 1, textDocument: null, segments: [] }, { 'POST /api/projects/book1/text/paste': () => ({ body: single, etag: '"2"' }) }, true);
    await user.type(screen.getByLabelText('Текст книги'), first.originalText); await user.click(screen.getByRole('button', { name: 'Импортировать текст' }));
    await screen.findByText('Версия 2'); expect(screen.getByRole('button', { name: 'Озвучить выбранные фрагменты' })).toBeEnabled();
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); expect(screen.getByLabelText('Текст книги')).toHaveValue('');
  });
  it('keeps a pasted append intent through a lost response and clears only after the document is acknowledged', async () => {
    const { user, calls, update } = await open(single, { 'POST /api/projects/book1/text/append/paste': () => { throw new Error('response lost'); } });
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' }));
    await user.type(screen.getByLabelText('Название нового текста'), 'Продолжение'); await user.type(screen.getByLabelText('Текст книги'), 'Новая история.');
    await user.click(screen.getByRole('button', { name: 'Добавить текст' })); await screen.findByRole('alert');
    expect(screen.getByLabelText('Текст книги')).toHaveValue('Новая история.');
    await user.click(screen.getByRole('button', { name: 'Добавить текст' })); await waitFor(() => expect(appendCalls(calls)).toHaveLength(2));
    const payloads = appendCalls(calls).map(call => JSON.parse(call.init!.body as string));
    expect(payloads[0]).toEqual({ textId: expect.stringMatching(/^[a-f0-9]{32}$/), displayName: 'Продолжение', text: 'Новая история.' }); expect(payloads[1]).toEqual(payloads[0]);
    await update({ ...multiple, textDocuments: [multiple.textDocuments[0], { ...multiple.textDocuments[1], id: payloads[0].textId }] });
    expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(payloads[0].textId); expect(screen.queryByLabelText('Название нового текста')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); expect(screen.getByLabelText('Текст книги')).toHaveValue('');
    expect(calls.filter(call => call.init?.method === 'POST' && /training|tts-corpus/.test(call.path))).toHaveLength(0);
  });
  it('does not mistake a resolved mutation for acceptance and allocates a new ID after the append payload changes', async () => {
    const mutate = vi.fn(async (_suffix: string, _body: unknown) => {}); const user = userEvent.setup(); render(<TextImport snapshot={single} busy={false} mutate={mutate} />);
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); await user.type(screen.getByLabelText('Текст книги'), 'Черновик.');
    await user.click(screen.getByRole('button', { name: 'Добавить текст' })); await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    expect(mutate.mock.calls[1][1]).toEqual(mutate.mock.calls[0][1]); expect(screen.getByLabelText('Текст книги')).toHaveValue('Черновик.');
    await user.type(screen.getByLabelText('Текст книги'), ' Ещё.'); await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    expect((mutate.mock.calls[2][1] as { textId: string }).textId).not.toBe((mutate.mock.calls[0][1] as { textId: string }).textId);
  });
  it('keeps a changed append draft when a delayed snapshot acknowledges an older intent', async () => {
    const mutate = vi.fn(async (_suffix: string, _body: unknown) => {}); const user = userEvent.setup();
    const { rerender } = render(<TextImport snapshot={single} busy={false} mutate={mutate} />);
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); await user.type(screen.getByLabelText('Текст книги'), 'Первая попытка.');
    await user.click(screen.getByRole('button', { name: 'Добавить текст' })); const oldId = (mutate.mock.calls[0][1] as { textId: string }).textId;
    await user.type(screen.getByLabelText('Текст книги'), ' Изменено.'); await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    const currentIntent = mutate.mock.calls[1][1];
    rerender(<TextImport snapshot={{ ...multiple, textDocuments: [multiple.textDocuments[0], { ...multiple.textDocuments[1], id: oldId }] }} busy={false} mutate={mutate} />);
    expect(screen.getByLabelText('Текст книги')).toHaveValue('Первая попытка. Изменено.'); await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    expect(mutate.mock.calls[2][1]).toEqual(currentIntent);
  });
  it('retries the same TXT with the same document ID and preserves multipart metadata and raw contents', async () => {
    const { user, calls } = await open(single, { 'POST /api/projects/book1/text/append/file': () => { throw new Error('response lost'); } });
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); await user.click(screen.getByLabelText('Файл TXT'));
    await user.upload(screen.getByLabelText('Текстовый файл TXT'), new File(['Ёжик читает.'], 'chapter.txt', { type: 'text/plain' }));
    await user.click(screen.getByRole('button', { name: 'Добавить текст' })); await screen.findByRole('alert'); await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    await waitFor(() => expect(appendCalls(calls)).toHaveLength(2)); const bodies = await Promise.all(appendCalls(calls).map(call => (call.init!.body as Blob).text()));
    const metadata = bodies.map(body => JSON.parse(body.split('Content-Type: application/json\r\n\r\n')[1].split('\r\n--')[0]));
    expect(metadata[0]).toEqual({ textId: expect.stringMatching(/^[a-f0-9]{32}$/), displayName: 'chapter.txt' }); expect(metadata[1]).toEqual(metadata[0]); expect(bodies.every(body => body.includes('Ёжик читает.'))).toBe(true);
  });
  it('preserves the picker on polling, scopes generation/export, resets fragment choice and retains the selected saved voice', async () => {
    const training = { id: 'training1', projectId: 'book1', revision: 1, state: 'completed', phase: 'completed', sourceRecordingId: 'record1', globalStep: 125, completedEpoch: 1, epochSize: 125,
      createdUtc: '2026-11-01T00:00:00Z', updatedUtc: '2026-11-01T00:00:00Z', diagnosticCode: null, canResume: true, models: [{ id: 'saved1', globalStep: 125, completedEpoch: 1, createdUtc: '2026-11-01T00:00:00Z', qualityEstablished: false, selectable: true }] };
    const { user, calls, update } = await open(multiple, { 'GET /api/projects/book1/training': () => ({ body: training, etag: '"1"' }),
      'GET /api/training/readiness': () => ({ body: { ready: true, experimental: true, diagnosticCode: null, cpuThreads: 2 } }), 'POST /api/projects/book1/jobs': () => ({ body: job, etag: '"1"', status: 202 }) });
    await screen.findByRole('option', { name: /шаг 125/ }); await user.selectOptions(screen.getByLabelText('Голос для озвучивания'), 'saved1'); await user.click(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/));
    await user.click(within(screen.getByRole('group', { name: 'Фрагменты для озвучивания' })).getByRole('checkbox'));
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), secondId);
    expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('saved1'); expect(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/)).toBeChecked();
    expect(screen.getByText(/Источник «Вторая книга» сохранён/)).toBeInTheDocument();
    expect(screen.getByLabelText('Произносимый текст 1')).toHaveValue(second.originalText); expect(within(screen.getByRole('group', { name: 'Фрагменты для озвучивания' })).getByRole('checkbox')).toBeChecked();
    await update({ ...multiple, revision: 4 }); expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(secondId);
    expect(screen.getByRole('link', { name: /Скачать выбранный текст/ })).toHaveAttribute('href', `/api/projects/book1/book.wav?revision=4&textDocumentId=${secondId}`);
    await user.click(screen.getByRole('button', { name: 'Озвучить выбранные фрагменты' })); const command = calls.find(call => call.path.endsWith('/jobs') && call.init?.method === 'POST')!;
    expect(JSON.parse(command.init!.body as string)).toEqual({ kind: 'generation', segmentIds: ['s2'], savedModelId: 'saved1', experimentalConfirmed: true }); expect(new Headers(command.init?.headers).get('If-Match')).toBe('"4"');
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), firstId); expect(screen.queryByRole('link', { name: /Скачать выбранный текст/ })).not.toBeInTheDocument();
    await update({ ...multiple, revision: 5, segments: [...multiple.segments, segment('s3', 2, 'Третья история.')], textDocuments: [...multiple.textDocuments, { id: thirdId, order: 2, document: document('Третья книга', 'Третья история.'), segmentIds: ['s3'] }] });
    expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(thirdId);
  });
  it('locks switching and appending while any spoken row is dirty, keeps drafts during polling, and unlocks after save or cancel', async () => {
    const initial = { ...multiple, segments: [segment('s1', 0, 'Первая строка.'), segment('s1b', 1, 'Вторая строка.'), multiple.segments[1]], textDocuments: [{ ...multiple.textDocuments[0], segmentIds: ['s1', 's1b'] }, multiple.textDocuments[1]] }; let saved = initial;
    const { user, update } = await open(initial, { 'PATCH /api/projects/book1/segments/s1': init => { saved = { ...saved, revision: 5, segments: saved.segments.map(value => value.id === 's1' ? { ...value, spokenText: JSON.parse(init!.body as string).spokenText } : value) }; return { body: saved, etag: '"5"' }; } });
    await user.type(screen.getByLabelText('Произносимый текст 1'), ' Правка.'); await user.type(screen.getByLabelText('Произносимый текст 2'), ' Другая.');
    expect(screen.getByLabelText('Текст для озвучивания')).toBeDisabled(); expect(screen.getByRole('button', { name: 'Добавить новый текст' })).toBeDisabled();
    await update({ ...initial, revision: 4 }); expect(screen.getByLabelText('Произносимый текст 1')).toHaveValue('Первая строка. Правка.');
    await user.click(screen.getByRole('button', { name: 'Отменить правку фрагмента 2' })); expect(screen.getByLabelText('Произносимый текст 2')).toHaveValue('Вторая строка.'); expect(screen.getByLabelText('Текст для озвучивания')).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' })); await screen.findByText('Версия 5'); expect(screen.getByLabelText('Текст для озвучивания')).toBeEnabled(); expect(screen.getByRole('button', { name: 'Добавить новый текст' })).toBeEnabled();
  });
  it('retains a dirty first text when another tab appends the second, then selects the new tail after cancel', async () => {
    const { user, update } = await open(single);
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); await user.type(screen.getByLabelText('Текст книги'), 'Будущий текст.');
    await user.type(screen.getByLabelText('Произносимый текст 1'), ' Несохранённая правка.');
    expect(screen.getByLabelText('Текст книги')).toBeDisabled(); expect(screen.getByLabelText('Текст книги')).toHaveValue('Будущий текст.');
    await update(multiple);
    expect(screen.getByLabelText('Произносимый текст 1')).toHaveValue(`${first.originalText} Несохранённая правка.`); expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(firstId); expect(screen.getByLabelText('Текст для озвучивания')).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Отменить правку фрагмента 1' }));
    expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(secondId); expect(screen.getByLabelText('Текст книги')).toHaveValue('Будущий текст.');
  });
  it('shows captured text titles and blocks append and editing for an unfinished generation job', async () => {
    await open(multiple, { 'GET /api/projects/book1/jobs': () => ({ body: [{ ...job, state: 'paused', completedUtc: null, textTitle: 'Вторая книга' }] }) }, true);
    expect(await screen.findByText('Текст: Вторая книга')).toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Добавить новый текст' })).toBeDisabled(); expect(screen.getByLabelText('Произносимый текст 1')).toBeDisabled();
  });
});
