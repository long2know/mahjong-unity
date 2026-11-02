import { onLanguageChange, t } from './i18n';

declare const __BUILD_SHA__: string;

// This is the identity of the JavaScript actually loaded, not a server lookup.
export const LOADED_UI_BUILD = typeof __BUILD_SHA__ === 'string' ? __BUILD_SHA__ : 'dev';
export const BUILD_INFO_TIMEOUT_MS = 5000;

export interface ServerBuild {
  buildSha: string;
  version: string;
}

export function parseServerBuild(value: unknown): ServerBuild | null {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) return null;
  const record = value as Record<string, unknown>;
  if (typeof record.buildSha !== 'string' || record.buildSha.trim() === ''
      || record.buildSha.length > 512 || /[\u0000-\u001f\u007f]/.test(record.buildSha)
      || typeof record.version !== 'string' || record.version.length > 64
      || !/^\d+(?:\.\d+){1,3}$/.test(record.version)) return null;
  return { buildSha: record.buildSha, version: record.version };
}

export function hasBuildIdentity(build: string): boolean {
  return !['', 'dev', 'development', 'local', 'unknown', 'unidentified', 'n/a']
    .includes(build.trim().toLowerCase());
}

export function shortBuildId(build: string): string {
  const abbreviated = build.replace(/^([a-f0-9]{40}|[a-f0-9]{64})(?=-|$)/i,
    commit => commit.slice(0, 12));
  return abbreviated.length <= 40
    ? abbreviated
    : `${abbreviated.slice(0, 12)}…${abbreviated.slice(-24)}`;
}

type BuildState =
  | { kind: 'loading' | 'unavailable' }
  | { kind: 'ready'; server: ServerBuild };

let installed: (() => void) | null = null;

/** Independent of authentication, the lobby/game bootstrap and the renderer. */
export function installBuildInfo(): () => void {
  if (installed !== null) return installed;
  const root = document.getElementById('lobby-build-info');
  const summary = root?.querySelector<HTMLElement>('#build-summary');
  const uiBuild = root?.querySelector<HTMLElement>('#build-ui-full');
  const serverBuild = root?.querySelector<HTMLElement>('#build-server-full');
  const version = root?.querySelector<HTMLElement>('#build-server-version');
  const status = root?.querySelector<HTMLElement>('#build-status');
  const mismatch = root?.querySelector<HTMLElement>('#build-mismatch');
  const retry = root?.querySelector<HTMLButtonElement>('#build-retry');
  const reload = root?.querySelector<HTMLButtonElement>('#build-reload');
  if (!summary || !uiBuild || !serverBuild || !version || !status
      || !mismatch || !retry || !reload) return () => {};

  let state: BuildState = { kind: 'loading' };
  let request: AbortController | null = null;
  let timer: number | undefined;
  let disposed = false;

  const render = (): void => {
    summary.textContent = t(state.kind === 'ready' ? 'build.summary_version' : 'build.summary', {
      build: hasBuildIdentity(LOADED_UI_BUILD) ? shortBuildId(LOADED_UI_BUILD) : t('build.unidentified'),
      version: state.kind === 'ready' ? state.server.version : '',
    });
    uiBuild.textContent = LOADED_UI_BUILD || t('build.unidentified');
    retry.hidden = state.kind !== 'unavailable';
    mismatch.hidden = true;
    if (state.kind === 'ready') {
      serverBuild.textContent = state.server.buildSha;
      version.textContent = state.server.version;
      const comparable = hasBuildIdentity(LOADED_UI_BUILD) && hasBuildIdentity(state.server.buildSha);
      const different = comparable && LOADED_UI_BUILD !== state.server.buildSha;
      mismatch.hidden = !different;
      status.textContent = t(!comparable ? 'build.unverified' : different ? 'build.server' : 'build.match', {
        version: state.server.version,
      });
    } else {
      serverBuild.textContent = version.textContent = t(
        state.kind === 'loading' ? 'common.loading' : 'common.unavailable',
      );
      status.textContent = t(state.kind === 'loading' ? 'build.loading' : 'build.unavailable');
    }
  };

  const refresh = async (): Promise<void> => {
    if (disposed || request !== null) return;
    const attempt = new AbortController();
    request = attempt;
    state = { kind: 'loading' };
    render();
    const timeout = window.setTimeout(() => {
      attempt.abort();
      request = null;
      timer = undefined;
      state = { kind: 'unavailable' };
      render();
    }, BUILD_INFO_TIMEOUT_MS);
    timer = timeout;
    try {
      const response = await fetch('/health?simple=1', {
        cache: 'no-store',
        signal: attempt.signal,
      });
      if (!response.ok) throw new Error('Build metadata HTTP failure');
      const server = parseServerBuild(await response.json());
      if (server === null) throw new Error('Invalid build metadata');
      if (disposed || attempt.signal.aborted) return;
      state = { kind: 'ready', server };
      render();
    } catch {
      if (!disposed && !attempt.signal.aborted) {
        state = { kind: 'unavailable' };
        render();
      }
    } finally {
      window.clearTimeout(timeout);
      // A timed-out response must not clear a newer user-initiated retry.
      if (request === attempt) {
        request = null;
        timer = undefined;
      }
    }
  };
  const retryFetch = (): void => { void refresh(); };
  const reloadPage = (): void => { window.location.reload(); };
  retry.addEventListener('click', retryFetch);
  reload.addEventListener('click', reloadPage);
  const unsubscribe = onLanguageChange(render);
  installed = () => {
    if (disposed) return;
    disposed = true;
    window.clearTimeout(timer);
    request?.abort();
    retry.removeEventListener('click', retryFetch);
    reload.removeEventListener('click', reloadPage);
    unsubscribe();
    installed = null;
  };
  void refresh();
  return installed;
}
