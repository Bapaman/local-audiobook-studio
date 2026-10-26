import { readFileSync, lstatSync } from 'node:fs';
import { resolve, join } from 'node:path';

// Vite's generated entry document uses only self-local /assets/<filename>
// script/style references. Reject other paths; never follow a URL or traversal.
try {
  const root = resolve(process.argv[2]);
  const ordinary = path => { const info = lstatSync(path); return info.isFile() && !info.isSymbolicLink() && info.nlink === 1; };
  if (!ordinary(join(root, 'index.html'))) process.exit(1);
  const assets = lstatSync(join(root, 'assets'));
  if (!assets.isDirectory() || assets.isSymbolicLink()) process.exit(1);
  const html = readFileSync(join(root, 'index.html'), 'utf8');
  const references = [...html.matchAll(/\b(?:src|href)=["']([^"']+)["']/g)].map(match => match[1]);
  if (!references.some(ref => ref.endsWith('.js')) || !references.some(ref => ref.endsWith('.css'))) process.exit(1);
  for (const ref of references) {
    if (!/^\/assets\/[A-Za-z0-9_-]+\.(?:js|css)$/.test(ref) || !ordinary(join(root, ref.slice(1)))) process.exit(1);
  }
} catch { process.exit(1); }
