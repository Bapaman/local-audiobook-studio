import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

export const repositoryRoot = resolve(fileURLToPath(new URL('.', import.meta.url)), '../../../..');
export default function setup() {
  const project = 'tests/LocalAudiobookStudio.BrowserHost/LocalAudiobookStudio.BrowserHost.csproj';
  for (const args of [['restore', project, '--locked-mode'], ['build', project, '-c', 'Debug', '--no-restore']]) {
    const result = spawnSync('dotnet', args, { cwd: repositoryRoot, encoding: 'utf8', windowsHide: true });
    if (result.status !== 0) throw new Error(`BrowserHost prerequisite failed:\n${result.stdout}\n${result.stderr}`);
  }
}
