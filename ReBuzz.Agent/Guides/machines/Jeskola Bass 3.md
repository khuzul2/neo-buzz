## How it sounds

A Buzz bass synth with an acid flavour: one oscillator (sine, saw, pulse, triangle, noise or a special acid saw) into a resonant filter with an envelope, per track. Squelchy acid lines.

## Use it for

- Acid and rolling bass lines with accent and slide.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Waveform | sawtooth | the special acid sawtooth is the classic choice |
| Cutoff | 40% | 0 to 100 %; fully open by default |
| Resonance | 50% | 0 to 100 %; the squelch |
| Envmod | 60% | how far the envelope opens the filter |
| Decay | 40% | how fast it closes again |

## Starting points

- Presets: "Bass — Round Saw", "Bass — Saw Pluck", "Bass — Open Saw"; load with `preset_load(machine="Bass3", preset="Bass — Saw Pluck")`.

## Pitfalls

- Each track is its own monophonic voice (8 at most).
- It has no level control and plays near full scale: a bass line peaks above -1 dBFS.
- Volume, Note length and Slide note are pattern columns, not set with `param_set`.

## Pairs well with

- Jeskola Distortion, Jeskola Delay.

## Successor

Neo Poly replaces Jeskola Bass 3: `machine_replace(machine="Bass3")` plays its line as a mono legato bass with notes, slides and note length carried over; cutoff, resonance and waveform carry over.

## Checklist

- [ ] `analyze(source="Bass3", metrics=["loudness"])`: the line sits near -11 LUFS.
- [ ] `param_get(machine="Bass3")`: `Cutoff` and `Resonance` read in %.
- [ ] `render(probes=["Bass3"])`: the peaks stay under full scale.
