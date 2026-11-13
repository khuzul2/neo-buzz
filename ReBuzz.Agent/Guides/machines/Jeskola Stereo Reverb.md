## How it sounds

A Buzz stereo reverb with dry, reverb and early-reflection levels, a reverb time up to 6.4 s, a pre-delay and a high cut. The same controls as Jeskola Reverb, but the output keeps a stereo image (a measured correlation of 0.98).

## Use it for

- Quick stereo ambience in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Rev Time | 2 s | 0.1 to 6.4 s |
| Rev Out | -18 dB | reverb level, silence up to 0 dB |
| Dry Out | 0 dB | dry level |
| ER Out | -20 dB | early reflections; off by default |
| Pre-Delay | 20 ms | 0 to 100 ms |
| High Cutoff | 5000 Hz | 100 Hz to 16.4 kHz |

## Starting points

- A short room: `param_set(machine="SReverb", changes=[{"param": "Rev Time", "value": "0.8 s"}, {"param": "Rev Out", "value": "-18 dB"}, {"param": "ER Out", "value": "-20 dB"}])`.

## Pitfalls

- `Rev Out` or `ER Out` at 0 dB peak above full scale on a hot input.
- Its own descriptions give shorter ranges than its values reach: use the ranges above.
- Measured, the early reflections arrive about 20 ms in and the tail about 40 ms in.

## Pairs well with

- Jeskola EQ-3 after it; Pedal Comp before it.

## Checklist

- [ ] `analyze(source="SReverb", metrics=["spectrum"])`: the tail is as dark as `High Cutoff` says.
- [ ] `param_get(machine="SReverb")`: `Rev Time` reads in s.
- [ ] `render(probes=["SReverb"])`: the tail decays inside your `tail_seconds`.
