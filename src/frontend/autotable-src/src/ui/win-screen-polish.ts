// Ferro — Win-screen polish (new self-contained module).
//
// Wraps the existing `#game-complete-modal` (rendered by
// game-ui.ts:setupGameCompleteModal + renderGameComplete) with:
//
//   • Rolling score counters (0 → target over 1.2s, ease-out)
//   • A fan-list reveal section beneath the totals, listing the
//     Changsha fan names with 中文 + Pinyin.  Pulls from Frost's
//     `Changsha/Scoring/Fan.cs` schema when available; otherwise
//     falls back to a hard-coded mock map of the basic Changsha
//     fans.
//
// We do not modify game-ui.ts (Hicks's trunk).  Strategy:
//   1) Subscribe to `client.gameComplete.on('update')` ourselves.
//   2) On completion, defer one tick (let game-ui.ts paint the
//      table body), then walk the score cells and wrap the delta
//      text node in a `<span class="ferro-roll-counter">`.
//   3) Insert a `#ferro-win-fans` section between the totals and
//      the recap so the existing modal layout stays intact.
//   4) Re-run on every subsequent render so a hand-history toggle
//      or full-resync repaint is preserved.
//
// Mobile sized: section reflows under 480px width.

import './win-screen-polish.css';

interface ScoreDelta { seat: number; delta: number }
interface HandResultEntry {
  winner: number;
  type: 'Hu' | 'Draw' | 'ZhaHu' | string;
  score?: ScoreDelta[];
  hand?: number[];
  fans?: Array<string | FanEntry>;
  Fans?: Array<string | FanEntry>;
}
interface FanEntry {
  // Multiple possible field names Frost may settle on.
  name?: string;
  Name?: string;
  zh?: string;
  Zh?: string;
  pinyin?: string;
  Pinyin?: string;
  points?: number;
  Points?: number;
}

interface GameCompleteEntry {
  isComplete?: boolean;
  IsComplete?: boolean;
  isGameComplete?: boolean;
  IsGameComplete?: boolean;
  totalScores?: Record<string, number>;
  TotalScores?: Record<string, number>;
  handHistory?: HandResultEntry[];
  HandHistory?: HandResultEntry[];
  maxHands?: number;
  MaxHands?: number;
}

interface GameCompleteCollectionLike {
  on(event: 'update', handler: (entries: Array<[string, GameCompleteEntry | null]>, full: boolean) => void): void;
  get(key: string): GameCompleteEntry | null | undefined;
}

interface ClientLike {
  gameComplete: GameCompleteCollectionLike;
}

interface GameLike {
  client?: ClientLike;
}

// Basic Changsha fans, 中文 + Pinyin + short English gloss.  When the
// runtime ships canonical fan names per Frost's Fan.cs we'll prefer
// those; this table is a fallback so the section renders something
// readable even pre-merge.
const CHANGSHA_FANS: Record<string, { zh: string; py: string; en: string; points: number }> = {
  // Default win baseline (every Hu).
  'PingHu':         { zh: '平胡', py: 'Píng Hú',         en: 'Plain Win',           points: 1 },
  'MenQian':        { zh: '门前清', py: 'Mén Qián Qīng',  en: 'Concealed Hand',      points: 1 },
  'ZiMo':           { zh: '自摸', py: 'Zì Mō',           en: 'Self-drawn Win',      points: 1 },
  'QiangGang':      { zh: '抢杠', py: 'Qiǎng Gàng',      en: 'Robbing the Kong',    points: 2 },
  'GangShangHua':   { zh: '杠上开花', py: 'Gàng Shàng Huā', en: 'Win on Replacement', points: 2 },
  'HaiDiLaoYue':    { zh: '海底捞月', py: 'Hǎi Dǐ Lāo Yuè', en: 'Last-tile Self-draw', points: 2 },
  'HeDiPaoQian':    { zh: '河底炮签', py: 'Hé Dǐ Pào Qiān', en: 'Last-discard Win',    points: 2 },
  'QingYiSe':       { zh: '清一色', py: 'Qīng Yī Sè',    en: 'Full Suit',           points: 6 },
  'HunYiSe':        { zh: '混一色', py: 'Hùn Yī Sè',     en: 'Half Suit',           points: 3 },
  'QiDui':          { zh: '七对', py: 'Qī Duì',         en: 'Seven Pairs',         points: 4 },
  'PengPengHu':     { zh: '碰碰胡', py: 'Pèng Pèng Hú',  en: 'All Triplets',        points: 3 },
  'JinGouDiao':     { zh: '金钩钓', py: 'Jīn Gōu Diào',  en: 'Single-tile Wait',    points: 2 },
};

