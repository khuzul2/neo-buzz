## How it sounds

A six-operator FM synth: glassy electric pianos, bells, mallets, punchy basses, brass and organs, and metallic or inharmonic tones a filter synth cannot make. Six sine operators (or half, absolute or pulse sine) modulate each other; `Algorithm` (1-32) decides which are heard (carriers) and which only modulate. Levels and rates run 0-99, coarse 0-31 and detune +-7, the common values of this synth family, so patches and know-how carry over, and `kind="dx7_bank"` imports voice banks in that format. Clean by default (2x oversampled); `Engine` Raw 1x brings back aliasing grit.

- Operator level 0-99: on a carrier it is loudness (about 0.75 dB a step near the top); on a modulator it is brightness.
- Ratio: `OpN coarse` 0 = 0.5, 1-31 = x1 to x31; `OpN fine` adds 0-99 %. Fixed mode picks 1, 10, 100 or 1000 Hz.
- Envelope: level 1 at rate 1, level 2 at rate 2, level 3 at rate 3 (held), then level 4 at rate 4 on release. Rate 99 is instant.
- Every control has a How and a Sound sentence in `machine_describe` (read one operator with `group="Operator 2"`); `param_set` takes the name or the id.

| Algorithm | Carriers | Good for |
|---|---|---|
| 1 | 1, 3 | brass, strings, rich leads |
| 5 | 1, 3, 5 | electric pianos, mallets, bells |
| 22 | 1, 3, 4, 5 | brass sections, organs |
| 32 | 1-6 | drawbar organs, additive |

## Use it for

- Electric pianos, bells, mallets, plucks, FM basses, brass, organs and metallic textures.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Algorithm | 5 | the wiring; 1-32 |
| Feedback | 3 | 0-7; 7 on a carrier is close to a saw |
| Op2 level | 70 | modulator: the tone's brightness |
| Op2 coarse | 1 | ratio 1 is saw-like; 3.5 or 1.41 bell-like |
| Op2 vel sens | 7 | hard notes get brighter |
| Op1 rate 4 | 60 | release speed of the carrier |
| Transpose | 0 st | -24 to +24 |
| Volume | -6 dB | check `peak_db` |

## Starting points

- "Init — Sine" (algorithm 1, operator 1 alone: build two-operator sounds from here), "Keys — Tine Piano", "Keys — Clav Funk", "Bell — Glass Bell", "Mallet — Marimba", "Pluck — Harp".
- "Bass — Solid", "Bass — Wood Mono", "Brass — Bright Section", "Pad — FM Strings", "Organ — Drawbars", "Lead — Saw Lead", "Texture — Metal Sweep".
- A bank: `machine_data_import(machine="FM6", kind="dx7_bank", path="C:/Banks/voices.syx", item=11)` sets every parameter from voice 11 of the bank.
- Brightness over time is the modulator's envelope: a modulator that decays makes a sound that darkens as it rings.

## Pitfalls

- `Algorithm` takes 1-32, not 0-31.
- An operator with `OpN on` Off is silent; carriers at level 0 are silent too.
- Fixed-frequency operators ignore the note; they follow pitch bend only.
- Feedback 7 on a loud modulator turns to noise.
- Unison up to 4 with 16 notes of 6 operators at 2x is the heaviest load of the Neo synths: `Engine` Raw 1x halves it and changes the sound.

## Pairs well with

- Neo Ensemble for the classic chorused piano, Neo Verb for bells, Neo Comp on basses, Neo EQ to cut the lows of bright voices, Neo Delay for echoes.

## Checklist

- [ ] `preset_load(machine="FM6", preset="Keys — Tine Piano")`, then `notes_write` a few notes and `render(probes=["FM6"])`.
- [ ] `machine_readings(machine="FM6")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS.
- [ ] `machine_describe(machine="FM6", group="Operator 2")` lists every control of the operator you are shaping, with its How and Sound.
- [ ] `analyze(source="FM6", metrics=["spectrum"])`: more `Op2 level` gives more upper partials.
