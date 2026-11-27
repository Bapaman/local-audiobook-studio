import { test as base, expect, type Page } from '@playwright/test';
import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { once } from 'node:events';
import { resolve } from 'node:path';
import { repositoryRoot } from './global-setup';

type Host = { launchUrl: string; origin: string; process: ChildProcessWithoutNullStreams };
const test = base.extend<{ host: Host; legacy: boolean }>({
  legacy: [false, { option: true }],
  host: async ({ legacy }, use) => {
    const host = spawn('dotnet', [resolve(repositoryRoot, 'tests/LocalAudiobookStudio.BrowserHost/bin/Debug/net10.0/LocalAudiobookStudio.BrowserHost.dll'), resolve(repositoryRoot, 'src/LocalAudiobookStudio.App/wwwroot'), ...(legacy ? ['--legacy'] : [])], { cwd: repositoryRoot, windowsHide: true, stdio: 'pipe' });
    let exited = false; host.once('exit', () => { exited = true; });
    const done = once(host, 'exit');
    try {
      const launchUrl = await new Promise<string>((ok, fail) => {
        let output = ''; const timer = setTimeout(() => fail(new Error('BrowserHost did not become ready')), 15_000);
        host.stdout.on('data', chunk => { output += chunk.toString(); if (output.includes('\n')) { clearTimeout(timer); const line = output.split(/\r?\n/)[0]; if (!/^http:\/\/127\.0\.0\.1:\d+\/_session\/launch\?token=/.test(line)) fail(new Error('Invalid BrowserHost launch protocol')); else ok(line); } });
        host.once('error', error => { clearTimeout(timer); fail(error); });
        host.once('exit', code => { clearTimeout(timer); fail(new Error(`BrowserHost exited before readiness (${code})`)); });
      });
      await use({ launchUrl, origin: new URL(launchUrl).origin, process: host });
    } finally {
      if (!exited) host.stdin.end('\n');
      let timer: ReturnType<typeof setTimeout>;
      try { await Promise.race([done, new Promise((_, fail) => { timer = setTimeout(() => fail(new Error('Owned BrowserHost failed to exit')), 10_000); })]); }
      catch (error) { if (!exited) { host.kill(); await done; } throw error; }
      finally { clearTimeout(timer!); }
      expect(exited, 'Owned BrowserHost exited').toBe(true);
      expect(host.exitCode).toBe(0);
      console.log(`Owned BrowserHost PID ${host.pid} exited; listener and temporary root disposed.`);
    }
  },
});

function wav(seconds = 30) {
  const bytes = Buffer.alloc(44 + seconds * 48000 * 2);
  bytes.write('RIFF'); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(48000, 24); bytes.writeUInt32LE(96000, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(bytes.length - 44, 40);
  for (let sample = 0; sample < seconds * 48000; sample++) bytes.writeInt16LE(Math.round(1500 * Math.sin(2 * Math.PI * 220 * sample / 48000)), 44 + sample * 2);
  return bytes;
}
const projectScreen = (page: Page, name: string) =>
  page.getByRole('navigation', { name: 'Разделы голоса' }).getByRole('button', { name, exact: true });

test.describe('Legacy Core persistence through the actual API and UI', () => {
  test.use({ legacy: true });
  test('accepted legacy content survives reopening and offers only compatible controls', async ({ page, host }, testInfo) => {
    await page.goto(host.launchUrl);
    await expect(page.getByText('Образец сохранён')).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('voice-catalog.png') });
    await page.getByRole('button', { name: 'Открыть голос «Старый проект»' }).click();
    await projectScreen(page, 'Голос').click();
    await expect(page.getByText(/Голос сохранён в старом формате/)).toBeVisible();
    await projectScreen(page, 'Тексты').click();
    await expect(page.getByLabel('Текст фрагмента 1')).toHaveValue('Старая правка.');
    await expect(page.getByLabel('Текст фрагмента 1')).toHaveAttribute('readonly', '');
    await expect(page.getByRole('button', { name: 'Импортировать текст' })).toHaveCount(0);
    await expect(page.getByLabel('Запись голоса WAV')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Сохранить фрагмент 1' })).toHaveCount(0);
    await expect(page.getByText(/Для редактирования добавьте/)).toBeVisible();
    await page.getByRole('button', { name: '← Все голоса' }).click();
    await page.getByRole('button', { name: 'Открыть голос «Старый проект»' }).click();
    await projectScreen(page, 'Тексты').click();
    await expect(page.getByLabel('Текст фрагмента 1')).toHaveValue('Старая правка.');
  });
});

