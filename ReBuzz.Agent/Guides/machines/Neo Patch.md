## How it sounds

A modular synth inside one machine. You build the sound from nodes wired together: oscillators (band-limited saw, square and triangle, sine, a wavetable oscillator on the ten built-in tables, a sample player, noise), envelopes, LFOs, a random generator, slew, sample and hold, filters (12 dB state variable in four types, and 24 and 12 dB ladders), shapers (soft clip, fold, hard clip), delays, mixers, math and a crossfade, up to 128 nodes. Every wire is one signal, any output to any input, and each wire has an amount. Each note runs its own copy of every per-voice node, so a filter, an LFO or an envelope is per note unless you switch the node to global, where it runs once for all notes (a global delay after the Voice sum is the usual case). It sounds as clean as the oscillators allow (the aliasing floor is below -85 dB) and as wild as the routing you make: FM from any oscillator into any other, a filter fed back on itself through a Feedback node (one block of delay), per-note modulation from pressure and slide.

## Use it for

Sounds the fixed-architecture synths cannot make: your own FM routings, filter feedback, per-note modulation, a global echo after the sum, MPE leads and evolving textures.

## Key controls

- The patch is the machine's state, not a parameter: it is saved with the song and carried by presets. Edit it on the canvas (double click the machine): drag from an output to an input to wire, click a node to see every control with a How and a Sound line in the side panel, and the Templates button loads one of ten starting patches. Agents use the `patch_*` tools; each call is one undo step, the same stack the canvas uses.
- `Volume` is the level after the patch's Out node; a safety limiter keeps the output under full scale. Presets sit near -22 dB RMS.
- `Voices` is how many notes sound at once (1 to 16). `Glide` slides the pitch between notes.
- `Macro 1` to `Macro 16` each move every node setting the patch maps to them, between a low and a high value the patch sets.
- `Audio-rate mod` reads control inputs (cutoff, level, delay time) for every sample instead of every 16 samples with smoothing: switch it on for metallic filter FM; it costs more CPU.
- 16 tracks of Note and Velocity, plus MIDI; MPE channels give each note its own bend, pressure (channel pressure) and slide (CC 74), which the Keys node outputs.

## Starting points

23 presets, all near -22 dB RMS. A few to begin with:
- Init - Saw: a saw through a low-pass with two envelopes; Macro 1 is the cutoff, Macro 2 the release.
- Lead - FM Bell: two sines, the second modulating the first through an envelope; Macro 1 is the modulation depth.
- Pluck — Karplus String: noise into a tuned delay, the string of the plucked-string template, in tune across the keys.
- Lead — Sync Sweep: a saw restarted by a square, with a pitch sweep on each note.
- Pad - Wavetable Drift: two detuned wavetable oscillators whose position drifts on a slow LFO.
- FX - Echo After the Sum: a per-note saw and VCA, then one echo and one filter for all notes.

Templates (Templates button, or `patch_load_template`): start (the empty patch with sound in three wires), subtractive, fm-2op, fm-4op, am-ring, wavefolder, sync-lead, plucked-string, vector-crossfade, sample-loop. Each sounds at -22 dB with Macros 1 to 3 at 50 percent, where the patch is as built. The `build-a-synth` guide walks them one node at a time.

## Pitfalls

- A patch with no Out node, or whose Out is not reached by any source, is silent; nodes that lead nowhere are not compiled and cost nothing.
- Feedback is one block (64 samples) of delay, so a feedback loop through it is a slow comb or echo, not a one-sample resonance; the Delay node's own feedback is sample accurate.
- Global nodes keep running while no note sounds (a global LFO or delay tail); per-voice nodes stop with their voice.
- A node after the Voice sum is global and hears the sum of all notes; its state carries on when you edit the patch.
- Wavetable and sample sources build in the background the first time they are used: an offline render waits for them (machine Busy).
- 16 voices at 8 per lane group: more than 8 notes cost a second pass over every per-voice node.

## Pairs well with

Neo Verb for space, Neo Delay on leads, Neo Ensemble to thicken pads, Neo Filter for sweeps over the sum.

## Checklist

- [ ] Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- [ ] Load a preset, then change one control at a time with `param_set`, play a long note (`notes_write`), then `render` with probes and check `machine_readings`.
- [ ] Start with `patch_load_template` (see `build-a-synth`), read it with `patch_describe`, and change it with `patch_connect`, `patch_add_node`, `patch_set` and `patch_macro_map`; keep it with `patch_save_preset`.
- [ ] Or replace the patch whole with `machine_data_import` and kind patch; the text is one line, "neopatch 1;n 1 Keys ...;w ...".
- [ ] Writes via `param_set` show on the Explain strip with the Agent badge.
