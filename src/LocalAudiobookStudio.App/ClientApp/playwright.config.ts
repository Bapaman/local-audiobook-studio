import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e', fullyParallel: false, workers: 1, retries: 0,
  timeout: 60_000, expect: { timeout: 10_000 },
  globalSetup: './e2e/global-setup.ts',
  use: { browserName: 'chromium', headless: true, viewport: { width: 1280, height: 900 }, trace: 'off' },
  reporter: 'list',
});
