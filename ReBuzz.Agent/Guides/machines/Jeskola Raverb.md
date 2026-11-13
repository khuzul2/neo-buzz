## How it sounds

A small Buzz reverb with a reverb time, high-frequency damping and separate reverb and dry levels in decibels. The tail is long at its defaults (about 3.5 s).

## Use it for

- Quick ambience where a simple reverb is enough.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Rev Time | 70 | 0 to 127; measured tail from 0.4 s to 4 s |
| HF Damp | 50 | 0 to 127; its effect was not isolated |
| Rev Out | -24 dB | reverb level, -60 to 0 dB |
| Dry Out | 0 dB | dry level, -60 to 0 dB |

## Starting points

- A shorter room: `param_set(machine="Raverb", changes=[{"param": "Rev Time", "value": "40"}, {"param": "Rev Out", "value": "-24 dB"}])`.

## Pitfalls

- The default tail is about 3.5 s: renders need a tail.
- `HF Damp`'s audible effect is unverified here.
- A bare 0 to 127 scale for `Rev Time`: no seconds.

## Pairs well with

- Jeskola EQ-3 after it.

## Checklist

- [ ] `analyze(source="Raverb", metrics=["spectrum"])`: the tail is as bright as you want.
- [ ] `param_get(machine="Raverb")`: `Rev Out` and `Dry Out` read in dB.
- [ ] `render(probes=["Raverb"])`: the tail decays inside your `tail_seconds`.
