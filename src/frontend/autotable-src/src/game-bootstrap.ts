import 'bootstrap/dist/js/bootstrap';
import type { Client } from './client';
import { mountChatPanel } from './lobby';
import { showToast } from './toast';
import { t } from './i18n';

let booted = false;

export async function bootstrapGame(): Promise<void> {
  if (booted) return;
  booted = true;
  markShellReady();
  try {
    const scene = await import('./scene-shell');
    const client = await scene.mountScene();
    if (/[?&]gameId=/.test(window.location.search)) {
      void mountChatPanel().catch(error => {
        showToast(t('social.load_failed', { reason: error instanceof Error ? error.message : String(error) }), 'error');
      });
    }
    if (shouldEnableVoice(client)) void import('./voice').then(module => module.installVoicePanel(client));
  } catch {
    const loading = document.getElementById('loading');
    if (loading) {
      loading.hidden = false;
      loading.textContent = 'Unity table could not start in this browser. Reload the page; lobby/chat remain available.';
    }
  }
}

export function preloadGameBootstrap(): void {
  void import('./game-bootstrap');
  void import('./scene-shell');
}

/** DOM-only feedback; scene/renderer readiness belongs exclusively to the C# FrameRendered callback. */
function markShellReady(): void {
  if (document.body.getAttribute('data-game-shell-ready') === 'true') return;
  document.body.setAttribute('data-game-shell-ready', 'true');
  const marker = document.createElement('div');
  marker.setAttribute('data-testid', 'game-shell-ready');
  marker.setAttribute('aria-hidden', 'true');
  marker.style.display = 'none';
  document.body.appendChild(marker);
  window.dispatchEvent(new CustomEvent('mahjong:game-shell-ready'));
}

function shouldEnableVoice(_client: Client): boolean {
  // The retained voice module renders its disabled toggle and checks the actual per-room flag.
  return true;
}
