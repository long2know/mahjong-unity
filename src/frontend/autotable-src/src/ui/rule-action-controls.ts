import type { Client } from '../client';
import type { Entry } from '../../server/protocol';
import type { ClaimWindowEntry, OwnTurnCommand, OwnTurnEntry } from '../types';
import { hideEl, setElHidden } from '../dom-utils';
import { onLanguageChange, t } from '../i18n';
import { showToast } from '../toast';
import { RuleActionChoice, type RuleChoiceKind, type RuleTileChoice } from './rule-action-choice';
import './rule-action-controls.css';

export type ClaimIntent = 'Pung' | 'Chow' | 'Kong' | 'Hu' | 'Pass';
type OwnAction = 'hu' | 'concealedKong' | 'addedKong';
type ClaimCommandContext = Readonly<
  { gameId: string; expectedVersion: number } |
  { gameId?: never; expectedVersion?: never }
>;
type PendingAction = { context: string; seat: number } & (
  { kind: 'own'; action: OwnAction; gameId: string; expectedVersion: number } |
  { kind: 'claim'; action: 'claim' | 'pass' }
);

const CONFIRMATION_TIMEOUT_MS = 15_000;
const controllers = new WeakMap<Client, RuleActionControls>();

function physicalTiles(value: unknown, count: number): value is number[] {
  return Array.isArray(value) && value.length === count
    && value.every((id) => typeof id === 'number' && Number.isInteger(id) && id >= 0 && id < 108)
    && new Set(value).size === count;
}

function physicalTileLabel(id: number): string {
  const face = Math.floor(id / 4);
  return `${face % 9 + 1}${['m', 'p', 's'][Math.floor(face / 9)]}`;
}

function seatEntry<T>(entries: Iterable<[string | number, T]>, seat: number): T | null {
  let selected: T | null = null;
  for (const [key, value] of entries) {
    if (String(key) === String(seat)) selected = value;
  }
  return selected;
}

export function getRuleActionControls(client: Client): RuleActionControls {
  let controller = controllers.get(client);
  if (controller === undefined) {
    controller = new RuleActionControls(client);
    controllers.set(client, controller);
  }
  return controller;
}

export class RuleActionControls {
  private readonly root: HTMLElement;
  private readonly hu: HTMLButtonElement;
  private readonly concealed: HTMLButtonElement;
  private readonly added: HTMLButtonElement;
  private readonly status: HTMLElement;
  private readonly recovery: HTMLElement;
  private readonly recoveryTitle: HTMLElement;
  private readonly recoveryMessage: HTMLElement;
  private readonly reload: HTMLButtonElement;
  private readonly chooser: RuleActionChoice;
  private readonly listeners = new Set<() => void>();
  private ownSnapshot: OwnTurnEntry | null = null;
  private claimSnapshot: ClaimWindowEntry | null = null;
  private claimGeneration = 0;
  private actor: string | null = null;
  private connectionGeneration = 0;
  private pending: PendingAction | null = null;
  private pendingTimer: number | null = null;
  private pendingTimedOut = false;
  private deadlineTimer: number | null = null;
  private awaitingSnapshot = false;

  constructor(private readonly client: Client) {
    this.root = document.getElementById('own-turn-actions')!;
    this.hu = document.getElementById('own-turn-hu') as HTMLButtonElement;
    this.concealed = document.getElementById('own-turn-concealed-kong') as HTMLButtonElement;
    this.added = document.getElementById('own-turn-added-kong') as HTMLButtonElement;
    this.status = document.getElementById('own-turn-action-status')!;
    this.recovery = document.getElementById('rule-action-recovery')!;
    this.recoveryTitle = document.getElementById('rule-action-recovery-title')!;
    this.recoveryMessage = document.getElementById('rule-action-recovery-message')!;
    this.reload = document.getElementById('rule-action-reload') as HTMLButtonElement;
    this.chooser = new RuleActionChoice();
    this.hu.onclick = () => this.requestOwn('hu');
    this.concealed.onclick = () => this.requestOwn('concealedKong');
    this.added.onclick = () => this.requestOwn('addedKong');
    this.reload.onclick = () => {
      if (!this.pendingTimedOut || this.pending === null || this.ownerContext() !== this.actor) {
        this.unavailable();
        return;
      }
      window.location.reload();
    };
    for (const button of [this.hu, this.concealed, this.added, this.reload]) {
      const activationKey = (event: KeyboardEvent): void => {
        if (event.key === ' ' || event.key === 'Enter') event.stopPropagation();
      };
      button.addEventListener('keydown', activationKey);
      button.addEventListener('keyup', activationKey);
    }
    this.client.on('connect', () => this.invalidate());
    this.client.on('disconnect', () => this.invalidate());
    // Collections finish applying the frame before this listener reads them.
    this.client.on('update', (entries, full) => this.refresh(entries, full));
    onLanguageChange(() => {
      this.localize();
      this.render();
    });
    this.localize();
    this.refresh();
  }

