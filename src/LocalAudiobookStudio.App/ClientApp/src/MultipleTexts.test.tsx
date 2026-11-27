import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
  const user = userEvent.setup(); render(<App />); await screen.findByText('Пока нет голосов');
  await user.type(screen.getByLabelText('Название голоса'), 'Моя книга'); await user.click(screen.getByRole('button', { name: 'Добавить голос' }));
  await screen.findByRole('heading', { name: 'Моя книга' });
  if (!active) await waitFor(() => expect(screen.getByRole('button', { name: 'Проба голоса' })).toBeEnabled());
  return { user, calls, update: async (value: ProjectSnapshot) => { current = value; await act(async () => { TestEventSource.instances.at(-1)!.emit('snapshot'); }); await screen.findByTitle(`Версия ${value.revision}`); } };
}
const appendCalls = (calls: { path: string; init?: RequestInit }[]) => calls.filter(call => call.path.includes('/text/append/'));
const show = (user: ReturnType<typeof userEvent.setup>, name: 'Тексты' | 'Озвучивание') =>
  user.click(within(screen.getByRole('navigation', { name: 'Разделы голоса' })).getByRole('button', { name }));

describe('Multiple saved texts', () => {
  it('queues one job for unfinished audio across texts using the current project revision', async () => {
    const book = { ...multiple, segments: [
      segment('s1', 0, 'Готовый фрагмент.', 'audio1'), segment('s1b', 1, 'Остаток первой главы.'),
      { ...multiple.segments[1], order: 2, outputArtifactId: null },
    ], textDocuments: [{ ...multiple.textDocuments[0], segmentIds: ['s1', 's1b'] }, multiple.textDocuments[1]] };
    const { user, calls, update } = await open(book, {
      'POST /api/projects/book1/jobs': () => ({ body: { ...job, state: 'queued', completedUnits: 0, totalUnits: 2, textCount: 2 }, etag: '"1"', status: 202 }),
    });
    await show(user, 'Тексты');
    const texts = within(screen.getByRole('region', { name: 'Тексты этого голоса' }));
    expect(texts.getByText('Вся книга · готово 0 из 2 текстов')).toBeVisible();
    expect(texts.getByText('Озвучено 1 из 3 фрагментов')).toBeVisible();
    await user.type(screen.getByLabelText('Текст фрагмента 1'), ' Черновик.');
    await show(user, 'Озвучивание');
    const start = screen.getByRole('button', { name: 'Озвучить остаток книги' });
    expect(start).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Вернуться к правкам' }));
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue('Готовый фрагмент. Черновик.');
    await user.click(screen.getByRole('button', { name: 'Отменить правку фрагмента 1' }));
    await update({ ...book, revision: 4 });

    await show(user, 'Озвучивание');
    await user.click(start);
    const commands = calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST');
    expect(commands).toHaveLength(1);
    expect(JSON.parse(commands[0].init!.body as string)).toEqual({ kind: 'generation', segmentIds: ['s1b', 's2'] });
    expect(new Headers(commands[0].init?.headers).get('If-Match')).toBe('"4"');
    const queue = await screen.findByRole('region', { name: 'Очередь озвучивания' });
    expect(within(queue).getByText('Текстов в задании · 2')).toBeVisible();
    expect(start).toBeDisabled();
  });
  it('offers the whole book only when all texts are ready and changes are saved', async () => {
    const { user, update } = await open(multiple);
    const narration = within(screen.getByRole('region', { name: 'Озвучивание' }));
    const wholeBook = () => narration.queryByRole('link', { name: 'Скачать всю книгу · WAV' });
    const archive = () => narration.queryByRole('link', { name: 'Скачать главы · ZIP' });
    const chapter = () => screen.getByRole('link', { name: 'Скачать выбранный текст · WAV' });
    expect(wholeBook()).not.toBeInTheDocument();
    expect(archive()).not.toBeInTheDocument();
    expect(narration.getByText('Общий WAV доступен после озвучивания всех текстов.')).toBeVisible();
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), secondId);
    expect(chapter()).toHaveAttribute('href', `/api/projects/book1/book.wav?revision=3&textDocumentId=${secondId}`);
    expect(wholeBook()).not.toBeInTheDocument();

    await update({ ...multiple, revision: 4, segments: [
      { ...multiple.segments[0], outputArtifactId: 'audio1' }, multiple.segments[1],
    ] });
    expect(wholeBook()).toHaveAttribute('href', '/api/projects/book1/book.wav?revision=4');
    expect(archive()).toHaveAttribute('href', '/api/projects/book1/chapters.zip?revision=4');
    expect(chapter()).toHaveAttribute('href', `/api/projects/book1/book.wav?revision=4&textDocumentId=${secondId}`);
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), firstId);
    expect(wholeBook()).toHaveAttribute('href', '/api/projects/book1/book.wav?revision=4');
    expect(chapter()).toHaveAttribute('href', `/api/projects/book1/book.wav?revision=4&textDocumentId=${firstId}`);
    await show(user, 'Тексты');
    await user.type(screen.getByLabelText('Текст фрагмента 1'), ' Черновик.');
    await show(user, 'Озвучивание');
    expect(wholeBook()).not.toBeInTheDocument();
    expect(archive()).not.toBeInTheDocument();
    await show(user, 'Тексты');
    await user.click(screen.getByRole('button', { name: 'Отменить правку фрагмента 1' }));
    await show(user, 'Озвучивание');
    expect(wholeBook()).toBeVisible();
    expect(archive()).toBeVisible();
  });
  it('keeps chapter download available when all project fragments exceed the WAV limit', async () => {
    const otherSegments = Array.from({ length: 256 }, (_, index) =>
      segment(`other${index}`, index + 1, second.originalText, `audio-other${index}`));
    await open({ ...multiple, segments: [{ ...multiple.segments[0], outputArtifactId: 'audio1' }, ...otherSegments],
      textDocuments: [multiple.textDocuments[0], { ...multiple.textDocuments[1], segmentIds: otherSegments.map(item => item.id) }],
    });
    const narration = within(screen.getByRole('region', { name: 'Озвучивание' }));
    expect(narration.queryByRole('link', { name: 'Скачать всю книгу · WAV' })).not.toBeInTheDocument();
    expect(narration.getByText('Один WAV поддерживает до 256 фрагментов.')).toBeVisible();
    expect(narration.getByRole('link', { name: 'Скачать главы · ZIP' })).toHaveAttribute('href', '/api/projects/book1/chapters.zip?revision=3');
    expect(screen.getByRole('link', { name: 'Скачать выбранный текст · WAV' })).toBeVisible();
  });
  it('downloads a long selected chapter in parts while another text is unfinished', async () => {
    const longChapter = Array.from({ length: 257 }, (_, index) =>
      segment(`long${index}`, index + 1, second.originalText, `audio-long${index}`));
    const partialChapter = [...longChapter.slice(0, -1), { ...longChapter[256], outputArtifactId: null }];
    const book = {
      ...multiple, segments: [multiple.segments[0], ...partialChapter],
      textDocuments: [multiple.textDocuments[0], { ...multiple.textDocuments[1], segmentIds: longChapter.map(item => item.id) }],
    };
    const { user, update } = await open(book);
    const narration = within(screen.getByRole('region', { name: 'Озвучивание' }));
    const bookZip = () => narration.queryByRole('link', { name: 'Скачать главы · ZIP' });
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), secondId);
    const audio = within(screen.getByRole('region', { name: 'Готовое аудио' }));
    const chapterZip = () => audio.queryByRole('link', { name: 'Скачать выбранный текст частями · ZIP' });

    expect(audio.getByRole('button', { name: 'Прослушать готовые фрагменты' })).toBeVisible();
    expect(chapterZip()).not.toBeInTheDocument();
    expect(bookZip()).not.toBeInTheDocument();

    await update({ ...book, revision: 4, segments: [multiple.segments[0], ...longChapter] });
    expect(chapterZip()).toHaveAttribute('href', `/api/projects/book1/chapters.zip?revision=4&textDocumentId=${secondId}`);
    expect(chapterZip()).toHaveAttribute('download', 'chapters.zip');
    expect(audio.queryByRole('link', { name: 'Скачать выбранный текст · WAV' })).not.toBeInTheDocument();
    expect(audio.getByText(/пронумерованные WAV-файлы/)).toBeVisible();
    expect(bookZip()).not.toBeInTheDocument();

    await update({ ...book, revision: 5, segments: [{ ...multiple.segments[0], outputArtifactId: 'audio1' }, ...longChapter] });
    expect(bookZip()).toHaveAttribute('href', '/api/projects/book1/chapters.zip?revision=5');
    expect(chapterZip()).toHaveAttribute('href', `/api/projects/book1/chapters.zip?revision=5&textDocumentId=${secondId}`);

    await show(user, 'Тексты');
    await user.type(screen.getByLabelText('Текст фрагмента 1'), '!');
    await show(user, 'Озвучивание');
    expect(chapterZip()).not.toBeInTheDocument();
    expect(bookZip()).not.toBeInTheDocument();
  });
  it('downloads a long single import in parts and hides the link during generation', async () => {
    const segments = Array.from({ length: 257 }, (_, index) =>
      segment(`long${index}`, index, first.originalText, `audio-long${index}`));
    const { user } = await open({
      ...single, segments, textDocuments: [{ ...multiple.textDocuments[0], segmentIds: segments.map(item => item.id) }],
    }, {
      'POST /api/projects/book1/jobs': () => ({
        body: { ...job, kind: 'preview', state: 'queued', phase: 'queued', completedUnits: 0, completedUtc: null },
        etag: '"1"', status: 202,
      }),
    });
    const audio = within(screen.getByRole('region', { name: 'Готовое аудио' }));
    const bookZip = () => audio.queryByRole('link', { name: 'Скачать всю книгу частями · ZIP' });

    expect(bookZip()).toHaveAttribute('href', '/api/projects/book1/chapters.zip?revision=2');
    expect(bookZip()).toHaveAttribute('download', 'chapters.zip');
    expect(audio.queryByRole('link', { name: /Скачать.+WAV/ })).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Проба голоса' }));
    await screen.findByRole('region', { name: 'Очередь озвучивания' });
    expect(bookZip()).not.toBeInTheDocument();
    expect(audio.getByRole('button', { name: 'Прослушать текст' })).toBeVisible();
  });
  it('shows one workspace at a time and keeps the selected text when moving between them', async () => {
    const { user } = await open(multiple);
    const navigation = screen.getByRole('navigation', { name: 'Разделы голоса' });
    const narration = within(navigation).getByRole('button', { name: 'Озвучивание' });
    expect(narration).toHaveAttribute('aria-current', 'page');
    expect(screen.queryByRole('heading', { name: 'Тексты этого голоса' })).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Ваш голос' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Прослушать текст' })).not.toBeInTheDocument();
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), secondId);
    expect(screen.getByRole('link', { name: 'Скачать выбранный текст · WAV' }))
      .toHaveAttribute('href', `/api/projects/book1/book.wav?revision=3&textDocumentId=${secondId}`);
    await user.click(within(navigation).getByRole('button', { name: 'Тексты' }));
    expect(screen.getByRole('heading', { name: 'Тексты этого голоса' })).toHaveFocus();
    expect(screen.queryByRole('heading', { name: 'Озвучивание' })).not.toBeInTheDocument();
    const reference = screen.getByText('Сверить с исходным текстом');
    expect(reference.closest('details')).not.toHaveAttribute('open');
    await user.click(reference);
    expect(reference.closest('details')).toHaveAttribute('open');
    expect(screen.getByRole('region', { name: 'Исходный текст' })).toHaveTextContent(second.originalText);
    expect(screen.queryByRole('region', { name: 'Текст после подготовки' })).not.toBeInTheDocument();
    await user.click(narration);
    expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(secondId);
  });
  it('opens the first unchecked recording in book order across chapters and pages', async () => {
    const later = Array.from({ length: 13 }, (_, index) => segment(`later${index}`, index + 2,
      `Строка второй главы ${index + 1}.`, index === 12 ? 'lastAudio' : null));
    const book = { ...multiple, segments: [
      { ...segment('s1', 0, 'Первая история.', 'audio1'), audioReviewed: true },
      segment('s1b', 1, 'Пока без записи.'), ...later,
    ], textDocuments: [
      { ...multiple.textDocuments[0], segmentIds: ['s1', 's1b'] },
      { ...multiple.textDocuments[1], segmentIds: later.map(item => item.id) },
    ] };
    const reviewed = { ...book, revision: 4, segments: book.segments.map(item =>
      item.id === 'later12' ? { ...item, audioReviewed: true } : item) };
    const { user, update } = await open(book, {
      'PATCH /api/projects/book1/segments/later12/review': () => ({ body: reviewed, etag: '"4"' }),
    });
    await show(user, 'Тексты');
    const overview = within(screen.getByRole('region', { name: 'Тексты этого голоса' }));
    const nextReview = () => overview.getByRole('button', { name: 'К следующей записи на проверку' });
    const picker = screen.getByLabelText('Текст для озвучивания');
    const firstEditor = screen.getByLabelText('Текст фрагмента 1');
    expect(overview.getByText('Проверено по книге 1 из 2')).toBeVisible();
    expect(overview.getByRole('button', { name: 'Первая книга' })).toHaveAccessibleDescription('Озвучено 1 из 2 Проверено 1 из 1');
    expect(overview.getByRole('button', { name: 'Вторая книга' })).toHaveAccessibleDescription('Озвучено 1 из 13 Проверено 0 из 1');
    await user.type(firstEditor, ' Черновик.');
    expect(nextReview()).toBeDisabled();
    expect(picker).toHaveValue(firstId);
    expect(firstEditor).toHaveValue('Первая история. Черновик.');
    await user.click(screen.getByRole('button', { name: 'Отменить правку фрагмента 1' }));

    await user.click(nextReview());
    expect(picker).toHaveValue(secondId);
    expect(screen.getByText('Страница 2 из 2')).toBeVisible();
    expect(screen.getByLabelText('Аудио фрагмента 13')).toHaveFocus();
    await user.click(screen.getByRole('checkbox', { name: 'Проверено на слух · фрагмент 13' }));
    expect(await overview.findByText('Проверено по книге 2 из 2')).toBeVisible();
    expect(overview.queryByRole('button', { name: 'К следующей записи на проверку' })).not.toBeInTheDocument();
    await update({ ...reviewed, revision: 5, segments: reviewed.segments.map(item =>
      item.id === 's1b' ? { ...item, outputArtifactId: 'audio1b' } : item) });
    await user.click(nextReview());
    expect(picker).toHaveValue(firstId);
    expect(screen.getByLabelText('Аудио фрагмента 2')).toHaveFocus();
  });
  it('keeps the prepared fragment when a voice sample still needs confirmation', async () => {
    const withVoice = { ...single, segments: [single.segments[0], multiple.segments[1]] };
    const { user, update } = await open({ ...withVoice, voiceAnchor: null }, {}, true);
    await show(user, 'Тексты');
    await user.click(screen.getByRole('button', { name: 'К озвучиванию фрагмента 2' }));
    await user.click(screen.getByRole('button', { name: 'Подтвердить образец' }));
    await update({ ...withVoice, revision: 3 });
    await user.click(screen.getByRole('button', { name: 'Перейти к озвучиванию' }));
    expect(screen.getByLabelText('Фрагмент для пробы')).toHaveValue('s2');
    expect(screen.getByRole('checkbox', { name: 'Фрагмент 1' })).not.toBeChecked();
  });
  it('routes book corrections to the editor and sends replacement audio back for review', async () => {
    const note = 'Проверить ударение в слове «замок».';
    const firstSegment = { ...multiple.segments[0], outputArtifactId: 'oldAudio',
      correction: { note, needsNewAudio: true } };
    const book = { ...multiple, segments: [firstSegment, multiple.segments[1]] };
    const { user, update } = await open(book);
    await show(user, 'Тексты');
    const overview = within(screen.getByRole('region', { name: 'Тексты этого голоса' }));
    const nextReview = () => overview.getByRole('button', { name: 'К следующей записи на проверку' });
    const nextCorrection = () => overview.getByRole('button', { name: 'К следующему исправлению' });
    const picker = screen.getByLabelText('Текст для озвучивания');
    expect(overview.getByText('Замечаний по книге · 1')).toBeVisible();
    expect(overview.getByRole('button', { name: 'Первая книга' }))
      .toHaveAccessibleDescription('Готово · 1 из 1 Проверено 0 из 1 Замечаний · 1');

    await user.click(nextReview());
    expect(picker).toHaveValue(secondId);
    expect(screen.getByLabelText('Аудио фрагмента 1')).toHaveAttribute('src', '/api/projects/book1/artifacts/audio2');
    await update({ ...book, revision: 4, segments: [{ ...firstSegment, outputArtifactId: null }, book.segments[1]] });
    await user.click(nextCorrection());
    expect(picker).toHaveValue(firstId);
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveFocus();
    expect(screen.getByText(note)).toBeVisible();

    await update({ ...book, revision: 5, segments: [
      { ...firstSegment, outputArtifactId: 'newAudio', correction: { note, needsNewAudio: false } },
      book.segments[1],
    ] });
    expect(overview.queryByRole('button', { name: 'К следующему исправлению' })).not.toBeInTheDocument();
    await user.click(nextReview());
    const audio = screen.getByLabelText('Аудио фрагмента 1');
    expect(audio).toHaveAttribute('src', '/api/projects/book1/artifacts/newAudio');
    expect(audio).toHaveFocus();
    expect(screen.getByText('К повторной проверке')).toBeVisible();
  });
  it('shows audio progress for each text and shares selection and draft protection with the picker', async () => {
    const { user } = await open({
      ...multiple,
      segments: [segment('s1', 0, first.originalText, 'first-audio'), segment('s1b', 1, 'Продолжение.'),
        { ...multiple.segments[1], order: 2 }],
      textDocuments: [{ ...multiple.textDocuments[0], segmentIds: ['s1', 's1b'] }, multiple.textDocuments[1]],
    });
    await show(user, 'Тексты');
    const list = screen.getByRole('list', { name: 'Список текстов голоса' });
    const partial = within(list).getByRole('button', { name: 'Первая книга' });
    const complete = within(list).getByRole('button', { name: 'Вторая книга' });
    expect(partial).toHaveAccessibleDescription('Озвучено 1 из 2 Проверено 0 из 1');
    expect(partial).toHaveAttribute('aria-current', 'true');
    expect(complete).toHaveAccessibleDescription('Готово · 1 из 1 Проверено 0 из 1');
    await user.click(complete);
    expect(complete).toHaveAttribute('aria-current', 'true');
    await show(user, 'Озвучивание');
    const picker = screen.getByLabelText('Текст для озвучивания');
    expect(picker).toHaveValue(secondId);
    expect(screen.getByRole('region', { name: 'Готовое аудио' })).toBeVisible();
    await user.selectOptions(picker, firstId);
    expect(partial).toHaveAttribute('aria-current', 'true');
    const partialPlayer = within(screen.getByRole('region', { name: 'Готовое аудио' }));
    expect(partialPlayer.getByRole('button', { name: 'Прослушать готовые фрагменты' })).toBeVisible();
    expect(partialPlayer.queryByRole('link', { name: 'Скачать выбранный текст · WAV' })).not.toBeInTheDocument();
    await show(user, 'Тексты');
    const editor = screen.getByLabelText('Текст фрагмента 1');
    await user.type(editor, ' Правка.');
    expect(complete).toBeDisabled();
    expect(picker).toBeDisabled();
    await user.click(complete);
    expect(editor).toHaveValue(`${first.originalText} Правка.`);
  });
  it('plays saved fragments in text order, keeps playback on refresh, and stops on text change', async () => {
    const firstAudio = segment('s1', 0, 'Первая строка.', 'audio1');
    const nextAudio = segment('s1b', 1, 'Вторая строка.', 'audio1b');
    const initial = { ...multiple, segments: [nextAudio, firstAudio, { ...multiple.segments[1], order: 2 }],
      textDocuments: [{ ...multiple.textDocuments[0], segmentIds: ['s1', 's1b'] }, multiple.textDocuments[1]] };
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue();
    const pause = vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
    vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {});
    const { user, update } = await open(initial);
    const ready = screen.getByRole('region', { name: 'Готовое аудио' });
    await user.click(within(ready).getByRole('button', { name: 'Прослушать текст' }));
    const player = within(ready).getByLabelText('Аудио выбранного текста');
    expect(player).toHaveAttribute('src', '/api/projects/book1/artifacts/audio1');
    fireEvent.ended(player);
    expect(player).toHaveAttribute('src', '/api/projects/book1/artifacts/audio1b');
    expect(within(ready).getByText('Фрагмент 2 из 2')).toBeVisible();
    expect(play).toHaveBeenCalledTimes(2);
    fireEvent.ended(player);
    expect(play).toHaveBeenCalledTimes(2);
    await update({ ...initial, revision: 4 });
    expect(within(ready).getByLabelText('Аудио выбранного текста')).toBe(player);
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), secondId);
    const nextReady = screen.getByRole('region', { name: 'Готовое аудио' });
    expect(pause).toHaveBeenCalledTimes(1);
    expect(play).toHaveBeenCalledTimes(2);
    await user.click(within(nextReady).getByRole('button', { name: 'Прослушать текст' }));
    expect(within(nextReady).getByLabelText('Аудио выбранного текста')).toHaveAttribute('src', '/api/projects/book1/artifacts/audio2');
    await update({ ...initial, revision: 5, segments: initial.segments.map(value =>
      value.id === 's2' ? { ...value, outputArtifactId: 'new-audio2' } : value) });
    expect(pause).toHaveBeenCalledTimes(2);
    expect(play).toHaveBeenCalledTimes(3);
    await user.click(screen.getByRole('button', { name: 'Прослушать текст' }));
    expect(screen.getByLabelText('Аудио выбранного текста')).toHaveAttribute('src', '/api/projects/book1/artifacts/new-audio2');
  });
  it('plays available fragments at their original positions and keeps playing when more audio arrives', async () => {
    const initial = { ...single, segments: [
      segment('s3', 2, 'Третий фрагмент.', 'audio3'),
      segment('s1', 0, 'Первый фрагмент.', 'audio1'),
      segment('s2', 1, 'Второй фрагмент.'),
      segment('s4', 3, 'Четвёртый фрагмент.'),
    ] };
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue();
    const pause = vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
    const { user, update } = await open(initial);
    const ready = within(screen.getByRole('region', { name: 'Готовое аудио' }));

    expect(ready.getByText('Готово 2 из 4. Плеер пропускает фрагменты без аудио.')).toBeVisible();
    expect(ready.queryByRole('link', { name: 'Скачать всю книгу · WAV' })).not.toBeInTheDocument();
    await user.click(ready.getByRole('button', { name: 'Прослушать готовые фрагменты' }));
    const player = ready.getByLabelText('Аудио выбранного текста');
    expect(player).toHaveAttribute('src', '/api/projects/book1/artifacts/audio1');
    expect(ready.getAllByRole('option').map(option => option.textContent)).toEqual(['Фрагмент 1', 'Фрагмент 3']);
    fireEvent.ended(player);
    expect(player).toHaveAttribute('src', '/api/projects/book1/artifacts/audio3');
    expect(ready.getByText('Фрагмент 3 из 4')).toBeVisible();

    await update({ ...initial, revision: 3, segments: initial.segments.map(value =>
      value.outputArtifactId ? value : { ...value, outputArtifactId: `audio${value.order + 1}` }) });
    expect(ready.getByLabelText('Аудио выбранного текста')).toBe(player);
    expect(player).toHaveAttribute('src', '/api/projects/book1/artifacts/audio3');
    expect(ready.getByText('Фрагмент 3 из 4')).toBeVisible();
    expect(pause).not.toHaveBeenCalled();
    expect(ready.getByRole('link', { name: 'Скачать всю книгу · WAV' })).toBeVisible();
    fireEvent.ended(player);
    expect(player).toHaveAttribute('src', '/api/projects/book1/artifacts/audio4');
    expect(play).toHaveBeenCalledTimes(3);
  });
  it('opens the playing fragment through a draft filter and prepares only its saved revision', async () => {
    const fragments = Array.from({ length: 14 }, (_, index) =>
      segment(`s${index + 1}`, index, `Текст номера ${index + 1}.`, index === 12 ? 'audio13' : null));
    const initial = { ...single, segments: fragments };
    vi.spyOn(HTMLMediaElement.prototype, 'play').mockResolvedValue();
    const pause = vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
    vi.spyOn(HTMLMediaElement.prototype, 'load').mockImplementation(() => {});
    const { user, calls } = await open(initial, {
      'PATCH /api/projects/book1/segments/s13': init => ({
        body: { ...initial, revision: 3, segments: fragments.map(item => item.id === 's13'
          ? { ...item, spokenText: JSON.parse(init!.body as string).spokenText, outputArtifactId: null,
              previousAudio: { artifactId: 'audio13', spokenText: item.spokenText } } : item) },
        etag: '"3"',
      }),
      'POST /api/projects/book1/jobs': () => ({ body: job, etag: '"1"', status: 202 }),
    });
    await show(user, 'Тексты');
    const editor = within(screen.getByRole('region', { name: /Фрагменты текста/ }));
    const commands = () => calls.filter(call => call.path.endsWith('/jobs') && call.init?.method === 'POST');
    await user.type(editor.getByLabelText('Текст фрагмента 1'), ' Черновик.');
    await user.click(editor.getByRole('button', { name: 'Показать правки · 1' }));
    await show(user, 'Озвучивание');
    const player = within(screen.getByRole('region', { name: 'Готовое аудио' }));
    await user.click(player.getByRole('button', { name: 'Прослушать готовые фрагменты' }));
    await user.click(player.getByRole('button', { name: 'Открыть фрагмент в редакторе' }));

    const text = editor.getByLabelText('Текст фрагмента 13');
    expect(text).toHaveFocus();
    expect(editor.getByText('Страница 2 из 2')).toBeVisible();
    expect(editor.getByLabelText('Фильтр фрагментов')).toHaveValue('all');
    expect(pause).toHaveBeenCalledTimes(1);
    const prepare = () => editor.getByRole('button', { name: 'К озвучиванию фрагмента 13' });
    expect(prepare()).toBeDisabled();
    await user.click(editor.getByRole('button', { name: 'Показать правки · 1' }));
    expect(editor.getByLabelText('Текст фрагмента 1')).toHaveValue('Текст номера 1. Черновик.');
    await user.click(editor.getByRole('button', { name: 'Отменить правку фрагмента 1' }));
    await show(user, 'Озвучивание');
    await user.click(player.getByRole('button', { name: 'Открыть фрагмент в редакторе' }));
    await user.type(editor.getByLabelText('Текст фрагмента 13'), ' Исправлено.');
    expect(prepare()).toBeDisabled();
    expect(editor.getByText('После сохранения потребуется новое озвучивание.')).toBeVisible();
    await user.click(editor.getByRole('button', { name: 'Сохранить фрагмент 13' }));
    await screen.findByTitle('Версия 3');
    expect(editor.getByText('Нужно озвучить')).toBeVisible();
    expect(editor.queryByLabelText('Аудио фрагмента 13')).not.toBeInTheDocument();
    expect(screen.queryByRole('region', { name: 'Готовое аудио' })).not.toBeInTheDocument();
    const previous = editor.getByText('Предыдущая запись', { exact: true });
    await user.click(previous);
    expect(within(previous.closest('details')!).getByText('Текст номера 13.', { exact: true })).toBeVisible();
    expect(editor.getByLabelText('Предыдущая запись фрагмента 13')).toHaveAttribute('src', '/api/projects/book1/artifacts/audio13');
    await user.click(prepare());

    const selection = screen.getByRole('group', { name: 'Фрагменты для озвучивания' });
    expect(selection).toHaveFocus();
    expect(within(selection).getByText('Выбрано 1 из 14')).toBeVisible();
    expect(within(selection).getAllByRole('checkbox', { checked: true })).toEqual([
      within(selection).getByRole('checkbox', { name: 'Фрагмент 13' }),
    ]);
    expect(screen.getByLabelText('Фрагмент для пробы')).toHaveValue('s13');
    expect(commands()).toHaveLength(0);
    await user.click(screen.getByRole('button', { name: 'Озвучить выбранные фрагменты' }));
    expect(commands()).toHaveLength(1);
    expect(JSON.parse(commands()[0].init!.body as string)).toEqual({ kind: 'generation', segmentIds: ['s13'] });
    expect(new Headers(commands()[0].init?.headers).get('If-Match')).toBe('"3"');
  });
  it('marks only the current recording and keeps drafts in the audio review filter', async () => {
    const initial = { ...single, segments: [
      segment('s1', 0, 'Первая история.', 'audio1'),
      segment('s2', 1, 'Вторая история.', 'audio2'),
      segment('s3', 2, 'Третья история.'),
    ] };
    const reviewed = { ...initial, revision: 3, segments: [{ ...initial.segments[0], audioReviewed: true }, ...initial.segments.slice(1)] };
    const { user, calls, update } = await open(initial, {
      'PATCH /api/projects/book1/segments/s1/review': () => ({ body: reviewed, etag: '"3"' }),
    });
    await show(user, 'Тексты');
    const review = (number: number) => screen.getByRole('checkbox', { name: `Проверено на слух · фрагмент ${number}` });
    expect(screen.getByText('Проверено 0 из 2')).toBeVisible();
    await user.type(screen.getByLabelText('Текст фрагмента 2'), ' Черновик.');
    expect(review(2)).toBeDisabled();
    await user.selectOptions(screen.getByLabelText('Фильтр фрагментов'), 'unreviewed');
    expect(screen.queryByLabelText('Текст фрагмента 3')).not.toBeInTheDocument();
    await user.click(review(1));
    await screen.findByText('Проверено 1 из 2');
    expect(screen.queryByLabelText('Текст фрагмента 1')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Текст фрагмента 2')).toHaveValue('Вторая история. Черновик.');
    const patch = calls.find(call => call.path.endsWith('/s1/review'))!;
    expect(JSON.parse(patch.init!.body as string)).toEqual({ artifactId: 'audio1', reviewed: true });
    expect(new Headers(patch.init?.headers).get('If-Match')).toBe('"2"');
    await update({ ...reviewed, revision: 4, segments: [
      { ...initial.segments[0], outputArtifactId: 'newAudio1' }, ...initial.segments.slice(1),
    ] });
    expect(review(1)).not.toBeChecked();
    expect(screen.getByText('Проверено 0 из 2')).toBeVisible();
  });
  it('keeps a correction draft while filtering and shows saved notes in the correction list', async () => {
    const initial = { ...multiple, segments: [
      { ...multiple.segments[0], outputArtifactId: 'audio1', audioReviewed: true },
      multiple.segments[1],
    ] };
    const note = 'Ударение в слове «замок».';
    const saved = { ...initial, revision: 4, segments: [
      { ...initial.segments[0], audioReviewed: false, correction: { note, needsNewAudio: true } },
      initial.segments[1],
    ] };
    const { user, calls, update } = await open(initial, {
      'PATCH /api/projects/book1/segments/s1/correction': () => ({ body: saved, etag: '"4"' }),
    });
    await show(user, 'Тексты');
    const picker = screen.getByLabelText('Текст для озвучивания');
    const back = screen.getByRole('button', { name: '← Все голоса' });
    const exit = screen.getByRole('button', { name: 'Выйти из приложения' });
    const reloadIsGuarded = () => {
      const event = new Event('beforeunload', { cancelable: true });
      window.dispatchEvent(event);
      return event.defaultPrevented;
    };
    await user.click(screen.getByRole('button', { name: 'Добавить замечание к фрагменту 1' }));
    expect(screen.getByLabelText('Замечание к фрагменту 1')).toHaveFocus();
    await user.type(screen.getByLabelText('Замечание к фрагменту 1'), note);
    expect(picker).toBeDisabled();
    expect(back).toBeDisabled();
    expect(exit).toBeDisabled();
    expect(reloadIsGuarded()).toBe(true);
    await user.selectOptions(screen.getByLabelText('Фильтр фрагментов'), 'missing');
    const pending = screen.getByRole('button', { name: 'Показать правки · 1' });
    await user.click(pending);
    expect(pending).toHaveFocus();
    expect(screen.getByLabelText('Замечание к фрагменту 1')).toHaveValue(note);
    await user.click(screen.getByRole('button', { name: 'Сохранить замечание' }));
    await screen.findByTitle('Версия 4');
    expect(picker).toBeEnabled();
    expect(back).toBeEnabled();
    expect(exit).toBeEnabled();
    expect(reloadIsGuarded()).toBe(false);
    expect(screen.getByLabelText('Фильтр фрагментов')).toHaveFocus();

    const patch = calls.find(call => call.path.endsWith('/s1/correction'))!;
    expect(JSON.parse(patch.init!.body as string)).toEqual({
      note, expected: { artifactId: 'audio1', note: null, needsNewAudio: false },
    });
    expect(new Headers(patch.init?.headers).get('If-Match')).toBe('"3"');
    await user.selectOptions(screen.getByLabelText('Фильтр фрагментов'), 'corrections');
    expect(screen.getByText(note, { exact: true })).toBeVisible();
    expect(screen.getByRole('checkbox', { name: 'Проверено на слух · фрагмент 1' })).not.toBeChecked();
    await update({ ...saved, revision: 5, segments: [
      { ...saved.segments[0], outputArtifactId: null }, saved.segments[1],
    ] });
    expect(screen.getByText(note, { exact: true })).toBeVisible();
    expect(screen.getByLabelText('Текст фрагмента 1')).toBeVisible();
  });
  it('lets the user annotate a previous WAV and asks for a fresh choice when the recording changes', async () => {
    const withoutAudio = { ...single.segments[0], previousAudio: { artifactId: 'prior', spokenText: first.originalText } };
    const initial = { ...single, segments: [withoutAudio] };
    const replaced = { ...initial, revision: 3, segments: [{ ...withoutAudio, outputArtifactId: 'replacement' }] };
    const note = 'Проверить интонацию.';
    const { user, update, calls } = await open(initial, {
      'PATCH /api/projects/book1/segments/s1/correction': () => ({ body: { ...replaced, revision: 4,
        segments: [{ ...replaced.segments[0], correction: { note, needsNewAudio: true } }] }, etag: '"4"' }),
    });
    await show(user, 'Тексты');
    await user.click(screen.getByRole('button', { name: 'Добавить замечание к фрагменту 1' }));
    const input = screen.getByLabelText('Замечание к фрагменту 1');
    await user.type(input, note);
    await update(replaced);
    expect(input).toHaveValue(note);
    const save = screen.getByRole('button', { name: 'Сохранить замечание' });
    expect(save).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Продолжить с текущей записью' }));
    expect(input).toHaveValue(note);
    await user.click(save);
    expect(JSON.parse(calls.find(call => call.path.endsWith('/correction'))!.init!.body as string)).toEqual({
      note, expected: { artifactId: 'replacement', note: null, needsNewAudio: false },
    });

    const awaiting = { ...replaced, revision: 5, segments: [
      { ...replaced.segments[0], outputArtifactId: 'nextAudio', correction: { note, needsNewAudio: false } },
    ] };
    await update(awaiting);
    await user.click(screen.getByRole('button', { name: 'Изменить замечание к фрагменту 1' }));
    const amendment = screen.getByLabelText('Замечание к фрагменту 1');
    await user.type(amendment, ' В конце предложения.');
    await update({ ...awaiting, revision: 6, segments: [
      { ...awaiting.segments[0], correction: { note: `${note} В конце предложения.`, needsNewAudio: true } },
    ] });
    expect(amendment).toHaveValue(`${note} В конце предложения.`);
    expect(screen.getByRole('button', { name: 'Продолжить с текущей записью' })).toBeVisible();
  });
  it('reports a playback error and lets the user retry the first fragment', async () => {
    const play = vi.spyOn(HTMLMediaElement.prototype, 'play')
      .mockRejectedValueOnce(new Error('unavailable')).mockResolvedValue();
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
    const { user } = await open({ ...single, segments: [segment('s1', 0, first.originalText, 'audio1')] });
    const ready = screen.getByRole('region', { name: 'Готовое аудио' });
    await user.click(within(ready).getByRole('button', { name: 'Прослушать текст' }));
    expect(await within(ready).findByRole('alert')).toHaveTextContent('Не удалось воспроизвести этот фрагмент.');
    await user.click(within(ready).getByRole('button', { name: 'Слушать с начала' }));
    expect(within(ready).queryByRole('alert')).not.toBeInTheDocument();
    expect(play).toHaveBeenCalledTimes(2);
  });
  it('starts a blank append after the initial text import is accepted', async () => {
    const { user } = await open({ ...single, revision: 1, textDocument: null, segments: [] }, { 'POST /api/projects/book1/text/paste': () => ({ body: single, etag: '"2"' }) }, true);
    await show(user, 'Тексты');
    await user.type(screen.getByLabelText('Текст книги'), first.originalText); await user.click(screen.getByRole('button', { name: 'Импортировать текст' }));
    await screen.findByTitle('Версия 2');
    await show(user, 'Озвучивание');
    expect(screen.getByRole('button', { name: 'Озвучить выбранные фрагменты' })).toBeEnabled();
    await show(user, 'Тексты');
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); expect(screen.getByLabelText('Текст книги')).toHaveValue('');
  });
  it('sends a book split only when requested and keeps the file for a corrected retry', async () => {
    const { user, calls } = await open({ ...single, revision: 1, textDocument: null, segments: [] }, {
      'POST /api/projects/book1/text/file': async init => (await (init!.body as Blob).text()).includes('"splitChapters":true')
        ? { body: { code: 'text.chapters.not_found' }, status: 400 }
        : { body: single, etag: '"2"' },
    }, true);
    await show(user, 'Тексты');
    await user.click(screen.getByLabelText('Файл TXT'));
    const file = new File(['Глава 1\nЁжик читает.'], 'book.txt', { type: 'text/plain' });
    const input = screen.getByLabelText<HTMLInputElement>('Текстовый файл TXT');
    await user.upload(input, file);
    const split = screen.getByRole('checkbox', { name: 'Разделить по заголовкам глав' });
    expect(split).not.toBeChecked();
    await user.click(split);
    await user.click(screen.getByRole('button', { name: 'Импортировать книгу' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Нужны хотя бы два заголовка глав');
    expect(input.files?.[0]).toBe(file);
    await user.click(split);
    await user.click(screen.getByRole('button', { name: 'Импортировать текст' }));
    await screen.findByTitle('Версия 2');
    const uploads = await Promise.all(calls.filter(call => call.path.endsWith('/text/file')).map(call => (call.init!.body as Blob).text()));
    expect(uploads).toHaveLength(2);
    expect(uploads[0]).toContain('"splitChapters":true');
    expect(uploads[1]).not.toContain('splitChapters');
    expect(uploads.every(body => body.includes('Глава 1\nЁжик читает.'))).toBe(true);
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' }));
    expect(screen.queryByRole('checkbox', { name: 'Разделить по заголовкам глав' })).not.toBeInTheDocument();
    expect(screen.getByLabelText<HTMLInputElement>('Текстовый файл TXT').files).toHaveLength(0);
  });
  it('starts an imported book at its first chapter', async () => {
    const { user } = await open({ ...single, revision: 1, textDocument: null, segments: [] }, {
      'POST /api/projects/book1/text/file': () => ({ body: multiple, etag: '"3"' }),
    }, true);
    await show(user, 'Тексты');
    await user.click(screen.getByLabelText('Файл TXT'));
    await user.upload(screen.getByLabelText('Текстовый файл TXT'), new File(['Глава 1\nПервая история.\nГлава 2\nВторая история.'], 'book.txt'));
    await user.click(screen.getByRole('checkbox', { name: 'Разделить по заголовкам глав' }));
    await user.click(screen.getByRole('button', { name: 'Импортировать книгу' }));
    const firstChapter = await screen.findByRole('button', { name: 'Первая книга' });
    expect(firstChapter).toHaveAttribute('aria-current', 'true');
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue(first.originalText);
  });
  it('keeps a pasted append intent through a lost response and clears only after the document is acknowledged', async () => {
    const { user, calls, update } = await open(single, { 'POST /api/projects/book1/text/append/paste': () => { throw new Error('response lost'); } });
    await show(user, 'Тексты');
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
    await show(user, 'Тексты');
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
    await user.click(screen.getByText('Выбор фрагментов'));
    await user.click(within(screen.getByRole('group', { name: 'Фрагменты для озвучивания' })).getByRole('checkbox'));
    await user.selectOptions(screen.getByLabelText('Текст для озвучивания'), secondId);
    expect(screen.getByLabelText('Голос для озвучивания')).toHaveValue('saved1'); expect(screen.getByLabelText(/Подтверждаю экспериментальное применение сохранённой модели/)).toBeChecked();
    await show(user, 'Тексты');
    expect(screen.getByRole('button', { name: 'Вторая книга' })).toHaveAttribute('aria-current', 'true');
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue(second.originalText);
    await show(user, 'Озвучивание');
    await user.click(screen.getByText('Выбор фрагментов'));
    expect(within(screen.getByRole('group', { name: 'Фрагменты для озвучивания' })).getByRole('checkbox')).toBeChecked();
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
    await show(user, 'Тексты');
    await user.type(screen.getByLabelText('Текст фрагмента 1'), ' Правка.'); await user.type(screen.getByLabelText('Текст фрагмента 2'), ' Другая.');
    expect(screen.getByLabelText('Текст для озвучивания')).toBeDisabled(); expect(screen.getByRole('button', { name: 'Добавить новый текст' })).toBeDisabled();
    await update({ ...initial, revision: 4 }); expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue('Первая строка. Правка.');
    await user.click(screen.getByRole('button', { name: 'Отменить правку фрагмента 2' })); expect(screen.getByLabelText('Текст фрагмента 2')).toHaveValue('Вторая строка.'); expect(screen.getByLabelText('Текст для озвучивания')).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' })); await screen.findByTitle('Версия 5'); expect(screen.getByLabelText('Текст для озвучивания')).toBeEnabled(); expect(screen.getByRole('button', { name: 'Добавить новый текст' })).toBeEnabled();
  });
  it('retains a dirty first text when another tab appends the second, then selects the new tail after cancel', async () => {
    const { user, update } = await open(single);
    await show(user, 'Тексты');
    await user.click(screen.getByRole('button', { name: 'Добавить новый текст' })); await user.type(screen.getByLabelText('Текст книги'), 'Будущий текст.');
    await user.type(screen.getByLabelText('Текст фрагмента 1'), ' Несохранённая правка.');
    expect(screen.getByLabelText('Текст книги')).toBeDisabled(); expect(screen.getByLabelText('Текст книги')).toHaveValue('Будущий текст.');
    await update(multiple);
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue(`${first.originalText} Несохранённая правка.`); expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(firstId); expect(screen.getByLabelText('Текст для озвучивания')).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Отменить правку фрагмента 1' }));
    expect(screen.getByLabelText('Текст для озвучивания')).toHaveValue(secondId); expect(screen.getByLabelText('Текст книги')).toHaveValue('Будущий текст.');
  });
  it('shows captured text titles and blocks append and editing for an unfinished generation job', async () => {
    const { user } = await open(multiple, { 'GET /api/projects/book1/jobs': () => ({ body: [{ ...job, state: 'paused', completedUtc: null, textTitle: 'Вторая книга' }] }) }, true);
    expect(await screen.findByText('Текст: Вторая книга')).toBeVisible();
    await show(user, 'Тексты');
    expect(screen.getByRole('button', { name: 'Добавить новый текст' })).toBeDisabled(); expect(screen.getByLabelText('Текст фрагмента 1')).toBeDisabled();
  });
});
