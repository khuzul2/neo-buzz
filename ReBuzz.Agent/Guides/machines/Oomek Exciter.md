## How it sounds

A harmonic exciter: it high-passes the sound, drives it through a soft shaper, high-passes the result again and adds it back to the dry signal. That adds brightness and presence without changing the low end.

## Use it for

- Presence on vocals and drums.
- Air on a mix.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Drive | 0.5 | 0.008 to 2; 1 at raw 120 |
| Pre high-pass | 3000 Hz | about 30 Hz to 7 kHz |
| Post high-pass | 5000 Hz | about 30 Hz to 7 kHz |
| Amount | -12 dB | silence to +6 dB |

## Starting points

- Presets: "Air — Subtle Sparkle", "Presence — Vocal", "Bright — Strong", "Bass — Harmonics"; load with `preset_load(machine="Exciter", preset="Air — Subtle Sparkle")`.

## Pitfalls

- It adds to the dry signal, so it gets louder as `Amount` rises: +7.2 dB on noise at the defaults.
- Its filter corners are one-pole estimates at 44.1 kHz and move with the sample rate.
- Before the engine has run, its own texts read every corner as 1 Hz: read them through the manifest.

## Pairs well with

- Jeskola EQ-3 after it to tame the top.

## Successor

Neo Exciter replaces Oomek Exciter: `machine_replace(machine="Exciter")` makes a Neo Exciter in Tape mode with Drive, the pre-cut crossover and Amount carried over; Post Cut has no counterpart because Neo Exciter adds only the harmonics.

## Checklist

- [ ] `analyze(source="Exciter", metrics=["loudness"])`: the output is above the input by the added harmonics.
- [ ] `param_get(machine="Exciter")`: `Amount` is what you set.
- [ ] `render(probes=["Exciter"])`: the top is brighter and nothing clips.
