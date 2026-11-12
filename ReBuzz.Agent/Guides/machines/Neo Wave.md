## How it sounds

A wavetable synth: two oscillators read through tables of 64 frames that morph smoothly, from glassy and vocal to hard and digital. Warps bend how each cycle is read, so one table gives many timbres. It runs at 2× with a clean decimator: harmonics reach 18 kHz at every pitch below 9 kHz, and the alias floor is −92 dB or lower. Good for evolving pads, wavetable sweeps, digital leads and modern basses.

## Signal flow

- Oscillators A and B: `A table` and `A position` (0–100 % through the table), `A warp` and `A warp amt`, octave, semitone and fine tune, level (B the same). A sub oscillator (sine or square, 1 or 2 octaves down) and noise.
- One filter: Ladder 24, Ladder 12, SVF LP, BP, HP, Notch or Off, with drive, envelope amount (±6 octaves), key tracking and velocity.
- Amp, filter and mod envelopes; two LFOs; an 8-slot matrix whose dests include each oscillator's position and warp.

| Table | Position 0 % → 100 % |
|---|---|
| Basic | sine → triangle (33 %) → saw (67 %) → square |
| PWM | pulse width 50 % → 3 % |
| Harmonics | more and more saw harmonics |
| Formants | vowels A → E → I → O → U |
| Sync sweep | hard-synced saw, ratio 1 → 8 |
| FM sweep | FM 1:1, index 0 → 6 |
| Organ | drawbar registrations |
| Resonant | a saw through a resonant low-pass sweeping up |
| Digital | stepped saw, 4 → 256 steps |
| Grit | seeded spectra, morphing |
| User | your table (`machine_data_import`) |

| Warp | What it does at full amount |
|---|---|
| Bend+, Bend− | reads the cycle slowly at the start and fast at the end (or the reverse): brighter, vocal |
| Squeeze | plays the whole cycle in 4 % of the period and holds: pulse-width modulation for any wave |
| Mirror | plays half the cycle forward then back: symmetric, hollow |
| Asym | wobbles the read speed through the cycle: phase distortion |
| Window sync | reads the cycle up to 8 times per period: hard-sync sounds |
| PM from B | phase modulation of A by B (oscillator A only; set `B level` to 0 % to hear only A) |

## How to use it

- Movement comes from modulating the position: the mod envelope for one sweep per note, an LFO (free or synced) for repeating motion, velocity or the mod wheel for playing it. Matrix amounts are fractions of the whole table: +50 % covers half of it.
- Pads: two tables at different positions, `B fine` +5 to +8 ct, unison 2–4, slow amp and mod envelopes.
- Basses: `Voice mode` Mono, the sub at −1 octave for weight, a synced LFO on the position for rhythm, drive into the ladder for growl.
- Your own table: a wavetable WAV in the common 2048-samples-per-frame layout, or a single cycle of any length. `machine_data_import(machine="Wave", kind="wavetable", path="C:/Tables/mine.wav")`, then set `A table` (or B) to User.

Movement on the position from the mod envelope:

| Parameter | Value | Why |
|---|---|---|
| Mod attack | 2 s | the sweep's length |
| Mod sustain | 100% | stays at the end of the sweep |
| M3 source | Mod env | |
| M3 dest | A position | |
| M3 amount | +60% | across 60 % of the table |

## Gotchas

- Freshly added it plays a sine: `A table` Basic at position 0 %. Set `A position` 67% for a saw.
- `Sub octave` takes the text "-1" or "-2" (quoted), not a number.
- Tables are built in the background on first use; renders wait for them (up to 5 s).
- Matrix slots 1–4 come with sources set and dest Off, as on Neo Poly.
- A ladder filter with heavy drive on a full table adds a lot of level: watch the peaks.

## Preset map

| Preset | What | Tweak first |
|---|---|---|
| "Init — Basic" | a saw (Basic at 66 %) | everything |
| "Pad — Vowel Choir" | two Formants positions, slow LFOs, unison 3 | LFO rates |
| "Pad — Organ Drift" | two Organ registrations drifting | `A position` |
| "Pad — Glass Harmonics" | Harmonics with Asym warp, slow mod sweep | `Mod attack` |
| "Lead — Sync Scream" | Sync sweep driven by the mod envelope | `M1 amount` |
| "Lead — Digital Steps" | Digital table, Bend+ warp, 8th-note position LFO | `LFO1 sync` |
| "Bass — Wave Sub" | saw with a sub, short filter envelope | `Cutoff`, `Sub level` |
| "Bass — FM Growl" | FM sweep position on an 8th-note LFO | `LFO1 sync`, `Drive` |
| "Bass — Reso Pluck" | Resonant table moved by the filter envelope | `Flt decay` |
| "Pluck — PWM Keys" | two PWM positions, fast filter envelope | `Filter env` |
| "Pluck — Mirror Bell" | Harmonics with Mirror warp, FM sweep an octave up | `A warp amt` |
| "Keys — Squeeze EP" | velocity drives the Squeeze warp | `M1 amount` |
| "Texture — Grit Motion" | Grit tables, band-pass, two LFOs | `Cutoff`, LFO rates |
| "Texture — Window Sync Sweep" | Window sync warp swept by an LFO | `LFO1 rate` |
| "Texture — PM Bloom" | phase modulation from B rising with the mod envelope | `Mod attack` |
| "Arp — Stepped Saw" | Digital table, random position per note | `M1 amount` |
