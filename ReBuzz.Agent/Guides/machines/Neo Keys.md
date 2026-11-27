## How it sounds

An electric-keys instrument by physical modelling. A hammer strikes a tine, a steel reed or a string, and a pickup reads it. Model picks what is struck: Tine (a tine beside a tone bar, read by a magnetic pickup), Reed (a short, buzzy reed read by an electrostatic pickup with odd-order grit) or String (a stiff string with a damper, read at the neck, the bridge or both). Pickup is the voice of the instrument: far is a clean bell, close adds a bark on hard notes that smooths out as the note dies. Hardness is the hammer's contact time (6 to 0.15 ms) and Touch lets velocity harden it. Decay is the fundamental's T60 (0.1 to 20 s) and Release is the damper. Key noise adds the hammer and damper thumps. Drive, Tone and a tremolo with auto-pan finish it. 16 voices, sustain pedal, a stolen voice fades out in 5 ms.

It is a normal instrument: a generator that plays by notes (keyboard, MIDI, a note wire), with no audio input.

| Model | Voice |
|---|---|
| Tine | bell-like and sweet, a clamped bar 1 : 6.27 : 17.5 plus a slow tone bar, a bark when pushed |
| Reed | shorter and buzzier, a push-pull electrostatic pickup makes odd harmonics |
| String | bright and plucky, a stiff string; Pickup select and Mute apply here only |

## Use it for

- Ballad and soul keys, barking drive keys, tremolo chords with auto-pan, funky plucks and muted staccato, glassy bells.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Model | Tine | Tine, Reed or String |
| Hardness | 1.5 ms | the hammer's contact time: 0.15 ms is hard and bright, 6 ms is soft felt |
| Pickup | 35% | 0 % is clean; 90 % and more barks on hard notes |
| Decay | 3 s | the fundamental's T60; the higher modes die sooner |
| Release | 120 ms | the damper: 8 ms is detached, 1.5 s rings on |
| Drive | 0 dB | 0 is bypassed; 10 to 20 dB crunches |
| Tremolo depth | 0% | 60 % at 4.5 Hz with Auto-pan 100 % sweeps left and right |
| Volume | -3 dB | the default patch sits at -22 dB RMS; check `peak_db` with a chord |

## Starting points

- "Tine — Classic Ballad", "Tine — Bark Drive", "Tine — Stereo Tremolo", "Tine — Glass Bells".
- "Reed — Soul Keys", "String — Funky Pluck", "String — Muted Staccato".

## Pitfalls

- It has no audio input, no Mix and no Amount: it only plays notes.
- Pickup select and Mute act on the String model only.
- A close Pickup and a high Drive on a full-velocity chord is loud and gritty: lower Volume and check `peak_db`.
- The sustain pedal holds notes until it lifts; a long Release makes the same ringing without it.
- Key noise is a soft low knock at the strike and on the key going up; a note that has already died has no key-up knock. It never hisses.

## Pairs well with

- Neo Delay and Neo Verb for space, Neo Ensemble for width, Neo EQ to shape the tone.

## Checklist

- [ ] `param_set(machine="Keys", changes=[{"param": "Model", "value": "Reed"}, {"param": "Pickup", "value": "80"}])`, then `render(probes=["Keys"])`.
- [ ] `machine_readings(machine="Keys")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS with a chord.
- [ ] `analyze(source="Keys", metrics=["spectrum"])`: the first line sits at the note and the bark shows as harmonics above it.
