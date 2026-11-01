# Working in ReBuzz: the loop

Build, listen, measure, adjust. Never claim a result you haven't rendered and measured.

## Start of every session
1. `studio_status`: tempo, length, what's in the song, unsaved changes, whether you're headless or in the user's studio.
2. `machine_list` if the song isn't empty, so you know what exists before you add anything.
3. In studio mode the user watches and can undo you. Say what you're about to do in a sentence.

## Build
- **Sound sources:** `gear_list(kind="generator")`, then `machine_add(gear=…, name="Bass")`. Name machines by role ("Kick", "Pad", "Lead"), not by gear. New machines connect to Master unless you say otherwise.
- **Effects:** `machine_add(gear="Neo Verb", name="Room", connect_to=…)`, then `connect`/`disconnect` to route. A send: connect the source to both Master and the effect, set the effect 100% wet.
- **Sound:** `machine_describe(machine)` lists every parameter with its value in units. `param_set` takes values in those units ("800 Hz", "-6 dB", "35%", "Saw"), relative changes (`by: "+3 dB"`) or factors (`times: 2`). Prefer a factory preset's character first and adjust; extreme values are rarely right.
- **Notes:** `pattern_create(machine, length_beats)` then `notes_write(machine, pattern, notes)`. Pitches are MIDI numbers or names (60 = "C4"); times are in beats from the pattern start. Chords are notes with the same start.
- **Arrangement:** `arrangement_place(machine, pattern, at=[0, 16, 32])` puts a pattern on the timeline (beats, or "bar.beat" text: "5.1" is bar 5). `arrangement_get` shows the whole timeline.

## Listen and measure
- `render(from=…, to=…)` renders faster than real time and returns a render id, the WAV file and a loudness/peak summary. Render a short range while you iterate (4–8 bars), the whole song to finish.
- `render(solo=["Bass"])` hears one part without changing the song.
- `analyze(source=<render id>)` gives integrated loudness (LUFS), loudness range, true peak (dBTP) and clipping, each with a plain read.

## Check before you say it's done
- Nothing clips: no clipped samples; true peak ≤ -1 dBTP for a master.
- It isn't silent: if a render is silent, check the pattern is placed (`arrangement_get`), has notes (`notes_read`) and the machine connects to Master (`machine_list`).
- Notes read back as you meant them (`notes_read`).
- Save: `song_save(path=…)`. `.bmxml` keeps everything.

## Undo
Each changing call is one undo step: `undo` reverts your last call whole. In studio mode the user's own edits share the same history, so check `studio_status`'s `next_undo` before undoing.

## Errors
Errors say what to do next ("Did you mean …", the valid range, the call that lists the names). Read them and adjust the call; don't retry the same arguments.
