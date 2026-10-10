// Phase J Wave 5 — Playwright E2E scaffold (Apone).
// Phase K Wave 15 — snapshotPathTemplate convention (Hicks).
//
// The frontend is served by the .NET backend at /autotable/ (single-image
// Docker deploy from Phase J Wave 3). The smoke spec targets the rendered
// bundle, NOT the Parcel dev-server, so contributors run a real container
// before invoking this config (see ./README.md for the exact commands).
//
// CI override (.github/workflows/e2e-playwright.yml) sets
//   E2E_BASE_URL=http://localhost:8080/autotable/
// and runs `npm run e2e` from src/frontend/autotable-src/ after the
// Docker container is healthy.
//
// W15 — `snapshotPathTemplate` is standardised so every visual-regression
// spec (today: manifest-screenshots-visual.spec.ts; tomorrow: any spec
// that uses `expect(page).toHaveScreenshot(...)`) reads / writes to the
// same canonical baseline tree.  Layout:
//
//   tests/e2e/__screenshots__/<spec-filename>/<arg>
//
// where `<arg>` is the first positional argument passed to
// `toHaveScreenshot()` (e.g. `"main-game.png"`).  The capture script
// `scripts/capture-real-surfaces.js` writes PNGs to this exact path, so
// once a baseline is captured the spec compares against it with no extra
// configuration.  Documented in `docs/frontend-pwa-audit.md §7.2`.

import { defineConfig, devices, type Project } from '@playwright/test';

// #119 revision (WP-D) — the deterministic committed-baseline `visual` project is
// gated behind E2E_VISUAL_GATE=1 so it runs ONLY in its dedicated pinned-container
// blocking job (.github/workflows/view-visual-gate.yml). This keeps the WebGL
// pixel baselines out of the general `npm run e2e` runs (WP-F/e2e-playwright.yml),
// whose host/font environment is not the pinned SwiftShader container and would
// otherwise raster-drift the baselines.
const visualProject: Project = {
  name: 'visual',
  testMatch: /view-mode-visual-baseline\.spec\.ts/,
  use: {
    ...devices['Desktop Chrome'],
    viewport: { width: 960, height: 540 },
    deviceScaleFactor: 1,
    reducedMotion: 'reduce',
    launchOptions: {
      args: [
        '--use-gl=angle',
        '--use-angle=swiftshader',
        '--enable-unsafe-swiftshader',
        '--force-color-profile=srgb',
        '--disable-lcd-text',
        '--disable-font-subpixel-positioning',
        '--hide-scrollbars',
        '--mute-audio',
      ],
    },
  },
};

const projects: Project[] = [
  {
    name: 'chromium',
    use: { ...devices['Desktop Chrome'] },
    // The committed-baseline WebGL visual spec runs ONLY under `visual`.
    testIgnore: /view-mode-visual-baseline\.spec\.ts/,
  },
  {
    name: 'mobile-chrome',
    // Functional mobile gate: keep Pixel 5 CSS-mobile semantics (viewport, touch,
    // isMobile, userAgent) but pin deviceScaleFactor:1. Pixel 5's native DSF 2.75
    // makes the SwiftShader backing buffer ~7.5x larger (2.15M vs 0.285M px), which
    // monopolises the browser main thread on the 4-vCPU hosted runner and starves
    // the client's WS/UI observation of server-pushed ceremony windows (deals park
    // at RollingDice/BreakPointMarked). DPR1 restores observation with NO change to
    // CSS layout / touch / raycasting (all pointer math is CSS-px) and no pixel
    // baselines are affected (no toHaveScreenshot runs on mobile-chrome).
    use: { ...devices['Pixel 5'], deviceScaleFactor: 1 },
    testIgnore: /view-mode-visual-baseline\.spec\.ts/,
  },
];
if (process.env.E2E_VISUAL_GATE === '1') projects.push(visualProject);

export default defineConfig({
  testDir: '.',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI ? 'github' : 'list',
  // W15 — canonical baseline tree.  Tokens documented at
  // https://playwright.dev/docs/api/class-testconfig#test-config-snapshot-path-template
  //   {testDir}            → <abs>/tests/e2e  (absolute testDir root)
  //   {testFileName}       → manifest-screenshots-visual.spec.ts
  //   {arg}                → first arg of toHaveScreenshot (sans extension)
  //   {ext}                → .png (or whatever the arg's extension was)
  // NB: the leading segment MUST be an absolute-base token (`{testDir}`),
  // NOT `{testFileDir}` — the latter is the test file's dir *relative to
  // testDir*, which is empty for specs that live directly in `tests/e2e`,
  // collapsing the template to the filesystem-root path `/__screenshots__/…`
  // (mkdir EACCES for non-root runners).  `{testDir}` yields the intended
  // `tests/e2e/__screenshots__/<spec>/<arg>.png` tree.
  // Pinned WITHOUT `{projectName}` / `{platform}` to keep baselines stable
  // across local-vs-CI runs; visual-regression specs already skip non-
  // chromium projects via testInfo.project.name guards.
  snapshotPathTemplate: '{testDir}/__screenshots__/{testFileName}/{arg}{ext}',
  use: {
    baseURL: process.env.E2E_BASE_URL || 'http://localhost:8080/autotable/',
    trace: 'on-first-retry',
  },
  projects,
});
