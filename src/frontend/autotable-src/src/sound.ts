// Phase J Wave 3 — Synth sound manager.
//
// A tiny game-event SFX layer that uses the Web Audio API to *synthesise*
// every effect on the fly.  Two reasons for synth-over-static-assets:
//
//   1. Zero net asset weight.  Each sound is built from a 2-3 oscillator +
//      gain-envelope graph that allocates ~1 KiB of state at play time.
//      Static MP3/OGG would add tens of KiB *and* require a separate
//      Dockerfile COPY for the `sounds/` directory.
//   2. Source-available.  Synth math is CC0 by construction — no
//      attribution table to maintain and no risk of accidentally shipping
//      a non-CC0 asset.  See `sounds/CREDITS.md` for the licence note.
//
// Browser autoplay policy: modern browsers block any AudioContext until the
// first user gesture.  Call `Sound.unlock()` from any click handler on
// boot — it lazy-instantiates the AudioContext on the first call and
// resumes it on every subsequent call (resume is idempotent and cheap).
//
// All event names are exported as the `SoundEvent` union so callers get
// compile-time validation of every `Sound.play(...)` site.

export type SoundEvent =
  | 'draw'
  | 'discard'
  | 'claim'
  | 'win'
  | 'washout'
  | 'gameComplete';

type AudioContextCtor = typeof AudioContext;

// Some legacy browsers expose only `webkitAudioContext`.  We don't ship
// those any more but a defensive cast keeps TS happy without polluting
// the global lib.dom.d.ts.
function getAudioContextCtor(): AudioContextCtor | null {
  const w = window as unknown as {
    AudioContext?: AudioContextCtor;
    webkitAudioContext?: AudioContextCtor;
  };
  return w.AudioContext ?? w.webkitAudioContext ?? null;
}

class SoundManager {
  private ctx: AudioContext | null = null;
  private master: GainNode | null = null;
  private muted: boolean = false;
  private unlockAttempted: boolean = false;

  // Initialise the AudioContext + master gain.  Called lazily on the
  // first user gesture (Sound.unlock() from a click handler).  Safe to
  // call multiple times — every subsequent call is a no-op aside from
  // resuming the context if the browser auto-suspended it.
  unlock(): void {
    if (this.ctx !== null) {
      void this.ctx.resume().catch(() => { /* ignore */ });
      return;
    }
    if (this.unlockAttempted) return;
    this.unlockAttempted = true;
    const Ctor = getAudioContextCtor();
    if (!Ctor) return;
    try {
      const ctx = new Ctor();
      const master = ctx.createGain();
      master.gain.value = 0.6;
      master.connect(ctx.destination);
      this.ctx = ctx;
      this.master = master;
      void ctx.resume().catch(() => { /* ignore */ });
    } catch {
      // AudioContext construction can fail in private/restricted modes.
      // Stay silent — Sound.play becomes a no-op.
    }
  }

  setMuted(muted: boolean): void {
    this.muted = muted;
  }

  isMuted(): boolean {
    return this.muted;
  }

  play(name: SoundEvent): void {
    if (this.muted) return;
    if (this.ctx === null || this.master === null) return;
    const ctx = this.ctx;
    const master = this.master;
    const t0 = ctx.currentTime;
    switch (name) {
      case 'draw':         this.playClack(ctx, master, t0, 0.15, 240, 600); break;
      case 'discard':      this.playClack(ctx, master, t0, 0.20, 180, 480, true); break;
      case 'claim':        this.playChime(ctx, master, t0); break;
      case 'win':          this.playFanfare(ctx, master, t0); break;
      case 'washout':      this.playWashout(ctx, master, t0); break;
      case 'gameComplete': this.playGameComplete(ctx, master, t0); break;
    }
  }

  // ── Synth primitives ─────────────────────────────────────────────────

  // A wood-clack: tight noise burst + a low-frequency body oscillator
  // shaped by an exponential decay envelope.  `noiseStart`/`noiseEnd`
  // bracket the bandpass-noise sweep that gives the click its bite;
  // `echo` adds a single delayed repeat at -8 dB for the discard variant.
  private playClack(
    ctx: AudioContext,
    out: GainNode,
    t0: number,
    durationSec: number,
    bandLow: number,
    bandHigh: number,
    echo: boolean = false,
  ): void {
    const buffer = ctx.createBuffer(1, Math.ceil(ctx.sampleRate * durationSec), ctx.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < data.length; i++) {
      data[i] = (Math.random() * 2 - 1);
    }
    const noise = ctx.createBufferSource();
    noise.buffer = buffer;

    const bp = ctx.createBiquadFilter();
    bp.type = 'bandpass';
    bp.Q.value = 1.2;
    bp.frequency.setValueAtTime(bandHigh, t0);
    bp.frequency.exponentialRampToValueAtTime(bandLow, t0 + durationSec);

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, t0);
    env.gain.exponentialRampToValueAtTime(0.8, t0 + 0.005);
    env.gain.exponentialRampToValueAtTime(0.0001, t0 + durationSec);

    noise.connect(bp).connect(env).connect(out);
    noise.start(t0);
    noise.stop(t0 + durationSec + 0.01);