  onChange(listener: () => void): () => void {
    this.listeners.add(listener);
    listener();
    return () => { this.listeners.delete(listener); };
  }

  get claim(): ClaimWindowEntry | null {
    if (this.pending !== null) return null;
    return this.currentClaim();
  }

  get claimPending(): boolean {
    return this.pending?.kind === 'claim';
  }

  private ownerContext(): string | null {
    const client = this.client;
    const query = new URLSearchParams(location.search);
    const room = query.get('gameId');
    const seat = client.seat;
    if (!client.connected() || room === null || room !== client.lastGameId
        || room !== client.serverSnapshotGameId
        || (query.get('variant') ?? 'changsha').toLowerCase() !== 'changsha'
        || document.body.classList.contains('spectating')
        || seat === null || seat < 0 || seat > 3
        || client.seatPlayers[seat] !== client.playerId()) return null;
    const complete = client.gameComplete.get('current');
    if (complete?.isComplete || complete?.IsComplete || complete?.isGameComplete || complete?.IsGameComplete) return null;
    return JSON.stringify([this.connectionGeneration, room, client.playerId(), seat]);
  }

  private currentOwn(): OwnTurnEntry | null {
    const owner = this.ownerContext();
    const own = this.ownSnapshot;
    const turn = this.client.turn.get('current');
    if (this.awaitingSnapshot || owner === null || owner !== this.actor || own === null
        || typeof own.gameId !== 'string' || own.gameId === ''
        || !Number.isSafeInteger(own.stateVersion) || own.stateVersion < 0 || own.stateVersion > 0x7fffffff
        || turn?.phase?.toLowerCase() !== 'awaitingdiscard'
        || turn.activeSeat !== this.client.seat || turn.awaitingDiscard !== true
        || !Array.isArray(own.concealedKongs) || !Array.isArray(own.addedKongs)) return null;
    return own;
  }

  private currentClaim(): ClaimWindowEntry | null {
    const owner = this.ownerContext();
    const claim = this.claimSnapshot;
    if (this.awaitingSnapshot || owner === null || owner !== this.actor || claim === null
        || this.client.turn.get('current')?.phase?.toLowerCase() !== 'awaitingclaim'
        || !Array.isArray(claim.available) || typeof claim.deadline !== 'number'
        || this.claimCommandContext(claim) === null
        || (claim.deadline > 0 && claim.deadline <= Date.now())) return null;
    const chowOptions = (claim.chowOptions ?? []).filter((tiles) => physicalTiles(tiles, 2));
    if (chowOptions.length === (claim.chowOptions ?? []).length
        && (!claim.available.includes('Chow') || chowOptions.length > 0)) return claim;
    return {
      ...claim,
      chowOptions,
      available: claim.available.filter((kind) => kind !== 'Chow' || chowOptions.length > 0),
    };
  }

  private claimCommandContext(claim: ClaimWindowEntry): ClaimCommandContext | null {
    const hasGameId = Object.prototype.hasOwnProperty.call(claim, 'gameId');
    const hasVersion = Object.prototype.hasOwnProperty.call(claim, 'stateVersion');
    if (!hasGameId && !hasVersion) return {};
    const { gameId, stateVersion } = claim;
    if (!hasGameId || !hasVersion || typeof gameId !== 'string' || gameId === ''
        || typeof stateVersion !== 'number' || !Number.isInteger(stateVersion)
        || stateVersion < 0 || stateVersion > 0x7fffffff) return null;
    return { gameId, expectedVersion: stateVersion };
  }

