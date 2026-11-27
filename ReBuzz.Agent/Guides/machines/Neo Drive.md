## How it sounds

A saturator with ten models, oversampled and anti-aliased (alias floor -100 dB for a 3.1 kHz sine at +30 dB drive, Transistor at 4x): from barely-there tape warmth to fuzz, hard clipping, rectifier grit and wavefolding. Auto gain holds the level steady while you change the drive, so you hear the colour, not the volume.

| Mode | Character | Use it for |
|---|---|---|
| Tape | soft, symmetric, with pre- and de-emphasis and a head bump; `Character` sets the emphasis | buses, bass warmth, glue |
| Tube | asymmetric: even harmonics, sags with level; `Character` sets the sag | keys, pads, vocals, gentle colour |
| Transistor | harder knee; `Character` sets the hardness | crunch, drums, bass grit, fuzz |
| Diode | asymmetric exponential; `Character` sets the asymmetry | overdrive with edge |
| Fold | a sine wavefolder: new harmonics as the drive rises; `Character` sets the folds | west-coast timbres on simple waves |
| Overdrive | soft asymmetric clip after a mid hump; `Character` moves the hump from 500 Hz to 2 kHz | guitar-like crunch, bass that speaks |
| Fuzz | hard asymmetric clip with a starved bias; `Character` adds gating and sputter as notes decay | fuzz bass and leads |
| Hard clip | a clean clipper; `Character` goes from a hard to a soft knee | loudness, drums, square-ish tones |
| Rectify | half-wave to full-wave rectifier; `Character` blends them: octave-up grit (full-wave has no fundamental) | octave effects, synthetic grit |
| Transformer | the lows saturate first, with a gentle memory; `Character` sets the low corner from 40 to 300 Hz | thick kick and bass, iron on pads |

Path: `Low cut` (before the shaper), `Drive`, the oversampled shaper (`Quality` 2x, 4x or 8x), a DC block, `Tone` (a tilt), `High cut`, `Output`, then `Mix` with the dry signal, delayed to match. Latency is 62 samples at every quality.

## Use it for

- Colour and glue on buses, warmth on bass and keys, crunch on drums, fuzz and wavefolding as sound design, a parallel saturation layer that keeps the dry punch.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Tape | soft and even; Transistor, Diode and Overdrive for edge, Fuzz and Hard clip for more, Fold and Rectify for simple waves, Transformer for lows |
| Drive | 4 dB | 0-36 dB into the shaper: 3-8 dB for colour, 20 and up for crunch |
| Character | 30% | the model's own trait (emphasis, sag, hardness, asymmetry, folds, hump, gating, knee, rectifier blend, low corner) |
| Low cut | 100 Hz | keeps a bass's lows clean (10-1000 Hz, before the shaper) |
| High cut | 8 kHz | tames fizz from heavy drive (with Tone -10 to -30 %) |
| Auto gain | On | the level stays put as Drive changes |
| Mix | 40% | 25-50 % for parallel saturation |
| Quality | 4× | 8x for high drive on bright material; 2x for light colour on many tracks |

`Amount` (default 100 %) scales drive and bias: 0 % is the bypassed dry, 200 % exaggerates. `Mix` stays its own dry/wet.

## Starting points

- "Tape — Warm Bus" (6 dB), "Tape — Hot Reels" (15 dB), "Tape — Cassette" (lo-fi, 60 Hz to 9 kHz).
- "Tube — Glow" (keys and pads), "Tube — Preamp Push", "Tube — Parallel Thickener" (24 dB at 35 % mix).
- "Transistor — Crunch", "Transistor — Fuzz Wall", "Transistor — Drum Smash"; "Diode — Overdrive", "Diode — Edge".
- "Fold — West Coast", "Fold — Harmonic Bloom"; "Utility — Subtle Glue" (almost transparent), "Utility — Bass Grit".
- "Overdrive — Warm Crunch" (bass, guitar), "Fuzz — Sputter Fuzz" (bass), "Hard clip — Loud Clip" (drums, Auto gain Off), "Rectify — Octave Grit" (pads), "Transformer — Heavy Iron" (kick, bass).
- Master colour: Mode Tape, Drive 4 dB, Character 30 %, Mix 40 %, Auto gain On.

## Pitfalls

- With Auto gain Off more drive is louder: watch the peaks.
- Parallel routes (dry on one connection, Neo Drive on another) are 62 samples apart and comb: use `Mix` instead.
- Fold on a full mix or chords gets harsh fast; Rectify drops the fundamental at full-wave, so keep `Mix` under 100 % on bass.
- A Mode change (an automation lane too) crossfades over about 10 ms through the dry signal: no click, but steps faster than that blur.
- Tube's THD at 0 dB drive is higher (about 1.4 %) than Tape's: its colour is the point.
- `Output` is a clean gain stage after everything, useful in front of a limiter. No other level readings: use `machine_readings` for `rms_db` and `peak_db`.

## Pairs well with

- Neo EQ before it (cut lows) and after it (tone); Neo Comp and Neo Limiter on a bus (`guide(topic="mastering-chain")`); Neo Filter after a fuzz.

## Checklist

- [ ] `machine_readings(machine="Drive")`: `peak_db` under 0 dBFS with the drive you chose.
- [ ] `analyze(source="Bass", metrics=["spectrum"])`: new harmonics appear, and the lows are not muddier than before.
- [ ] `machine_set(machine="Drive", bypass=true)`, then `compare` the two renders at matched loudness.
