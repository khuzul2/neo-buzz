## How it sounds

A noise source with an attack, sustain and release envelope per track and a colour from dark to white. A track sounds when its Trigger column is written. It follows no pitch.

## Use it for

- Hats, bursts, risers and noise sweeps.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Attack | 1 | 1 to 65535 ms |
| Sustain | 50 | 1 to 65535 ms; a hold time |
| Release | 200 | 1 to 65535 ms |
| Color | 4096 | 0 dark to 4096 white |
| Volume | 128 | 0 to 254; 128 is 100 %; at 0 nothing sounds |

## Starting points

- A hat: `param_set(machine="Noise", changes=[{"param": "Attack", "value": "1"}, {"param": "Sustain", "value": "5"}, {"param": "Release", "value": "60"}, {"param": "Color", "value": "4096"}])`.
- A riser: raise `Color` over time with `automation_write(machine="Noise", pattern="Rise", param="Color", points=[{"at": 0, "value": "500"}, {"at": 8, "value": "4096"}])`.

## Pitfalls

- The Trigger column is a pattern event, not a state parameter: write it in the pattern.
- At `Volume` 0 a trigger makes no sound.
- There is no pitch control: it is only noise.

## Pairs well with

- Jeskola Filter to shape the noise; Jeskola Reverb for a wash.

## Checklist

- [ ] `analyze(source="Noise", metrics=["spectrum"])`: `Color` moves the brightness.
- [ ] `param_get(machine="Noise")`: `Attack`, `Sustain` and `Release` read in ms.
- [ ] `render(probes=["Noise"])`: the envelope ends in silence.
