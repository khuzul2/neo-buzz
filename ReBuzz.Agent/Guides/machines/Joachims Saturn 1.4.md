## How it sounds

An all-purpose vintage degrader: input and output tone tilt, record noise, a small room, bit-depth and sample-rate reduction, an antique frequency response, pitch wobble, ring modulation, tube-style overdrive and a high-pass.

## Use it for

- Lo-fi character and old-gear tone.
- Old-record or tape wobble.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bit depth | 8-bit | off, 8, 4, 2 or 1 bit |
| Sample rate | 11 kHz | about 410 Hz to 22 kHz, no anti-alias filter |
| Noise amount | -30 dB | silence to 0 dB |
| Detune amount | 20 | 0 to 127 |
| Tube drive | 30 | 0 to 127 |
| Highpass | 40 Hz | 5 to 200 Hz |

## Starting points

- Old sampler: `param_set(machine="Saturn", changes=[{"param": "Bit depth", "value": "8-bit"}, {"param": "Sample rate", "value": "11 kHz"}])`.

## Pitfalls

- The tilt controls are hot at their top: +18.7 dBFS peak in the profile.
- Noise needs its sample files beside the DLL; the package ships them.
- Unverified: what each Noise type is, and Detune speed's own text repeats the amount's.
- It is 0 dB at the defaults.

## Pairs well with

- Jeskola Reverb after it.

## Successor

Neo Drive covers the saturation, but nothing maps the other stages onto it: `machine_replace` has no rule for it, so set it up by hand.

## Checklist

- [ ] `analyze(source="Saturn", metrics=["loudness"])`: about the input level at the defaults.
- [ ] `param_get(machine="Saturn")`: `Bit depth` is what you set.
- [ ] `render(probes=["Saturn"])`: no tilt setting clips.
