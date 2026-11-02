## How it sounds

A compressor with four characters, measured to the curve (the static curve within 0.015 dB of ideal in Clean; attack and release within 0.5 % of their settings):

| Mode | Character | Use it for |
|---|---|---|
| Clean | transparent feed-forward VCA, no colour below the threshold | control, limiting, sidechain ducking |
| Glue | bus VCA with auto release; `Character` adds VCA colour | drum and mix buses |
| FET | fast feedback, program-dependent release, light harmonics | vocals, drums, aggressive bass, parallel |
| Opto | smooth, program-dependent attack, two-stage release | basses, vocals, pads: levelling |

## Signal flow

Input 1 (the signal) and input 2 ("Sidechain", for an external trigger) → detector (`SC low cut` high-pass, `Stereo link`) → gain computer (`Threshold`, `Ratio`, `Knee`, `Attack`, `Release`, `Auto release`) → `Lookahead` delay on the audio → `Makeup` (or `Auto makeup`) → `Character` → `Mix` with the dry signal → `Output`.

- `Attack` is the time to 63 % of the gain change, `Release` back to 37 %.
- `Auto makeup` adds the gain a −12 dBFS signal loses.
- `Lookahead` (0–5 ms) delays the audio so peaks are caught (overshoot within 1 dB at ratio ∞ and attack 0); it adds latency, which ReBuzz is told about.
- `Ratio` at its top is ∞:1: limiting.

## How to use it

- Threshold from a measurement: the reduction at level L is (L − Threshold) × (1 − 1/Ratio). Measure the part's loud level (`analyze(source=…, metrics=["dynamics"])`, the p95 RMS) and set Threshold = p95 − reduction × Ratio ÷ (Ratio − 1).
- Slow attack (10–30 ms) keeps transients; under 1 ms flattens them. Release under the spacing of the hits.
- Parallel compression: `Mix` 25–40 % with heavy settings (8:1, 10–15 dB of reduction).
- Bus: `SC low cut` 80–150 Hz so the kick and bass don't drive all the reduction.

Sidechain ducking (the kick at input 2: `connect(from="Kick", to=…, input="Sidechain")`):

| Parameter | Value | Why |
|---|---|---|
| Mode | Clean | |
| Sidechain | External | detect from input 2 |
| Threshold | -30 dB | under the kick's level at input 2 |
| Ratio | 8:1 | |
| Knee | 0 dB | |
| Attack | 1 ms | |
| Release | 150 ms | back up before the next 8th at 124 BPM |

## Gotchas

- With `Sidechain` External and nothing connected to input 2, it never compresses. With nothing at input 1 it is silent.
- The sidechain's level is the connection gain into input 2: the threshold depends on it.
- Changing `Lookahead` changes the latency: keep it fixed on parallel routes, or use `Mix`.
- `Character` does nothing in Clean mode.

## Preset map

| Preset | What |
|---|---|
| "Clean — Gentle Leveler" | 2:1, soft knee, auto makeup |
| "Clean — Vocal Control" | 3.5:1, 5 ms attack |
| "Clean — Brickwall" | ∞:1, 0.05 ms attack, 2 ms lookahead: a limiter |
| "Glue — Mix Bus" | 2:1, auto release, a little colour |
| "Glue — Drum Bus" | 4:1, 3 ms attack, more colour |
| "FET — Vocal Grab" | 4:1, very fast attack |
| "FET — All Buttons" | 20:1, maximum colour: smashed drums |
| "FET — Bass Tamer" | 8:1, 1 ms attack |
| "Opto — Smooth Vocal" | 3:1, slow release |
| "Opto — Bass Leveler" | 4:1, 0.8 s release |
| "Parallel — New York Drums" | FET 8:1 at 35 % mix |
| "Sidechain — Kick Duck" | external trigger, 8:1, 150 ms release |
| "Sidechain — Pump" | external trigger, ∞:1, 300 ms release: audible pumping |
| "Mastering — Transparent" | 1.5:1, auto release, 1 ms lookahead, 80 Hz SC low cut |