  private ownContext(own: OwnTurnEntry): string {
    // The metadata gameId is the runtime id, not necessarily the URL room alias.
    return JSON.stringify([this.actor, 'own', own.gameId, own.stateVersion]);
  }

  private claimContext(claim: ClaimWindowEntry): string {
    // Version binds a selection, but is not an arbitration receipt for a sent claim.
    return JSON.stringify([
      this.claimWindowContext(claim), this.claimCommandContext(claim), claim.available, claim.chowOptions,
    ]);
  }

  private claimWindowContext(claim: ClaimWindowEntry): string {
    return JSON.stringify([
      this.actor, this.claimGeneration, 'claim', claim.gameId, claim.source, claim.tile, claim.deadline,
    ]);
  }

  private setClaimSnapshot(claim: ClaimWindowEntry | null): void {
    const identity = (value: ClaimWindowEntry | null): string =>
      JSON.stringify(value === null ? null : [value.gameId, value.source, value.tile, value.deadline]);
    if (identity(claim) !== identity(this.claimSnapshot)) this.claimGeneration++;
    this.claimSnapshot = claim;
  }

  private refresh(entries: Entry[] = [], full = false): void {
    if (full) this.awaitingSnapshot = false;
    const actor = this.ownerContext();
    const seat = this.client.seat;
    if (actor !== this.actor) {
      this.clearPending();
      this.chooser.close();
      this.actor = actor;
      this.ownSnapshot = actor !== null && seat !== null ? seatEntry(this.client.ownTurn.entries(), seat) : null;
      this.setClaimSnapshot(actor !== null && seat !== null ? seatEntry(this.client.claim.entries(), seat) : null);
    } else if (full && seat !== null) {
      this.ownSnapshot = seatEntry(this.client.ownTurn.entries(), seat);
      this.setClaimSnapshot(seatEntry(this.client.claim.entries(), seat));
    }
    for (const [kind, key, value] of entries) {
      if (actor !== null && String(key) === String(seat)) {
        if (kind === 'ownTurn') this.ownSnapshot = value;
        if (kind === 'claim') this.setClaimSnapshot(value);
      }
      if (kind === 'actionRejected' && key === 'current' && value !== null) {
        const rejection = this.client.actionRejected.get('current');
        if (rejection !== null
            && ['ownTurn', 'hu', 'concealedKong', 'addedKong', 'claim', 'pass'].includes(rejection.action)) {
          if (this.pending !== null && rejection.requestedSeat === this.pending.seat
              && ['ownTurn', this.pending.action].includes(rejection.action)) {
            this.clearPending();
            this.chooser.close();
            this.awaitingSnapshot = true;
          }
          showToast(t('actions.rejected', { reason: rejection.reason }), 'error');
        }
      }
    }
    const own = this.currentOwn();
    const claim = this.currentClaim();
    const ownContext = own === null ? null : this.ownContext(own);
    const claimContext = claim === null ? null : this.claimContext(claim);
    if (this.pending?.kind === 'own') {
      if (own === null || own.gameId !== this.pending.gameId || own.stateVersion > this.pending.expectedVersion) {
        this.clearPending();
      }
    } else if (this.pending?.kind === 'claim') {
      // A local countdown expiring is not an authoritative arbitration receipt.
      if (this.claimSnapshot === null
          || this.client.turn.get('current')?.phase?.toLowerCase() !== 'awaitingclaim'
          || this.pending.context !== this.claimWindowContext(this.claimSnapshot)) this.clearPending();
    }
    if (this.chooser.contextKey !== null && this.chooser.contextKey !== ownContext
        && this.chooser.contextKey !== claimContext) this.chooser.close();
    if (this.deadlineTimer !== null) window.clearTimeout(this.deadlineTimer);
    this.deadlineTimer = null;
    if (claim !== null && claim.deadline > Date.now()) {
      this.deadlineTimer = window.setTimeout(() => this.refresh(), claim.deadline - Date.now());
    }
    this.render();
    this.emitChange();
  }

