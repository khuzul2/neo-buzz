## How it sounds

A clipper: `Drive` pushes the signal into the `Ceiling` and the curve flattens what goes over. It has no attack and no release, so it shaves the first milliseconds of transients that a limiter after it would otherwise pump on. Pushed hard it adds harmonics: density and edge.

- `Shape`: Hard (flat at the ceiling), Soft (a cubic that bends at once), Tanh (smooth, never quite reaches the ceiling) or Knee (straight up to `Knee` dB under the ceiling, then a smooth bend; the default, 3 dB).
- `Oversampling` 4x with antiderivative anti-aliasing keeps the aliasing of a clipped sine under -90 dB (8x: -100 dB; 2x: -75 dB; 1x: about -40 dB, the gritty clipper).
- No sample leaves above the ceiling at Mix 100 %. The true peak between samples can still be about 0.7 dB over at 4x: put Neo Limiter after it on a master.
- Latency is the same at every setting (about 7.6 ms at 48 kHz), reported and compensated, so changing a setting never moves the audio.
- `Bands` (zero-latency Linkwitz-Riley split at `Crossover 1` and `Crossover 2`) lets the lows clip harder than the highs, or the reverse, through `Low drive`, `Mid drive` and `High drive`.

## Use it for

- Taking 1 to 3 dB off the loudest transients before a limiter, so the limiter works less.
- Density and bite on drums and buses (Hard for snap, Soft or Tanh for weight).
- A deliberate lo-fi crunch at 1x.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Drive | 2 dB | raise until `clip_db` in `machine_readings` reads 1-3 dB on the loudest hits |
| Ceiling | -0.3 dB | just under full scale; Neo Limiter after it sets the true peak |
| Shape | Knee | transparent until the bend |
| Knee | 3 dB | where the bend starts under the ceiling (0 is a hard cut) |
| Oversampling | 4× | clean top; 1x aliases |
| Bands | 1 band | 2 or 3 split the clipping by frequency; set before playing |
| Mix | 100% | below it the dry signal can pass the ceiling |
| Output | 0 dB | the editor's Match sets it from `gain_match_db` |

## Starting points

- "Mastering — Transparent (Before a Limiter)": +2 dB into -0.3 dB, 3 dB knee, 4x. "Mastering — Loud (Knee 6 dB)": +4 dB, 8x.
- "Mastering — Three Bands": the lows 2 dB less, the highs 1 dB more. "Mastering — Clip the Mid (M/S)": +3 dB on mid and side.
- "Drums — Hard Snap" (Hard, +6 dB) for bite, "Drums — Soft Weight" (Soft) for weight, "Drums — Kick Only (Low Band)" for the kick alone.
- "Bus — Tanh Glue": Tanh, +3 dB, Mix 70 %. "Mixing — Catch the Peaks": no drive, a -6 dB ceiling: overs only.
- "Character — 1× Grit": Hard at 1×, +12 dB, Mix 40 %.

Then lower the limiter's `Input gain` by about what the clipper took, render, and compare loudness and PLR with `analyze`.

## Pitfalls

- Below Mix 100 % the dry signal can pass the ceiling.
- The clipper does not promise a true peak; Neo Limiter after it does.
- Clicks or a buzz in `Delta` (the difference, what you would lose) mean too much drive. If the kick distorts, use three bands with `Low drive` -2 dB, or less drive.
- Stereo M/S clips mid and side, then left and right again at the ceiling; on a mono signal it equals L/R.
- Changing `Bands` jumps (the crossovers restart): set it before playing.
- For a fair comparison, after 3 s of sound let Match set `Output` from `gain_match_db`, or send `param_set(machine="M Clip", changes=[{"param": "Output", "value": "-2.4 dB"}])` with the negative of the reading.

## Pairs well with

- Neo Limiter straight after it; Neo Drive before it for tone; Neo Analyzer last to read the result (`guide(topic="mastering-chain")`).

## Checklist

- [ ] `machine_readings(machine="M Clip")`: `clip_db` about 1-3 dB on the loudest hits, `clipped_percent` low.
- [ ] `param_set(machine="M Clip", changes=[{"param": "Delta", "value": "On"}])`, listen once for clicks or a buzz, then switch it Off.
- [ ] `analyze(source="master", metrics=["peaks", "loudness"])`: peaks under the ceiling, loudness up as intended.
- [ ] `machine_readings` after matching: `gain_match_db` near 0.
