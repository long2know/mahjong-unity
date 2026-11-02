import type { Client } from './client';
import { Game } from './game';
import { attachLobbyClient } from './lobby';
import { loadPatternOrderingFromApi } from './pattern-utils';
import { showToast } from './toast';

let game: Game | null = null;

/** Model, room and DOM controls mount independently of a missing Unity build. */
export async function mountScene(): Promise<Client> {
  if (game) return game.client;
  void loadPatternOrderingFromApi();
  game = new Game();
  Object.assign(window, { game, world: game.world, client: game.client, __mahjongClient: game.client });
  attachLobbyClient(game.client);
  game.start();
  void import('./scene-effects')
    .then(module => module.mountEffects(game!, game!.client))
    .catch(() => showToast('Table controls failed to load. Reload the table; lobby/chat remain available.', 'error'));
  return game.client;
}
