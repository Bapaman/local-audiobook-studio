import type { Bootstrap, JobControl, JobKind, JobResponse, JobSnapshot, ProjectListItem, ProjectSnapshot, RuntimeReadiness, SavedVoiceSelection, SnapshotResponse, TrainingControl, TrainingReadiness, TrainingResponse, TrainingSnapshot, TtsCorpusClipUpdate, TtsCorpusReadiness, TtsCorpusResponse, TtsCorpusSnapshot } from './types';
import type { VoxCpmReferenceCatalog, VoxCpmTrainingReadiness, VoxCpmTrainingResponse, VoxCpmTrainingSnapshot } from './types';

const messages: Record<string, string> = {
  'voxcpm_training.conflict': 'Состояние дообучения изменилось. Проверьте текущий прогресс перед следующей командой.',
  'voxcpm_training.runtime.not_ready': 'Локальный движок дообучения не готов. Сохранённые модели и история остаются доступны.',
  'voxcpm_training.runtime.changed': 'Файлы движка изменились. Для продолжения нужен прежний проверенный движок.',
  'voxcpm_training.confirmation.required': 'Подтвердите экспериментальное обучение.',
  'voxcpm_training.active': 'Дообучение уже выполняется. Дождитесь окончания прохода или остановите его.',
  'voxcpm_training.transition.invalid': 'Команда недоступна в текущем состоянии. Проверьте обновлённый прогресс.',
  'voxcpm_training.source.changed': 'Исходная запись изменилась. Продолжение этой линии недоступно.',
  'voxcpm_training.corpus.changed': 'Подтверждённый корпус изменился. Продолжение этой линии заблокировано.',
  'voxcpm_training.corpus.invalid': 'Не удалось проверить подтверждённый корпус. Проверьте сохранённую версию и исходную запись.',
  'voxcpm_training.checkpoint.invalid': 'Не удалось проверить сохранение модели. Последний принятый шаг не заменён.',
  'voxcpm_training.idempotency.conflict': 'Содержимое повторного запроса изменилось. Проверьте выбранную команду.',
  'voxcpm_training.idempotency.expired': 'Ключ старого запроса устарел. Проверьте историю: автоматически новое обучение не запускается.',
  'voxcpm_training.disk.insufficient': 'Недостаточно свободного места. Старые модели сохранены; освободите место перед продолжением.',
  'voxcpm_training.memory.insufficient': 'Для локального обучения нужно не менее 16 ГиБ свободной оперативной памяти.',
  'voxcpm_training.memory.reserve': 'Обучение остановлено, чтобы оставить память системе. Последний принятый шаг сохранён.',
  'voxcpm_training.window.deadline': 'Текущий фрагмент не уложился в лимит времени. Последний принятый шаг сохранён.',
  'voxcpm_training.cache.changed': 'Проверка базовых весов не пройдена. Дальнейшее обучение заблокировано.',
  'voxcpm_training.cache.busy': 'Базовые веса заняты другим запуском. Дождитесь его завершения.',
  'voxcpm_training.cleanup': 'Студия ещё не подтвердила остановку процесса. Новые вычисления заблокированы.',
  'voxcpm_training.interrupted': 'Работа была прервана. Продолжение возможно только по вашей команде после проверки сохранения.',
  'voxcpm_training.state.limit': 'Достигнут лимит сохранений этой линии. Существующие модели не удаляются.',
  'voxcpm_training.failed': 'Не удалось завершить проход. Последнее принятое сохранение остаётся в истории.',
  'tts_corpus.conflict': 'Корпус изменён из другой вкладки. Показано текущее состояние; проверьте правки перед следующей командой.',
  'tts_corpus.runtime.not_ready': 'Локальное распознавание не настроено или его файлы изменились. Подготовка станет доступна после проверки движка.',
  'tts_corpus.active': 'Подготовка корпуса ещё выполняется. Дождитесь завершения или остановите её.',
  'tts_corpus.experimental.confirmation': 'Подтвердите экспериментальную подготовку корпуса.',
  'tts_corpus.transition.invalid': 'Это действие недоступно в текущем состоянии корпуса. Проверьте обновлённое состояние.',
  'tts_corpus.resume.unavailable': 'Продолжение недоступно для этой записи, плана или локального движка.',
  'tts_corpus.source.changed': 'Исходная запись изменилась. Продолжение этого корпуса недоступно.',
  'tts_corpus.runtime.changed': 'Локальный движок изменился. Продолжение этого корпуса недоступно.',
  'tts_corpus.clip.invalid': 'Проверьте текст клипа: нужен дословный текст до 4000 символов.',
  'tts_corpus.review.required': 'Сначала сохраните правки, затем отдельно подтвердите каждый включённый клип.',
  'tts_corpus.approval.invalid': 'Для подтверждения нужен непустой корпус с проверенным текстом и границами.',
  'tts_corpus.approval.confirmation': 'Подтвердите дословный текст и границы всех включённых клипов.',
  'tts_corpus.idempotency.conflict': 'Данные повторного запроса изменились. Проверьте корпус и выполните действие заново.',
  'tts_corpus.idempotency.expired': 'Этот старый запрос уже нельзя повторить. Обновите данные перед новым действием.',
  'tts_corpus.confirmation.required': 'Явно подтвердите подготовку или проверку текста и границ корпуса перед этим действием.',
  'tts_corpus.source.missing': 'Сначала сохраните исходную запись голоса в этом проекте.',
  'tts_corpus.text.invalid': 'Проверьте дословный текст: у включённого клипа он должен быть непустым и не длиннее 4000 символов.',
  'tts_corpus.empty': 'Нельзя подтвердить пустой корпус. Включите и проверьте хотя бы один клип с текстом.',
  'tts_corpus.clip.missing': 'Клип недоступен. Обновите состояние корпуса.',
  'tts_corpus.plan.missing': 'План подготовки недоступен. Сохранённые данные нужно проверить перед продолжением.',
  'tts_corpus.audio.invalid': 'Не удалось проверить сохранённый звук клипа. Обновите состояние корпуса.',
  'tts_corpus.input.invalid': 'Не удалось принять действие с корпусом. Проверьте данные и текущее состояние.',
  'tts_corpus.state.limit': 'Достигнут лимит сохраняемых данных корпуса. Создайте новый проект для следующей записи.',
  'tts_corpus.storage': 'Сохранённые данные корпуса недоступны. Проверьте доступ к локальному хранилищу.',
  'tts_corpus.missing': 'Корпус недоступен. Обновите проект.',
  'worker.output.limit': 'Не удалось подтвердить, что фрагмент прочитан до конца. Разделите его на более короткие фрагменты.',
  session_required: 'Откройте студию через ярлык приложения: нужна локальная сессия.',
  antiforgery_failed: 'Сессия обновилась. Перезагрузите страницу через ярлык приложения.',
  invalid_origin: 'Запрос не принадлежит этой локальной студии. Откройте её через ярлык.',
  invalid_host: 'Откройте студию через ярлык приложения.',
  'project.conflict': 'Проект изменён из другой вкладки. Загружена текущая версия; проверьте её и повторите свою правку.',
  'project.unavailable': 'Проект недоступен. Вернитесь к списку проектов и попробуйте снова.',
  'project.title.invalid': 'Укажите название проекта длиной до 120 символов UTF-16.',
  'project.idempotency.conflict': 'Запрос создания изменился. Вернитесь к списку и создайте проект снова.',
  'voice.rights': 'Подтвердите, что это ваш голос и вы имеете право использовать запись.',
  'voice.recording.exists': 'Исходная запись уже сохранена. Для другой записи создайте новый проект.',
  'voice.exists': 'Голосовой фрагмент уже подтверждён. Для другого фрагмента создайте новый проект.',
  'voice.recording.too_short': 'Запись слишком короткая: требуется от 5 до 20 минут.',
  'voice.recording.too_long': 'Запись слишком длинная: требуется от 5 до 20 минут.',
  'voice.recording.too_large': 'Размер записи превышает 1 ГиБ.',
  'voice.recording.missing': 'Сначала сохраните исходную запись голоса.',
  'voice.anchor.confirmation': 'Прослушайте фрагмент и явно подтвердите пару «голос — текст».',
  'voice.anchor.range': 'Выберите фрагмент длительностью 10–20 секунд внутри записи.',
  'voice.transcript': 'Укажите точный текст выбранного голосового фрагмента.',
  'voice.clipped': 'В выбранном фрагменте есть перегрузка звука. Выберите чистый участок.',
  'wav.format': 'Нужен WAV: PCM16, моно, 48 кГц. Поддержка M4A и MP3 появится с модулем FFmpeg.',
  'wav.empty': 'WAV не содержит аудио. Выберите корректную запись.',
  'wav.samples': 'WAV содержит некорректные аудиоданные.',
  'wav.truncated': 'WAV повреждён или обрезан. Выберите полную запись.',
  'text.exists': 'Текст уже сохранён. Для другого исходного текста создайте новый проект.',
  'text.reimport_required': 'Для этого старого проекта требуется новый проект и повторный импорт текста. Заменить исходник в текущем проекте на этапе 1 нельзя.',
  'text.encoding.unsupported': 'Кодировка TXT не поддерживается. Сохраните файл в UTF-8 и повторите импорт.',
  'text.encoding.invalid': 'Не удалось прочитать кодировку TXT. Сохраните файл в UTF-8.',
  'text.binary': 'Нужен обычный текстовый файл TXT, без бинарных данных.',
  'text.empty': 'Добавьте непустой текст книги.',
  'text.too_large': 'Превышен лимит текста: файл до 16 МиБ, вставка до 8 000 000 символов UTF-16.',
  'text.token_too_long': 'В тексте слишком длинное слово. Разделите его и импортируйте в новый проект.',
  'text.segment.missing': 'Фрагмент недоступен. Обновите проект.',
  'text.grapheme_boundary': 'Правка нарушает границу символа. Проверьте ударения и повторите.',
  'text.display_name.invalid': 'Укажите корректное название текстового источника.',
  'request.too_large': 'Запрос превышает допустимый размер.',
  'request.invalid': 'Не удалось принять данные. Проверьте заполненные поля и формат файла.',
  'request.multipart.invalid': 'Не удалось принять файл. Проверьте формат и повторите загрузку.',
  'request.media_type': 'Формат запроса не поддерживается. Обновите страницу и повторите.',
  'storage.busy': 'Проект занят другой операцией. Подождите немного и повторите.',
  'storage.unavailable': 'Локальное хранилище недоступно. Проверьте доступ к диску и повторите.',
  network: 'Нет связи с локальной студией. Если приложение закрыто, откройте его через ярлык.',
  'job.conflict': 'Задание изменено из другой вкладки. Показано текущее состояние; проверьте его перед следующей командой.',
  'job.active': 'В проекте уже есть незавершённое задание. Завершите или отмените его перед новым запуском или правкой текста.',
  'job.input.invalid': 'Выберите сохранённые фрагменты и подтверждённый голос.',
  'job.input_changed': 'Входные данные задания изменились. Создайте новое задание после проверки текста и голоса.',
  'job.transition.invalid': 'Это действие недоступно в текущем состоянии задания. Проверьте обновлённый прогресс.',
  'job.idempotency.conflict': 'Данные повторного запроса изменились. Проверьте выбор и запустите новое задание.',
  'job.missing': 'Задание недоступно. Обновите проект.',
  'worker.runtime.not_ready': 'Движок озвучивания не настроен. Генерация станет доступна после установки и проверки локальной модели.',
  'worker.memory.insufficient': 'Для озвучивания нужно не менее 16 ГиБ свободной оперативной памяти. Освободите память перед новой пробой.',
  'worker.memory.reserve': 'Озвучивание остановлено, чтобы оставить память системе. Готовые результаты сохранены.',
  'worker.memory.policy': 'Не удалось подтвердить ограничения памяти и процессора. Озвучивание остановлено.',
  'training.conflict': 'Состояние обучения обновилось, возможно из другой вкладки. Показано текущее состояние; проверьте его перед следующей командой.',
  'training.active': 'В проекте уже идёт обучение. Дождитесь завершения или остановите его.',
  'training.runtime.not_ready': 'Локальный движок обучения не настроен или изменился. Проверьте его готовность.',
  'training.path_too_long': 'Папка проектов слишком глубоко вложена для обучения в Windows. Используйте стандартную короткую папку локальной студии.',
  'training.experimental.confirmation': 'Подтвердите экспериментальный режим обучения.',
  'training.transition.invalid': 'Это действие недоступно в текущем состоянии обучения. Проверьте обновлённый прогресс.',
  'training.resume.unavailable': 'Продолжение недоступно: ещё нет принятой версии модели для этой записи.',
  'training.source.changed': 'Исходная запись изменилась. Продолжение этой модели недоступно.',
  'training.time_budget': 'Достигнут лимит времени этого запуска. Прогресс сохранён; продолжите обучение, чтобы завершить текущую эпоху.',
  'training.checkpoint_budget': 'Достигнут лимит сохранений этого запуска. Прогресс сохранён; продолжите обучение, чтобы завершить текущую эпоху.',
  'training.model.unavailable': 'Эта сохранённая версия модели недоступна для озвучивания. Проверьте выбор или используйте короткий образец.',
  'training.model.confirmation_required': 'Подтвердите экспериментальное применение выбранной сохранённой модели.',
  'training.model.source_changed': 'Сохранённая модель не относится к этой исходной записи. Выберите доступную версию этого проекта.',
  'voice.conversion.failed': 'Не удалось применить сохранённую модель. Необработанный голос не сохранён; проверьте модель и повторите пробу.',
};
export class ApiError extends Error {
  constructor(public code: string, public current?: SnapshotResponse, public currentJob?: JobResponse, public currentTraining?: TrainingResponse, public currentCorpus?: TtsCorpusResponse, public currentVoxCpmTraining?: VoxCpmTrainingResponse) { super(messages[code] ?? 'Не удалось выполнить действие. Проверьте данные и повторите.'); }
}
// FormData cannot assign application/json to a non-file field. Compose native
// Blob parts instead: the File remains a blob part, never read into a JS buffer.
export function multipart(metadata: unknown, file: File): Blob {
  const boundary = `las-${crypto.randomUUID()}`;
  return new Blob([
    `--${boundary}\r\nContent-Disposition: form-data; name="metadata"\r\nContent-Type: application/json\r\n\r\n${JSON.stringify(metadata)}\r\n--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="upload"\r\nContent-Type: application/octet-stream\r\n\r\n`,
    file, `\r\n--${boundary}--\r\n`,
  ], { type: `multipart/form-data; boundary=${boundary}` });
}
export class StudioClient {
  private token = '';
  private bootstrapRequest?: Promise<Bootstrap>;

