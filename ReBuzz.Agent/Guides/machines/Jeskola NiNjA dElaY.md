## How it sounds

A Buzz delay with a filter inside its feedback loop, so every repeat is filtered again: it darkens, thins or narrows as it fades. Up to 8 taps, each with its own length, feedback, filter and level.

## Use it for

- Dubby, darkening echoes and filtered repeats that sit behind the dry sound.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Length | 3 | in the length unit |
| Length unit | tick | tick follows the tempo |
| Feedback | 50% | -100 to +100 %; stay inside 98.4 % |
| f. type | lowpass | lowpass darkens each repeat |
| f. cutoff | 2000 Hz | 10 to 20000 Hz; its default is a high-pass at 550 Hz |
| f. bandwidth | 100 Hz | only for bandpass and bandreject |
| Wet out | 75% | echo level, 0 to 100 % |

## Starting points

- Dub echo: `param_set(machine="Ninja", changes=[{"param": "f. type", "value": "lowpass"}, {"param": "f. cutoff", "value": "2000 Hz"}, {"param": "Feedback", "value": "60%"}])`.

## Pitfalls

- The filter defaults to a high-pass at 550 Hz: repeats lose their lows until you change `f. type`.
- `Feedback` near either end never decays.
- The x64 build ships for private use only, as it is.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Reverb after it; Pedal Gate before it to keep the input clean.

## Successor

Neo Delay replaces Jeskola NiNjA dElaY: `machine_replace(machine="Ninja")` uses its stereo mode with the first track's length as Sync and Feel (or a free Time), the loop filter as the low cut or high cut, and Feedback carried over.

## Checklist

- [ ] `analyze(source="Ninja", metrics=["spectrum"])`: the repeats are as dark as `f. cutoff` says.
- [ ] `param_get(machine="Ninja")`: `f. type` is what you meant.
- [ ] `render(probes=["Ninja"])`: the echoes decay to silence.
