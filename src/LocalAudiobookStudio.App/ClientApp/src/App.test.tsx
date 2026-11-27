import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import App from './App';

const empty = { id: 'book1', displayName: 'Моя книга', revision: 1, voiceRecording: null, voiceAnchor: null, textDocument: null, segments: [] };
const source = 'Ёжик. Ма\u0301ма.';
const text = { ...empty, revision: 2,
  textDocument: { sourceKind: 'Paste', displayName: 'Текст книги', encoding: 'utf-8', sha256: 'hash', byteCount: 25, originalText: source, normalizedText: 'Ёжик. Ма\u0301ма.' },
  segments: [{ id: 'segment1', order: 0, originalText: source, normalizedText: source, spokenText: source, contentHash: 'hash', status: 'Pending' }] };
const recording = { ...empty, revision: 2, voiceRecording: { artifactId: 'record1', sha256: 'hash', byteLength: 2880044, durationMilliseconds: 30000, hasClippingWarning: false } };
const twoSegments = { ...text, segments: [text.segments[0], { ...text.segments[0], id: 'segment2', order: 1, originalText: 'Вторая история.', normalizedText: 'Вторая история.', spokenText: 'Вторая история.' }] };
const bootstrap = { version: '1.0', antiforgeryRequestToken: 'csrf', capabilities: { generation: false, training: false } };
type Reply = { body: unknown; status?: number; etag?: string };
function serve(replies: Record<string, Reply | Reply[]>) {
  const requests: { path: string; init?: RequestInit }[] = [];
  let latest: unknown = empty;
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    requests.push({ path, init });
    const entry = replies[`${init?.method ?? 'GET'} ${path}`]
      ?? (path === '/api/voxcpm-training/readiness' ? { body: { ready: false, diagnosticCode: 'voxcpm_training.runtime.not_ready', provider: 'voxcpm2-lora-cpu-v1', cpuThreads: 2 } }
        : path.endsWith('/voxcpm-training') && init?.method === 'GET' ? { body: null }
        : path === '/api/tts-corpus/readiness' ? { body: { ready: false, diagnosticCode: 'tts_corpus.runtime.not_ready', provider: 'whisper-large-v3-turbo', cpuThreads: 2 } }
        : path.endsWith('/tts-corpus') && init?.method === 'GET' ? { body: null }
        : path === '/api/training/readiness' ? { body: { ready: false, experimental: true, diagnosticCode: 'training.runtime.not_ready', cpuThreads: 2 } }
        : path.endsWith('/training') && init?.method === 'GET' ? { body: null }
        : path === '/api/runtime/readiness' ? { body: { ready: false, experimental: false, diagnosticCode: 'worker.runtime.not_ready', profileId: 'q8-f16-cpu' } }
        : path.endsWith('/jobs') && init?.method === 'GET' ? { body: [] }
        : path === '/api/projects/book1' && init?.method === 'GET' ? { body: latest, etag: `"${(latest as typeof empty).revision}"` } : undefined);
    if (!entry) throw new Error(`Unexpected request ${path}`);
    const reply = Array.isArray(entry) ? entry.shift()! : entry;
    if ((reply.status ?? 200) < 300 && (reply.body as typeof empty)?.id === 'book1') latest = reply.body;
    return new Response(JSON.stringify(reply.body), { status: reply.status ?? 200, headers: { 'Content-Type': 'application/json', ...(reply.etag ? { ETag: reply.etag } : {}) } });
  });
  return requests;
}
async function launch(snapshot: unknown = empty, extras: Record<string, Reply | Reply[]> = {}) {
  const calls = serve({ 'GET /api/bootstrap': { body: bootstrap }, 'GET /api/projects': { body: [] }, 'POST /api/projects': { body: snapshot, status: 201, etag: '"1"' }, ...extras });
  const user = userEvent.setup(); render(<App />);
  await screen.findByText('Пока нет голосов');
  await user.type(screen.getByLabelText('Название голоса'), 'Моя книга');
  await user.click(screen.getByRole('button', { name: 'Добавить голос' }));
  await screen.findByRole('heading', { name: 'Моя книга' });
  return { user, calls };
}
describe('Stage 1 project setup', () => {
  it('uploads a voice message and opens corpus preparation straight from the saved recording', async () => {
    const { user, calls } = await launch(empty, {
      'GET /api/bootstrap': { body: { ...bootstrap, capabilities: { ...bootstrap.capabilities, voiceMessages: true } } },
      'POST /api/projects/book1/voice/recording': { body: recording, etag: '"2"' },
    });
    await user.click(screen.getByRole('button', { name: 'Голос' }));
    const input = screen.getByLabelText('Файл записи голоса');
    expect(input).toHaveAttribute('accept', '.wav,.ogg,.oga,.opus,.m4a,.mp3');
    await user.upload(input, new File(['synthetic'], 'voice.ogg', { type: 'audio/ogg' }));
    await user.click(screen.getByLabelText(/мой голос/));
    await user.click(screen.getByRole('button', { name: 'Сохранить запись' }));
    await screen.findByText(/Исходная запись сохранена/);
    const upload = calls.find(call => call.path.endsWith('/voice/recording'))?.init?.body as Blob;
    const body = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(reader.error);
      reader.readAsText(upload);
    });
    expect(body).toContain('{"fileName":"voice.ogg","rightsConfirmed":true}');
    await user.click(screen.getByRole('button', { name: 'Перейти к дообучению' }));
    expect(screen.getByRole('heading', { name: 'Дообучение голоса' })).toHaveFocus();
    expect(screen.getByRole('button', { name: 'Подготовить запись для обучения VoxCPM2' })).toBeVisible();
  });
  it('shows accepted legacy text and voice read-only and hides incompatible imports', async () => {
    const { user } = await launch({ ...text, textDocument: null, voiceAnchor: null, hasAcceptedText: true, hasAcceptedVoice: true });
    await user.click(screen.getByRole('button', { name: 'Открыть тексты' }));
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue(source);
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveAttribute('readonly');
    expect(screen.queryByRole('button', { name: 'Сохранить фрагмент 1' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Импортировать текст' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Голос' }));
    expect(screen.queryByLabelText('Запись голоса WAV')).not.toBeInTheDocument();
    expect(screen.queryByRole('heading', { name: 'Выберите голосовой фрагмент' })).not.toBeInTheDocument();
    expect(screen.getByText(/Голос сохранён в старом формате/)).toBeInTheDocument();
    expect(screen.getAllByText(/добавьте/).length).toBeGreaterThan(0);
  });
  it('limits submitted titles to 120 UTF-16 units and explains the same server limit', async () => {
    const calls = serve({ 'GET /api/bootstrap': { body: bootstrap }, 'GET /api/projects': { body: [] },
      'POST /api/projects': { body: { code: 'project.title.invalid' }, status: 400 } });
    const user = userEvent.setup(); render(<App />); await screen.findByText('Пока нет голосов');
    await user.type(screen.getByLabelText('Название голоса'), 'я'.repeat(121));
    await user.click(screen.getByRole('button', { name: 'Добавить голос' }));
    expect(JSON.parse(calls.find(x => x.init?.method === 'POST')!.init!.body as string).displayName).toHaveLength(120);
    expect(await screen.findByRole('alert')).toHaveTextContent('до 120 символов UTF-16');
  });
  it('retains an independently edited segment while saving another with successive ETags', async () => {
    const firstSaved = { ...twoSegments, revision: 3, segments: [{ ...twoSegments.segments[0], spokenText: 'Первая правка.' }, twoSegments.segments[1]] };
    const bothSaved = { ...firstSaved, revision: 4, segments: [firstSaved.segments[0], { ...firstSaved.segments[1], spokenText: 'Вторая правка.' }] };
    const { user, calls } = await launch(twoSegments, {
      'POST /api/projects': { body: twoSegments, etag: '"2"' },
      'PATCH /api/projects/book1/segments/segment1': { body: firstSaved, etag: '"3"' },
      'PATCH /api/projects/book1/segments/segment2': { body: bothSaved, etag: '"4"' },
    });
    await user.click(screen.getByRole('button', { name: 'Тексты' }));
    await user.clear(screen.getByLabelText('Текст фрагмента 1')); await user.type(screen.getByLabelText('Текст фрагмента 1'), 'Первая правка.');
    await user.clear(screen.getByLabelText('Текст фрагмента 2')); await user.type(screen.getByLabelText('Текст фрагмента 2'), 'Вторая правка.');
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' }));
    await screen.findByTitle('Версия 3');
    expect(screen.getByLabelText('Текст фрагмента 2')).toHaveValue('Вторая правка.');
    expect(screen.getByRole('button', { name: 'Сохранить фрагмент 1' })).toBeDisabled();
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 2' }));
    await screen.findByTitle('Версия 4');
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue('Первая правка.');
    expect(screen.getByRole('button', { name: 'Сохранить фрагмент 2' })).toBeDisabled();
    expect(calls.filter(x => x.init?.method === 'PATCH').map(x => new Headers(x.init?.headers).get('If-Match'))).toEqual(['"2"', '"3"']);
  });
  it('retains pasted book text while saving the unrelated recording', async () => {
    const { user } = await launch(empty, { 'POST /api/projects/book1/voice/recording': { body: recording, etag: '"2"' } });
    await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    await user.type(screen.getByLabelText('Текст книги'), 'Несохранённая книга с ё.');
    await user.click(screen.getByRole('button', { name: 'Голос' }));
    await user.upload(screen.getByLabelText('Запись голоса WAV'), new File(['synthetic'], 'voice.wav', { type: 'audio/wav' }));
    await user.click(screen.getByLabelText(/мой голос/));
    await user.click(screen.getByRole('button', { name: 'Сохранить запись' }));
    await screen.findByRole('heading', { name: 'Выберите голосовой фрагмент' });
    await user.click(screen.getByRole('button', { name: 'Тексты' }));
    expect(screen.getByLabelText('Текст книги')).toHaveValue('Несохранённая книга с ё.');
  });
  it('retains unconfirmed voice range, transcript and explicit confirmation across text import', async () => {
    vi.spyOn(HTMLMediaElement.prototype, 'pause').mockImplementation(() => {});
    const { user } = await launch(recording, {
      'POST /api/projects': { body: recording, etag: '"2"' },
      'POST /api/projects/book1/text/paste': { body: { ...text, revision: 3, voiceRecording: recording.voiceRecording }, etag: '"3"' },
    });
    await user.click(screen.getByRole('button', { name: 'Голос' }));
    await user.clear(screen.getByLabelText('Начало, секунды')); await user.type(screen.getByLabelText('Начало, секунды'), '5');
    await user.clear(screen.getByLabelText('Конец, секунды')); await user.type(screen.getByLabelText('Конец, секунды'), '17');
    await user.type(screen.getByLabelText('Точный текст фрагмента'), 'Ёжик читает ма\u0301ме.');
    await user.click(screen.getByLabelText('Прослушал и подтверждаю'));
    await user.click(screen.getByRole('button', { name: 'Тексты' }));
    await user.type(screen.getByLabelText('Текст книги'), source);
    await user.click(screen.getByRole('button', { name: 'Импортировать текст' }));
    await screen.findByTitle('Версия 3');
    await user.click(screen.getByRole('button', { name: 'Озвучивание' }));
    await user.click(screen.getByRole('button', { name: 'Подтвердить образец' }));
    expect(screen.getByLabelText('Начало, секунды')).toHaveValue(5);
    expect(screen.getByLabelText('Конец, секунды')).toHaveValue(17);
    expect(screen.getByLabelText('Точный текст фрагмента')).toHaveValue('Ёжик читает ма\u0301ме.');
    expect(screen.getByLabelText('Прослушал и подтверждаю')).toBeChecked();
    await user.type(screen.getByLabelText('Точный текст фрагмента'), ' Ещё.');
    expect(screen.getByLabelText('Прослушал и подтверждаю')).not.toBeChecked();
  });
  it('refreshes the conflicting editor even when its server text is unchanged while retaining another dirty draft', async () => {
    const authoritative = { ...twoSegments, revision: 3, segments: [twoSegments.segments[0], { ...twoSegments.segments[1], spokenText: 'Чужая вторая правка.' }] };
    const secondSaved = { ...authoritative, revision: 4, segments: [authoritative.segments[0], { ...authoritative.segments[1], spokenText: 'Моя вторая правка.' }] };
    const { user, calls } = await launch(twoSegments, {
      'POST /api/projects': { body: twoSegments, etag: '"2"' },
      'PATCH /api/projects/book1/segments/segment1': { body: { code: 'project.conflict', conflictType: 'project', currentSnapshot: authoritative }, status: 412, etag: '"3"' },
      'PATCH /api/projects/book1/segments/segment2': { body: secondSaved, etag: '"4"' },
    });
    await user.click(screen.getByRole('button', { name: 'Тексты' }));
    await user.clear(screen.getByLabelText('Текст фрагмента 1')); await user.type(screen.getByLabelText('Текст фрагмента 1'), 'Несохранённая первая правка.');
    await user.clear(screen.getByLabelText('Текст фрагмента 2')); await user.type(screen.getByLabelText('Текст фрагмента 2'), 'Моя вторая правка.');
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' }));
    await screen.findByRole('alert');
    expect(screen.getByRole('alert')).toHaveTextContent(/Правка фрагмента не сохранена. Загружена текущая версия фрагмента/);
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue(source);
    expect(screen.getByLabelText('Текст фрагмента 2')).toHaveValue('Моя вторая правка.');
    expect(calls.filter(x => x.init?.method === 'PATCH')).toHaveLength(1);
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 2' }));
    await screen.findByTitle('Версия 4');
    expect(new Headers(calls.filter(x => x.init?.method === 'PATCH')[1].init?.headers).get('If-Match')).toBe('"3"');
  });
  it('bootstraps once and guides the new project to text import', async () => {
    const { user, calls } = await launch();
    expect(calls.filter(x => x.path === '/api/bootstrap')).toHaveLength(1);
    const create = calls.find(x => x.path === '/api/projects' && x.init?.method === 'POST')!;
    expect(new Headers(create.init!.headers).get('X-CSRF-TOKEN')).toBe('csrf');
    expect(new Headers(create.init!.headers).get('Idempotency-Key')).toBeTruthy();
    expect(new Headers(create.init!.headers).has('Origin')).toBe(false);
    expect(screen.queryByRole('button', { name: 'Озвучить выбранные фрагменты' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Обучить голос' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    expect(screen.getByLabelText('Текст книги')).toBeVisible();
    await user.click(screen.getByRole('button', { name: 'Дообучение' }));
    expect(screen.queryByRole('heading', { name: 'Подготовка записи' })).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Добавить запись голоса' }));
    expect(screen.getByLabelText('Запись голоса WAV')).toBeVisible();
  });
  it('renders a stable Russian error and never raw server detail', async () => {
    serve({ 'GET /api/bootstrap': { body: { code: 'session_required', detail: 'C:\\private\\secret' }, status: 401 } });
    render(<App />);
    expect(await screen.findByRole('alert')).toHaveTextContent('Откройте студию через ярлык');
    expect(screen.queryByText(/private/)).not.toBeInTheDocument();
  });
  it('requires voice rights and explains the supported format when the decoder is absent', async () => {
    const { user, calls } = await launch(empty, { 'POST /api/projects/book1/voice/recording': { body: recording, etag: '"2"' } });
    await user.click(screen.getByRole('button', { name: 'Голос' }));
    expect(screen.getByText(/PCM16, моно, 48 кГц/)).toBeInTheDocument();
    expect(screen.getByLabelText('Запись голоса WAV')).toHaveAttribute('accept', '.wav,audio/wav');
    await user.upload(screen.getByLabelText('Запись голоса WAV'), new File(['synthetic'], 'voice.wav', { type: 'audio/wav' }));
    expect(screen.getByRole('button', { name: 'Сохранить запись' })).toBeDisabled();
    await user.click(screen.getByLabelText(/мой голос/));
    await user.click(screen.getByRole('button', { name: 'Сохранить запись' }));
    await screen.findByRole('heading', { name: 'Выберите голосовой фрагмент' });
    const upload = calls.find(x => x.path.endsWith('/voice/recording'))!;
    expect(new Headers(upload.init?.headers).get('If-Match')).toBe('"1"');
    expect(new Headers(upload.init?.headers).get('Content-Type')).toMatch(/^multipart\/form-data; boundary=las-/);
    expect(upload.init?.body).toBeInstanceOf(Blob);
    const wire = await new Promise<string>((resolve, reject) => { const reader = new FileReader(); reader.onload = () => resolve(reader.result as string); reader.onerror = reject; reader.readAsText(upload.init?.body as Blob); });
    expect(wire).toContain('name="metadata"\r\nContent-Type: application/json\r\n\r\n{"fileName":"voice.wav","rightsConfirmed":true}');
    expect(wire).toContain('name="file"; filename="upload"');
    expect(wire).toContain('synthetic');
  });
  it('offers paste or TXT input and displays original and normalized text distinctly', async () => {
    const { user } = await launch(empty, { 'POST /api/projects/book1/text/paste': { body: { ...text, textDocument: { ...text.textDocument, normalizedText: 'Ёжик. Мама.' } }, etag: '"2"' } });
    await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    await user.click(screen.getByLabelText('Файл TXT'));
    expect(screen.getByLabelText('Текстовый файл TXT')).toBeInTheDocument();
    await user.click(screen.getByLabelText('Вставить текст'));
    await user.type(screen.getByLabelText('Текст книги'), source);
    await user.click(screen.getByRole('button', { name: 'Импортировать текст' }));
    await user.click(await screen.findByText('Сверить с исходным текстом'));
    const original = screen.getByRole('region', { name: 'Исходный текст' });
    expect(original.textContent).toBe(source);
    expect(screen.getByRole('region', { name: 'Текст после подготовки' }).textContent).toBe('Ёжик. Мама.');
  });
  it('saves spoken text with the latest ETag, then refreshes a stale snapshot without replaying', async () => {
    const changed = { ...text, revision: 3, segments: [{ ...text.segments[0], spokenText: 'Ёжик говорит.' }] };
    const refreshed = { ...changed, revision: 4, segments: [{ ...changed.segments[0], spokenText: 'Текст из другой вкладки.' }] };
    const { user, calls } = await launch(text, { 'POST /api/projects': { body: text, etag: '"2"' }, 'PATCH /api/projects/book1/segments/segment1': [
      { body: changed, etag: '"3"' }, { body: { code: 'project.conflict', conflictType: 'project', detail: 'private', currentSnapshot: refreshed }, etag: '"4"', status: 412 }, { body: { ...refreshed, revision: 5 }, etag: '"5"' },
    ] });
    await user.click(screen.getByRole('button', { name: 'Тексты' }));
    let edit = screen.getByLabelText('Текст фрагмента 1');
    await user.clear(edit); await user.type(edit, 'Ёжик говорит.');
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' }));
    await waitFor(() => expect(screen.getByTitle('Версия 3')).toBeInTheDocument());
    edit = screen.getByLabelText('Текст фрагмента 1'); await user.clear(edit); await user.type(edit, 'Несохранённая правка');
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/из другой вкладки/);
    expect(screen.getByLabelText('Текст фрагмента 1')).toHaveValue('Текст из другой вкладки.');
    expect(calls.filter(x => x.init?.method === 'PATCH').map(x => new Headers(x.init?.headers).get('If-Match'))).toEqual(['"2"', '"3"']);
    edit = screen.getByLabelText('Текст фрагмента 1'); await user.clear(edit); await user.type(edit, 'Следующая правка');
    await user.click(screen.getByRole('button', { name: 'Сохранить фрагмент 1' }));
    await waitFor(() => expect(screen.getByTitle('Версия 5')).toBeInTheDocument());
    expect(new Headers(calls.filter(x => x.init?.method === 'PATCH')[2].init?.headers).get('If-Match')).toBe('"4"');
  });
  it('reopens from the authoritative catalog snapshot after page reload', async () => {
    serve({ 'GET /api/bootstrap': { body: bootstrap }, 'GET /api/projects': { body: [{ id: 'book1', displayName: 'Моя книга', createdUtc: '2026-10-09T00:00:00Z', lastOpenedUtc: '2026-10-09T00:00:00Z', revision: 2, hasRecording: false, hasAnchor: false, hasText: true }] },
      'GET /api/projects/book1': { body: text, etag: '"2"' }, 'POST /api/projects/book1/open': { body: text, etag: '"2"' } });
    const user = userEvent.setup(); render(<App />);
    await user.click(await screen.findByRole('button', { name: 'Открыть голос «Моя книга»' }));
    await user.click(await screen.findByRole('button', { name: 'Тексты' }));
    expect(await screen.findByLabelText('Текст фрагмента 1')).toHaveValue(source);
    await user.click(screen.getByText('Сверить с исходным текстом'));
    expect(within(screen.getByRole('region', { name: 'Исходный текст' })).queryByText(source)).toBeInTheDocument();
  });
  it('explains legacy reimport requires a new project', async () => {
    const { user } = await launch(empty, { 'POST /api/projects/book1/text/paste': { body: { code: 'text.reimport_required' }, status: 409 } });
    await user.click(screen.getByRole('button', { name: 'Добавить текст' }));
    await user.type(screen.getByLabelText('Текст книги'), source);
    await user.click(screen.getByRole('button', { name: 'Импортировать текст' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/Добавьте голос заново/);
  });
});
