## How it sounds

A synthesised drum kit, nothing sampled: kick, snare, clap, rim, closed and open hats (the closed one chokes the open one), three toms, cowbell, crash and ride. Each of the twelve voices has its tune, decay, tone (or snap), level and pan in real units, so a kit is a set of numbers and sounds the same in every song. Velocity sets a hit's level and opens its tone; an accent makes every hit of its tick louder and brighter.

| Voice | Key | Column | How it is made |
|---|---|---|---|
| Kick | C2 | BD | a sine falling from `Kick sweep` above `Kick tune`, a punch, a click of noise, drive |
| Snare | D2 | SD | two tones an octave apart and a band of noise round `Snare tone` |
| Clap, Rim | D#2, C#2 | CP, RS | three bursts of band-passed noise; three pings and a tick |
| Closed, open hat | F#2, A#2 | CH, OH | six square oscillators and noise, band-passed round the tone |
| Low, mid, high tom | G2, B2, D3 | LT, MT, HT | a sine falling an octave in about 25 ms, with attack noise |
| Cowbell, crash, ride | G#3, C#3, D#3 | CB, CC, RC | two squares through a band-pass; metallic oscillators and noise |

Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id. One stereo output; a hit sounds out its decay whatever the note's length.

## Use it for

- Electronic beats from notes or the tracker's hit columns, kits you can reshape by number, one-shot percussion for sketches and full tracks.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Kick tune | 50 Hz | the body's settled pitch, 30-150 Hz |
| Kick decay | 0.8 s | short is tight, long is a boom |
| Kick sweep | 26 st | how far above its tune the kick starts |
| Snare snap | 50% | the noise against the body |
| Closed hat decay | 0.2 s | the open hat's decay is separate |
| Vel → level | 70% | how much velocity sets the level; 0 % is flat |
| Volume | -6 dB | the whole kit; check `peak_db` |

## Starting points

- "909 — Classic" (the defaults), "909 — Techno", "808 — Classic", "808 — Electro Boom", "Lo-Fi — Dusty", "Minimal — Tight".
- A beat: `notes_write(machine="Drums", pattern="Beat", notes=[{"pitch": "C2", "start": 0, "length": 0.25, "velocity": 120}, {"pitch": "F#2", "start": 0.5, "length": 0.25, "velocity": 90}])`; vary the hats' velocities for a groove.
- Tracker way: hit columns BD to RC take a velocity (1-127), and Accent accents every hit of its tick; `machine_describe` lists them under `played_parameters`.

## Pitfalls

- Only the listed keys play; note-offs are ignored. A new hit of the same voice fades the old one in 3 ms.
- A hit keeps the tune, decay and tone it started with: a slide on `Kick tune` changes the next hit. Level and pan move the sounding hit.
- Only the closed hat chokes (the open hat). Accent is per tick, not per track.
- One stereo output: for a kick on its own channel, use a second Neo Drums with the other voices' levels at -inf.
- `Kick sweep time` is a time constant (63 % of the fall), not the whole fall.

## Pairs well with

- Neo Comp on the drum bus (glue, 4:1, 3 ms attack), Neo Transient for punch, Neo Drive on the kick, Neo EQ, Neo Verb on a short room send; Neo Sampler's Kit mode for your own samples beside it.

## Successor

Neo Drums replaces Rout 909 and FSM KickXP: `machine_replace(machine="909")` maps the trigger columns to hit columns and the settings to the voices, and the reply says what differs.

## Checklist

- [ ] `render(probes=["Drums"])`, then `analyze` loudness and spectrum: the kick has its weight near its `Kick tune`.
- [ ] `machine_readings(machine="Drums")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS with all voices.
- [ ] `notes_read(machine="Drums", pattern="Beat")` shows the keys you intended.
- [ ] `param_set(machine="Drums", changes=[{"param": "Kick decay", "value": "0.8 s"}])` applies, and the next render shows it.
