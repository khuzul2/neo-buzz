## How it sounds

A clipper: `Drive` pushes the signal into the `Ceiling`, and the curve flattens what goes over. It is faster than any limiter (no attack, no release), so it shaves the first milliseconds of transients the limiter after it would otherwise pump on. Pushed hard it adds harmonics: density and edge.

- `Shape`: Hard (flat at the ceiling), Soft (a cubic that starts bending at once), Tanh (smooth, never quite reaches the ceiling) or Knee (straight up to `Knee` dB under the ceiling, then a smooth bend; the default, 3 dB).
- `Oversampling` 4× (the default) with antiderivative anti-aliasing keeps the aliasing of a sine clipped 12 dB into the knee under −90 dB (8×: −100 dB; 2×: −75 dB; 1×: −40 dB, the classic gritty clipper).
- **No sample leaves above the ceiling** at Mix 100 %. A clipped signal rings past it after the oversampling filter, so a 5 ms lookahead gain takes those samples under it smoothly; the true peak (between samples) can still be up to about 0.7 dB over at 4×: put Neo Limiter after it on a master.
- Under the knee it is straight: THD+N under −130 dB. 4× costs 0.5 dB at 20 kHz (8×: 0.1 dB; 2×: 2 dB).

## Signal flow

`Drive` → (`Stereo` M/S: mid and side) → (`Bands`: two or three bands, each with its own drive) → the curve at `Oversampling` → the bands summed (mid/side decoded, and with bands or M/S, the curve again on left and right) → the ceiling guard → `Mix` with the dry signal → `Delta` → `Output`.

- The latency is the same at every setting (about 7.6 ms at 48 kHz: 124 samples of oversampling and the 5 ms guard), reported to NeoBuzz Live and compensated, so changing a setting never moves the audio.
- `Bands` uses zero-latency Linkwitz–Riley crossovers (`Crossover 1`, `Crossover 2`); `Low drive`, `Mid drive` and `High drive` set each band against `Drive`, so the lows can clip harder than the highs, or the other way round.
- `Delta` plays only what the clipper changes (the output minus the input): what you would lose. `Output` comes after it.

## How to use it

Before Neo Limiter on a master, clipping 1–3 dB of the loudest transients so the limiter works less:

| Parameter | Value | Why |
|---|---|---|
| Ceiling | -0.3 dB | just under full scale; Neo Limiter after it sets the true peak |
| Shape | Knee | |
| Knee | 3 dB | |
| Oversampling | 4× | |
| Drive | 2 dB | until `clip_db` in `machine_readings` reads 1–3 dB on the loudest hits |

Then lower the limiter's `Input gain` by about what the clipper took, render and compare PLR and loudness (`analyze`). If the kick distorts, use three bands with `Low drive` −2 dB, or less drive.

- **Drums:** "Drums — Hard Snap" (Hard, +6 dB) for bite, "Drums — Soft Weight" (Soft) for weight. Drive until it sounds denser, then back off 1 dB.
- **Hear what it takes:** `Delta` On. Clicks or a buzz in the delta mean too much drive.
- **Loudness match:** after 3 s of sound, the editor's "Match" sets `Output` from the gain-match reading (`gain_match_db`, short-term loudness out − in), so comparing it bypassed and on is fair. One undo step. An agent does the same with `param_set(machine="M Clip", changes=[{"param": "Output", "value": "-2.4 dB"}])`, the value the negative of `gain_match_db`.

## Gotchas

- Below Mix 100 % the dry signal can pass the ceiling.
- 1× aliases (the alias floor about −40 dB on a hard-clipped 3 kHz sine): it's a sound, not a mastering setting.
- The clipper doesn't promise true peak. Neo Limiter after it does.
- Stereo M/S clips mid and side, then left and right again at the ceiling: on a mono signal it is the same as L/R.
- Changing `Bands` jumps (the crossovers start again): set it before playing.

## Preset map

| Preset | What |
|---|---|
| "Mastering — Transparent (Before a Limiter)" | +2 dB into −0.3 dB, 3 dB knee, 4× |
| "Mastering — Loud (Knee 6 dB)" | +4 dB, 6 dB knee, 8× |
| "Mastering — Three Bands" | +3 dB, the lows 2 dB less, the highs 1 dB more |
| "Mastering — Clip the Mid (M/S)" | +3 dB on mid and side |
| "Drums — Hard Snap" | Hard, +6 dB into −1 dB |
| "Drums — Soft Weight" | Soft, +5 dB into −1 dB |
| "Drums — Kick Only (Low Band)" | two bands at 150 Hz: the low band +6 dB, the rest −6 dB |
| "Bus — Tanh Glue" | Tanh, +3 dB, Mix 70 % |
| "Character — 1× Grit" | Hard at 1×, +12 dB into −3 dB, Mix 40 % |
| "Mixing — Catch the Peaks" | no drive, a −6 dB ceiling with a 2 dB knee: overs only |
