## How it sounds

A classic Buzz flanger: a very short delayed copy swept by an LFO, giving a jet-like comb that moves through the sound. With feedback it rings and whistles. It is mono, and its wet level starts inverted.

## Use it for

- Sweeps on drums, guitars-like leads and pads in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| freq | 0.3 Hz | 0 to 40.96 Hz; slow for a jet sweep |
| depth | 80% | 0 to 100 % |
| min. delay | 1 ms | 0 to 5 ms; short is a tight comb |
| feedback | 50% | -100 to +100 %; the safe range ends at 98.4 % |
| dry out | 100% | level of the untouched sound |
| wet out | 100% | its default is -100 %, an inverted copy |

## Starting points

- A jet sweep: `param_set(machine="Flanger", changes=[{"param": "freq", "value": "0.3 Hz"}, {"param": "depth", "value": "80%"}, {"param": "feedback", "value": "50%"}, {"param": "wet out", "value": "100%"}])`.

## Pitfalls

- `wet out` defaults to -100 % (an inverted copy): set it before judging the sound.
- `feedback` at +100 % diverges; `param_set` keeps it at 98.4 % or below.
- It is hot at its defaults (+1.2 dBFS peak).
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Pedal Comp after it to tame the peaks; Jeskola Delay before it for a rhythmic wash.

## Successor

Neo Ensemble replaces Jeskola Flanger: `machine_replace(machine="Flanger")` uses its flanger mode with the rate in Hz and the delay and depth carried over. An inverted wet level has no counterpart.

## Checklist

- [ ] `analyze(source="Flanger", metrics=["loudness"])`: the output stays under full scale.
- [ ] `param_get(machine="Flanger")`: `wet out` is not -100 % unless you meant it.
- [ ] `render(probes=["Flanger"])`: `feedback` does not run away.
