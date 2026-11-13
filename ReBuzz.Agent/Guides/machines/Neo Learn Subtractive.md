## How it sounds

A teaching subtractive synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A wave that morphs sine, triangle, saw, pulse; an optional sub square one octave down; a 24 dB/octave ladder low-pass with resonance and drive; one ADSR that shapes both amplitude and filter; unison and an output gain. Warm basses, pulse and saw leads, slow pads, soft keys, plucks and resonant sweeps. Band-limited oscillators keep the top clean. 16 notes.

- `Wave Shape` crossfades 0 % sine, 33 % triangle, 66 % saw, 100 % pulse. `Sub Osc` adds a phase-locked square an octave down.
- `Drive` saturates before the filter and adds slight per-voice pitch drift.
- `Env to Filter` sweeps the cutoff by up to 6 octaves with the envelope (negative closes it; 50 %, raw 500, is zero).
- `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (default about -5.5 dB, so presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS.

## Use it for

- Learning how subtractive synthesis works: each control has a How and a Sound sentence (`machine_describe` returns them with the id, unit, range and curve; `param_set` takes the name or the id; writes show on the Explain strip with the Agent badge).
- Basses, leads, pads, plucks and sweeps when you want only the essentials.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 800 Hz | 300-900 Hz for basses; open it for leads |
| Resonance | 20% | 20-40 % for leads; very high with a swept cutoff can pass 0 dBFS |
| Wave Shape | 66% | saw; pulse at 100 %, sine at 0 % |
| Env to Filter | 30% | positive values give plucks and brass-like sweeps (-100 to +100 %) |
| Sustain | 70% | 0 % for plucks |
| Unison / Spread | 0% | 30-60 % for pads and wide leads |
| Output | -6 dB | lower it if `peak_db` passes 0 dBFS |

## Starting points

- "Bass — Sub Pluck", "Bass — Fat Saw", "Lead — Pulse Morph", "Lead — Wide Saw".
- "Pad — Slow Triangle", "Pad — Unison Wash", "Keys — Soft Sine", "Pluck — Filter Snap", "FX — Resonant Sweep".
- Bass: saw or pulse, Sub Osc 50-80 %, cutoff 300-900 Hz, Env to Filter positive, short Decay, Unison 0. Pad: slow Attack and Release, Unison 30-60 %, triangle or saw. Pluck: Sustain 0, short Decay, positive Env to Filter.

## Pitfalls

- Unison 0 to anything above it jumps from 1 voice to 5 (the level stays even; the sound gets wider).
- The oscillator that plays sine below 50 % Wave Shape changes to pulse above it at a silent point.
- With high resonance and a swept cutoff the output can pass 0 dBFS: watch `peak_db` in `machine_readings` and lower Output.
- Only these 12 controls exist: the other Dye Essence controls are fixed and cannot be set here.

## Pairs well with

- Neo Filter and Neo Drive for more colour, Neo Delay and Neo Verb for space, Neo Ensemble for width, Neo Comp on a bass.
- Dye Essence itself when you outgrow the 12 controls.

## Checklist

- [ ] `machine_describe(machine="Learn")` lists each control's `how` and `sound`; read the one you are changing.
- [ ] `param_set(machine="Learn", changes=[{"param": "Cutoff", "value": "800 Hz"}])`, then `machine_readings(machine="Learn")`: `rms_db` near -22 dB, `peak_db` under 0.
- [ ] `midi_play(machine="Learn", notes=[...])` or a pattern, then `analyze(source="Learn", metrics=["spectrum"])` shows the brightness you meant.
