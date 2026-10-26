# Local studio client

Development requires Node.js 24 and the exact toolchain pinned in `package-lock.json`.
Run `npm ci`, then `npm exec -- playwright install chromium` once for the matching
test browser. Run `npm run test`, `npm run build`, and `npm run test:e2e`.

The browser tests launch only their own BrowserHost and temporary synthetic data.
They record browser request origins and dispose the owned host after every test.
Screenshots contain generated test text and go to the ignored SDD workspace.

Production assets are built by publish or `scripts/app/Start-LocalStudio.ps1` and
served by authenticated loopback Kestrel. Installed users need no Node/npm runtime.
Ordinary .NET builds never install client packages. There is no production Vite server.
