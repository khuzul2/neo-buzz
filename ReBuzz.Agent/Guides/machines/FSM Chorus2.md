## How it sounds

A stereo chorus: one to six copies of the sound, each delayed and swept by a slow LFO, mixed with the dry sound in a fixed balance of about 29 percent dry. It thickens and widens; there is no dry and wet knob.

## Use it for

- Widening pads, guitars and synths.
- A lush chorus with up to six voices.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Minimum delay | 12 ms | shortest delay the taps reach, about 0.05 to 25 ms |
| Modulation depth | 1 ms | how far each tap swings, 0 to 2.5 ms |
| LFO rate | 0.8 Hz | 0 to 6.6 Hz |
| Taps | 4 | 1 to 6 voices |
| Stereo phase | 90° | 0 to 180 degrees, divided by the tap count |

## Starting points

- A wide pad: `param_set(machine="Chorus2", changes=[{"param": "Taps", "value": "4"}, {"param": "Minimum delay", "value": "15 ms"}, {"param": "LFO rate", "value": "0.6 Hz"}])`.
- A tight doubler: `Taps` 1, `Minimum delay` 8 ms, `Modulation depth` 0.5 ms.

## Pitfalls

- There is no dry/wet control: the balance is fixed, so use the sound's own level to change how much chorus you hear.
- It sounds about 5.5 dB quieter than its input at the defaults (the taps are averaged).
- Its times count samples at 44.1 kHz, so they get shorter in milliseconds at higher sample rates.
- All its controls are track parameters of its single track.

## Pairs well with

- Jeskola Reverb after it for space.

## Successor

Neo Ensemble replaces FSM Chorus2: `machine_replace(machine="Chorus2")` makes a chorus-mode Neo Ensemble that keeps its fixed 1 : 2.5 dry-to-wet balance, with Fatness on the voices (four at most, where Chorus2 has six) and Stereo on Spread.

## Checklist

- [ ] `analyze(source="Chorus2", metrics=["loudness"])`: about 5 dB below the input at the defaults.
- [ ] `param_get(machine="Chorus2")`: `Taps` and `Minimum delay` are what you set.
- [ ] `render(probes=["Chorus2"])`: the sound stays steady, with no clicks as the taps sweep.