  private invalidate(): void {
    this.connectionGeneration++;
    this.actor = null;
    this.ownSnapshot = null;
    this.setClaimSnapshot(null);
    this.awaitingSnapshot = true;
    this.clearPending();
    this.chooser.close();
    if (this.deadlineTimer !== null) window.clearTimeout(this.deadlineTimer);
    this.deadlineTimer = null;
    hideEl(this.root);
    hideEl(this.recovery);
    this.emitChange();
  }

  private localize(): void {
    document.getElementById('own-turn-actions-title')!.textContent = t('actions.own_turn');
    this.hu.textContent = t('actions.self_draw_hu');
    this.concealed.textContent = t('actions.concealed_kong');
    this.added.textContent = t('actions.added_kong');
    this.reload.textContent = t('actions.reload_table');
    this.chooser.localize();
  }

  private ownOptions(own: OwnTurnEntry, action: Exclude<OwnAction, 'hu'>): number[][] {
    return action === 'concealedKong'
      ? own.concealedKongs.filter((tiles) => physicalTiles(tiles, 4))
      : own.addedKongs.filter((tile) => physicalTiles([tile], 1)).map((tile) => [tile]);
  }

  private render(): void {
    const own = this.currentOwn();
    const canHu = own?.hu === true;
    const canConcealed = own !== null && this.ownOptions(own, 'concealedKong').length > 0;
    const canAdded = own !== null && this.ownOptions(own, 'addedKong').length > 0;
    setElHidden(this.root, !(canHu || canConcealed || canAdded) || this.pendingTimedOut);
    setElHidden(this.recovery, !this.pendingTimedOut || this.pending === null || this.ownerContext() === null);
    const waitingForClaim = this.pending?.kind === 'claim';
    const recoveryTitle = t(waitingForClaim ? 'actions.claim_waiting_title' : 'actions.recovery_title');
    const recoveryMessage = t(waitingForClaim ? 'actions.claim_waiting' : 'actions.no_confirmation');
    this.recovery.setAttribute('role', waitingForClaim ? 'status' : 'alert');
    if (this.recoveryTitle.textContent !== recoveryTitle) this.recoveryTitle.textContent = recoveryTitle;
    if (this.recoveryMessage.textContent !== recoveryMessage) this.recoveryMessage.textContent = recoveryMessage;
    for (const [button, available] of [
      [this.hu, canHu], [this.concealed, canConcealed], [this.added, canAdded],
    ] as const) {
      setElHidden(button, !available);
      button.disabled = !available || this.pending !== null;
    }
    this.root.setAttribute('aria-busy', String(this.pending?.kind === 'own'));
    this.status.textContent = this.pending?.kind === 'own' ? t('actions.pending') : '';
  }

  private emitChange(): void {
    for (const listener of this.listeners) listener();
  }

  private unavailable(): void {
    this.chooser.close();
    this.refresh();
    showToast(t('actions.unavailable'), 'info');
  }

  private clearPending(): void {
    this.pending = null;
    this.pendingTimedOut = false;
    if (this.pendingTimer !== null) window.clearTimeout(this.pendingTimer);
    this.pendingTimer = null;
  }

  private submit(pending: PendingAction, entry: Entry): void {
    this.pending = pending;
    this.chooser.close();
    this.render();
    this.emitChange();
    try {
      this.client.update([entry]);
    } catch (error) {
      this.clearPending();
      this.refresh();
      showToast(t('actions.rejected', { reason: error instanceof Error ? error.message : String(error) }), 'error');
      throw error;
    }
    this.pendingTimer = window.setTimeout(() => {
      this.pendingTimer = null;
      this.pendingTimedOut = true;
      this.refresh();
    }, CONFIRMATION_TIMEOUT_MS);
  }

