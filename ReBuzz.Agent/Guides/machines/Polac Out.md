## How it sounds

An output stage with a volume control from silence to +12 dB and a panning control. At its defaults it passes the sound unchanged.

## Use it for

- A level trim or a pan at the end of a chain.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Volume | 0 dB | silence to +12 dB; 0 dB at 100 |
| Pan | 0 | -64 left to +64 right; unit unverified |

## Starting points

- A trim: `param_set(machine="Out", changes=[{"param": "Volume", "value": "-6 dB"}])`.

## Pitfalls

- Volume above 0 dB boosts the signal and can clip.
- Unverified: the unit of Panning and what it does to the sound; the profile did not move it.
- Neo Utility does the same with width and polarity.

## Pairs well with

- Neo Utility for width and polarity.

## Successor

Neo Utility replaces it in practice, but `machine_replace` has no rule for it: set the level and pan by hand.

## Checklist

- [ ] `analyze(source="Out", metrics=["loudness"])`: the output is the input's level at the defaults.
- [ ] `param_get(machine="Out")`: `Volume` is what you set.
- [ ] `render(probes=["Out"])`: nothing clips when Volume is above 0 dB.
