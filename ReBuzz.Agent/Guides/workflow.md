# Working in NeoBuzz Live: the loop

Build, listen, measure, adjust. Never claim a result you haven't rendered and measured.

## Start of every session
1. `studio_status`: tempo, length, what's in the song, unsaved changes, whether you're headless or in the user's studio.
2. `machine_list` if the song isn't empty, so you know what exists before you add anything.
   `song_notes_read` gives the person's notes about the song (ideas, lyrics, to-dos); `song_notes_write(text=…, mode="append")` leaves yours below theirs.
3. In studio mode the user watches and can undo you. Say what you're about to do in a sentence.

## Build
- **Sound sources:** `machine_find(intent="warm bass synth")` ranks the installed machines for what you want (the resource rebuzz://catalog lists them all by category, with what each is for); `gear_list(kind="generator")` lists them plainly. Then `machine_add(gear=…, name="Bass")`. Name machines by role ("Kick", "Pad", "Lead"), not by gear. New machines connect to Master unless you say otherwise.
- **Effects:** `machine_add(gear="Neo Verb", name="Room", connect_to=…)` or `machine_add(gear=…, insert_after="Bass")` for an insert, then `connect`/`disconnect` to route. A send: connect the source to both Master and the effect, set the effect 100% wet.
- **Sound:** start from a preset (`preset_list(machine=…)`, `preset_load(machine=…, preset=…)`): the Neo machines and the curated classic ones (Pedal, Jeskola, FSM, Oomek…) have factory presets named for what they do ("Echo — Dotted Eighth"), ours levelled to peak below −1 dBFS (a machine's own bank, such as Pedal Juno106's, can run hotter: render and check); then `machine_describe(machine)` lists every parameter with its value in units. `param_set` takes values in those units ("800 Hz", "-6 dB", "35%", "Saw"), relative changes (`by: "+3 dB"`) or factors (`times: 2`). Extreme values are rarely right.
- **Notes:** `pattern_create(machine, length_beats)` then `notes_write(machine, pattern, notes)`. Pitches are MIDI numbers or names (60 = "C4"); times are in beats from the pattern start. Chords are notes with the same start. `theory` gives exact chord and scale pitches; `notes_transform` edits notes already written.
- **Arrangement:** `arrangement_place(machine, pattern, at=[0, 16, 32])` puts a pattern on the timeline (beats, or "bar.beat" text: "5.1" is bar 5). `arrangement_markers` names sections; `arrangement_get` shows the whole timeline. `automation_write` moves a parameter inside a pattern.

## Listen and measure
- `render(from=…, to=…)` renders faster than real time and returns a render id, the WAV file and a loudness/peak summary. Render a short range while you iterate (4–8 bars, or `section="Drop"`), the whole song to finish.
- `render(solo=["Bass"])` hears one part without changing the song; `render(probes=["Kick", "Bass"])` captures parts alongside the master, each with its own id ("r3:Bass").
- `analyze(source=<render id>, metrics=[…])` measures loudness, peaks, dynamics, spectrum, stereo, pitch, key and tempo, each with a plain read.
- `analyze_mix(section="Drop")` renders with a probe on every track feeding Master: per-track loudness, peaks and bands, which pairs mask each other, the headroom, and the worst problems first.
- `compare(a="r2", b="r3")`: two renders, probes or WAV files (before and after, or your mix against a reference), with the bands level-matched.
- `plot(kind="spectrum", sources=["r3:Bass", "r3:Kick"])` draws an image you can look at: spectrum, spectrogram, waveform, loudness over time, goniometer, bands.
- Compare versions: `snapshot_save(name=…)` before a change, `snapshot_diff(a=…)` after, `snapshot_restore(name=…)` or `undo` when it measured worse.

## Live, with the user (studio mode)
`studio_status` says the mode. In studio mode the user listens through NeoBuzz Live's audio output, and these tools play with them; live mode is the same with no window (rebuzz-headless playing through a device). Headless, they answer with the way to hear instead, so use render there.
- `transport(action="play", at="9.1")`, `transport(action="stop")`, `transport(action="status")`: the song as it plays, with the session's clips.
- `session_launch(scene=2)` launches a scene on the next bar (or at the clip's quantization). `session_launch(machine="Bass", pattern="Fill")` launches one clip; `session_launch(stop=true)` stops everything.
- `meters_read(machines=["Bass"], window_seconds=2)`: loudness and peaks now, measured as analyze measures renders.
- `midi_play(machine="Keys", notes=[…])` plays a phrase on the next bar for the user to hear.
- `session_record(action="start", machine="Keys", bars=4)` records a take of what is played on the keyboard (the user, or midi_play); `session_record(action="capture")` keeps what armed tracks were just played.
- `machine_readings(machine="Analyzer")` reads an analysis machine where the user put it.

These change what plays, not the song: they aren't undo steps.

## Guides
Read the one for the job with `guide(topic=…)` (or the resource rebuzz://guides/<topic>):

| Topic | When |
|---|---|
| `concepts` | how NeoBuzz Live works: machines, patterns, time, pitch, levels |
| `composition` | harmony, melody, drum grids, arrangement |
| `genres` | forms by genre, moods, and a template per genre |
| `sound-design` | describing a sound in numbers; the Neo effects as sound tools |
| `synth-recipes` | recipes for Neo Poly and Neo Wave |
| `fm-recipes` | recipes for Neo FM6 |
| `mixing` | gain staging, balance, EQ, space, stereo |
| `mixing-dynamics` | compression starting points and sidechain |
| `mastering` | loudness targets, matching a reference, what not to do |
| `mastering-chain` | the chain stage by stage with settings |
| `dye-essence-in-depth`, `dye-essence-effects`, `neo-eq-in-depth` | the parts of those two machines that their pages leave out |
| `app-control` | preferences, the audio device, MIDI bindings, the studio window, export to WAV |
| `troubleshooting` | silence, clipping, phase, CPU, notes that hang or don't play |

Machine manuals: `guide(machine="Neo Poly")` (every parameter, how to use it, presets). The resource rebuzz://reference/tools lists every tool with its inputs and units, generated from the tools themselves, so it is always current. Prompts give a whole plan: compose, design-sound, mix, master, critique.

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