export class WinScreenPolish {
  private readonly game: GameLike;
  private observer: MutationObserver | null = null;
  private attached: boolean = false;
  private rafHandles: Set<number> = new Set();
  // Track the last payload so we can re-render after game-ui.ts repaints.
  private lastPayload: GameCompleteEntry | null = null;

  constructor(game: GameLike) {
    this.game = game;
  }

  attach(): void {
    if (this.attached) return;
    const client = this.game.client;
    if (client === undefined) return;
    this.attached = true;
    client.gameComplete.on('update', this.onGameCompleteUpdate);
    // Reflect any pre-existing complete state (reconnect path).
    const cur = client.gameComplete.get('current');
    if (cur !== null && cur !== undefined && this.readCompleteFlag(cur)) {
      this.lastPayload = cur;
      this.scheduleEnhance();
    }
  }

  detach(): void {
    this.attached = false;
    if (this.observer !== null) {
      this.observer.disconnect();
      this.observer = null;
    }
    for (const h of this.rafHandles) cancelAnimationFrame(h);
    this.rafHandles.clear();
    const fans = document.getElementById('ferro-win-fans');
    fans?.remove();
  }

  // ---------------------------------------------------------------------
  // Subscription.
  // ---------------------------------------------------------------------

  private readonly onGameCompleteUpdate = (
    entries: Array<[string, GameCompleteEntry | null]>,
  ): void => {
    for (const [key, value] of entries) {
      if (key !== 'current') continue;
      if (value === null) {
        // Tombstone — game-ui.ts hides the modal; we just clean up.
        this.lastPayload = null;
        if (this.observer !== null) {
          this.observer.disconnect();
          this.observer = null;
        }
        const fans = document.getElementById('ferro-win-fans');
        fans?.remove();
        continue;
      }
      if (!this.readCompleteFlag(value)) continue;
      this.lastPayload = value;
      this.scheduleEnhance();
    }
  };

  private readCompleteFlag(v: GameCompleteEntry): boolean {
    return Boolean(
      v.isComplete || v.IsComplete || v.isGameComplete || v.IsGameComplete,
    );
  }

  // ---------------------------------------------------------------------
  // Enhancement.
  //
  // game-ui.ts:onGameCompleteUpdate paints the modal body, then opens
  // it via $('#game-complete-modal').modal('show').  Our subscriber
  // may fire in either order relative to game-ui's, so we:
  //   1) Defer one tick (let game-ui.ts paint).
  //   2) Run the enhancement.
  //   3) Install a MutationObserver on the modal body — if game-ui.ts
  //      re-renders (e.g. handHistory arrives in a follow-up update),
  //      we re-apply the polish.
  // ---------------------------------------------------------------------

  private scheduleEnhance(): void {
    window.setTimeout(() => this.enhance(), 0);
  }

  private enhance(): void {
    const modal = document.getElementById('game-complete-modal');
    if (modal === null) return;
    const totalsBody = modal.querySelector<HTMLTableSectionElement>('#game-complete-totals tbody');
    if (totalsBody === null) return;

    this.installObserver(modal);

    this.animateScoreCells(totalsBody);
    this.renderFanList(modal);
  }

