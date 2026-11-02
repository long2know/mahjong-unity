// Contract tests — New Game persistent-control binding (Hicks, ng-persistent /
// ng-not-relay). Locks the `data-action="new-game"` activation convention that
// client-ui.ts delegates through, so Ferro's PERSISTENT outside-sidebar button
// (any id, any location, even nested icon/label targets) triggers the ONE
// authoritative fresh-game path — never id-coupled to the legacy in-sidebar
// `#new-game`. The full-app click→fresh-game acceptance (auto-seat/auto-deal,
// old-game release, real hit-testing) is Hudson's browser lane; these pin the
// pure predicate + a real-DOM delegation regression for the mechanism.
import { test, expect } from '@playwright/test';
import { isNewGameActivation, NEW_GAME_ACTION_SELECTOR } from '../../src/new-game-action';

test.describe('new-game persistent binding — activation predicate (isNewGameActivation)', () => {
  const stub = (closest: (s: string) => unknown): EventTarget =>
    ({ closest }) as unknown as EventTarget;

  test('the convention is the data-action attribute, not a hard-coded id', () => {
    expect(NEW_GAME_ACTION_SELECTOR).toBe('[data-action="new-game"]');
  });

  test('matches when the target is (or is nested in) a [data-action="new-game"] control', () => {
    // closest() returns a node ⇒ the click is on/inside a New Game control
    expect(isNewGameActivation(stub((s) => (s === NEW_GAME_ACTION_SELECTOR ? {} : null)))).toBe(true);
  });

  test('does NOT match an unrelated control (closest finds nothing)', () => {
    expect(isNewGameActivation(stub(() => null))).toBe(false);
  });

  test('fails closed on a null target or a non-Element target (no closest)', () => {
    expect(isNewGameActivation(null)).toBe(false);
    expect(isNewGameActivation({} as unknown as EventTarget)).toBe(false);       // text node etc.
  });
});

test.describe('new-game persistent binding — real-DOM delegation regression', () => {
  // Proves the delegation SHAPE client-ui uses (document listener + the exported
  // selector + closest ancestor-walk + a one-shot in-flight guard) against a
  // PERSISTENT control that is OUTSIDE any sidebar and has a NON-`new-game` id
  // with a nested <span> click target — plus debounce (rapid double-click ⇒ one
  // activation). Keyed on the exported selector so it can't drift from source.
  test('persistent outside-sidebar control (nested target) activates once; rapid double-click debounces; unrelated click ignored', async ({ page }) => {
    const result = await page.evaluate((selector) => {
      document.body.innerHTML = `
        <main id="table"><button id="decoy">Decoy</button></main>
        <div id="hud-bar">
          <button id="persistent-new-game" data-action="new-game">
            <span id="ng-label">🆕 New Game</span>
          </button>
        </div>`;
      let activations = 0;
      let inFlight = false;                          // mirrors newGameInFlight
      document.addEventListener('click', (ev: Event) => {
        const t = ev.target as Element | null;
        if (t === null || t.closest(selector) === null) return;
        ev.preventDefault();
        if (inFlight) return;                        // debounce guard
        inFlight = true;
        activations++;
      });
      const label = document.getElementById('ng-label') as HTMLElement;  // nested target
      const decoy = document.getElementById('decoy') as HTMLElement;
      decoy.click();                                 // unrelated ⇒ ignored
      label.click();                                 // persistent control ⇒ 1
      label.click();                                 // rapid second ⇒ debounced
      return activations;
    }, NEW_GAME_ACTION_SELECTOR);
    expect(result).toBe(1);
  });
});
