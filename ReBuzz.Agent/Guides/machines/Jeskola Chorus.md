## How it sounds

A classic Buzz chorus: one delayed copy of the sound, swept by a slow LFO, mixed back with the dry signal. It thickens and widens a source with a gentle pitch shimmer; with feedback it gets metallic. It is mono and runs hot at its defaults.

## Use it for

- Thickening a pad, a lead or a bass line in an old-school Buzz song.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| freq | 0.5 Hz | 0 to 40.96 Hz; slow sweeps are the classic chorus |
| depth | 40% | 0 to 100 %; how far the delay moves |
| min. delay | 20 ms | 0 to 50 ms; 15 to 30 ms for chorus |
| feedback | 0% | -100 to +100 %; keep it low |
| dry out | 100% | level of the untouched sound |
| wet out | 50% | level of the delayed copy |

## Starting points

- Classic chorus: `param_set(machine="Chorus", changes=[{"param": "freq", "value": "0.5 Hz"}, {"param": "depth", "value": "40%"}, {"param": "min. delay", "value": "20 ms"}, {"param": "wet out", "value": "50%"}])`.
- Thicken only: depth 0, wet out 50 %.

## Pitfalls

- It is hot at its defaults (+1.8 dBFS peak): lower `wet out` or follow it with a gain.
- `feedback` near either end rings very hard (the level climbs past +16 dBFS).
- `dry out` and `wet out` run from -100 to +100 %: a negative value flips the polarity and hollows the sound.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Reverb after it for space; Pedal Comp before it to even out the source.

## Successor

Neo Ensemble replaces Jeskola Chorus: `machine_replace(machine="Chorus")` makes a one-voice chorus with the rate in Hz and the delay and depth carried over. An inverted dry or wet level has no counterpart.

## Checklist

- [ ] `analyze(source="Chorus", metrics=["loudness"])`: the output is not above full scale at your settings.
- [ ] `param_get(machine="Chorus")`: `freq` reads in Hz and `min. delay` in ms.
- [ ] `render(probes=["Chorus"])`: no ringing from `feedback`.
