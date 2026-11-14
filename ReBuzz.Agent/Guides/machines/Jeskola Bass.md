## How it sounds

An early Buzz bass synth: four voices of saw, pulse or triangle into a resonant filter, with slides between notes. Simple and rough; resonance near the top is very loud.

## Use it for

- Simple basses and leads in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 64 | 0 to 128; 128 is fully open |
| Resonance | 40 | 0 to 128; hot near the top |
| Waveform | 1 | 1 sawtooth, 2 pulse, 3 triangle |
| Volume | 128 | 0 to 254; 128 is 100 % |
| Slide lenght | 4 | ticks, 1 to 255 |

## Starting points

- A rounded saw: `param_set(machine="Bass", changes=[{"param": "Cutoff", "value": "60"}, {"param": "Resonance", "value": "30"}])`.

## Pitfalls

- `Resonance` near the top peaks past +16 dBFS.
- `Volume` and `Slide lenght` are pattern columns, not state: set them in the pattern.
- Velocity does not change its level.

## Pairs well with

- Jeskola Filter after it; Pedal Comp for level.

## Successor

Neo Poly replaces Jeskola Bass: `machine_replace(machine="Bass")` plays its line as a mono legato bass with slides as glides and Volume as velocity; cutoff, resonance and waveform carry over.

## Checklist

- [ ] `analyze(source="Bass", metrics=["loudness"])`: a note sits near -9 LUFS.
- [ ] `param_get(machine="Bass")`: `Resonance` is moderate.
- [ ] `render(probes=["Bass"])`: no clipping on a low `Cutoff`.