  bootstrap(): Promise<Bootstrap> {
    return this.bootstrapRequest ??= this.request<Bootstrap>('/api/bootstrap').then(({ value }) => { this.token = value.antiforgeryRequestToken; return value; });
  }
  async list(): Promise<ProjectListItem[]> { return (await this.request<ProjectListItem[]>('/api/projects')).value; }
  async voxCpmTrainingReadiness(signal?: AbortSignal): Promise<VoxCpmTrainingReadiness> {
    return (await this.request<VoxCpmTrainingReadiness>('/api/voxcpm-training/readiness', 'GET', undefined, undefined, undefined, signal)).value;
  }
  async voxCpmTraining(projectId: string, signal?: AbortSignal): Promise<VoxCpmTrainingResponse | null> {
    const result = await this.request<VoxCpmTrainingSnapshot | null>(`/api/projects/${encodeURIComponent(projectId)}/voxcpm-training`, 'GET', undefined, undefined, undefined, signal);
    if (result.value === null) return null;
    if (!result.etag) throw new ApiError('request.invalid'); return { snapshot: result.value, etag: result.etag };
  }
  async startVoxCpmTraining(projectId: string, etag: string, corpusVersionId: string, experimentalConfirmed: boolean, key: string, signal?: AbortSignal): Promise<VoxCpmTrainingResponse> {
    return this.voxCpmTrainingRequest(`/api/projects/${encodeURIComponent(projectId)}/voxcpm-training`, { corpusVersionId, experimentalConfirmed }, etag, key, signal);
  }
  async controlVoxCpmTraining(projectId: string, training: VoxCpmTrainingResponse, command: 'stop' | 'resume', key: string, signal?: AbortSignal): Promise<VoxCpmTrainingResponse> {
    return this.voxCpmTrainingRequest(`/api/projects/${encodeURIComponent(projectId)}/voxcpm-training/${encodeURIComponent(training.snapshot.id)}/${command}`, undefined, training.etag, key, signal);
  }
  private async voxCpmTrainingRequest(path: string, body: unknown, etag: string, key: string, signal?: AbortSignal): Promise<VoxCpmTrainingResponse> {
    const result = await this.request<VoxCpmTrainingSnapshot>(path, 'POST', body, etag, key, signal);
    if (!result.etag) throw new ApiError('request.invalid'); return { snapshot: result.value, etag: result.etag };
  }
  async readiness(signal?: AbortSignal): Promise<RuntimeReadiness> { return (await this.request<RuntimeReadiness>('/api/runtime/readiness', 'GET', undefined, undefined, undefined, signal)).value; }
  async voxCpmReadiness(signal?: AbortSignal): Promise<RuntimeReadiness> { return (await this.request<RuntimeReadiness>('/api/voxcpm-inference/readiness', 'GET', undefined, undefined, undefined, signal)).value; }
  async voxCpmReferences(projectId: string, modelId: string, signal?: AbortSignal): Promise<VoxCpmReferenceCatalog> {
    return (await this.request<VoxCpmReferenceCatalog>(`/api/projects/${encodeURIComponent(projectId)}/voxcpm-generation/models/${encodeURIComponent(modelId)}/references`, 'GET', undefined, undefined, undefined, signal)).value;
  }
  async ttsCorpusReadiness(signal?: AbortSignal): Promise<TtsCorpusReadiness> { return (await this.request<TtsCorpusReadiness>('/api/tts-corpus/readiness', 'GET', undefined, undefined, undefined, signal)).value; }
  async ttsCorpus(projectId: string, signal?: AbortSignal): Promise<TtsCorpusResponse | null> {
    const result = await this.request<TtsCorpusSnapshot | null>(`/api/projects/${encodeURIComponent(projectId)}/tts-corpus`, 'GET', undefined, undefined, undefined, signal);
    if (result.value === null) return null;
    if (!result.etag) throw new ApiError('request.invalid');
    return { snapshot: result.value, etag: result.etag };
  }
  async startTtsCorpus(projectId: string, etag: string, key: string, signal?: AbortSignal): Promise<TtsCorpusResponse> {
    return this.corpusRequest(`/api/projects/${encodeURIComponent(projectId)}/tts-corpus`, 'POST', { experimentalConfirmed: true }, etag, key, signal);
  }
  async controlTtsCorpus(projectId: string, corpus: TtsCorpusResponse, command: 'stop' | 'resume', key: string, signal?: AbortSignal): Promise<TtsCorpusResponse> {
    return this.corpusRequest(`${this.corpusPath(projectId, corpus.snapshot.id)}/${command}`, 'POST', undefined, corpus.etag, key, signal);
  }
  async updateTtsCorpusClip(projectId: string, corpus: TtsCorpusResponse, clipId: string, body: TtsCorpusClipUpdate, key: string, signal?: AbortSignal): Promise<TtsCorpusResponse> {
    return this.corpusRequest(`${this.corpusPath(projectId, corpus.snapshot.id)}/clips/${encodeURIComponent(clipId)}`, 'PUT', body, corpus.etag, key, signal);
  }
  async approveTtsCorpus(projectId: string, corpus: TtsCorpusResponse, key: string, signal?: AbortSignal): Promise<TtsCorpusResponse> {
    return this.corpusRequest(`${this.corpusPath(projectId, corpus.snapshot.id)}/approve`, 'POST', { transcriptAndBoundariesConfirmed: true }, corpus.etag, key, signal);
  }
  ttsCorpusAudioUrl(projectId: string, corpusId: string, clipId: string): string {
    return `${this.corpusPath(projectId, corpusId)}/clips/${encodeURIComponent(clipId)}/audio`;
  }
  private corpusPath(projectId: string, corpusId: string): string { return `/api/projects/${encodeURIComponent(projectId)}/tts-corpus/${encodeURIComponent(corpusId)}`; }
  private async corpusRequest(path: string, method: 'POST' | 'PUT', body: unknown, etag: string, key: string, signal?: AbortSignal): Promise<TtsCorpusResponse> {
    const result = await this.request<TtsCorpusSnapshot>(path, method, body, etag, key, signal);
    if (!result.etag) throw new ApiError('request.invalid');
    return { snapshot: result.value, etag: result.etag };
  }
  async trainingReadiness(signal?: AbortSignal): Promise<TrainingReadiness> { return (await this.request<TrainingReadiness>('/api/training/readiness', 'GET', undefined, undefined, undefined, signal)).value; }
  async training(projectId: string, signal?: AbortSignal): Promise<TrainingResponse | null> {
    const result = await this.request<TrainingSnapshot | null>(`/api/projects/${projectId}/training`, 'GET', undefined, undefined, undefined, signal);
    if (result.value === null) return null;
    if (!result.etag) throw new ApiError('request.invalid');
    return { snapshot: result.value, etag: result.etag };
  }
  async startTraining(projectId: string, etag: string, key: string, signal?: AbortSignal): Promise<TrainingResponse> {
    return this.trainingRequest(`/api/projects/${projectId}/training`, { experimentalConfirmed: true }, etag, key, signal);
  }
  async controlTraining(projectId: string, training: TrainingResponse, command: TrainingControl, key: string, signal?: AbortSignal): Promise<TrainingResponse> {
    return this.trainingRequest(`/api/projects/${projectId}/training/${training.snapshot.id}/${command}`, undefined, training.etag, key, signal);
  }
  private async trainingRequest(path: string, body: unknown, etag: string, key: string, signal?: AbortSignal): Promise<TrainingResponse> {
    const result = await this.request<TrainingSnapshot>(path, 'POST', body, etag, key, signal);
    if (!result.etag) throw new ApiError('request.invalid');
    return { snapshot: result.value, etag: result.etag };
  }
  async jobs(projectId: string): Promise<JobSnapshot[]> { return (await this.request<JobSnapshot[]>(`/api/projects/${projectId}/jobs`)).value; }
  async enqueue(projectId: string, kind: JobKind, segmentIds: string[], etag: string, key: string, voice?: SavedVoiceSelection, signal?: AbortSignal): Promise<JobResponse> {
    const selection = voice ? { ...(voice.savedVoxCpmModelId ? { savedVoxCpmModelId: voice.savedVoxCpmModelId } : { savedModelId: voice.savedModelId }), experimentalConfirmed: voice.experimentalConfirmed } : {};
    return this.jobRequest(`/api/projects/${projectId}/jobs`, { kind, segmentIds, ...selection, ...(voice?.voxCpmOptions ? { voxCpmOptions: voice.voxCpmOptions } : {}) }, etag, key, signal);
  }
  async control(projectId: string, job: JobSnapshot, command: JobControl, key: string, signal?: AbortSignal): Promise<JobResponse> {
    return this.jobRequest(`/api/projects/${projectId}/jobs/${job.id}/${command}`, undefined, `"${job.revision}"`, key, signal);
  }
  private async jobRequest(path: string, body: unknown, etag: string, key: string, signal?: AbortSignal): Promise<JobResponse> {
    const result = await this.request<JobSnapshot>(path, 'POST', body, etag, key, signal);
    if (!result.etag) throw new ApiError('request.invalid');
    return { snapshot: result.value, etag: result.etag };
  }
  async snapshot(path: string, method = 'GET', body?: unknown, etag?: string, key?: string): Promise<SnapshotResponse> {
    const result = await this.request<ProjectSnapshot>(path, method, body, etag, key);
    if (!result.etag) throw new ApiError('request.invalid');
    return { snapshot: result.value, etag: result.etag };
  }
  async exit(): Promise<void> { await this.request('/api/app/exit', 'POST'); }

