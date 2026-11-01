# Working in ReBuzz: the loop

Build, listen, measure, adjust. Never claim a result you haven't rendered and measured.

## Start of every session
1. `studio_status`: tempo, length, what's in the song, unsaved changes, whether you're headless or in the user's studio.
2. `machine_list` if the song isn't empty, so you know what exists before you add anything.
3. In studio mode the user watches and can undo you. Say what you're about to do in a sentence.

## Build
- **Sound sources:** `gear_list(kind="generator")`, then `machine_add(gear=…, name="Bass")`. Name machines by role ("Kick", "Pad", "Lead"), not by gear. New machines connect to Master unless you say otherwise.
- **Effects:** `machine_add(gear="Neo Verb", name="Room", connect_to=…)` or `machine_add(gear=…, insert_after="Bass")` for an insert, then `connect`/`disconnect` to route. A send: connect the source to both Master and the effect, set the effect 100% wet.
- **Sound:** start from a preset (`preset_list(machine=…)`, `preset_load(machine=…, preset=…)`), then `machine_describe(machine)` lists every parameter with its value in units. `param_set` takes values in those units ("800 Hz", "-6 dB", "35%", "Saw"), relative changes (`by: "+3 dB"`) or factors (`times: 2`). Extreme values are rarely right.
- **Notes:** `pattern_create(machine, length_beats)` then `notes_write(machine, pattern, notes)`. Pitches are MIDI numbers or names (60 = "C4"); times are in beats from the pattern start. Chords are notes with the same start. `theory` gives exact chord and scale pitches; `notes_transform` edits notes already written.
- **Arrangement:** `arrangement_place(machine, pattern, at=[0, 16, 32])` puts a pattern on the timeline (beats, or "bar.beat" text: "5.1" is bar 5). `arrangement_markers` names sections; `arrangement_get` shows the whole timeline. `automation_write` moves a parameter inside a pattern.

## Listen and measure
- `render(from=…, to=…)` renders faster than real time and returns a render id, the WAV file and a loudness/peak summary. Render a short range while you iterate (4–8 bars, or `section="Drop"`), the whole song to finish.
- `render(solo=["Bass"])` hears one part without changing the song; `render(probes=["Kick", "Bass"])` captures parts alongside the master, each with its own id ("r3:Bass").
- `analyze(source=<render id>, metrics=[…])` measures loudness, peaks, dynamics, spectrum, stereo, pitch, key and tempo, each with a plain read.
- Compare versions: `snapshot_save(name=…)` before a change, `snapshot_diff(a=…)` after, `snapshot_restore(name=…)` or `undo` when it measured worse.

## Guides
Read the one for the job with `guide(topic=…)` (or the resource rebuzz://guides/<topic>):

| Topic | When |
|---|---|
| `concepts` | how ReBuzz works: machines, patterns, time, pitch, levels |
| `composition` | forms, harmony, melody, drum grids, arrangement, genre templates |
| `sound-design` | describing a sound in numbers; recipes for the Neo synths and effects |
| `mixing` | gain staging, balance, EQ, compression, sidechain, space, stereo |
| `mastering` | loudness targets and the mastering chain |
| `troubleshooting` | silence, clipping, phase, CPU, notes that hang or don't play |

Machine manuals: `guide(machine="Neo Poly")` (every parameter, how to use it, presets). Prompts give a whole plan: compose, design-sound, mix, master, critique.

## Undo
Each changing call is one undo step: `undo` reverts your last call whole. In studio mode the user's own edits share the same history, so check `studio_status`'s `next_undo` before undoing.

## Errors
Errors say what to do next ("Did you mean …", the valid range, the call that lists the names). Read them and adjust the call; don't retry the same arguments.

## Checklist
- [ ] You know the starting state: `studio_status`, and `machine_list` when the song isn't empty.
- [ ] It isn't silent: `render` doesn't read silent; if it does, check placements (`arrangement_get`), notes (`notes_read`) and the route to Master (`machine_list`).
- [ ] Nothing clips: `analyze(source=…, metrics=["peaks"])` shows no clipped samples; a master's true peak is -1 dBTP or lower.
- [ ] Notes read back as you meant them: `notes_read`.
- [ ] Every claim has a number behind it: `analyze` on the render you describe.
- [ ] Saved: `song_save(path=…)`; `.bmxml` keeps everything.