  private installObserver(modal: HTMLElement): void {
    if (this.observer !== null) return;
    const totalsBody = modal.querySelector<HTMLTableSectionElement>('#game-complete-totals tbody');
    const recap = modal.querySelector<HTMLElement>('#game-complete-recap');
    if (totalsBody === null && recap === null) return;
    this.observer = new MutationObserver((mutations) => {
      // Skip self-mutations: if we added the .ferro-* classes ourselves,
      // don't re-enter.  Simple gate: ignore mutations whose only added
      // nodes are our roll-counter spans.
      let externalChange = false;
      for (const m of mutations) {
        for (const n of Array.from(m.addedNodes)) {
          if (n instanceof HTMLElement) {
            if (!n.classList.contains('ferro-roll-counter')) {
              externalChange = true;
              break;
            }
          } else {
            externalChange = true;
            break;
          }
        }
        if (externalChange) break;
      }
      if (!externalChange) return;
      // Re-run shortly after the external paint settles.
      window.setTimeout(() => {
        if (totalsBody !== null) this.animateScoreCells(totalsBody);
        this.renderFanList(modal);
      }, 0);
    });
    if (totalsBody !== null) this.observer.observe(totalsBody, { childList: true, subtree: true });
    if (recap !== null) this.observer.observe(recap, { childList: true });
  }

  // ---------------------------------------------------------------------
  // Rolling score counters.
  // ---------------------------------------------------------------------

  private animateScoreCells(tbody: HTMLTableSectionElement): void {
    // The third <td> in each row is the delta (per game-ui.ts:renderGameComplete).
    const rows = Array.from(tbody.querySelectorAll('tr'));
    rows.forEach((tr, rowIdx) => {
      const tds = tr.querySelectorAll('td');
      if (tds.length < 3) return;
      const deltaTd = tds[2] as HTMLElement;
      // Already animated this paint?
      if (deltaTd.querySelector('.ferro-roll-counter') !== null) return;
      const raw = (deltaTd.textContent ?? '').trim();
      const match = raw.match(/^([+-]?)(\d+)/);
      if (match === null) return;
      const sign = match[1] === '-' ? -1 : 1;
      const target = sign * parseInt(match[2], 10);
      // Preserve the existing color the trunk set (game-ui.ts assigns
      // green/red via inline style.color).
      const span = document.createElement('span');
      span.className = 'ferro-roll-counter';
      span.dataset.target = String(target);
      span.textContent = target >= 0 ? '+0' : '0';
      deltaTd.textContent = '';
      deltaTd.appendChild(span);
      this.runRollAnimation(span, target, 1200 + rowIdx * 80);
    });
  }

  private runRollAnimation(span: HTMLElement, target: number, durationMs: number): void {
    const start = performance.now();
    const startVal = 0;
    const step = (now: number): void => {
      const elapsed = now - start;
      const t = Math.min(1, elapsed / durationMs);
      // ease-out cubic.
      const eased = 1 - Math.pow(1 - t, 3);
      const cur = Math.round(startVal + (target - startVal) * eased);
      span.textContent = cur > 0 ? `+${cur}` : String(cur);
      if (t < 1) {
        const h = requestAnimationFrame(step);
        this.rafHandles.add(h);
      } else {
        // Final snap to the precise target.
        span.textContent = target > 0 ? `+${target}` : String(target);
        span.classList.add('ferro-roll-counter-done');
      }
    };
    const h = requestAnimationFrame(step);
    this.rafHandles.add(h);
  }

  // ---------------------------------------------------------------------
  // Fan list reveal.
  //
  // Aggregates fans across the hand history.  When the runtime hasn't
  // populated `fans` on each HandResultEntry (Frost's Changsha/Scoring/
  // Fan.cs not yet merged), we surface the hard-coded Changsha basic
  // fans against the played hand types so the player still sees a
  // readable summary instead of a blank section.
  // ---------------------------------------------------------------------

