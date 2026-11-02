## How it sounds

A saturator with five models, oversampled and anti-aliased (alias floor −100 dB for a 3.1 kHz sine at +30 dB drive, Transistor at 4×): from barely-there tape warmth to fuzz and west-coast wavefolding. Auto gain holds the level steady while you change the drive, so you hear the colour, not the volume.

| Mode | Character | Use it for |
|---|---|---|
| Tape | soft, symmetric, with pre- and de-emphasis and a head bump; `Character` sets the emphasis | buses, bass warmth, glue |
| Tube | asymmetric: even harmonics (the 2nd above the 3rd), sags with level; `Character` sets the sag | keys, pads, vocals, gentle colour |
| Transistor | harder knee; `Character` sets the hardness | crunch, drums, bass grit, fuzz |
| Diode | asymmetric exponential; `Character` sets the asymmetry | overdrive with edge |
| Fold | a sine wavefolder: new harmonics as the drive rises; `Character` sets the folds | west-coast timbres on simple waves |

At 0 dB drive and −12 dBFS the distortion is small (Tape 0.34 %, Transistor 0.37 %, Tube 1.44 % THD: Tube's colour is the point).

## Signal flow

`Low cut` (high-pass before the shaper) → `Drive` → oversampled shaper (`Quality` 2×, 4× or 8×) → DC block → `Tone` (a tilt: darker or brighter) → `High cut` (low-pass after) → `Output` → `Mix` with the dry signal, which is delayed to match. Latency is 62 samples at every quality.

## How to use it

- Insert after the sound for colour; `Mix` 25–50 % for parallel saturation that keeps the dry punch.
- Keep a bass's lows clean: `Low cut` 100–150 Hz with `Mix` 30–40 %; the dry path carries the lows.
- Tame fizz from heavy drive with `High cut` 5–8 kHz and `Tone` −10 to −30 %.
- `Quality` 8× for high drive on bright material; 2× for light colour on many tracks.
- `Output` is a clean gain stage (−24 … +24 dB) after everything: useful in front of a limiter.

Warm, gentle bus colour:

| Parameter | Value | Why |
|---|---|---|
| Mode | Tape | |
| Drive | 4 dB | |
| Character | 30% | |
| Auto gain | On | level unchanged |
| Mix | 40% | |

## Gotchas

- With `Auto gain` Off, more drive is louder: watch the peaks.
- Parallel routes (dry on one connection, Neo Drive on another) are 62 samples apart: comb filtering. Use `Mix` instead.
- Fold on a full mix or chords gets harsh fast: it is for simple waves.

## Preset map

| Preset | What |
|---|---|
| "Tape — Warm Bus" | 6 dB of tape, slightly dark: buses |
| "Tape — Hot Reels" | 15 dB, more emphasis |
| "Tape — Cassette" | heavy, dark, band-limited (60 Hz – 9 kHz): lo-fi |
| "Tube — Glow" | 8 dB, slight bias: keys and pads |
| "Tube — Preamp Push" | 16 dB, more bias and brightness |
| "Tube — Parallel Thickener" | 24 dB at 35 % mix |
| "Transistor — Crunch" | 18 dB with an 80 Hz low cut |
| "Transistor — Fuzz Wall" | 34 dB, band-limited fuzz |
| "Transistor — Drum Smash" | 20 dB at 50 % mix: drums |
| "Diode — Overdrive" | 20 dB, 100 Hz – 7 kHz |
| "Diode — Edge" | 12 dB, brighter |
| "Fold — West Coast" | folding on simple waves |
| "Fold — Harmonic Bloom" | gentler folding at 60 % mix |
| "Utility — Subtle Glue" | 3 dB of tape: almost transparent |
| "Utility — Bass Grit" | transistor at 40 % mix for bass |
