## How it sounds

A synthesised drum kit in the TR-909 and TR-808 manner, nothing sampled: twelve voices, each with its tune, decay, tone (or snap), level and pan in real units, so a kit is a set of numbers and sounds the same in every song. Its defaults were voiced against the Rout 909's samples at their defaults (third-octave spectra, pitch over time, decay to −60 dB); the "808 — Classic" kit is the TR-808's manner. Velocity sets a hit's level and opens its tone; an accent makes every hit of its tick louder and brighter, as a TR-909's accent step does.

| Voice | Key | Column | How it is made |
|---|---|---|---|
| Kick | C2 (also B1) | BD | a sine falling from `Kick sweep` above `Kick tune` (time constant `Kick sweep time`), a punch that starts it louder, a click of noise, drive |
| Snare | D2 (also E2) | SD | two tones an octave apart falling 8 st, and a band of noise round `Snare tone` (`Snare snap` balances them) |
| Clap | D#2 | CP | three bursts of band-passed noise `Clap spread` apart, then the tail |
| Rim | C#2 | RS | three pings (`Rim tune`, half and 0.23 of it) and a tick |
| Closed hat | F#2 (also G#2) | CH | six square oscillators at the TR-808's ratios and noise, band-passed round `Closed hat tone`; it chokes the open hat |
| Open hat | A#2 | OH | the same, longer |
| Low, mid, high tom | G2, B2, D3 (also F2, A2, C3) | LT, MT, HT | a sine falling an octave in about 25 ms then slowly to its tune, with attack noise (`tone`) |
| Cowbell | G#3 | CB | two squares a fifth-ish apart (1 : 1.48) through a band-pass |
| Crash, ride | C#3 (also A3), D#3 (also B3) | CC, RC | the metallic oscillators and noise, band-passed round their tone |

Measured (NeoBuzz.MachineTests, DrumsTests): the kick, toms, snare and rim settle within 3 cents of their tune; the kick's pitch follows 2^(sweep × e^(−t / sweep time)) within 0.6 % per cycle; every decay is the time to fall 60 dB within 6 %; the metallic oscillators are band-limited (aliases under −115 dB at 44.1 and 48 kHz, tune ±12 st, tone 16 kHz); a retrigger fades the voice's last hit over 3 ms and a closed hat cuts the open one in 5 ms without a click; all twelve voices at their longest decays run about 50 times faster than real time; nothing is allocated while it plays, and every block size gives the same samples.

## Signal flow

A note on a key (or a hit column with its velocity) → its voice: the hit takes the voice's tune, decay and tone as they are now (level and pan follow while it sounds) → velocity (`Vel → level`, `Vel → tone`) and the tick's accent (`Accent gain`) → the voice's level and pan → the voice's own bus (each voice holds three hits; a new hit fades the older ones of its voice in 3 ms) → the twelve buses mixed → `Volume` → one stereo output.

## How to use it

A beat from notes (the General MIDI drum keys from C2, as Neo Sampler's Kit mode):

1. `machine_add(gear="Neo Drums", name="Drums")`; a kit: `preset_load(machine="Drums", preset="909 — Techno")`, or keep the defaults (a TR-909).
2. `notes_write(machine="Drums", pattern="Beat", notes=[{"pitch": "C2", "start": 0, "length": 0.25, "velocity": 120}, {"pitch": "F#2", "start": 0.5, "length": 0.25, "velocity": 90}, …])`: C2 kick, D2 snare, D#2 clap, C#2 rim, F#2 closed hat, A#2 open hat, G2 B2 D3 toms, G#3 cowbell, C#3 crash, D#3 ride. A note's length doesn't matter: every hit sounds out its decay.
3. Dynamics: velocity sets the level (gain = velocity^(2 × `Vel → level`): at the default 70 %, velocity 64 is 8 dB under 127) and opens the tone; vary the hats' velocities for a groove.
4. Shape a voice: `param_set(machine="Drums", changes=[{"param": "Kick decay", "value": "0.8 s"}, {"param": "Kick drive", "value": "9 dB"}, {"param": "Closed hat pan", "value": 20}])`.

The tracker way, as Buzz drum machines: each track has the hit columns BD, SD, CP, RS, CH, OH, LT, MT, HT, CB, CC and RC (a value is the hit's velocity, 1–127) and an Accent column (it accents every hit of its tick, on any track). They hold no value: `machine_describe` lists them under `played_parameters`, and `automation_write` writes them, one hit per point.

Check a kit: `render(probes=["Drums"])`, then `analyze` its loudness and spectrum; a voice's hit at velocity 100 shows in the editor's Hit picture (its pad selects it).

Songs with Rout 909 or FSM KickXP move over with `machine_replace(machine="909")`: the drums' trigger columns become Neo Drums' hit columns, the KickXP's notes and Trigger become kick hits, and their settings map to the voices' (the reply says what came out different and what has no counterpart).

A 909 techno kick:

| Parameter | Value | Why |
|---|---|---|
| Kick tune | 50 Hz | |
| Kick sweep | 26 st | starts a little over two octaves up |
| Kick sweep time | 20 ms | |
| Kick decay | 0.80 s | |
| Kick punch | 70 % | the first few milliseconds louder |
| Kick click | 70 % | |
| Kick drive | 9.0 dB | rounder, louder body at the same peak |

An 808 boom kick and its hats:

| Parameter | Value | Why |
|---|---|---|
| Kick tune | 45 Hz | |
| Kick sweep | 7 st | an 808 barely falls |
| Kick sweep time | 35 ms | |
| Kick decay | 2.0 s | |
| Kick click | 15 % | |
| Kick punch | 0 % | |
| Closed hat tone | 8.00 kHz | |
| Open hat decay | 0.45 s | |

## Gotchas

- Only the keys above play; others are silent. Note-offs are ignored: a hit is one-shot, and a new hit of the same voice fades the old one in 3 ms (two hits of a voice on one tick play as one).
- A hit keeps the tune, decay and tone it started with: a slide on `Kick tune` changes the next hit, not the one sounding. Level and pan move the sounding hit (smoothed over 10 ms).
- The closed hat (F#2, the pedal key G#2 and CH) chokes the open hat; nothing else chokes.
- Accent is per tick, not per track: on any track it accents every hit of that tick, notes and hit columns alike.
- One stereo output: per-voice outputs aren't there (ReBuzz's machines output one stereo pair to every connection). For a kick on its own channel, use a second Neo Drums with the other voices' levels at −inf, or the same kit twice with complementary levels.
- `Kick sweep` is in semitones, `Kick sweep time` is a time constant (63 % of the fall), not the whole fall.
- The hats', crash's and ride's tune is in semitones from the TR-808's oscillators (205 Hz … 800 Hz), not a pitch.

## Preset map

| Preset | What |
|---|---|
| "909 — Classic" | the defaults: voiced against the Rout 909's samples |
| "909 — Techno" | a driven, punchy kick, a snappy snare, short bright hats, a long ride |
| "808 — Classic" | the long, nearly pure kick, two-tone snare, 540 and 800 Hz cowbell, higher toms |
| "808 — Electro Boom" | a 2.5 s kick with drive, snappy snare, longer clap and open hat |
| "Lo-Fi — Dusty" | duller, drier, the kick pushed into its drive |
| "Minimal — Tight" | short everything: a clicky kick, a rim and hats that barely ring |