    // Sub-body — a low sine that gives the clack its weight without
    // dominating the perceived attack.
    const body = ctx.createOscillator();
    body.type = 'sine';
    body.frequency.setValueAtTime(90, t0);
    body.frequency.exponentialRampToValueAtTime(50, t0 + durationSec);
    const bodyEnv = ctx.createGain();
    bodyEnv.gain.setValueAtTime(0.0001, t0);
    bodyEnv.gain.exponentialRampToValueAtTime(0.35, t0 + 0.01);
    bodyEnv.gain.exponentialRampToValueAtTime(0.0001, t0 + durationSec);
    body.connect(bodyEnv).connect(out);
    body.start(t0);
    body.stop(t0 + durationSec + 0.01);

    if (echo) {
      const delay = ctx.createDelay(0.5);
      delay.delayTime.value = 0.08;
      const echoGain = ctx.createGain();
      echoGain.gain.value = 0.35;
      env.connect(delay).connect(echoGain).connect(out);
    }
  }

  // Chime: two stacked sine partials with a slow decay, voiced at a
  // major-second interval for a "ding-dong"-shaped claim cue.
  private playChime(ctx: AudioContext, out: GainNode, t0: number): void {
    const fundamentals = [880, 1108]; // A5 + C#6 (major third)
    const duration = 0.4;
    for (let i = 0; i < fundamentals.length; i++) {
      const osc = ctx.createOscillator();
      osc.type = 'sine';
      osc.frequency.value = fundamentals[i];
      const env = ctx.createGain();
      const offset = i * 0.05;
      env.gain.setValueAtTime(0.0001, t0 + offset);
      env.gain.exponentialRampToValueAtTime(0.5, t0 + offset + 0.01);
      env.gain.exponentialRampToValueAtTime(0.0001, t0 + offset + duration);
      osc.connect(env).connect(out);
      osc.start(t0 + offset);
      osc.stop(t0 + offset + duration + 0.02);
    }
  }

  // Win fanfare: a quick triumphal 3-note arpeggio (C5-E5-G5-C6) using a
  // bright triangle voice plus a sine sub.  ~1 second long.
  private playFanfare(ctx: AudioContext, out: GainNode, t0: number): void {
    const notes = [523.25, 659.25, 783.99, 1046.50]; // C5 E5 G5 C6
    const gap = 0.08;
    const sustain = 0.45;
    notes.forEach((freq, i) => {
      const start = t0 + i * gap;
      const tri = ctx.createOscillator();
      tri.type = 'triangle';
      tri.frequency.value = freq;
      const sine = ctx.createOscillator();
      sine.type = 'sine';
      sine.frequency.value = freq / 2;
      const env = ctx.createGain();
      env.gain.setValueAtTime(0.0001, start);
      env.gain.exponentialRampToValueAtTime(0.45, start + 0.015);
      env.gain.exponentialRampToValueAtTime(0.0001, start + sustain);
      tri.connect(env).connect(out);
      sine.connect(env);
      tri.start(start);
      sine.start(start);
      tri.stop(start + sustain + 0.02);
      sine.stop(start + sustain + 0.02);
    });
  }

  // Washout: a sad descending tone — a sawtooth glissando from A4 down
  // to A3 over ~600 ms with a slow decay.
  private playWashout(ctx: AudioContext, out: GainNode, t0: number): void {
    const duration = 0.6;
    const osc = ctx.createOscillator();
    osc.type = 'sawtooth';
    osc.frequency.setValueAtTime(440, t0);
    osc.frequency.exponentialRampToValueAtTime(220, t0 + duration);

    const lp = ctx.createBiquadFilter();
    lp.type = 'lowpass';
    lp.frequency.setValueAtTime(1200, t0);
    lp.frequency.exponentialRampToValueAtTime(400, t0 + duration);
    lp.Q.value = 0.5;

    const env = ctx.createGain();
    env.gain.setValueAtTime(0.0001, t0);
    env.gain.exponentialRampToValueAtTime(0.4, t0 + 0.05);
    env.gain.exponentialRampToValueAtTime(0.0001, t0 + duration);

    osc.connect(lp).connect(env).connect(out);
    osc.start(t0);
    osc.stop(t0 + duration + 0.02);
  }

  // Game-complete closing chord: a slow C-major (C-E-G-C) tutti held
  // ~1.5 s on triangles + sine sub, with a small velocity stagger so
  // the chord rolls in instead of hitting flat.
  private playGameComplete(ctx: AudioContext, out: GainNode, t0: number): void {
    const chord = [261.63, 329.63, 392.00, 523.25]; // C4 E4 G4 C5
    const duration = 1.5;
    chord.forEach((freq, i) => {
      const start = t0 + i * 0.04;
      const tri = ctx.createOscillator();
      tri.type = 'triangle';
      tri.frequency.value = freq;
      const sine = ctx.createOscillator();
      sine.type = 'sine';
      sine.frequency.value = freq / 2;
      const env = ctx.createGain();
      env.gain.setValueAtTime(0.0001, start);
      env.gain.exponentialRampToValueAtTime(0.32, start + 0.06);
      env.gain.setValueAtTime(0.32, start + duration * 0.5);
      env.gain.exponentialRampToValueAtTime(0.0001, start + duration);
      tri.connect(env).connect(out);
      sine.connect(env);
      tri.start(start);
      sine.start(start);
      tri.stop(start + duration + 0.02);
      sine.stop(start + duration + 0.02);
    });
  }
}

// Module-level singleton so every caller shares the same AudioContext
// (browsers count contexts against a small per-page budget) and the
// same mute state.
export const Sound = new SoundManager();
