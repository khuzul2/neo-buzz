## How it sounds

An output stage with one volume control from silence to +12 dB. At its default it passes the sound unchanged.

## Use it for

- A level trim at the end of a chain.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Volume | 0 dB | silence to +12 dB; 0 dB at 100 |

## Starting points

- A trim: `param_set(machine="Out II", changes=[{"param": "Volume", "value": "-6 dB"}])`.

## Pitfalls

- Volume above 0 dB boosts the signal and can clip.
- Neo Utility does the same with pan, width and polarity.

## Pairs well with

- Neo Utility for pan, width and polarity.

## Successor

Neo Utility replaces it in practice, but `machine_replace` has no rule for it: set the level by hand.

## Checklist

- [ ] `analyze(source="Out II", metrics=["loudness"])`: the output is the input's level at the default.
- [ ] `param_get(machine="Out II")`: `Volume` is what you set.
- [ ] `render(probes=["Out II"])`: nothing clips when Volume is above 0 dB.
