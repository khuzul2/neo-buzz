## How it sounds

A threshold-and-clamp distortion: wherever the signal crosses a threshold it is replaced by a fixed clamp level, blended with the original by Amount. The result is a harsh, square-edged fuzz that is louder than the input at its defaults.

## Use it for

- Fuzzy bass and leads, bit-rough drums and gated-fuzz special effects.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| +threshold | 10% | the level above which positive peaks are cut |
| +clamp | 20% | the level a cut peak becomes; the default is 200 % |
| -threshold | 10% | the same for the negative half |
| -clamp | 20% | the negative clamp |
| Amount | 100% | how much of a cut sample is the clamp |

## Starting points

- Presets: "Fuzz — Hard Clip", "Drive — Asymmetric", "Crunch — Light", "Square — Gated Fuzz"; load with `preset_load(machine="Dist", preset="Crunch — Light")`.

## Pitfalls

- Hot at its defaults: `+clamp` is 200 % of full scale. Keep the clamps at 100 % or less.
- Levels are percent of full scale: 100 % is 0 dBFS, 25 % is -12 dBFS.
- The negative threshold and clamp act only with the machine's Symmetric attribute off.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Filter after it to tame the fizz; Jeskola EQ-3 before it to shape what gets clipped.

## Successor

Neo Drive replaces Jeskola Distortion: `machine_replace(machine="Dist")` uses Neo Drive's hardest clip with the threshold as the drive, the clamp as the output level and Amount as the mix.

## Checklist

- [ ] `analyze(source="Dist", metrics=["loudness"])`: the output stays under full scale.
- [ ] `param_get(machine="Dist")`: `+clamp` and `-clamp` read in %.
- [ ] `render(probes=["Dist"])`: no clipping at the master.