test('Russian Stage 1 flow persists confirmed audio range, exact source and edited spoken text', async ({ page, host }, testInfo) => {
  const requests: string[] = []; const foreign: string[] = []; const violations: string[] = []; const errors: string[] = [];
  page.on('request', request => { const url = new URL(request.url()); requests.push(`${request.method()} ${url.pathname}`); if (url.hostname !== '127.0.0.1') foreign.push(url.hostname); });
  page.on('console', message => { if (/Content Security Policy|violates.*directive/.test(message.text())) violations.push(message.text()); });
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(host.launchUrl);
  expect(page.url()).toBe(host.origin + '/');
  await expect(page.getByText('Пока нет голосов')).toBeVisible();
  await page.getByLabel('Название голоса').fill('Синтетическая история');
  await page.getByRole('button', { name: 'Добавить голос', exact: true }).click();
  await expect(page.getByRole('heading', { name: 'Синтетическая история', exact: true })).toBeVisible();
  await projectScreen(page, 'Голос').click();
  await page.getByLabel('Запись голоса WAV').setInputFiles({ name: 'synthetic.wav', mimeType: 'audio/wav', buffer: wav() });
  await expect(page.getByRole('button', { name: 'Сохранить запись' })).toBeDisabled();
  await page.getByLabel(/мой голос/).check();
  await page.getByRole('button', { name: 'Сохранить запись' }).click();
  await expect(page.getByRole('heading', { name: 'Выберите голосовой фрагмент' })).toBeVisible();
  await page.getByLabel('Начало, секунды').fill('5'); await page.getByLabel('Конец, секунды').fill('17');
  const audio = page.getByLabel('Прослушивание записи');
  await page.getByRole('button', { name: 'Прослушать выбранный фрагмент' }).click();
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => element.currentTime)).toBeGreaterThan(5);
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => element.paused)).toBe(false);
  await audio.evaluate((element: HTMLAudioElement) => element.pause());
  await page.getByLabel('Точный текст фрагмента').fill('Ёжик читает ма\u0301ме.');
  await expect(page.getByRole('button', { name: 'Подтвердить голосовой фрагмент' })).toBeDisabled();
  await page.getByLabel('Прослушал и подтверждаю').check();
  await page.getByRole('button', { name: 'Подтвердить голосовой фрагмент' }).click();
  await expect(page.getByRole('heading', { name: 'Голосовой фрагмент подтверждён' })).toBeVisible();
  await expect(page.getByText('5–17 с · 12 секунд')).toBeVisible();
  const original = 'Ёжик читает ма\u0301ме.\r\n\r\n  Ещё один абзац.  ';
  await projectScreen(page, 'Тексты').click();
  await page.getByLabel('Текст книги', { exact: true }).fill(original);
  await page.getByRole('button', { name: 'Импортировать текст' }).click();
  await page.getByText('Сверить с исходным текстом', { exact: true }).click();
  const originalRegion = page.getByRole('region', { name: 'Исходный текст', exact: true });
  // Textarea line endings are browser-normalized to LF before paste; verify that exact submitted value.
  await expect(originalRegion).toHaveText(original.replaceAll('\r\n', '\n'), { useInnerText: false });
  const normalizedRegion = page.getByRole('region', { name: 'Текст после подготовки', exact: true });
  await expect(normalizedRegion).toContainText('Ёжик читает ма\u0301ме.');
  const spoken = page.getByLabel('Текст фрагмента 1', { exact: true });
  await spoken.fill('Ёжик рассказывает маме историю.');
  await spoken.evaluate((field: HTMLTextAreaElement) => {
    const vowel = field.value.indexOf('маме') + 1;
    field.setSelectionRange(vowel, vowel + 1);
  });
  await page.getByRole('button', { name: 'Ударение · фрагмент 1', exact: true }).click();
  await expect(spoken).toHaveValue('Ёжик рассказывает ма\u0301ме историю.');
  await expect(spoken).toBeFocused();
  await page.getByRole('button', { name: 'Сохранить фрагмент 1', exact: true }).click();
  await expect(page.getByTitle('Версия 5')).toBeVisible();
  await page.reload();
  await page.getByRole('button', { name: 'Открыть голос «Синтетическая история»' }).click();
  await projectScreen(page, 'Тексты').click();
  await expect(spoken).toHaveValue('Ёжик рассказывает ма\u0301ме историю.');
  await page.getByText('Сверить с исходным текстом', { exact: true }).click();
  await expect(originalRegion).toContainText('Ёжик читает ма\u0301ме.');
  await projectScreen(page, 'Голос').click();
  await expect(page.getByText('5–17 с · 12 секунд')).toBeVisible();
  await expect(page.getByRole('region', { name: 'Ваш голос' }).getByText('Ёжик читает ма\u0301ме.', { exact: true })).toBeVisible();
  const generationJump = projectScreen(page, 'Озвучивание');
  await generationJump.focus({ timeout: 2000 });
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: 'Озвучивание', exact: true })).toBeFocused();
  await expect(page.getByRole('button', { name: 'Озвучить выбранные фрагменты' })).toBeDisabled();
  await expect(page.getByRole('heading', { name: 'Ваш голос' })).toHaveCount(0);
  const trainingTools = projectScreen(page, 'Дообучение');
  const train = page.getByRole('button', { name: 'Обучить голос' });
  await trainingTools.click();
  await expect(page.getByRole('heading', { name: 'Дообучение голоса' })).toBeFocused();
  await expect(page.getByRole('heading', { name: 'Подготовка записи' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Проба голоса' })).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Тексты этого голоса' })).toHaveCount(0);
  await expect(train).toHaveCount(0);
  await expect(page.getByText('Прежний способ обучения · RVC')).toBeHidden();
  await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: testInfo.outputPath('project-workspace-desktop.png') });
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  await page.screenshot({ path: testInfo.outputPath('project-workspace-mobile.png') });
  await generationJump.click();
  await expect(page.getByRole('heading', { name: 'Дообучение голоса' })).toHaveCount(0);
  expect(requests.filter(request => request === 'GET /api/bootstrap')).toHaveLength(2);
  expect(requests.some(request => request.includes('/artifacts/'))).toBe(true);
  expect(foreign).toEqual([]); expect(violations).toEqual([]); expect(errors).toEqual([]);
  await testInfo.attach('loopback-request-paths', { body: JSON.stringify(requests, null, 2), contentType: 'application/json' });
  console.log(`Recorded ${requests.length} browser requests; every hostname exactly 127.0.0.1; no CSP violations or page errors.`);
});