  private async request<T>(path: string, method = 'GET', body?: unknown, etag?: string, key?: string, signal?: AbortSignal): Promise<{ value: T; etag: string | null }> {
    const headers = new Headers();
    if (method !== 'GET') headers.set('X-CSRF-TOKEN', this.token);
    if (etag) headers.set('If-Match', etag);
    if (key) headers.set('Idempotency-Key', key);
    const upload = body instanceof Blob;
    if (body !== undefined) headers.set('Content-Type', upload ? body.type : 'application/json');
    let response: Response;
    try { response = await fetch(path, { method, headers, credentials: 'same-origin', signal, body: body === undefined ? undefined : upload ? body : JSON.stringify(body) }); }
    catch (failure) { if (signal?.aborted) throw failure; throw new ApiError('network'); }
    const value = await response.json().catch(() => null);
    if (!response.ok) {
      const code = typeof value?.code === 'string' ? value.code : 'request.invalid';
      const current = response.status === 412 && value?.conflictType === 'project' && value?.currentSnapshot && response.headers.get('ETag')
        ? { snapshot: value.currentSnapshot as ProjectSnapshot, etag: response.headers.get('ETag')! } : undefined;
      const currentJob = response.status === 412 && value?.conflictType === 'job' && value?.currentSnapshot && response.headers.get('ETag')
        ? { snapshot: value.currentSnapshot as JobSnapshot, etag: response.headers.get('ETag')! } : undefined;
      const currentTraining = response.status === 412 && value?.conflictType === 'training' && value?.currentSnapshot && response.headers.get('ETag')
        ? { snapshot: value.currentSnapshot as TrainingSnapshot, etag: response.headers.get('ETag')! } : undefined;
      const currentCorpus = response.status === 412 && value?.conflictType === 'tts_corpus' && value?.currentSnapshot && response.headers.get('ETag')
        ? { snapshot: value.currentSnapshot as TtsCorpusSnapshot, etag: response.headers.get('ETag')! } : undefined;
      const currentVoxCpmTraining = response.status === 412 && value?.conflictType === 'voxcpm_training' && value?.currentSnapshot && response.headers.get('ETag')
        ? { snapshot: value.currentSnapshot as VoxCpmTrainingSnapshot, etag: response.headers.get('ETag')! } : undefined;
      throw new ApiError(code, current, currentJob, currentTraining, currentCorpus, currentVoxCpmTraining);
    }
    return { value: value as T, etag: response.headers.get('ETag') };
  }
}