  private renderFanList(modal: HTMLElement): void {
    const payload = this.lastPayload;
    if (payload === null) return;
    const totalsSection = modal.querySelector<HTMLElement>('#game-complete-totals')?.closest('.game-complete-section') as HTMLElement | null;
    if (totalsSection === null) return;

    let fansBlock = modal.querySelector<HTMLElement>('#ferro-win-fans');
    if (fansBlock === null) {
      fansBlock = document.createElement('div');
      fansBlock.id = 'ferro-win-fans';
      fansBlock.className = 'game-complete-section ferro-win-fans';
      // Insert AFTER the totals section, before the recap section.
      totalsSection.parentNode?.insertBefore(fansBlock, totalsSection.nextSibling);
    }

    const hands = payload.handHistory ?? payload.HandHistory ?? [];
    const aggregated = this.aggregateFans(hands);

    fansBlock.innerHTML = '';
    const title = document.createElement('h4');
    title.className = 'game-complete-section-title';
    title.innerHTML = '<span class="zh">番种</span> <span class="py">Fans Scored</span>';
    fansBlock.appendChild(title);

    const list = document.createElement('div');
    list.className = 'ferro-fan-list';
    if (aggregated.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'ferro-fan-empty';
      empty.textContent = 'No bonus fans this match (base wins only).';
      list.appendChild(empty);
    } else {
      aggregated.forEach((row, idx) => {
        const card = document.createElement('div');
        card.className = 'ferro-fan-card';
        card.style.animationDelay = `${100 + idx * 80}ms`;
        const zh = document.createElement('div');
        zh.className = 'ferro-fan-zh';
        zh.textContent = row.zh;
        const py = document.createElement('div');
        py.className = 'ferro-fan-py';
        py.textContent = row.py;
        const en = document.createElement('div');
        en.className = 'ferro-fan-en';
        en.textContent = row.en;
        const count = document.createElement('div');
        count.className = 'ferro-fan-count';
        count.textContent = row.count > 1 ? `×${row.count}` : '';
        const pts = document.createElement('div');
        pts.className = 'ferro-fan-points';
        pts.textContent = row.points !== undefined ? `${row.points} 番` : '';
        card.appendChild(zh);
        card.appendChild(py);
        card.appendChild(en);
        card.appendChild(count);
        card.appendChild(pts);
        list.appendChild(card);
      });
    }
    fansBlock.appendChild(list);
  }

  private aggregateFans(hands: HandResultEntry[]): Array<{ zh: string; py: string; en: string; count: number; points: number | undefined }> {
    const byKey = new Map<string, { zh: string; py: string; en: string; count: number; points: number | undefined }>();
    let realFansFound = false;

    for (const h of hands) {
      const raw = h.fans ?? h.Fans ?? [];
      if (raw.length === 0) continue;
      realFansFound = true;
      for (const f of raw) {
        const parsed = this.parseFan(f);
        if (parsed === null) continue;
        const key = parsed.zh + '|' + parsed.py;
        const prev = byKey.get(key);
        if (prev === undefined) {
          byKey.set(key, { zh: parsed.zh, py: parsed.py, en: parsed.en, count: 1, points: parsed.points });
        } else {
          prev.count += 1;
        }
      }
    }

    if (realFansFound) return Array.from(byKey.values());

    // Fallback: synthesise display rows from hand result types so the
    // section isn't empty when Frost's Fan.cs hasn't shipped yet.
    for (const h of hands) {
      if (h.type === 'Hu') {
        const key = 'PingHu';
        const meta = CHANGSHA_FANS[key];
        const prev = byKey.get(key);
        if (prev === undefined) {
          byKey.set(key, { zh: meta.zh, py: meta.py, en: meta.en, count: 1, points: meta.points });
        } else {
          prev.count += 1;
        }
      }
    }
    return Array.from(byKey.values());
  }

  private parseFan(f: string | FanEntry): { zh: string; py: string; en: string; points: number | undefined } | null {
    if (typeof f === 'string') {
      // Try canonical key lookup first.
      const meta = CHANGSHA_FANS[f];
      if (meta !== undefined) {
        return { zh: meta.zh, py: meta.py, en: meta.en, points: meta.points };
      }
      // Unknown id — display as-is.
      return { zh: f, py: f, en: f, points: undefined };
    }
    const name = f.name ?? f.Name ?? '';
    const meta = name !== '' ? CHANGSHA_FANS[name] : undefined;
    const zh = f.zh ?? f.Zh ?? meta?.zh ?? name;
    const py = f.pinyin ?? f.Pinyin ?? meta?.py ?? name;
    const en = meta?.en ?? '';
    const points = f.points ?? f.Points ?? meta?.points;
    if (zh === '' && py === '') return null;
    return { zh, py, en, points };
  }
}
