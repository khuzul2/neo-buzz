## How it sounds

A spectral effect that works through an FFT: a rough pitch shifter, boosting and clipping of frequency bands, a cleaner that mutes weak bands and a phase scrambler, with a volume threshold. It delays the sound by its FFT size.

## Use it for

- Glitchy, vocoded and ghostly sounds.
- Spectral clean-up or destruction.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Threshold | -60 dB | -97.7 to +15.5 dB; lower diverges |
| Spectral boost | 0% | -100 to +100 percent |
| Spectral clean | 20% | 0 to 100 percent |
| Phase scramble | 30% | 0 to 100 percent |
| Spectral distortion | 0% | 0 to 100 percent |
| FFT size | 8192 | 128 to 32,768 samples |

## Starting points

- A ghostly texture: `param_set(machine="Neptune", changes=[{"param": "Spectral clean", "value": "40%"}, {"param": "Phase scramble", "value": "50%"}])`.

## Pitfalls

- Threshold at its lowest step diverges: keep it above -97.7 dB.
- Latency is 8,191 samples at the default FFT size and grows with it.
- Spectral boost is hot at both ends: about +10 dBFS in the profile.
- Its pitch shifter is, in its own words, bad and messed up: rough by design.

## Pairs well with

- Jeskola Reverb after it for space.

## Checklist

- [ ] `analyze(source="Neptune", metrics=["loudness"])`: no peaks above full scale.
- [ ] `param_get(machine="Neptune")`: `FFT size` is what you set.
- [ ] `render(probes=["Neptune"])`: the effect ends in silence after a short tail.
