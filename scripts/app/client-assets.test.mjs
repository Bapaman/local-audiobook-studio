import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

for (const missing of ['none', 'js', 'css']) test(`generated assets completeness: missing ${missing}`, () => {
  const root = mkdtempSync(join(tmpdir(), 'las-assets-'));
  try {
    mkdirSync(join(root, 'assets'));
    writeFileSync(join(root, 'index.html'), '<script type="module" src="/assets/index-a.js"></script><link rel="stylesheet" href="/assets/index-b.css">');
    if (missing !== 'js') writeFileSync(join(root, 'assets/index-a.js'), 'export {}');
    if (missing !== 'css') writeFileSync(join(root, 'assets/index-b.css'), 'body {}');
    const result = spawnSync(process.execPath, [fileURLToPath(new URL('./client-assets.mjs', import.meta.url)), root], { encoding: 'utf8' });
    assert.equal(result.stderr, '');
    assert.equal(result.status, missing === 'none' ? 0 : 1, result.stderr);
  } finally { rmSync(root, { recursive: true }); }
});
