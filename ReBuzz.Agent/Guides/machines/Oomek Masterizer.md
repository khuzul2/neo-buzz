## How it sounds

A three-band limiter for the master: input gain, a threshold and release for each band, two crossovers, a soft-to-hard knee and a lookahead of 176 samples. It raises loudness and keeps each band under control.

## Use it for

- Loudness and control on a finished mix.
- Taming bass or harsh highs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Input gain | 0 dB | silence to +24 dB |
| Low band threshold | -6 dB | silence to +6 dB |
| Mid band threshold | -6 dB | silence to +6 dB |
| High band threshold | -6 dB | silence to +6 dB |
| Low band release | 0.3 s | 40 to 960 ms |
| Knee | 16 | 0 soft to 32 hard |

## Starting points

- Presets: "Master — Gentle Glue", "Master — Dense", "Bass — Tame Lows", "Bright — De-harsh"; load with `preset_load(machine="Masterizer", preset="Master — Gentle Glue")`.

## Pitfalls

- Latency: 176 samples (4 ms) from its lookahead.
- Its envelope times assume 44.1 kHz and its crossover frequencies are estimates that move with the sample rate.
- Before the engine has run its own crossover texts read 1 Hz: read them through the manifest.
- Put it last on the master.

## Pairs well with

- Jeskola EQ-3 before it.

## Successor

Neo Comp is the modern dynamics machine, but nothing maps the three-band limiter onto it: `machine_replace` has no rule for it, so set it up by hand.

## Checklist

- [ ] `analyze(source="Masterizer", metrics=["loudness"])`: peaks stay below the thresholds you set.
- [ ] `param_get(machine="Masterizer")`: `Input gain` and the thresholds are what you set.
- [ ] `render(probes=["Masterizer"])`: nothing clips and the low end stays tight.
