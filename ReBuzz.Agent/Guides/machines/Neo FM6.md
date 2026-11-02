## How it sounds

A six-operator FM synth built like a DX7: glassy electric pianos, bells, mallets, punchy basses, brass and organs, and metallic or inharmonic tones no filter synth makes. Every parameter keeps the DX7's own values (levels and rates 0–99, coarse 0–31, detune ±7), so DX7 patches and know-how carry over, and DX7 SysEx banks load directly. Clean by default (2× oversampled); `Engine` Raw 1× brings back the original's aliasing grit.

## Signal flow

- Six operators, each a sine (or half, absolute or pulse sine) with its own frequency, output level and rate/level envelope. `Algorithm` (1–32, the DX7's numbering) decides which operators are heard (carriers) and which modulate others (modulators); one operator per algorithm has `Feedback` (0–7).
- Pitch envelope (`PEG rate 1` … `PEG level 4`, 50 = no shift), one LFO (pitch and amplitude depth, delay, key sync), controllers (mod wheel and aftertouch to pitch and amplitude).
- Voice: poly, mono or legato, glide, unison up to 4, transpose, `Tone` (a gentle low-pass), `Volume`.

## Operator values

- Output level 0–99: on a carrier it is loudness (about 0.75 dB per step near the top, so 8 steps ≈ 6 dB); on a modulator it is brightness (the modulation index).
- Ratio: `OpN coarse` 0 = 0.5, 1–31 = ×1 … ×31; `OpN fine` adds 0–99 % (coarse 2, fine 50 = 3.0; coarse 3, fine 50 = 3.5). Fixed mode: coarse picks 1, 10, 100 or 1000 Hz, fine multiplies by ×1 … ×9.77.
- Envelope: at note on the level moves to level 1 at rate 1, level 2 at rate 2, level 3 at rate 3 (held while the note is); at note off to level 4 at rate 4. Rate 99 is instant; about 6 rate steps halve or double a segment's time.
- Velocity sensitivity 0–7, rate scaling 0–7 (higher notes faster), amplitude-modulation sensitivity 0–3, keyboard level scaling around a break point (39 = C4) with depths and curves per side.
- Detune −7 … +7, about 1 cent a step at A4.

| Algorithm | Carriers | Modulation | Good for |
|---|---|---|---|
| 1 | 1, 3 | 2→1; 6→5→4→3, feedback on 6 | brass, strings, rich leads |
| 2 | 1, 3 | 2→1 with feedback on 2; 6→5→4→3 | leads and basses with bite |
| 5 | 1, 3, 5 | 2→1, 4→3, 6→5, feedback on 6 | electric pianos, mallets, bells |
| 16 | 1 | 2→1, 4→3→1, 6→5→1, feedback on 6 | one complex tone |
| 22 | 1, 3, 4, 5 | 2→1; 6→3, 4 and 5, feedback on 6 | brass sections, organs |
| 31 | 1–5 | 6→5, feedback on 6 | organ with a bright partial |
| 32 | 1–6 | none, feedback on 6 | drawbar organs, additive |

## How to use it

- Start from "Init — Sine" (algorithm 1, only operator 1 sounding) for two-operator sounds: operator 2 modulates operator 1, so set its level, ratio and envelope.
- Brightness over time is the modulator's envelope: a modulator that decays makes a sound that darkens as it rings (pianos, bells, plucks).
- Ratios: 1:1 is saw-like, 1:2 square-like (odd harmonics), 1:3.5 or 1:1.41 bell-like, a fixed-frequency modulator gives metal that doesn't follow the note.
- Velocity on modulators (vel sens 5–7) makes hard notes brighter: the electric piano's bark.
- Read one operator at a time: `machine_describe(machine=…, group="Operator 2")`.
- DX7 banks: `machine_data_import(machine=…, kind="dx7_bank", path=…, item=11)` sets every parameter from voice 11 of the bank (E.PIANO 1 in the original ROM1A).

A two-operator electric piano from "Init — Sine":

| Parameter | Value | Why |
|---|---|---|
| Op2 coarse | 1 | |
| Op2 level | 70 | the tone's brightness |
| Op2 vel sens | 7 | hard notes bark |
| Op2 rate 2 | 40 | the brightness fades |
| Op2 level 2 | 30 | |
| Op2 rate 3 | 30 | |
| Op2 level 3 | 0 | |
| Op1 rate 2 | 30 | the tone decays like a struck key |
| Op1 level 2 | 70 | |
| Op1 rate 3 | 25 | |
| Op1 level 3 | 0 | |
| Op1 rate 4 | 60 | |
| Op1 rate sc | 3 | high notes decay faster |
| Op2 rate sc | 3 | |

## Gotchas

- `Algorithm` takes 1–32 (the DX7's numbers), not 0–31.
- An operator with `OpN on` Off is silent, as on the DX7; carriers at level 0 are silent too.
- Fixed-frequency operators ignore the note (they follow pitch bend only).
- Feedback 7 on a carrier is close to a saw; on a loud modulator it turns to noise.
- Unison up to 4 and 16 notes with 6 operators each at 2× is the heaviest load of the Neo synths; `Engine` Raw 1× halves it and changes the sound.

## Preset map

| Preset | What | Tweak first |
|---|---|---|
| "Init — Sine" | algorithm 1, operator 1 alone | build from here |
| "Keys — Tine Piano" | algorithm 5: body, ratio-14 tine, feedback edge | `Op4 level`, `Op2 level` |
| "Keys — Clav Funk" | algorithm 3, bright plucked ratios | `Op2 level` |
| "Keys — Squeeze EP" | algorithm 5 with an abs-sine modulator and tremolo | `LFO amp` |
| "Bell — Glass Bell" | algorithm 5, modulators at 3.5, 7.42 and 11 | `Op2 fine` |
| "Bell — Random Chimes" | bells with a sample-and-hold pitch LFO | `LFO pitch` |
| "Mallet — Marimba" | ratio-4 and ratio-10 strikes | `Op2 level` |
| "Pluck — Harp" | ratio-2 and ratio-5 plucks | `Op4 level` |
| "Bass — Solid" | a ratio-0.5 pair under a ratio-1 pair | `Op2 level` |
| "Bass — Wood Mono" | algorithm 2, mono legato | `Op2 level`, `Glide` |
| "Brass — Bright Section" | algorithm 22, detuned carriers, delayed vibrato | `Op2 level` |
| "Pad — FM Strings" | algorithm 1, unison 2, slow vibrato | `Uni detune` |
| "Pad — Crystal Choir" | half- and abs-sine operators, slow LFO | `LFO amp` |
| "Organ — Drawbars" | algorithm 32 at 0.5, 1, 1.5, 2, 3, 4 | each operator's level |
| "Organ — Percussive" | drawbars plus a decaying ratio-3 click | `Op4 level` |
| "Lead — Saw Lead" | algorithm 2 with feedback 6, legato | `Feedback` |
| "Texture — Metal Sweep" | inharmonic ratios with amplitude LFO | `LFO speed` |