test('TXT input preserves synthetic source and keyboard navigation is usable', async ({ page, host }, testInfo) => {
  const foreign: string[] = []; const requests: string[] = [];
  page.on('request', request => { const url = new URL(request.url()); requests.push(`${request.method()} ${url.hostname} ${url.pathname}`); if (url.hostname !== '127.0.0.1') foreign.push(url.hostname); });
  await page.goto(host.launchUrl);
  await page.keyboard.press('Tab'); await expect(page.getByRole('link', { name: 'К содержимому' })).toBeFocused();
  await page.keyboard.press('Enter');
  await page.getByLabel('Название голоса').fill('TXT пример'); await page.getByRole('button', { name: 'Добавить голос', exact: true }).click();
  await page.getByRole('button', { name: 'Добавить текст', exact: true }).click();
  await page.getByLabel('Файл TXT', { exact: true }).check();
  const source = 'Ёлка и ма\u0301ма.\r\nВторая строка.\r\n';
  await page.getByLabel('Текстовый файл TXT').setInputFiles({ name: 'synthetic.txt', mimeType: 'text/plain', buffer: Buffer.from(source, 'utf8') });
  await page.getByRole('button', { name: 'Импортировать текст' }).click();
  await page.getByText('Сверить с исходным текстом', { exact: true }).click();
  expect(await page.getByRole('region', { name: 'Исходный текст', exact: true }).textContent()).toBe(source);
  await page.reload(); await page.getByRole('button', { name: 'Открыть голос «TXT пример»' }).click();
  await projectScreen(page, 'Тексты').click();
  await page.getByText('Сверить с исходным текстом', { exact: true }).click();
  expect(await page.getByRole('region', { name: 'Исходный текст', exact: true }).textContent()).toBe(source);
  expect(foreign).toEqual([]);
  await testInfo.attach('loopback-request-paths', { body: JSON.stringify(requests, null, 2), contentType: 'application/json' });
});

