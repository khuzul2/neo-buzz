## How it sounds

The richest of the three wah machines: eight filters, including two vocal formant filters, and 17 LFO shapes that sweep the cutoff, the LFO free in Hz or locked to the tempo. Mono, one track.

## Use it for

- Talking, vowel-like filter sweeps.
- Tempo-locked filter patterns.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 120 | 264 Hz x 32^(v/240): 120 is about 1.5 kHz; a vowel position on the vocal filters |
| Resonance | 100 | 0 to 240 |
| Filter type | Vocal I | eight filters |
| LFO rate | 1 Hz | 0.05 to 26 Hz, then tempo-locked |
| LFO depth | 60 | 0 to 240 cutoff steps |
| LFO shape | sine | 17 waves |

## Starting points

- A vowel sweep: `param_set(machine="WahPro2", changes=[{"param": "Filter type", "value": "Vocal I"}, {"param": "LFO rate", "value": "0.5 Hz"}, {"param": "LFO depth", "value": "120"}])`.

## Pitfalls

- +3.3 dB at its defaults and hot at Cutoff's top (+3.1 dBFS peak).
- It is mono: NeoBuzz Live feeds it left + right summed.
- On the vocal filters Cutoff is a vowel position, not a frequency.
- LFO rates 240 to 254 lock to the tempo, 1 to 128 ticks a cycle.

## Pairs well with

- Jeskola Gain after it to take the level down.

## Successor

No machine_replace rule for it: Neo Filter's LFO covers the idea, but only FSM WahMan has a fitted mapping, so set it up by hand.

## Checklist

- [ ] `analyze(source="WahPro2", metrics=["loudness"])`: a few dB above the input at the defaults.
- [ ] `param_get(machine="WahPro2")`: `Filter type` is what you set.
- [ ] `render(probes=["WahPro2"])`: the sweep is smooth and peaks stay below full scale.
