# FM recipes: Neo FM6

Recipes with exact values for Neo FM6: electric piano, bell, FM bass, metallic percussion, brass and organ, from its init patch. Values are what `param_set` takes. How to read a sound and the effects that shape it are in `guide(topic="sound-design")`.

## Neo FM6

A DX7 in DX7 units: six operators, the 32 algorithms, rate/level envelopes, DX7 SysEx banks.

- **Carriers** are heard; **modulators** set brightness. `OpN level` 0–99, about 0.75 dB per step near the top (8 steps ≈ 6 dB).
- **Ratio**: `OpN coarse` 0 = 0.5, 1–31 = ×1 … ×31; `OpN fine` adds 0–99 % of it (coarse 3, fine 50 = 3.5). Carrier 1 with a modulator at 1 sounds saw-like (all harmonics), at 2 square-like (odd harmonics); non-integer ratios are inharmonic (bells, metal).
- **Fixed**: `OpN mode` Fixed: coarse picks 1, 10, 100 or 1000 Hz, fine multiplies by ×1 … ×9.77; the operator ignores the note.
- **Envelope**: on a note the level moves to level 1 at rate 1, level 2 at rate 2, level 3 at rate 3 and holds; on release to level 4 at rate 4. Rate 99 is instant; about 6 rate steps halve or double a segment's time.
- **Velocity sensitivity** 0–7 per operator: on modulators, harder notes are brighter (the electric piano's bark). **Rate scaling** 0–7: higher notes decay faster (3–4 for pianos and plucks). Level scaling: break point 39 is C4.
- **Detune** −7 … +7, about 1 cent a step at A4. **Feedback** 0–7 on the algorithm's feedback operator; 6–7 approach a saw.
- `Algorithm` takes 1–32 as the DX7 numbers them. `Engine` Clean 2× (oversampled) or Raw 1× (the original's aliasing). `Tone`: a gentle low-pass, 20 kHz is open.
- A freshly added Neo FM6 (or "Init — Sine") is algorithm 1 with only operator 1 sounding: a sine. In algorithm 1, operator 2 modulates operator 1, so a two-operator patch only needs operator 2's level, ratio and envelope.

| Algorithm | Carriers | Modulation | Good for |
|---|---|---|---|
| 1 | 1, 3 | 2→1; 6→5→4→3, feedback on 6 | brass, strings, rich leads |
| 2 | 1, 3 | 2→1 with feedback on 2; 6→5→4→3 | leads, basses with bite |
| 5 | 1, 3, 5 | 2→1, 4→3, 6→5, feedback on 6 | electric pianos, mallets, bells: three two-operator voices |
| 16 | 1 | 2→1, 4→3→1, 6→5→1, feedback on 6 | one complex tone: leads, basses |
| 22 | 1, 3, 4, 5 | 2→1; 6→3, 4 and 5, feedback on 6 | brass sections, organs |
| 31 | 1–5 | 6→5, feedback on 6 | organ with one bright partial |
| 32 | 1–6 | none, feedback on 6 | additive organs, drawbars |

Read one operator at a time: `machine_describe(machine="Keys", group="Operator 2")`.

DX7 banks: `machine_data_import(machine="Keys", kind="dx7_bank", path="C:/DX7/rom1a.syx", item=11)` loads a voice into every parameter. In the original ROM1A bank: 1 BRASS 1, 4 STRINGS 1, 11 E.PIANO 1, 15 BASS 1, 22 MARIMBA, 26 TUB BELLS.

### Electric piano: the tine

Start from "Keys — Tine Piano": algorithm 5, feedback 6. Three two-operator pairs: 2→1 (body, operator 2 at velocity sensitivity 7 for the bark), 4→3 (operator 4 at ratio 14: the tine's ping, quieter on high notes through its level scaling), 6→5 (operator 6 with feedback for edge). Carriers 3 and 5 are detuned +3 and −3 for the chorus. Variations:

| Parameter | Value | Why |
|---|---|---|
| Op4 level | 82 | louder tine (the preset has 76) |
| Op2 level | 66 | more bark on hard notes (60) |
| Feedback | 4 | smoother edge (6) |
| Unison | 2 | width |
| Uni detune | 20% | ±5 cents |
| Tone | 8 kHz | darker, vintage |

Play velocities 60–110: the bark appears above about 100. Pair with Neo Drive "Tube — Glow" and a slow chorus-like unison for the classic stage sound.

### Bell (two operators, from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Op2 coarse | 3 | |
| Op2 fine | 50 | ratio 3.5: inharmonic, bell-like |
| Op2 level | 72 | brightness of the strike |
| Op2 rate 2 | 35 | the modulator fades: the bell darkens as it rings |
| Op2 level 2 | 0 | |
| Op2 rate 3 | 35 | |
| Op2 level 3 | 0 | |
| Op2 rate 4 | 30 | |
| Op2 vel sens | 5 | harder strikes are brighter |
| Op1 rate 2 | 28 | the carrier rings longer than the modulator |
| Op1 level 2 | 0 | |
| Op1 rate 3 | 28 | |
| Op1 level 3 | 0 | |
| Op1 rate 4 | 30 | rings on after release |
| Op1 vel sens | 2 | |

Other bell ratios: 1.41 (coarse 1, fine 41) for glassy, 7 for a high ping. For a richer bell use algorithm 5 and stack three pairs at different ratios: "Bell — Glass Bell".

### FM bass (two operators, from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Voice mode | Mono | |
| Op2 coarse | 1 | 1:1, saw-like; 2 for a hollow square |
| Op2 level | 80 | |
| Op2 rate 2 | 55 | the brightness drops after the attack |
| Op2 level 2 | 60 | |
| Op2 rate 3 | 40 | |
| Op2 level 3 | 45 | held brightness |
| Op2 rate 4 | 70 | |
| Op2 vel sens | 5 | |
| Op1 rate 4 | 70 | short release |
| Volume | -6 dB | |

Play E1–E3. Thicker: "Bass — Solid" (algorithm 5, a pair at ratio 0.5 under a pair at 1).

### Metallic percussion (fixed modulator, from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Op2 mode | Fixed | the modulator ignores the note |
| Op2 coarse | 2 | 100 Hz |
| Op2 fine | 50 | × 3.16: 316 Hz |
| Op2 level | 85 | |
| Op2 rate 2 | 60 | |
| Op2 level 2 | 0 | |
| Op2 rate 3 | 60 | |
| Op2 level 3 | 0 | |
| Op1 rate 2 | 45 | |
| Op1 level 2 | 0 | |
| Op1 rate 3 | 45 | |
| Op1 level 3 | 0 | |
| Op1 rate 4 | 45 | |

Each note gives a different metal, because the carrier moves and the modulator doesn't.

### Brass swell (from "Init — Sine")

| Parameter | Value | Why |
|---|---|---|
| Op2 level | 85 | |
| Op2 rate 1 | 55 | the brightness rises slower than the level: the brass "blat" |
| Op2 rate 2 | 50 | |
| Op2 level 2 | 88 | |
| Op2 rate 3 | 40 | |
| Op2 level 3 | 85 | held brightness |
| Op2 rate 4 | 60 | |
| Op1 rate 1 | 75 | |
| Op1 rate 4 | 60 | |
| LFO speed | 35 | about 6 Hz |
| LFO delay | 40 | vibrato comes in late |
| LFO pitch | 6 | |
| Unison | 2 | |
| Uni detune | 30% | |

A section: "Brass — Bright Section" (algorithm 22).

### Organ

Algorithm 32 makes all six operators carriers: six drawbars. Ratios for the drawbar footages:

| Drawbar | 16′ | 8′ | 5⅓′ | 4′ | 2⅔′ | 2′ | 1⅗′ | 1⅓′ | 1′ |
|---|---|---|---|---|---|---|---|---|---|
| Ratio | 0.5 | 1 | 1.5 | 2 | 3 | 4 | 5 | 6 | 8 |
| Coarse, fine | 0, 0 | 1, 0 | 1, 50 | 2, 0 | 3, 0 | 4, 0 | 5, 0 | 6, 0 | 8, 0 |

"Organ — Drawbars" uses 16′ to 2′; "Organ — Percussive" adds a fast-decaying partial at ratio 3. Each operator's output level is its drawbar: 99 full, about 8 steps per 6 dB.

## Checklist

- [ ] The recipe started from the named preset: `preset_load(machine=…, preset=…)`, then `param_get(machine=…)` reads the values back.
- [ ] It is in tune: `analyze(source=…, metrics=["pitch"])` reads within ±10 cents; fixed-ratio metallic sounds are inharmonic on purpose.
- [ ] It doesn't clip: `render(solo=[…])` reports no clipped samples and a true peak below -1 dBTP.
- [ ] The brightness fits the part: `analyze(source=…, metrics=["spectrum"])` reads the centroid and tilt the recipe names.
