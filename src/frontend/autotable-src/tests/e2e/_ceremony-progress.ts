// Shared manual-deal ceremony PROGRESS + STALL-GUARD helpers (Hudson, 2026-08-12).
//
// Why this exists: the three mobile manual-pickup gates (D2 orchestration, NEW-2
// human-driven pickup, G17 S1/S2 endpoint signal) previously polled the authoritative
// pickup/turn state on a FIXED wall-clock budget (22s / 90s). Under a CPU-saturated CI
// runner the server-authoritative ceremony genuinely advances, just slower — the pickup
// cursor still rotates seat→seat and the hand still climbs — but the fixed window expired
// before the awaited state (a designation targeting our seat / 13-14 tiles) landed, so a
// perfectly healthy deal flaked RED (CI run 31576171218). These helpers replace the fixed
// window with PROGRESS-AWARE polling: we keep going as long as an AUTHORITATIVE progress
// fingerprint keeps changing, and we surface a GENUINE stall (a real RollingDice park /
// a handoff that never reaches our seat) as a hard failure — never hide it.
//
// This file adds NEW helpers only; it does not touch _playability.ts (kept byte-frozen).

import type { Page } from '@playwright/test';

// A single, authoritative progress fingerprint for the local seat's manual deal:
//   turn.phase | turn.activeSeat | turn.awaitingDiscard | pickup.phase | pickup.seatIndex
//   | pickup.count | localSeatHandCount
// Every one of these is server-pushed (Bishop's translator: turn/pickup collections;
// `things` for the hand). ANY change ⇒ the ceremony advanced (progress). No change for a
// bounded window while not-done ⇒ a genuine stall. The hand count excludes `hand.extra@`
// so it matches the gates' own non-extra hand tallies.
export async function readCeremonyKey(page: Page): Promise<string> {
  return page.evaluate(() => {
    /* eslint-disable @typescript-eslint/no-explicit-any */
    const g = (window as any).game;
    const cli = g?.client;
    const w = g?.world;
    const tn = cli?.turn?.get ? cli.turn.get('current') ?? null : null;
    const pu = cli?.pickup?.get ? cli.pickup.get('current') ?? cli.pickup.get(0) ?? null : null;
    const seat = typeof w?.seat === 'number' ? w.seat
      : typeof cli?.seat === 'number' ? cli.seat : null;
    let hand = 0;
    if (w?.things && seat !== null) {
      for (const t of w.things.values()) {
        const s = String(t?.slot?.name ?? '');
        if (/^hand\.\d+@\d+$/.test(s) && s.endsWith('@' + seat)) hand++;
      }
    }
    return [
      tn?.phase ?? '-', tn?.activeSeat ?? '-', tn?.awaitingDiscard ?? '-',
      pu?.phase ?? '-', pu?.seatIndex ?? '-', pu?.count ?? '-',
      hand,
    ].join('|');
    /* eslint-enable @typescript-eslint/no-explicit-any */
  });
}

export interface StallGuardOptions {
  // Max time with NO authoritative progress-fingerprint change (while not done) before we
  // declare a genuine stall. Must comfortably exceed the worst measured inter-progress gap
  // under saturation so slow-but-advancing deals are never misread as stalled.
  stallMs: number;
  // Hard safety bound for a pathological progressing-but-never-done run. Sized under the
  // caller's test.setTimeout minus its connect/seat/deal setup.
  capMs: number;
  // Poll CADENCE between ticks (NOT a settle "fix"): forward progress is gated on an
  // authoritative state change, never on this timer. Pass 0 when the step itself blocks
  // (e.g. an anti-auto-advance hand-hold sample provides the cadence).
  pollMs: number;
}

export interface StallGuardOutcome {
  done: boolean;      // the awaited authoritative outcome was reached
  stalled: boolean;   // no authoritative progress for stallMs while not done (GENUINE stall)
  capped: boolean;    // capMs elapsed with intermittent progress but never done
  elapsedMs: number;
  polls: number;
  keyChanges: number; // number of authoritative progress-fingerprint transitions observed
  maxIdleMs: number;  // longest observed no-progress gap (measured rationale for stallMs)
  lastKey: string;
}

// Progress-aware, stall-guarded poll loop. Each tick awaits `step()`, which performs any
// REAL action (a genuine press / an observation read) and returns:
//   • done — the awaited authoritative outcome has been reached (loop returns success), and
//   • key  — an authoritative progress fingerprint (use readCeremonyKey). A change resets
//            the stall timer.
// The loop:
//   • returns { done: true }         the moment step reports done,
//   • returns { stalled: true }      when `key` is unchanged for stallMs and NOT done — a
//                                    genuine stall the CALLER must surface as a failure
//                                    (this helper never asserts, sleeps-to-pass, or hides),
//   • returns { capped: true }       when capMs elapses with progress but no done.
// It NEVER swallows a stall: `stalled`/`capped` both leave `done: false`, so the caller's
// own authoritative assertion (presses ≥ 1 / hand === 13 / seen.length > 0 / …) fails with
// its RollingDice-stall diagnostic. `maxIdleMs` is reported so a run's real worst-case
// inter-progress gap is measurable (the rationale for `stallMs`).
export async function pollWithStallGuard(
  page: Page,
  step: () => Promise<{ done: boolean; key: string }>,
  opts: StallGuardOptions,
): Promise<StallGuardOutcome> {
  const start = Date.now();
  const INIT = '\u0000init';
  let lastKey = INIT;
  let lastChange = start;
  let polls = 0;
  let keyChanges = 0;
  let maxIdleMs = 0;
  for (;;) {
    const r = await step();
    polls++;
    const now = Date.now();
    if (r.key !== lastKey) {
      if (lastKey !== INIT) keyChanges++;
      lastKey = r.key;
      lastChange = now;
    }
    const idle = now - lastChange;
    if (idle > maxIdleMs) maxIdleMs = idle;
    if (r.done) {
      return { done: true, stalled: false, capped: false, elapsedMs: now - start, polls, keyChanges, maxIdleMs, lastKey };
    }
    if (idle >= opts.stallMs) {
      return { done: false, stalled: true, capped: false, elapsedMs: now - start, polls, keyChanges, maxIdleMs, lastKey };
    }
    if (now - start >= opts.capMs) {
      return { done: false, stalled: false, capped: true, elapsedMs: now - start, polls, keyChanges, maxIdleMs, lastKey };
    }
    if (opts.pollMs > 0) await page.waitForTimeout(opts.pollMs);
  }
}
