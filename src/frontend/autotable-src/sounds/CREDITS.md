# Sound assets — credits & licensing

## Phase J Wave 3 — Synth-only manifest

The autotable client ships with **zero static audio assets** in this
directory.  Every game-event SFX is synthesised at play time by
`src/frontend/autotable-src/src/sound.ts` using the Web Audio API.

### Why synth-only?

Per the Phase J Wave 3 directive Hicks evaluated CC0 asset sourcing
from freesound.org / opengameart.org first, but adopted the synth
fallback for the following reasons:

1. **No content-licensing risk.**  Mathematical waveforms are CC0 by
   construction — there's no asset to mis-attribute, no per-file
   credit to chase, and no risk of accidentally importing a
   non-CC0-compatible MP3 from a freesound bundle.
2. **Zero asset footprint.**  Each click / chime / fanfare adds
   ~0 KB to the bundle; only the synth code (a small chunk of
   `sound.ts`) ships.  Static assets would add 50-200 KB and would
   force a Parcel build-time copy + a Dockerfile change (see
   `infra/docker/Dockerfile`).
3. **No autoplay-policy issues with preload.**  AudioContext is
   created lazily on the first user gesture, so the browser
   autoplay restrictions are honoured without a `<audio preload>`
   tag farm in `index.html`.

### Sound event → synth recipe

| Event           | Recipe                                                    | Duration |
|---              |---                                                        |---       |
| `draw`          | High-band noise burst + sine sub, 240→600 Hz BPF sweep    | ~150 ms  |
| `discard`       | Lower-band noise burst + sine sub + 80 ms delay echo      | ~200 ms  |
| `claim`         | Two stacked sine partials (A5 + C#6) major-third stagger  | ~400 ms  |
| `win`           | C-major arpeggio (C5-E5-G5-C6) triangle voice + sine sub  | ~1.0 s   |
| `washout`       | Sawtooth glissando A4→A3 through a low-pass filter        | ~600 ms  |
| `gameComplete`  | Rolled C-major chord (C4-E4-G4-C5) on triangle + sine sub | ~1.5 s   |

Volume is held to ~0.4-0.5 RMS via per-voice gain envelopes so the
overall mix never clips.  A master gain node sits at 0.6 to leave a
comfortable headroom margin.

### Re-introducing CC0 asset files (future work)

If a designer wants warmer / sample-based effects later:

1. Place WAV/OGG/MP3 files in this directory (one per event).
2. Add a `<audio preload id="sound-{event}" src="./sounds/{file}">`
   tag to `index.html`.
3. Switch `sound.ts` to load via `<audio>` elements + use the existing
   per-channel mixer pattern from the upstream `sound-player.ts`.
4. Update Apone's `infra/docker/Dockerfile` if the parcel build doesn't
   copy raw audio files automatically.
5. Document each file's source + licence in this table below.

### Future-asset credits table (placeholder)

| File | Source | Licence | Attribution |
|---   |---     |---      |---          |
| _none yet_ | — | — | — |

### Licence

The synth code in `sound.ts` is © 2026 the autotable contributors,
released under the same terms as the rest of the project (see
`/COPYING`).  No third-party CC0 assets are bundled at this revision.
