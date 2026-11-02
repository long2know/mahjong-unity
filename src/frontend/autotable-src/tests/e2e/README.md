# Playwright E2E suite

Phase J Wave 5 (Apone) scaffolded the Playwright framework so future waves
can add specs as new flows ship. The current smoke layer proves the
framework wires up against the production-style Docker container.

## Layout

```
src/frontend/autotable-src/tests/
├── selectors.md          # Vasquez's testid stability contract
└── e2e/
    ├── README.md         # this file
    ├── playwright.config.ts
    └── smoke.spec.ts     # 4 smoke tests covering load / lobby / quick-match / mobile
```

All selectors used by specs MUST appear in `../selectors.md`. Adding a
new selector means adding the documentation row first, in the same PR.

## Quickstart — local

> **Title note.** The upstream `index.html` ships `<title>Autotable</title>`
> (Hicks's lane). The smoke spec asserts `/autotable/i` rather than
> `Mahjong` so a future rebrand to "Mahjong Autotable" keeps the test green
> without coupling to the exact string.

```bash
# 1. Install the Node deps (one time).
cd src/frontend/autotable-src
npm install

# 2. Install the Playwright browsers (one time).
npm run e2e:install

# 3. Start the application (single-image Docker build from Phase J Wave 3).
#    The smoke spec expects the autotable bundle at /autotable/.
docker build -t mahjong-autotable:local ../../..
docker run --rm -d --name mahjong-e2e -p 8080:8080 mahjong-autotable:local

# 4. Wait for /health to report green.
until curl -fsS http://localhost:8080/health > /dev/null; do sleep 1; done

# 5. Run the smoke suite.
npm run e2e

# 6. Tear the container down.
docker stop mahjong-e2e
```

Or, if you prefer the bundled dev-server (Parcel-only — no backend):

```bash
npx parcel index.html
# In another shell:
E2E_BASE_URL=http://localhost:1234/ npm run e2e
```

The dev-server flow will fail every test that depends on backend state
(Quick Match, reconnect, etc.) — use it only for selector / layout
iteration.

## CI

`.github/workflows/e2e-playwright.yml` runs the suite on every push to
`main` and every pull request targeting `main`. The job:

1. Checks out the repo.
2. Installs Node 20 + Playwright browsers (chromium only, `--with-deps`).
3. Builds the Docker image with `BUILD_SHA=${{ github.sha }}`.
4. Starts the container on port 8080.
5. Waits up to 30s for `/health`.
6. Runs `npm run e2e` with `E2E_BASE_URL=http://localhost:8080/autotable/`.
7. On failure: uploads `playwright-report/` as an artifact for triage.

## Conventions

- **Selector source of truth** is `../selectors.md`. Tests MUST use
  `page.getByTestId(...)` — never CSS class names, ids, or DOM-tree
  positions.
- **No flaky waits.** Use `await expect(...).toBeVisible({ timeout })`
  instead of `page.waitForTimeout(...)`.
- **Network calls** to the backend are real — the suite targets the
  full container, not a mocked frontend. If you need to stub something,
  prefer a backend-side test in `src/backend/tests/`.
- **Projects.** `chromium` covers the desktop layout (≥1025px); the
  `mobile-chrome` Pixel-5 project covers the 768px / 480px breakpoints.

## Troubleshooting

- **`expect(page).toHaveTitle` fails** — check that the bundle is
  actually being served at `E2E_BASE_URL` (curl it manually). The
  upstream `index.html` `<title>` is `Mahjong`.
- **`getByTestId('lobby-quick-match')` not found** — Hicks's Wave-2/4
  lobby surface may have rotated; reconcile against `../selectors.md`
  before patching the test.
- **`HEALTHCHECK` never green** — the container needs SQLite write
  access to its `/data` volume. `docker logs mahjong-e2e` surfaces the
  EF Core bootstrap error in 99% of cases.
- **Mobile project skipped** — the Pixel-5 viewport-only test is
  guarded with `test.skip(projectName !== 'mobile-chrome')`. Running
  only `--project=chromium` will skip it intentionally.

## Future waves

The reserved-prefix surfaces in `selectors.md` (`hud-*`,
`result-modal-*`, `game-over-*`) MUST acquire data-testid coverage in
production before the corresponding spec lands here. Per the Stability
Contract section of `selectors.md`, an empty placeholder gives a false
signal to the integration suite.
