## How it sounds

A Buzz mono reverb with separate dry, reverb and early-reflection levels, a reverb time up to 6.4 s, a pre-delay and a high cut. A quick, simple ambience; it sums stereo to mono.

## Use it for

- Quick ambience in old-school Buzz songs and sends with reverb only.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Rev Time | 2 s | 0.1 to 6.4 s |
| Rev Out | -18 dB | reverb level, silence up to 0 dB |
| Dry Out | 0 dB | dry level |
| ER Out | -20 dB | early reflections; off by default |
| Pre-Delay | 20 ms | 0 to 100 ms |
| High Cutoff | 5000 Hz | 100 Hz to 16.4 kHz; lower is darker |

## Starting points

- Presets: "Room — Small", "Hall — Large", "Plate — Bright", "Send — Reverb Only", "Ambience — Early Only"; load with `preset_load(machine="Reverb", preset="Hall — Large")`.

## Pitfalls

- `Rev Out` or `ER Out` at 0 dB peak above full scale on a hot input.
- On a send set `Dry Out` to silence.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Pedal EQ after it to thin the tail; Jeskola Delay before it.

## Successor

Neo Verb replaces Jeskola Reverb: `machine_replace(machine="Reverb")` turns Dry Out, Rev Out and ER Out into Mix, Late and Early, and carries Rev Time, Pre-Delay and High Cutoff over in seconds and Hz.

## Checklist

- [ ] `analyze(source="Reverb", metrics=["spectrum"])`: the tail is as dark as `High Cutoff` says.
- [ ] `param_get(machine="Reverb")`: `Rev Time` reads in s.
- [ ] `render(probes=["Reverb"])`: the tail decays to silence inside your `tail_seconds`.
