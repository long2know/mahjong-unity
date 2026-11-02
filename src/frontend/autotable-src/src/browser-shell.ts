import { getActiveLocale, onLanguageChange, t } from './i18n';
import { setDrawerAccessibility } from './ui/drawer-accessibility';
import './browser-shell.css';

/** Reparent the existing controls; their IDs, handlers and feature gates stay authoritative. */
export function installBrowserShell(): boolean {
  if (document.getElementById('browser-shell')) return true;
  const ids = ['new-game', 'lobby-toggle', 'settings-toggle', 'settings-button', 'move-log-toggle',
    'variant-badge', 'turn-banner', 'chat-panel', 'move-log'] as const;
  const nodes = ids.map(id => document.getElementById(id));
  if (nodes.some(node => node === null)) return false;
  const [newGame, lobby, gameSettings, preferences, moves, variant, turn, chat, log] = nodes as HTMLElement[];

  const shell = document.createElement('header');
  shell.id = 'browser-shell';
  const controls = document.createElement('nav');
  controls.className = 'browser-toolbar';
  controls.setAttribute('aria-label', 'Table controls');
  controls.append(newGame, lobby, gameSettings, preferences, moves);
  const context = document.createElement('div');
  context.className = 'browser-context';
  context.append(variant, turn);
  const panels = document.createElement('div');
  panels.className = 'browser-panels';
  const actions = document.createElement('div');
  actions.id = 'browser-action-slot';
  for (const id of ['own-turn-actions', 'pickup-hud', 'bot-banner']) {
    const action = document.getElementById(id);
    if (action) actions.appendChild(action);
  }
  panels.append(actions, chat, log);
  shell.append(controls, context, panels);

  const label = (button: HTMLElement, full: string, short = full): void => {
    const longLabel = document.createElement('span');
    longLabel.className = 'browser-label-full';
    longLabel.textContent = full;
    const shortLabel = document.createElement('span');
    shortLabel.className = 'browser-label-short';
    shortLabel.textContent = short;
    button.replaceChildren(longLabel, shortLabel);
  };
  const localize = (): void => {
    const locale = getActiveLocale();
    const gameLabel = locale === 'en' ? 'Game' : locale === 'zh-Hans' ? '对局' : '對局';
    const movesLabel = locale === 'en' ? 'Moves' : locale === 'zh-Hans' ? '牌谱' : '牌譜';
    label(lobby, t('lobby.title'));
    label(gameSettings, `${gameLabel} · ${t('settings.title')}`, gameLabel);
    label(preferences, t('settings.title'));
    label(moves, t('replay.move_log'), movesLabel);
    gameSettings.setAttribute('title', t('lobby.new_table_settings'));
    moves.setAttribute('aria-label', t('replay.move_log'));
    lobby.setAttribute('aria-controls', 'lobby-panel');
    lobby.setAttribute('aria-haspopup', 'dialog');
  };
  localize();
  onLanguageChange(localize);
  document.body.classList.add('browser-shell-active');
  document.body.appendChild(shell);
  for (const [id, toggle] of [['settings-drawer', gameSettings], ['settings-drawer-v2', preferences]] as const) {
    const drawer = document.getElementById(id);
    if (drawer) setDrawerAccessibility(drawer, toggle, drawer.getAttribute('aria-hidden') === 'false');
  }

  // One observer, not a frame loop: wrapping text and visual-viewport changes
  // must move the information panels and action controls together.
  const measure = (): void => {
    const height = `${Math.ceil(shell.getBoundingClientRect().height)}px`;
    const style = document.documentElement.style;
    if (style.getPropertyValue('--browser-shell-height') !== height) {
      style.setProperty('--browser-shell-height', height);
    }
  };
  measure();
  if (typeof ResizeObserver !== 'undefined') new ResizeObserver(measure).observe(shell);
  return true;
}
