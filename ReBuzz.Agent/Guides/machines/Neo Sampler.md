## How it sounds

A sampler and drum machine that plays the song's waves (the slots `wave_import` fills, saved with the song) and 26 built-in samples made by code: a drum kit, looped single waves and a few instruments. Sixteen zones each map a wave to a range of keys and velocities, with its own root, tuning, level, pan, start, end, loop, trigger, reverse and choke group. Pitch is exact from A0 to C8 and loops have no seam. Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id.

| Mode | Zones play | Use it for |
|---|---|---|
| Instrument | every zone whose key and velocity ranges hold the note, tuned from its root | pianos, pads, basses, velocity layers |
| Kit | zone n plays key `Kit base` + n - 1, one-shot | drum kits, one-shots, FX |

Built-in waves: 201-215 the drum and percussion hits (kick, snare, hats, toms, crash, ride, cowbell, shaker, clave); 216-218 sine, saw, square; 219-220 electric piano; 221 strings; 222-224 bass, bell, pluck; 225-226 vox and noise.

## Use it for

- A drum kit from your own hits, a sampled instrument with velocity layers and loops, pads from looped waves, reversed swells and one-shot effects.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Kit | Instrument for melodic zones |
| Z1 wave | 201 | 0 off, 1-200 a song slot, 201-226 built-in |
| Z1 root | C3 | the key that plays the wave unchanged; Wave takes the wavetable's own |
| Amp attack | 5 ms | 350 ms for a slow pad swell |
| Amp release | 300 ms | the tail after the key |
| Cutoff | 20 kHz | with Filter on; open is no filtering |
| Volume | -7 dB | a kick, snare and hat together stay under -3 dBFS |

## Starting points

- Kits: "Kit — Electro (GM Layout)", "Kit — 808 Style (Long Kick, Cowbell)", "Kit — Tight and Dry (Short Decays)", "Kit — Lo-Fi (Pitched Down, Low-Pass 3.5 kHz)", "Kit — Percussion (Cowbell, Shaker, Clave)".
- Your samples: import with `wave_import(path="C:/Samples/kick.wav", slot=1, name="Kick")`, then "Kit — Song Waves 1–16 (Hats in Choke 1)" (zone n plays slot n) or "Instrument — Song Waves 1–4 (Velocity Layers)". Give pitched samples a root: `root_note="C3"`.
- Instruments: "Keys — Electric Piano (Two Velocity Layers)", "Pad — Strings (Looped, Slow Attack)", "Pad — Vox Aah (Looped)", "Bass — Sub Pluck", "Bell — Glass (Long Release)", "Pluck — Nylon", "Lead — Saw (Filter Envelope)".
- Looping: `Z1 loop` Forward with `Z1 loop start` and `Z1 loop end` in % and a `Z1 crossfade` of 10-100 ms. Layers: `wave_import(..., layer=true)` adds a file to a slot with its own root.

## Pitfalls

- A zone plays nothing while its slot is empty or its wave is 0.
- Without `root_note` a wave's root is C3: played at C4 it sounds an octave up. Kit mode ignores roots, ranges, loops and triggers.
- A zone loop's crossfade reads samples before `Z1 loop start`: a loop starting at the first sample has none.
- One-shot ignores note-offs and loops: the amp envelope still shapes it (`Amp sustain` 0 and a short decay shorten hits).
- `Polyphony` counts voices, not notes: a note layered by two zones takes two.

## Pairs well with

- Neo Comp and Neo Transient on drum kits, Neo EQ for each sample's weight, Neo Verb and Neo Delay for space, Neo Tape for grit; Neo Drums when you want a synthesised kit.

## Successor

Neo Sampler replaces Pedal M1: `machine_replace(machine="Keys")` turns its oscillator waves and key zones into zones 1-8 at the same pitch, and the reply says what has no counterpart.

## Checklist

- [ ] `wave_list()` shows each slot's root and loop; `preset_load(machine="Drums", preset="Kit — Electro (GM Layout)")` plays without files.
- [ ] `render(probes=["Drums"])`, then `analyze` loudness and spectrum: kick, snare and hats balance.
- [ ] `machine_readings(machine="Drums")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS.
- [ ] `notes_write` a beat on C2, D2, F#2 and A#2: the closed hat cuts the open one when both are in choke group 1.
