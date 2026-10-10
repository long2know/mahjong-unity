import { GameUi } from './game-ui';
import { MoveLog } from './move-log';
import type { Game } from './game';
import type { Client } from './client';

let mounted = false;

/** Retained DOM effects mount without waiting for Unity or claiming a rendered-frame receipt. */
export async function mountEffects(game: Game, client: Client): Promise<void> {
  if (!mounted) {
    mounted = true;
    game.installGameUi(GameUi);
    new MoveLog(client).start();
  }
  markEffectsReady();
}

function markEffectsReady(): void {
  if (document.body.getAttribute('data-scene-effects-ready') === 'true') return;
  document.body.setAttribute('data-scene-effects-ready', 'true');
  const marker = document.createElement('div');
  marker.setAttribute('data-testid', 'scene-effects-ready');
  marker.setAttribute('aria-hidden', 'true');
  marker.style.display = 'none';
  document.body.appendChild(marker);
  window.dispatchEvent(new CustomEvent('mahjong:scene-effects-ready'));
}
