import type { Client } from './client';
import { SoundInfo, SoundType } from './types';
import { Sound } from './sound';

/** Keep the existing sound wire, using the source's synthesized audio instead of excluded recordings. */
export class SoundPlayer {
  muted = false;
  constructor(private readonly client: Client) {
    client.sound.on('update', this.onUpdate.bind(this));
  }
  play(type: SoundType, side: number | null): void {
    this.doPlay(type);
    this.client.sound.set(0, { type, side, seat: this.client.seat! });
  }
  private onUpdate(entries: Array<[number, SoundInfo | null]>): void {
    for (const [, sound] of entries) if (sound !== null && sound.seat !== this.client.seat) this.doPlay(sound.type);
  }
  private doPlay(type: SoundType): void {
    if (this.muted) return;
    Sound.unlock();
    Sound.play(type === SoundType.DISCARD ? 'discard' : 'draw');
  }
}
