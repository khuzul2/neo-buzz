## How it sounds

A simple auto-wah: a resonant low-pass whose cutoff a sine LFO sweeps up and down. Cutoff, resonance, LFO rate, depth and a settable LFO phase, with a glide on the cutoff. Mono, one track, and hot.

## Use it for

- Funky, talking filter movement on guitars and synths.
- Slow filter breathing on pads.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 120 | 264 Hz x 32^(v/240): 120 is about 1.5 kHz, 0 to 240 is 264 Hz to 8.4 kHz |
| Resonance | 100 | 0 to 240; Q about 1 to 6 |
| LFO rate | 1 Hz | 0.05 to 27 Hz |
| LFO depth | 60 | 0 to 240 cutoff steps: plus or minus half the value |
| Inertia | 40 | 0 to 240; higher glides more slowly |

## Starting points

- A wah: `param_set(machine="WahMan", changes=[{"param": "Cutoff", "value": "110"}, {"param": "LFO rate", "value": "2 Hz"}, {"param": "LFO depth", "value": "90"}])`.
- Restart it each bar by writing `Set LFO phase` 0.

## Pitfalls

- Hot: +10 dB at its defaults and +3.5 dBFS peak on the profile's signal: turn the source down or follow it with Jeskola Gain.
- It is mono: NeoBuzz Live feeds it left + right summed.
- A machine attribute (Theviderness factor) can scale resonance down at low cutoffs; it is 0 by default.
- LFO depth swings plus or minus half its value in cutoff steps, so 240 sweeps five octaves.

## Pairs well with

- Jeskola Gain after it to take the level down.

## Successor

Neo Filter replaces FSM WahMan: `machine_replace(machine="WahMan")` makes a state-variable low-pass at the cutoff and Q fitted to its response, with Neo Filter's LFO running free at the same rate and depth.

## Checklist

- [ ] `analyze(source="WahMan", metrics=["loudness"])`: about 10 dB above the input at the defaults: lower it.
- [ ] `param_get(machine="WahMan")`: `Cutoff` and `LFO rate` are what you set.
- [ ] `render(probes=["WahMan"])`: the sweep is smooth and peaks stay below full scale.
