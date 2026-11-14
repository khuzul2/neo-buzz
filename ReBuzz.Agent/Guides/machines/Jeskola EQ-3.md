## How it sounds

A Buzz three-band EQ: low, mid and high gains of plus or minus 24 dB, with two crossover frequencies that split the bands. Broad tone shaping rather than surgery. Mono, and about 6 dB louder than its input at flat settings.

## Use it for

- Warming or brightening a mix, scooping the mids and cutting the lows below a corner.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Low Gain | +3 dB | up to 24 dB either way |
| Mid Gain | 0 dB | up to 24 dB either way |
| High Gain | -3 dB | up to 24 dB either way |
| Low Cutoff | 400 Hz | 10 Hz to 16 kHz; low band ends here |
| High Cutoff | 5000 Hz | 10 Hz to 16 kHz; high band starts here |

## Starting points

- Presets: "Tone — Warm", "Tone — Bright", "Mid — Scoop", "Mid — Telephone", "Low — Cut Below 150 Hz"; load with `preset_load(machine="EQ3", preset="Tone — Warm")`.

## Pitfalls

- Gains near +24 dB are hot (+18.8 dBFS on noise in the profile).
- `Dry Thru` is a 0 or 1 whose effect is not documented: leave it at 1.
- The crossovers are wide: it is broad shaping, not a narrow cut.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Distortion after it; Pedal Comp before it.

## Successor

Neo EQ replaces Jeskola EQ-3: `machine_replace(machine="EQ3")` makes a low and a high shelf at the two crossovers with each band's gain relative to the mid.

## Checklist

- [ ] `analyze(source="EQ3", metrics=["spectrum"])`: the bands moved by the gains you set.
- [ ] `param_get(machine="EQ3")`: gains read in dB and cutoffs in Hz.
- [ ] `render(probes=["EQ3"])`: no clipping with a boosted band.