  private requestOwn(action: OwnAction): void {
    const own = this.currentOwn();
    if (own === null || this.pending !== null) {
      this.unavailable();
      return;
    }
    const context = this.ownContext(own);
    if (action === 'hu') {
      this.sendOwn(action, [], context);
      return;
    }
    const options = this.ownOptions(own, action);
    if (options.length === 1) {
      this.sendOwn(action, options[0], context);
    } else if (options.length > 1) {
      this.chooseTiles(action, context, options, (tiles, key) => this.sendOwn(action, tiles, key));
    } else {
      this.unavailable();
    }
  }

  private sendOwn(action: OwnAction, tileIds: number[], context: string): void {
    const own = this.currentOwn();
    const seat = this.client.seat;
    if (own === null || seat === null || this.pending !== null || this.ownContext(own) !== context
        || (action === 'hu' ? own.hu !== true
          : !this.ownOptions(own, action).some((option) => this.sameTiles(option, tileIds)))) {
      this.unavailable();
      return;
    }
    const target = { gameId: own.gameId, expectedVersion: own.stateVersion };
    const command: OwnTurnCommand = action === 'hu'
      ? { ...target, action } : { ...target, action, tileIds: [...tileIds] };
    this.submit({ kind: 'own', action, seat, context, ...target }, ['ownTurn', seat, command]);
  }

  requestClaim(intent: ClaimIntent): void {
    const claim = this.claim;
    if (claim === null || (intent !== 'Pass' && !claim.available.includes(intent))) {
      this.unavailable();
      return;
    }
    const context = this.claimContext(claim);
    const commandContext = this.claimCommandContext(claim);
    if (commandContext === null) {
      this.unavailable();
      return;
    }
    if (intent === 'Chow') {
      const options = claim.chowOptions ?? [];
      if (options.length > 1) {
        this.chooseTiles('chow', context, options, (tiles, key) => this.sendClaim(intent, key, commandContext, tiles));
        return;
      }
      if (options.length === 1) {
        this.sendClaim(intent, context, commandContext, options[0]);
        return;
      }
      this.unavailable();
      return;
    }
    this.sendClaim(intent, context, commandContext);
  }

  private sendClaim(
    intent: ClaimIntent, context: string, commandContext: ClaimCommandContext, tileIds?: number[],
  ): void {
    const claim = this.claim;
    const seat = this.client.seat;
    if (claim === null || seat === null || this.claimContext(claim) !== context
        || (intent !== 'Pass' && !claim.available.includes(intent))
        || (intent === 'Chow' && (tileIds === undefined
          || !(claim.chowOptions ?? []).some((option) => this.sameTiles(option, tileIds))))) {
      this.unavailable();
      return;
    }
    const action = intent === 'Pass' ? 'pass' : 'claim';
    const command = {
      action, type: intent === 'Pass' ? null : intent,
      ...(tileIds === undefined ? {} : { tileIds: [...tileIds] }),
      ...commandContext,
    };
    this.submit({ kind: 'claim', action, seat, context: this.claimWindowContext(claim) }, ['claim', String(seat), command]);
  }

  private sameTiles(first: ReadonlyArray<number>, second: ReadonlyArray<number>): boolean {
    return first.length === second.length && first.every((tile, index) => tile === second[index]);
  }

  private chooseTiles(
    kind: RuleChoiceKind, context: string, options: ReadonlyArray<number[]>,
    choose: (tiles: number[], context: string) => void,
  ): void {
    const claimedTile = this.claimSnapshot?.tile;
    if (kind === 'chow' && typeof claimedTile !== 'number') {
      this.unavailable();
      return;
    }
    const choices: RuleTileChoice[] = options.map((tileIds) => {
      const shownTiles = kind === 'chow' && typeof claimedTile === 'number'
        ? [...tileIds, claimedTile].sort((a, b) => a - b) : tileIds;
      return {
        tileIds,
        label: () => kind === 'chow'
          ? t('actions.chow_option', { tiles: shownTiles.map(physicalTileLabel).join(' + ') })
          : t('actions.kong_option', {
            kind: t(kind === 'concealedKong' ? 'actions.concealed_kong' : 'actions.added_kong'),
            tiles: physicalTileLabel(tileIds[0]),
          }),
      };
    });
    this.chooser.show(kind, context, choices, choose);
  }
}
