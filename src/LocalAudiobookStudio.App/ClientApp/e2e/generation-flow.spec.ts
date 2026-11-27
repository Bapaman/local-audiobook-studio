import { test as base, expect, type Page } from '@playwright/test';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { repositoryRoot } from './global-setup';

const test = base.extend<{ generationHost: string }>({
  generationHost: async ({}, use) => {
    const host = spawn('dotnet', [resolve(repositoryRoot, 'tests/LocalAudiobookStudio.BrowserHost/bin/Debug/net10.0/LocalAudiobookStudio.BrowserHost.dll'), resolve(repositoryRoot, 'src/LocalAudiobookStudio.App/wwwroot'), '--generation'], { cwd: repositoryRoot, windowsHide: true, stdio: 'pipe' });
    let exited = false; host.once('exit', () => { exited = true; }); const done = once(host, 'exit');
    let stderr = ''; host.stderr.on('data', chunk => { stderr += chunk.toString(); });
    try {
      const launch = await new Promise<string>((ok, fail) => {
        let output = ''; const timeout = setTimeout(() => fail(new Error('Generation BrowserHost did not become ready')), 15_000);
        host.stdout.on('data', chunk => { output += chunk.toString(); if (output.includes('\n')) { clearTimeout(timeout); const line = output.split(/\r?\n/)[0]; if (/^http:\/\/127\.0\.0\.1:\d+\/_session\/launch\?token=/.test(line)) ok(line); else fail(new Error('Invalid launch protocol')); } });
        host.once('error', error => { clearTimeout(timeout); fail(error); });
        host.once('exit', code => { clearTimeout(timeout); fail(new Error(`Generation host exited before readiness (${code})`)); });
      });
      await use(launch);
    } finally {
      if (!exited) host.stdin.end('\n'); let timeout: ReturnType<typeof setTimeout>;
      try { await Promise.race([done, new Promise((_, fail) => { timeout = setTimeout(() => fail(new Error('Owned generation host failed to exit')), 10_000); })]); }
      catch (error) { if (!exited) { host.kill(); await done; } throw error; }
      finally { clearTimeout(timeout!); }
      expect(stderr).toBe(''); expect(host.exitCode).toBe(0);
      console.log(`Owned generation BrowserHost PID ${host.pid} exited; synthetic root disposed.`);
    }
  },
});
function wav(seconds: number) {
  const bytes = Buffer.alloc(44 + seconds * 96000);
  bytes.write('RIFF'); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(48000, 24); bytes.writeUInt32LE(96000, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(bytes.length - 44, 40);
  for (let i = 44; i < bytes.length; i += 2) bytes.writeInt16LE(1000, i); return bytes;
}
const projectScreen = (page: Page, name: 'Озвучивание' | 'Тексты' | 'Голос') =>
  page.getByRole('navigation', { name: 'Разделы голоса' }).getByRole('button', { name, exact: true });

async function prepareVoice(page: Page, launchUrl: string, title: string, recordingSeconds = 30) {
  await page.goto(launchUrl);
  await page.getByLabel('Название голоса').fill(title);
  await page.getByRole('button', { name: 'Добавить голос', exact: true }).click();
  await projectScreen(page, 'Голос').click();
  await page.getByLabel('Запись голоса WAV').setInputFiles({ name: 'synthetic.wav', mimeType: 'audio/wav', buffer: wav(recordingSeconds) });
  await page.getByLabel(/мой голос/).check();
  await page.getByRole('button', { name: 'Сохранить запись' }).click();
  await page.getByLabel('Точный текст фрагмента').fill('Синтетический голосовой образец.');
  await page.getByLabel('Прослушал и подтверждаю').check();
  await page.getByRole('button', { name: 'Подтвердить голосовой фрагмент' }).click();
}

async function prepareProject(page: Page, launchUrl: string, title: string, text: string, recordingSeconds = 30) {
  await prepareVoice(page, launchUrl, title, recordingSeconds);
  await projectScreen(page, 'Тексты').click();
  await page.getByLabel('Текст книги', { exact: true }).fill(text);
  await page.getByRole('button', { name: 'Импортировать текст' }).click();
  await page.getByRole('button', { name: 'Перейти к озвучиванию' }).click();
}

async function appendText(page: Page, title: string, text: string) {
  await projectScreen(page, 'Тексты').click();
  await page.getByRole('button', { name: 'Добавить новый текст' }).click();
  await page.getByLabel('Название нового текста').fill(title);
  await page.getByLabel('Текст книги', { exact: true }).fill(text);
  await page.getByRole('button', { name: 'Добавить текст', exact: true }).click();
}

test('production UI previews, plays, keeps running without a tab and controls saved generation', async ({ page, context, generationHost }, testInfo) => {
  const foreign: string[] = []; const violations: string[] = []; const errors: string[] = [];
  context.on('request', request => { const url = new URL(request.url()); if (url.hostname !== '127.0.0.1') foreign.push(url.hostname); });
  const observe = (tab: typeof page) => { tab.on('pageerror', error => errors.push(error.message)); tab.on('console', message => { if (/Content Security Policy|violates.*directive/.test(message.text())) violations.push(message.text()); }); };
  observe(page);
  const origin = new URL(generationHost).origin;
  await prepareProject(page, generationHost, 'Синтетическая озвучка', 'Первая история.\n\nВторая история.');
  await expect(page.getByRole('button', { name: 'Проба голоса', exact: true })).toBeEnabled(); await page.getByRole('button', { name: 'Проба голоса', exact: true }).click();
  await expect(page.getByText('Готово: 1 из 1', { exact: true })).toBeVisible();
  await projectScreen(page, 'Тексты').click();
  const audio = page.getByLabel('Аудио фрагмента 1'); await audio.evaluate((element: HTMLAudioElement) => element.play());
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => element.currentTime)).toBeGreaterThan(0);
  const originalAudio = (await audio.getAttribute('src'))!;
  const segmentPlayer = await audio.elementHandle();
  const fragmentFilter = page.getByLabel('Фильтр фрагментов');
  await fragmentFilter.selectOption('missing');
  await expect(audio).toHaveCount(0);
  await expect(page.getByLabel('Текст фрагмента 2')).toBeVisible();
  await expect.poll(() => segmentPlayer!.evaluate((media: HTMLAudioElement) => media.paused)).toBe(true);
  await fragmentFilter.selectOption('ready');
  await expect(audio).toBeVisible();
  await expect(page.getByLabel('Текст фрагмента 2')).toHaveCount(0);
  await fragmentFilter.selectOption('all');
  await fragmentFilter.selectOption('missing');
  await projectScreen(page, 'Озвучивание').click();
  const partialPlayer = page.getByRole('region', { name: 'Готовое аудио' });
  await expect(partialPlayer.getByText('Готово 1 из 2. Плеер пропускает фрагменты без аудио.')).toBeVisible();
  await expect(partialPlayer.getByRole('link', { name: /Скачать.*WAV/ })).toHaveCount(0);
  await partialPlayer.getByRole('button', { name: 'Прослушать готовые фрагменты' }).click();
  const partialAudio = page.getByLabel('Аудио выбранного текста');
  await expect(partialAudio).toHaveAttribute('src', originalAudio);
  await expect.poll(() => partialAudio.evaluate((media: HTMLAudioElement) => media.currentTime)).toBeGreaterThan(0);
  await partialPlayer.screenshot({ path: testInfo.outputPath('partial-ready-player.png') });
  await partialPlayer.getByRole('button', { name: 'Открыть фрагмент в редакторе' }).click();
  await expect(page.getByLabel('Текст фрагмента 1')).toBeFocused();
  await expect(fragmentFilter).toHaveValue('all');
  await expect.poll(() => partialAudio.evaluate((media: HTMLAudioElement) => media.paused)).toBe(true);
  const selection = page.getByRole('group', { name: 'Фрагменты для озвучивания' });
  await page.getByRole('button', { name: 'К озвучиванию фрагмента 2' }).click();
  await expect(selection).toBeFocused();
  await expect(selection.getByRole('checkbox', { name: 'Фрагмент 1' })).not.toBeChecked();
  await expect(selection.getByRole('checkbox', { name: 'Фрагмент 2' })).toBeChecked();
  await expect(page.getByLabel('Фрагмент для пробы').locator('option:checked')).toHaveText('Фрагмент 2');
  await expect(page.getByRole('region', { name: 'Очередь озвучивания' })).toHaveCount(0);
  await selection.getByRole('button', { name: 'Выбрать все' }).click();
  await selection.getByRole('button', { name: 'Выбрать без аудио' }).click();
  await expect(selection.getByRole('checkbox', { name: 'Фрагмент 1' })).not.toBeChecked();
  await expect(selection.getByRole('checkbox', { name: 'Фрагмент 2' })).toBeChecked();
  await selection.screenshot({ path: testInfo.outputPath('select-missing-audio.png') });
  const startGeneration = page.getByRole('button', { name: 'Озвучить выбранные фрагменты' });
  await startGeneration.click();
  const partialGeneration = page.getByRole('region', { name: 'Последнее задание' }).getByRole('article', { name: 'Задание озвучивания' });
  await expect(partialGeneration.getByText('Готово: 1 из 1', { exact: true })).toBeVisible();
  await expect(audio).toHaveAttribute('src', originalAudio);
  await expect(page.getByLabel('Аудио фрагмента 2')).toHaveAttribute('src', /\/artifacts\//);
  await expect(partialPlayer.getByRole('link', { name: /Скачать.*WAV/ })).toBeVisible();
  await expect(partialAudio).toHaveAttribute('src', originalAudio);
  await expect(selection.getByText('Выбрано 0 из 2')).toBeVisible();
  await expect(startGeneration).toBeDisabled();
  await selection.getByRole('button', { name: 'Выбрать все' }).click();
  await page.getByRole('button', { name: 'Озвучить выбранные фрагменты' }).click();
  await expect(page.getByRole('article', { name: 'Задание озвучивания' }).getByText('Озвучивание', { exact: true })).toBeVisible();
  await page.close();
  const reopened = await context.newPage(); observe(reopened); await reopened.goto(origin);
  await reopened.getByRole('button', { name: 'Открыть голос «Синтетическая озвучка»' }).click();
  const queue = reopened.getByRole('region', { name: 'Очередь озвучивания' });
  const generation = queue.getByRole('article', { name: 'Задание озвучивания' });
  const latest = reopened.getByRole('region', { name: 'Последнее задание' });
  await expect(generation.getByText('Озвучивание', { exact: true })).toBeVisible();
  await generation.getByRole('button', { name: 'Пауза', exact: true }).click(); await expect(generation.getByText('На паузе', { exact: true })).toBeVisible();
  await expect(reopened.getByRole('region', { name: 'Готовое аудио' })).toBeVisible();
  await queue.screenshot({ path: testInfo.outputPath('generation-queue-active.png') });
  await generation.getByRole('button', { name: 'Продолжить задание' }).click();
  await expect(latest.getByText('Готово: 2 из 2', { exact: true })).toBeVisible();
  await expect(latest.getByText('Завершено', { exact: true })).toBeVisible(); await expect(reopened.getByLabel('Аудио фрагмента 2')).toHaveAttribute('src', /\/artifacts\//);
  await reopened.getByRole('button', { name: 'Озвучить выбранные фрагменты' }).click();
  await expect(generation.getByText('Озвучивание', { exact: true })).toBeVisible(); await generation.getByRole('button', { name: 'Отменить задание' }).click();
  await expect(latest.getByText('Отменено', { exact: true })).toBeVisible(); await expect(reopened.getByLabel('Аудио фрагмента 2')).toHaveAttribute('src', /\/artifacts\//);
  await expect(queue).toHaveCount(0);
  const history = reopened.getByText('История заданий', { exact: true });
  const generationJobs = reopened.getByRole('article', { name: 'Задание озвучивания' });
  await expect(generationJobs).toHaveCount(1);
  await history.click();
  await expect(generationJobs).toHaveCount(3);
  const savedAudio = generationJobs.filter({ hasText: 'Готово: 2 из 2' }).getByLabel('Аудио результата 1');
  await savedAudio.evaluate((element: HTMLAudioElement) => element.play());
  await expect.poll(() => savedAudio.evaluate((element: HTMLAudioElement) => element.currentTime)).toBeGreaterThan(0);
  await history.click();
  await expect(generationJobs).toHaveCount(1);
  await reopened.reload();
  await reopened.getByRole('button', { name: 'Открыть голос «Синтетическая озвучка»' }).click();
  await appendText(reopened, 'Продолжение', 'Третья история.');
  const navigation = reopened.getByRole('navigation', { name: 'Разделы голоса' });
  const narration = reopened.getByRole('region', { name: 'Озвучивание', exact: true });
  const picker = reopened.getByLabel('Текст для озвучивания');
  const texts = reopened.getByRole('list', { name: 'Список текстов голоса' });
  const bookDownload = narration.getByRole('link', { name: 'Скачать всю книгу · WAV' });
  const finishedText = texts.getByRole('button', { name: 'Текст книги', exact: true });
  const newText = texts.getByRole('button', { name: 'Продолжение', exact: true });
  await expect(newText).toHaveAccessibleDescription('Озвучено 0 из 1');
  await expect(newText).toHaveAttribute('aria-current', 'true');
  await expect(finishedText).toHaveAccessibleDescription('Готово · 2 из 2 Проверено 0 из 2');
  await projectScreen(reopened, 'Озвучивание').click();
  await expect(narration.getByText('Озвучено 0 из 1')).toBeVisible();
  await expect(bookDownload).toHaveCount(0);
  await expect(narration.getByText('Общий WAV доступен после озвучивания всех текстов.')).toBeVisible();
  const downloadLink = reopened.getByRole('link', { name: 'Скачать выбранный текст · WAV' });
  await expect(downloadLink).toHaveCount(0);
  await projectScreen(reopened, 'Тексты').click();
  await finishedText.click();
  await expect(finishedText).toHaveAttribute('aria-current', 'true');
  await projectScreen(reopened, 'Озвучивание').click();
  await expect(picker.locator('option:checked')).toHaveText('1 · Текст книги');
  await expect(narration.getByText('Озвучено 2 из 2')).toBeVisible();
  const [download] = await Promise.all([reopened.waitForEvent('download'), downloadLink.click()]);
  const wavBytes = await readFile((await download.path())!);
  expect(wavBytes.subarray(0, 4).toString('ascii')).toBe('RIFF');
  expect(wavBytes.subarray(8, 12).toString('ascii')).toBe('WAVE');
  expect(wavBytes.length).toBeGreaterThan(44);
  const selectedArchive = new URL((await downloadLink.getAttribute('href'))!, origin);
  selectedArchive.pathname = selectedArchive.pathname.replace(/\/book\.wav$/, '/chapters.zip');
  const archiveResponse = await reopened.request.get(selectedArchive.href);
  expect(archiveResponse.status()).toBe(200);
  expect(archiveResponse.headers()['content-type']).toBe('application/zip');
  const chapterZip = await archiveResponse.body();
  expect(chapterZip.readUInt32LE(0)).toBe(0x04034b50);
  expect(chapterZip.includes(Buffer.from('01 - Текст книги.wav'))).toBe(true);
  expect(chapterZip.includes(Buffer.from('Продолжение'))).toBe(false);
  const ready = reopened.getByRole('region', { name: 'Готовое аудио' });
  const firstSource = (await reopened.getByLabel('Аудио фрагмента 1').getAttribute('src'))!;
  const secondSource = (await reopened.getByLabel('Аудио фрагмента 2').getAttribute('src'))!;
  expect(firstSource).not.toBe(secondSource);
  await ready.getByRole('button', { name: 'Прослушать текст' }).click();
  const player = ready.getByLabel('Аудио выбранного текста');
  await expect(player).toHaveAttribute('src', firstSource);
  await expect(player).toHaveAttribute('src', secondSource);
  await expect(ready.getByText('Фрагмент 2 из 2', { exact: true })).toBeVisible();
  await expect.poll(() => player.evaluate((media: HTMLAudioElement) => media.currentTime)).toBeGreaterThan(0);
  await picker.selectOption({ label: '2 · Продолжение' });
  await expect(ready).toHaveCount(0);
  await picker.selectOption({ label: '1 · Текст книги' });
  await expect(ready.getByRole('button', { name: 'Прослушать текст' })).toBeVisible();
  await ready.getByRole('button', { name: 'Прослушать текст' }).click();
  await reopened.evaluate(() => window.scrollTo(0, 0));
  await reopened.mouse.move(1, 1);
  await reopened.screenshot({ path: testInfo.outputPath('project-workspace-ready-desktop.png') });
  await reopened.setViewportSize({ width: 390, height: 844 });
  expect(await reopened.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await reopened.screenshot({ path: testInfo.outputPath('project-workspace-ready-mobile.png') });
  await projectScreen(reopened, 'Тексты').click();
  await projectScreen(reopened, 'Озвучивание').focus();
  await reopened.keyboard.press('Enter');
  const heading = reopened.getByRole('heading', { name: 'Озвучивание', exact: true });
  await expect(heading).toBeFocused();
  expect((await heading.boundingBox())!.y).toBeGreaterThanOrEqual((await navigation.boundingBox())!.y + (await navigation.boundingBox())!.height);
  await reopened.setViewportSize({ width: 1280, height: 900 });
  await reopened.getByRole('region', { name: 'Озвучивание', exact: true }).screenshot({ path: testInfo.outputPath('generation-panel.png') });

  await picker.selectOption({ label: '2 · Продолжение' });
  await reopened.getByRole('button', { name: 'Озвучить выбранные фрагменты' }).click();
  await projectScreen(reopened, 'Тексты').click();
  await expect(newText).toHaveAccessibleDescription('Готово · 1 из 1 Проверено 0 из 1');
  await projectScreen(reopened, 'Озвучивание').click();
  const [nextDownload] = await Promise.all([reopened.waitForEvent('download'), downloadLink.click()]);
  const nextBytes = await readFile((await nextDownload.path())!);
  const nextPcm = nextBytes.subarray(44);
  expect(wavBytes.subarray(44, 44 + nextPcm.length).equals(nextPcm)).toBe(false);

  await expect(bookDownload).toBeVisible();
  await narration.screenshot({ path: testInfo.outputPath('whole-book-download.png') });
  const [wholeDownload] = await Promise.all([reopened.waitForEvent('download'), bookDownload.click()]);
  const bookBytes = await readFile((await wholeDownload.path())!);
  const expectedPcm = Buffer.concat([wavBytes.subarray(44), nextPcm]);
  expect(bookBytes.subarray(0, 4).toString('ascii')).toBe('RIFF');
  expect(bookBytes.subarray(36, 40).toString('ascii')).toBe('data');
  expect(bookBytes.readUInt32LE(40)).toBe(expectedPcm.length);
  expect(bookBytes.subarray(44).equals(expectedPcm)).toBe(true);
  expect(foreign).toEqual([]); expect(violations).toEqual([]); expect(errors).toEqual([]);
});

test('imports a TXT book by headings and exports the narrated chapters in their original order', async ({ page, context, generationHost }, testInfo) => {
  const title = 'Импорт книги по главам';
  await prepareVoice(page, generationHost, title, 60);
  await page.setViewportSize({ width: 390, height: 844 });
  await projectScreen(page, 'Тексты').click();
  await page.getByLabel('Файл TXT', { exact: true }).check();
  await page.getByLabel('Текстовый файл TXT', { exact: true }).setInputFiles({
    name: 'Книга.txt', mimeType: 'text/plain',
    buffer: Buffer.from('\ufeffГлава 1\r\nЛида открыла окно.\r\nГлава IV\r\nВо дворе загорелся фонарь.'),
  });
  await page.getByRole('checkbox', { name: 'Разделить по заголовкам глав' }).check();
  await page.getByRole('button', { name: 'Импортировать книгу', exact: true }).screenshot({ path: testInfo.outputPath('txt-import-submit.png') });
  await page.getByRole('button', { name: 'Импортировать книгу', exact: true }).click();
  const texts = page.getByRole('region', { name: 'Тексты этого голоса' });
  await expect(texts.getByRole('button', { name: 'Глава 1', exact: true })).toHaveAttribute('aria-current', 'true');
  await expect(texts.getByRole('button', { name: 'Глава IV', exact: true })).toBeVisible();
  await expect(texts.getByText('Озвучено 0 из 4 фрагментов')).toBeVisible();

  const reopened = await context.newPage();
  await reopened.goto(new URL(generationHost).origin);
  await reopened.getByRole('button', { name: `Открыть голос «${title}»` }).click();
  await expect(reopened.getByLabel('Текст для озвучивания').getByRole('option')).toHaveText(['1 · Глава 1', '2 · Глава IV']);
  await expect(reopened.getByRole('link', { name: 'Скачать главы · ZIP' })).toHaveCount(0);
  await reopened.getByRole('button', { name: 'Озвучить остаток книги' }).click();
  const queue = reopened.getByRole('region', { name: 'Очередь озвучивания' });
  await expect(queue.getByText('Озвучивание', { exact: true })).toBeVisible();
  await queue.getByRole('button', { name: 'Пауза', exact: true }).click();
  await expect(queue.getByText('На паузе', { exact: true })).toBeVisible();
  await expect(queue.getByText('Готово: 1 из 4', { exact: true })).toBeVisible();
  await queue.getByRole('button', { name: 'Продолжить задание', exact: true }).click();
  const archive = reopened.getByRole('link', { name: 'Скачать главы · ZIP' });
  await expect(archive).toBeVisible({ timeout: 25_000 });
  await expect(reopened.getByRole('region', { name: 'Последнее задание' }).getByText('Готово: 4 из 4')).toBeVisible();
  const [zip] = await Promise.all([reopened.waitForEvent('download'), archive.click()]);
  const zipBytes = await readFile((await zip.path())!);
  const chapter1 = zipBytes.indexOf(Buffer.from('01 - Глава 1.wav'));
  const chapter2 = zipBytes.indexOf(Buffer.from('02 - Глава IV.wav'));
  expect(chapter1).toBeGreaterThan(0);
  expect(chapter2).toBeGreaterThan(chapter1);
  await expect(reopened.getByRole('link', { name: 'Скачать всю книгу · WAV' })).toBeVisible();
});

test('completes a partially narrated book in one durable job and downloads both chapters', async ({ page, context, generationHost }, testInfo) => {
  await prepareProject(page, generationHost, 'Сборка аудиокниги', 'Первая история.\n\nВторая история.');
  await page.getByRole('button', { name: 'Проба голоса', exact: true }).click();
  const savedAudio = page.getByLabel('Аудио фрагмента 1');
  await expect(savedAudio).toHaveAttribute('src', /\/artifacts\//);
  const original = (await savedAudio.getAttribute('src'))!;
  await appendText(page, 'Вторая глава', 'Третья история.');

  const summary = page.getByRole('region', { name: 'Тексты этого голоса' });
  await expect(summary.getByText('Вся книга · готово 0 из 2 текстов')).toBeVisible();
  await expect(summary.getByText('Озвучено 1 из 3 фрагментов')).toBeVisible();
  await projectScreen(page, 'Озвучивание').click();
  await expect(page.getByRole('link', { name: 'Скачать главы · ZIP' })).toHaveCount(0);
  const batch = page.getByRole('button', { name: 'Озвучить остаток книги' });
  await expect(batch).toHaveAccessibleDescription('Осталось озвучить 2 из 3 фрагментов во всех текстах.');
  const batchPanel = page.locator('.book-generation');
  await batchPanel.evaluate(element => element.scrollIntoView({ block: 'center' }));
  await batchPanel.screenshot({ path: testInfo.outputPath('whole-book-generation.png') });
  await batch.click();
  const queue = page.getByRole('region', { name: 'Очередь озвучивания' });
  await expect(queue.getByText('Текстов в задании · 2')).toBeVisible();
  await expect(queue.getByText('Озвучивание', { exact: true })).toBeVisible();
  await queue.getByRole('button', { name: 'Пауза', exact: true }).click();
  await expect(queue.getByText('На паузе', { exact: true })).toBeVisible();
  await expect(queue.getByText('Готово: 1 из 2')).toBeVisible();
  await projectScreen(page, 'Тексты').click();
  await expect(summary.getByText('Озвучено 2 из 3 фрагментов')).toBeVisible();
  await page.close();

  const reopened = await context.newPage();
  await reopened.goto(new URL(generationHost).origin);
  await reopened.getByRole('button', { name: 'Открыть голос «Сборка аудиокниги»' }).click();
  const reopenedQueue = reopened.getByRole('region', { name: 'Очередь озвучивания' });
  await expect(reopenedQueue.getByText('На паузе', { exact: true })).toBeVisible();
  await reopenedQueue.getByRole('button', { name: 'Продолжить задание' }).click();
  const latest = reopened.getByRole('region', { name: 'Последнее задание' });
  await expect(latest.getByText('Текстов в задании · 2')).toBeVisible();
  await expect(latest.getByText('Готово: 2 из 2')).toBeVisible();
  await projectScreen(reopened, 'Тексты').click();
  const finished = reopened.getByRole('region', { name: 'Тексты этого голоса' });
  await expect(finished.getByText('Вся книга · готово 2 из 2 текстов')).toBeVisible();
  await expect(finished.getByText('Озвучено 3 из 3 фрагментов')).toBeVisible();
  await expect(reopened.getByLabel('Аудио фрагмента 1')).toHaveAttribute('src', original);
  await projectScreen(reopened, 'Озвучивание').click();
  await expect(reopened.getByRole('button', { name: 'Озвучить остаток книги' })).toHaveCount(0);
  const narration = reopened.getByRole('region', { name: 'Озвучивание', exact: true });
  const whole = narration.getByRole('link', { name: 'Скачать всю книгу · WAV' });
  const archive = narration.getByRole('link', { name: 'Скачать главы · ZIP' });
  await expect(whole).toBeVisible();
  await narration.screenshot({ path: testInfo.outputPath('chapter-archive-actions.png') });
  const [zip] = await Promise.all([reopened.waitForEvent('download'), archive.click()]);
  expect(zip.suggestedFilename()).toBe('chapters.zip');
  const zipBytes = await readFile((await zip.path())!);
  expect(zipBytes.readUInt32LE(0)).toBe(0x04034b50);
  expect(zipBytes.includes(Buffer.from('01 - Текст книги.wav'))).toBe(true);
  expect(zipBytes.includes(Buffer.from('02 - Вторая глава.wav'))).toBe(true);
  await projectScreen(reopened, 'Тексты').click();

  const nextReview = finished.getByRole('button', { name: 'К следующей записи на проверку' });
  const picker = reopened.getByLabel('Текст для озвучивания');
  const review = (number: number) => reopened.getByRole('checkbox', { name: `Проверено на слух · фрагмент ${number}` });
  const firstAudio = reopened.getByLabel('Аудио фрагмента 1', { exact: true });
  await expect(finished.getByText('Проверено по книге 0 из 3')).toBeVisible();
  await nextReview.click();
  await expect(picker.locator('option:checked')).toHaveText('1 · Текст книги');
  await expect(firstAudio).toBeFocused();
  expect(await firstAudio.evaluate((media: HTMLAudioElement) => media.paused)).toBe(true);
  await review(1).click();
  await expect(finished.getByText('Проверено по книге 1 из 3')).toBeVisible();

  await nextReview.click();
  await expect(reopened.getByLabel('Аудио фрагмента 2', { exact: true })).toBeFocused();
  await review(2).click();
  await expect(finished.getByRole('button', { name: 'Текст книги', exact: true })).toHaveAccessibleDescription('Готово · 2 из 2 Проверено 2 из 2');
  await expect(finished.getByText('Проверено по книге 2 из 3')).toBeVisible();
  await nextReview.click();
  await expect(picker.locator('option:checked')).toHaveText('2 · Вторая глава');
  await expect(firstAudio).toBeFocused();
  await expect(firstAudio).not.toHaveAttribute('src', original);
  await finished.evaluate(element => element.scrollIntoView({ block: 'center' }));
  await finished.screenshot({ path: testInfo.outputPath('book-audio-review.png') });
  await review(1).click();
  await expect(finished.getByText('Проверено по книге 3 из 3')).toBeVisible();
  await expect(nextReview).toHaveCount(0);

  const issue = 'Проверить интонацию в конце фразы.';
  await reopened.getByRole('button', { name: 'Добавить замечание к фрагменту 1' }).click();
  await reopened.getByLabel('Замечание к фрагменту 1').fill(issue);
  await reopened.getByRole('button', { name: 'Сохранить замечание' }).click();
  await expect(finished.getByText('Замечаний по книге · 1')).toBeVisible();
  await expect(nextReview).toHaveCount(0);
  await finished.getByRole('button', { name: 'Текст книги', exact: true }).click();
  const nextCorrection = finished.getByRole('button', { name: 'К следующему исправлению' });
  await nextCorrection.click();
  await expect(picker.locator('option:checked')).toHaveText('2 · Вторая глава');
  await expect(reopened.getByLabel('Текст фрагмента 1')).toBeFocused();
  await expect(reopened.getByText(issue, { exact: true })).toBeVisible();
  await finished.evaluate(element => element.scrollIntoView({ block: 'center' }));
  await finished.screenshot({ path: testInfo.outputPath('book-corrections.png') });
  await reopened.getByRole('button', { name: 'Снять замечание' }).click();
  await expect(nextCorrection).toHaveCount(0);
  await expect(nextReview).toBeVisible();
});

test('keeps the previous recording beside the regenerated fragment after reopening', async ({ page, generationHost }, testInfo) => {
  await prepareProject(page, generationHost, 'Сравнение озвучки', 'Первая история.', 5);
  const start = page.getByRole('button', { name: 'Озвучить выбранные фрагменты' });
  await start.click();
  const currentAudio = page.getByLabel('Аудио фрагмента 1', { exact: true });
  const previousAudio = page.getByLabel('Предыдущая запись фрагмента 1');
  const previous = page.getByText('Предыдущая запись', { exact: true });
  const download = page.getByRole('link', { name: 'Скачать всю книгу · WAV' });
  const reviewed = page.getByRole('checkbox', { name: 'Проверено на слух · фрагмент 1' });
  const filter = page.getByLabel('Фильтр фрагментов');
  await expect(download).toBeVisible();
  await projectScreen(page, 'Тексты').click();
  await expect(currentAudio).toBeVisible();
  await expect(previous).toHaveCount(0);
  const oldSource = (await currentAudio.getAttribute('src'))!;
  await reviewed.click();
  await expect(reviewed).toBeChecked();
  await expect(page.getByText('Проверено 1 из 1')).toBeVisible();
  await filter.selectOption('unreviewed');
  await expect(currentAudio).toHaveCount(0);
  await page.reload();
  await page.getByRole('button', { name: 'Открыть голос «Сравнение озвучки»' }).click();
  await projectScreen(page, 'Тексты').click();
  await expect(reviewed).toBeChecked();

  const note = 'Уточнить ударение в слове «история».';
  await page.getByRole('button', { name: 'Добавить замечание к фрагменту 1' }).click();
  await page.getByLabel('Замечание к фрагменту 1').fill(note);
  await page.getByRole('button', { name: 'Сохранить замечание' }).click();
  await expect(page.getByText(note, { exact: true })).toBeVisible();
  await expect(reviewed).not.toBeChecked();
  await filter.selectOption('unreviewed');
  await expect(currentAudio).toHaveCount(0);
  await filter.selectOption('corrections');
  await expect(page.getByText(note, { exact: true })).toBeVisible();
  await page.reload();
  await page.getByRole('button', { name: 'Открыть голос «Сравнение озвучки»' }).click();
  await projectScreen(page, 'Тексты').click();
  await expect(page.getByText(note, { exact: true })).toBeVisible();

  await projectScreen(page, 'Озвучивание').click();
  await page.getByRole('button', { name: 'Прослушать текст' }).click();
  await page.getByRole('button', { name: 'Открыть фрагмент в редакторе' }).click();
  await page.getByLabel('Текст фрагмента 1').fill('Первая история началась утром.');
  await expect(page.getByText('После сохранения потребуется новое озвучивание.')).toBeVisible();
  await page.getByRole('button', { name: 'Сохранить фрагмент 1' }).click();
  await expect(page.getByText('Нужно исправить', { exact: true })).toBeVisible();
  await expect(page.getByText(note, { exact: true })).toBeVisible();
  await expect(reviewed).toHaveCount(0);
  await expect(currentAudio).toHaveCount(0);
  await projectScreen(page, 'Озвучивание').click();
  await expect(download).toHaveCount(0);
  await expect(page.getByRole('region', { name: 'Готовое аудио' })).toHaveCount(0);
  await projectScreen(page, 'Тексты').click();
  await previous.click();
  const comparison = page.locator('details').filter({ has: previous });
  await expect(comparison.getByText('Первая история.', { exact: true })).toBeVisible();
  await expect(previousAudio).toHaveAttribute('src', oldSource);

  await page.getByRole('button', { name: 'К озвучиванию фрагмента 1' }).click();
  await start.click();
  await expect(download).toBeVisible();
  const bookHref = (await download.getAttribute('href'))!;
  await projectScreen(page, 'Тексты').click();
  await expect(currentAudio).toBeVisible();
  await expect(currentAudio).not.toHaveAttribute('src', oldSource);
  await expect(reviewed).not.toBeChecked();
  await expect(page.getByText('К повторной проверке', { exact: true })).toBeVisible();
  const newSource = (await currentAudio.getAttribute('src'))!;
  const oldFile = await page.request.get(new URL(oldSource, generationHost).href);
  const currentFile = await page.request.get(new URL(newSource, generationHost).href);
  const book = await page.request.get(new URL(bookHref, generationHost).href);
  expect(oldFile.ok()).toBe(true);
  expect(currentFile.ok()).toBe(true);
  expect(book.ok()).toBe(true);
  const currentBytes = await currentFile.body();
  expect((await oldFile.body()).equals(currentBytes)).toBe(false);
  expect((await book.body()).subarray(44).equals(currentBytes.subarray(44))).toBe(true);

  await page.reload();
  await page.getByRole('button', { name: 'Открыть голос «Сравнение озвучки»' }).click();
  await projectScreen(page, 'Тексты').click();
  await expect(currentAudio).toHaveAttribute('src', newSource);
  await expect(reviewed).not.toBeChecked();
  await expect(page.getByText(note, { exact: true })).toBeVisible();
  const amendedNote = 'Проверить ударение и паузу после слова «история».';
  await page.getByRole('button', { name: 'Изменить замечание к фрагменту 1' }).click();
  await page.getByLabel('Замечание к фрагменту 1').fill(amendedNote);
  await page.getByRole('button', { name: 'Сохранить замечание' }).click();
  await expect(page.getByText(amendedNote, { exact: true })).toBeVisible();
  await expect(page.getByText('К повторной проверке', { exact: true })).toBeVisible();
  await expect(previousAudio).toBeHidden();
  await previous.click();
  await expect(previousAudio).toHaveAttribute('src', oldSource);
  await previousAudio.evaluate((media: HTMLAudioElement) => media.play());
  await expect.poll(() => previousAudio.evaluate((media: HTMLAudioElement) => media.paused)).toBe(false);
  await currentAudio.evaluate((media: HTMLAudioElement) => media.play());
  await expect.poll(() => previousAudio.evaluate((media: HTMLAudioElement) => media.paused)).toBe(true);
  const compareAudio = page.getByRole('button', { name: 'Сравнить записи · фрагмент 1', exact: true });
  const stopComparison = page.getByRole('button', { name: 'Остановить сравнение · фрагмент 1', exact: true });
  await compareAudio.click();
  await expect(stopComparison).toBeVisible();
  await expect.poll(() => previousAudio.evaluate((media: HTMLAudioElement) => !media.paused)).toBe(true);
  await expect(page.getByText('Играет текущая запись', { exact: true })).toBeVisible();
  await expect.poll(() => currentAudio.evaluate((media: HTMLAudioElement) => !media.paused)).toBe(true);
  await expect(compareAudio).toBeVisible();
  await expect(reviewed).not.toBeChecked();

  await compareAudio.click();
  await expect(page.getByText('Играет предыдущая запись', { exact: true })).toBeVisible();
  await stopComparison.click();
  await expect(compareAudio).toBeVisible();
  await expect.poll(() => previousAudio.evaluate((media: HTMLAudioElement) => media.paused)).toBe(true);

  await compareAudio.click();
  await expect(stopComparison).toBeVisible();
  await projectScreen(page, 'Озвучивание').click();
  await expect.poll(() => Promise.all([
    previousAudio.evaluate((media: HTMLAudioElement) => media.paused),
    currentAudio.evaluate((media: HTMLAudioElement) => media.paused),
  ])).toEqual([true, true]);
  await projectScreen(page, 'Тексты').click();
  await expect(compareAudio).toBeVisible();
  await page.locator('.segment').evaluate(element => element.scrollIntoView({ block: 'center' }));
  await page.locator('.segment').screenshot({ path: testInfo.outputPath('previous-fragment-audio.png') });

  const rejectAgain = page.getByRole('button', { name: 'Вернуть на исправление' });
  await expect(rejectAgain).toBeVisible();
  await rejectAgain.click();
  await expect(page.getByText('Нужно исправить', { exact: true })).toBeVisible();
  await expect(page.getByText(amendedNote, { exact: true })).toBeVisible();
  await expect(currentAudio).toHaveAttribute('src', newSource);
  await reviewed.click();
  await expect(reviewed).toBeChecked();
  await expect(page.getByText(amendedNote, { exact: true })).toHaveCount(0);
  const projectUrl = new URL(newSource.split('/artifacts/')[0], generationHost).href;
  const project = await page.request.get(projectUrl);
  const current = await project.json();
  expect(current.segments[0].audioReviewed).toBe(true);
  const bootstrap = await (await page.request.get(new URL('/api/bootstrap', generationHost).href)).json();
  const mutationHeaders = { 'If-Match': project.headers().etag, 'X-CSRF-TOKEN': bootstrap.antiforgeryRequestToken,
    Origin: new URL(generationHost).origin };
  const staleCorrection = await page.request.patch(`${projectUrl}/segments/${current.segments[0].id}/correction`, {
    headers: mutationHeaders,
    data: { note: 'Черновик о старой записи.', expected: { artifactId: oldSource.split('/').at(-1), note: null, needsNewAudio: false } },
  });
  expect(staleCorrection.status()).toBe(412);
  const staleReview = await page.request.patch(`${projectUrl}/segments/${current.segments[0].id}/review`, {
    headers: mutationHeaders,
    data: { artifactId: oldSource.split('/').at(-1), reviewed: false },
  });
  expect(staleReview.status()).toBe(412);
  await expect(reviewed).toBeChecked();
  await reviewed.click();
  await expect(reviewed).not.toBeChecked();
  await reviewed.click();
  await expect(reviewed).toBeChecked();
  await page.getByRole('button', { name: 'К озвучиванию фрагмента 1' }).click();
  await start.click();
  await projectScreen(page, 'Тексты').click();
  await expect(currentAudio).not.toHaveAttribute('src', newSource);
  await expect(reviewed).not.toBeChecked();

  await page.getByLabel('Текст фрагмента 1').fill('Первая история началась на рассвете.');
  await page.getByRole('button', { name: 'Сохранить фрагмент 1' }).click();
  await expect(currentAudio).toHaveCount(0);
  await page.getByRole('button', { name: 'Добавить замечание к фрагменту 1' }).click();
  await page.getByLabel('Замечание к фрагменту 1').fill('Сверить интонацию предыдущего WAV.');
  await page.getByRole('button', { name: 'Сохранить замечание' }).click();
  await expect(page.getByText('Сверить интонацию предыдущего WAV.', { exact: true })).toBeVisible();
  await expect(page.getByText('Нужно исправить', { exact: true })).toBeVisible();
});
