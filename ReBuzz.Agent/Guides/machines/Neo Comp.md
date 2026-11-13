## How it sounds

A compressor with four characters, measured to the curve (the static curve within 0.015 dB of ideal in Clean; attack and release within 0.5 % of their settings):

| Mode | Character | Use it for |
|---|---|---|
| Clean | transparent feed-forward VCA, no colour below the threshold | control, limiting, sidechain ducking |
| Glue | bus VCA with auto release; `Character` adds VCA colour | drum and mix buses |
| FET | fast feedback, program-dependent release, light harmonics | vocals, drums, aggressive bass, parallel |
| Opto | smooth, program-dependent attack, two-stage release | basses, vocals, pads: levelling |

Signal path: input 1 (the signal) and input 2 (the external trigger) go to the detector (`SC low cut`, `Stereo link`), then the gain computer, then the `Lookahead` delay on the audio, `Makeup` or `Auto makeup`, `Character`, `Mix` with the dry signal and `Output`. `Attack` is the time to 63 % of the gain change and `Release` back to 37 %. `Ratio` at its top is infinity: limiting.

## Use it for

- Levelling a vocal or bass, glueing a drum or mix bus, parallel compression, sidechain ducking, a quick limiter.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Glue | bus character; Clean for transparent control |
| Threshold | -18 dB | 1-3 dB of reduction on the loudest parts |
| Ratio | 2:1 | gentle; 4:1 to 8:1 for control |
| Attack | 30 ms | 10-30 ms keeps transients; under 1 ms flattens them |
| Release | 300 ms | shorter than the spacing of the hits; or turn Auto release On |
| Makeup | 1 dB | about the reduction, or use Auto makeup |
| Mix | 100% | 25-40 % with heavy settings for parallel compression |

Threshold from a measurement: the reduction at level L is (L - Threshold) x (1 - 1/Ratio). Measure the loud level with `analyze(source="Drums", metrics=["dynamics"])` (the p95 RMS) and set Threshold = p95 - reduction x Ratio / (Ratio - 1).

## Starting points

- "Glue — Mix Bus" (2:1, auto release), "Glue — Drum Bus" (4:1, 3 ms attack), "Mastering — Transparent" (1.5:1, auto release, 1 ms lookahead, 80 Hz SC low cut).
- "Clean — Gentle Leveler", "Clean — Vocal Control", "Clean — Brickwall" (infinity:1, 2 ms lookahead: a limiter).
- "FET — Vocal Grab", "FET — All Buttons" (20:1, smashed drums), "FET — Bass Tamer", "Opto — Smooth Vocal", "Opto — Bass Leveler".
- "Parallel — New York Drums" (FET 8:1 at 35 % mix).
- Sidechain duck with the kick at input 2 (`connect(from="Kick", to="Pad", input="Sidechain")`): Sidechain External, Threshold -30 dB, Ratio 8:1, Knee 0 dB, Attack 1 ms, Release 150 ms, or the presets "Sidechain — Kick Duck" and "Sidechain — Pump".

## Pitfalls

- With `Sidechain` External and nothing connected to input 2 it never compresses. With nothing at input 1 it is silent. The detector level is the connection gain into input 2, so the threshold depends on it.
- `Lookahead` (0-5 ms) adds latency that NeoBuzz Live is told about; changing it moves the audio, so keep it fixed on parallel routes, or use `Mix`.
- `Character` does nothing in Clean.
- On a bus, set `SC low cut` 80-150 Hz so the kick and bass do not drive all the reduction.

## Pairs well with

- Neo EQ before it (cut the lows) and after it (tone); Neo Limiter after a bus compressor; Neo Multiband when one band misbehaves; Neo Transient to restore punch.
- The whole chain: `guide(topic="mixing-dynamics")` and `guide(topic="mastering-chain")`.

## Successor

Neo Comp replaces Pedal Comp: `machine_replace(machine="Comp")` carries its settings over in their units.

## Checklist

- [ ] `machine_readings(machine="Comp")` gives `rms_db` and `peak_db` out; compare with the input's `analyze` figures: the loudest parts are down 1-3 dB (glue) or the amount you planned.
- [ ] `analyze(source="Bus", metrics=["dynamics"])`: crest factor down, loudness matched with Auto makeup or Makeup.
- [ ] For a duck: `connection_set` or `connect` shows input 2 connected, and `analyze` shows the level drop on every kick.
- [ ] `render` with the machine bypassed (`machine_set(machine="Comp", bypass=true)`) and compare with `compare`.
