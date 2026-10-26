import { test as base, expect } from '@playwright/test';
import { spawn } from 'node:child_process';
import { once } from 'node:events';
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
function wav() {
  const bytes = Buffer.alloc(44 + 30 * 96000);
  bytes.write('RIFF'); bytes.writeUInt32LE(bytes.length - 8, 4); bytes.write('WAVEfmt ', 8); bytes.writeUInt32LE(16, 16); bytes.writeUInt16LE(1, 20); bytes.writeUInt16LE(1, 22); bytes.writeUInt32LE(48000, 24); bytes.writeUInt32LE(96000, 28); bytes.writeUInt16LE(2, 32); bytes.writeUInt16LE(16, 34); bytes.write('data', 36); bytes.writeUInt32LE(bytes.length - 44, 40);
  for (let i = 44; i < bytes.length; i += 2) bytes.writeInt16LE(1000, i); return bytes;
}
test('production UI previews, plays, keeps running without a tab and controls saved generation', async ({ page, context, generationHost }, testInfo) => {
  const foreign: string[] = []; const violations: string[] = []; const errors: string[] = [];
  context.on('request', request => { const url = new URL(request.url()); if (url.hostname !== '127.0.0.1') foreign.push(url.hostname); });
  const observe = (tab: typeof page) => { tab.on('pageerror', error => errors.push(error.message)); tab.on('console', message => { if (/Content Security Policy|violates.*directive/.test(message.text())) violations.push(message.text()); }); };
  observe(page); await page.goto(generationHost); const origin = new URL(generationHost).origin;
  await page.getByLabel('Название проекта').fill('Синтетическая озвучка'); await page.getByRole('button', { name: 'Создать проект', exact: true }).click();
  await page.getByLabel('Запись голоса WAV').setInputFiles({ name: 'synthetic.wav', mimeType: 'audio/wav', buffer: wav() });
  await page.getByLabel(/мой голос/).check(); await page.getByRole('button', { name: 'Сохранить запись' }).click();
  await page.getByLabel('Точный текст фрагмента').fill('Синтетический голосовой образец.'); await page.getByLabel('Прослушал и подтверждаю').check();
  await page.getByRole('button', { name: 'Подтвердить голосовой фрагмент' }).click();
  await page.getByLabel('Текст книги', { exact: true }).fill('Первая история.\n\nВторая история.'); await page.getByRole('button', { name: 'Импортировать текст' }).click();
  await expect(page.getByRole('button', { name: 'Проба голоса', exact: true })).toBeEnabled(); await page.getByRole('button', { name: 'Проба голоса', exact: true }).click();
  await expect(page.getByText('Готово: 1 из 1', { exact: true })).toBeVisible();
  const audio = page.getByLabel('Аудио фрагмента 1'); await audio.evaluate((element: HTMLAudioElement) => element.play());
  await expect.poll(() => audio.evaluate((element: HTMLAudioElement) => element.currentTime)).toBeGreaterThan(0);
  await audio.evaluate((element: HTMLAudioElement) => element.pause());
  await page.getByLabel('Фрагмент 1', { exact: true }).check(); await page.getByLabel('Фрагмент 2', { exact: true }).check();
  await page.getByRole('button', { name: 'Озвучить выбранные фрагменты' }).click();
  await expect(page.getByRole('article', { name: 'Задание озвучивания' }).getByText('Озвучивание', { exact: true })).toBeVisible();
  await page.close();
  const reopened = await context.newPage(); observe(reopened); await reopened.goto(origin);
  await reopened.getByRole('button', { name: 'Продолжить «Синтетическая озвучка»' }).click();
  const generation = reopened.getByRole('article', { name: 'Задание озвучивания' });
  await expect(generation.getByText('Озвучивание', { exact: true })).toBeVisible();
  await generation.getByRole('button', { name: 'Пауза', exact: true }).click(); await expect(generation.getByText('На паузе', { exact: true })).toBeVisible();
  await expect(reopened.getByLabel('Аудио фрагмента 1')).toBeVisible();
  await generation.getByRole('button', { name: 'Продолжить задание' }).click(); await expect(generation.getByText('Готово: 2 из 2', { exact: true })).toBeVisible();
  await expect(generation.getByText('Завершено', { exact: true })).toBeVisible(); await expect(reopened.getByLabel('Аудио фрагмента 2')).toBeVisible();
  await reopened.getByRole('button', { name: 'Озвучить выбранные фрагменты' }).click();
  const newest = reopened.getByRole('article', { name: 'Задание озвучивания' }).first();
  await expect(newest.getByText('Озвучивание', { exact: true })).toBeVisible(); await newest.getByRole('button', { name: 'Отменить задание' }).click();
  await expect(newest.getByText('Отменено', { exact: true })).toBeVisible(); await expect(reopened.getByLabel('Аудио фрагмента 2')).toBeVisible();
  await reopened.setViewportSize({ width: 390, height: 844 });
  expect(await reopened.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  expect(foreign).toEqual([]); expect(violations).toEqual([]); expect(errors).toEqual([]);
  await reopened.setViewportSize({ width: 1280, height: 900 });
  await reopened.getByRole('region', { name: 'Озвучивание', exact: true }).screenshot({ path: testInfo.outputPath('generation-panel.png') });
});