test('searches a long text and saves an off-page draft through the editor', async ({ page, host }, testInfo) => {
  await page.goto(host.launchUrl);
  await page.getByLabel('Название голоса').fill('Редактура длинного текста');
  await page.getByRole('button', { name: 'Добавить голос', exact: true }).click();
  await projectScreen(page, 'Тексты').click();
  const paragraphs = Array.from({ length: 14 }, (_, index) => `Контрольный абзац номер ${index + 1}.`);
  await page.getByLabel('Текст книги', { exact: true }).fill(paragraphs.join('\n\n'));
  await page.getByRole('button', { name: 'Импортировать текст' }).click();
  const pages = page.getByRole('navigation', { name: 'Страницы фрагментов' });
  await expect(pages.getByText('Страница 1 из 2')).toBeVisible();
  await expect(page.getByLabel('Текст фрагмента 13')).toHaveCount(0);
  const toolbar = page.locator('.segment-browser');
  await toolbar.evaluate(element => element.scrollIntoView({ block: 'center' }));
  await toolbar.screenshot({ path: testInfo.outputPath('segment-browser.png') });

  await pages.getByRole('button', { name: 'Следующая страница' }).click();
  await page.getByLabel('Текст фрагмента 13').fill('Доработанный абзац с ёлкой.');
  const allProjects = page.getByRole('button', { name: '← Все голоса' });
  const exit = page.getByRole('button', { name: 'Выйти из приложения' });
  await expect(allProjects).toBeDisabled();
  await expect(exit).toBeDisabled();
  const warning = page.waitForEvent('dialog', { timeout: 10_000 });
  await page.evaluate(() => { window.setTimeout(() => window.location.reload(), 0); });
  const dialog = await warning;
  expect(dialog.type()).toBe('beforeunload');
  await dialog.dismiss();
  await expect(page.getByLabel('Текст фрагмента 13')).toHaveValue('Доработанный абзац с ёлкой.');
  const search = page.getByLabel('Найти фрагмент');
  await search.fill('#14');
  await expect(page.getByLabel('Текст фрагмента 14')).toBeVisible();
  await expect(page.getByLabel('Текст фрагмента 13')).toHaveCount(0);
  await page.getByRole('button', { name: 'Показать правки · 1' }).click();
  await expect(page.getByLabel('Текст фрагмента 13')).toHaveValue('Доработанный абзац с ёлкой.');
  await page.getByRole('button', { name: 'Сохранить фрагмент 13' }).click();
  await expect(page.getByRole('button', { name: /Показать правки/ })).toHaveCount(0);
  await expect(allProjects).toBeEnabled();
  await expect(exit).toBeEnabled();

  await page.reload();
  await page.getByRole('button', { name: 'Открыть голос «Редактура длинного текста»' }).click();
  await projectScreen(page, 'Тексты').click();
  await page.getByLabel('Найти фрагмент').fill('ЕЛКОЙ');
  await expect(page.getByLabel('Текст фрагмента 13')).toHaveValue('Доработанный абзац с ёлкой.');
  await expect(page.getByLabel('Текст фрагмента 14')).toHaveCount(0);
  await page.setViewportSize({ width: 390, height: 844 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
});

test('unrelated saves retain pasted, voice and simultaneous segment drafts', async ({ page, host }, testInfo) => {
  const requests: string[] = []; const foreign: string[] = [];
  page.on('request', request => { const url = new URL(request.url()); requests.push(`${request.method()} ${url.hostname} ${url.pathname}`); if (url.hostname !== '127.0.0.1') foreign.push(url.hostname); });
  await page.goto(host.launchUrl);
  await page.getByLabel('Название голоса').fill('Независимые черновики');
  await page.getByRole('button', { name: 'Добавить голос', exact: true }).click();
  await projectScreen(page, 'Тексты').click();
  const source = 'Ёжик читает ма\u0301ме.\n\nВторая история.';
  await page.getByLabel('Текст книги', { exact: true }).fill(source);
  await projectScreen(page, 'Голос').click();
  await page.getByLabel('Запись голоса WAV').setInputFiles({ name: 'synthetic.wav', mimeType: 'audio/wav', buffer: wav() });
  await page.getByLabel(/мой голос/).check();
  await page.getByRole('button', { name: 'Сохранить запись' }).click();
  await expect(page.getByRole('heading', { name: 'Выберите голосовой фрагмент' })).toBeVisible();
  await expect(page.getByLabel('Текст книги', { exact: true })).toHaveValue(source);
  await page.getByLabel('Начало, секунды').fill('5'); await page.getByLabel('Конец, секунды').fill('17');
  await page.getByLabel('Точный текст фрагмента').fill('Ёжик читает ма\u0301ме.');
  await page.getByLabel('Прослушал и подтверждаю').check();
  await projectScreen(page, 'Тексты').click();
  await page.getByRole('button', { name: 'Импортировать текст' }).click();
  await expect(page.getByTitle('Версия 3')).toBeVisible();
  await expect(page.getByLabel('Начало, секунды')).toHaveValue('5');
  await expect(page.getByLabel('Конец, секунды')).toHaveValue('17');
  await expect(page.getByLabel('Точный текст фрагмента')).toHaveValue('Ёжик читает ма\u0301ме.');
  await expect(page.getByLabel('Прослушал и подтверждаю')).toBeChecked();
  await page.getByLabel('Текст фрагмента 1', { exact: true }).fill('Первая независимая правка.');
  await page.getByLabel('Текст фрагмента 2', { exact: true }).fill('Вторая независимая правка.');
  await page.getByRole('button', { name: 'Сохранить фрагмент 1', exact: true }).click();
  await expect(page.getByTitle('Версия 4')).toBeVisible();
  await expect(page.getByLabel('Текст фрагмента 2', { exact: true })).toHaveValue('Вторая независимая правка.');
  await expect(page.getByRole('button', { name: 'Сохранить фрагмент 1', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Сохранить фрагмент 2', exact: true }).click();
  await expect(page.getByTitle('Версия 5')).toBeVisible();
  await projectScreen(page, 'Голос').click();
  await expect(page.getByLabel('Точный текст фрагмента')).toHaveValue('Ёжик читает ма\u0301ме.');
  await page.getByLabel('Конец, секунды').fill('16');
  await expect(page.getByLabel('Прослушал и подтверждаю')).not.toBeChecked();
  await page.reload(); await page.getByRole('button', { name: 'Открыть голос «Независимые черновики»' }).click();
  await projectScreen(page, 'Тексты').click();
  await expect(page.getByLabel('Текст фрагмента 1', { exact: true })).toHaveValue('Первая независимая правка.');
  await expect(page.getByLabel('Текст фрагмента 2', { exact: true })).toHaveValue('Вторая независимая правка.');
  await expect(page.getByLabel('Точный текст фрагмента')).toHaveValue('');
  expect(foreign).toEqual([]);
  await testInfo.attach('loopback-request-paths', { body: JSON.stringify(requests, null, 2), contentType: 'application/json' });
});
