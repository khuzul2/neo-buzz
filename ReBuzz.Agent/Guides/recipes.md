# Recipes

Short step lists made of real tool calls, each one run headless in a test (`RecipeTests`) and checked for its result, so the calls below are known to work as written. Read the result of every step before the next; an error names the fix. Where a step says `r1`, use the render id the first `render` of the recipe returned.

A recipe that says "Needs" starts from the machines and patterns an earlier recipe makes: run that one first, or have the same names in the song. Names (Bass, Line, Drums) are the recipes' own; use yours.

## Recipes

### hear-a-bass

A sound you can measure: a synth, a preset, two notes, rendered and checked.

1. `song_set(tempo_bpm=120)`
2. `machine_add(gear="Neo Poly", name="Bass")`
3. `preset_load(machine="Bass", preset="Bass — Classic Sub")`
4. `pattern_create(machine="Bass", name="Line", length_beats=4)`
5. `notes_write(machine="Bass", pattern="Line", notes=[{"pitch": "C2", "start": 0, "length": 1}, {"pitch": "G2", "start": 2, "length": 1}])`
6. `arrangement_place(machine="Bass", pattern="Line", at=0)`
7. `render(from=0, to=4)`
8. `analyze(source="r1", metrics=["loudness", "peaks"])`

Result: the render is not silent and the analysis reads a true peak below 0 dBTP.

### movement-from-a-modulator

Needs: hear-a-bass. A slow cutoff sweep from a modulator machine.

1. `machine_add(gear="Neo LFO", name="Wobble", connect_to="none")`
2. `modulate(source="Wobble", target_machine="Bass", param="F1 cutoff", depth=30)`
3. `modulation_list(machine="Bass")`
4. `render(from=0, to=4, probes=["Bass"])`
5. `modulation_remove(source="Wobble", all=true)`

Result: the list shows Bass's `F1 cutoff` under Wobble; after step 5 it shows nothing.

### safe-experiment

Needs: hear-a-bass. Try a change you can take back whole.

1. `snapshot_save(name="before-cutoff")`
2. `param_set(machine="Bass", changes=[{"param": "F1 cutoff", "value": "3 kHz"}])`
3. `snapshot_diff(a="before-cutoff")`
4. `snapshot_restore(name="before-cutoff")`
5. `param_get(machine="Bass", params=["F1 cutoff"])`

Result: the diff names `F1 cutoff`; after the restore the cutoff is back to the preset's (about 400 Hz), not 3 kHz.

### arrange-a-section

Needs: hear-a-bass. Name the sections, repeat the pattern, move the sound across the second one, hear it.

1. `arrangement_markers(set=[{"name": "Intro", "at": 0}, {"name": "Main", "at": "3.1"}])`
2. `arrangement_place(machine="Bass", pattern="Line", at=[4, 8])`
3. `automation_write(machine="Bass", pattern="Line", param="F1 cutoff", points=[{"at": 0, "value": "300 Hz"}, {"at": 4, "value": "3 kHz"}])`
4. `arrangement_get()`
5. `render(section="Main")`

Result: `arrangement_markers` answers with both markers, `arrangement_get` lists three placements of Line; the render of Main is not silent.

### two-scenes

Needs: hear-a-bass. A Session View with an intro scene and a groove scene, launched live.

1. `machine_add(gear="Neo Drums", name="Drums")`
2. `pattern_create(machine="Drums", name="Beat", length_beats=4)`
3. `notes_write(machine="Drums", pattern="Beat", notes=[{"pitch": "C2", "start": 0, "length": 0.25}, {"pitch": "C2", "start": 2, "length": 0.25}])`
4. `session_scene_set(scene=1, name="Intro")`
5. `session_scene_set(scene=2, name="Groove")`
6. `session_clip_set(machine="Bass", scene=1, pattern="Line", quantization="Bar")`
7. `session_clip_set(machine="Drums", scene=2, pattern="Beat", quantization="Bar")`
8. `session_get()`
9. `session_launch(scene=2)`
10. `session_launch(stop=true, immediate=true)`

Result: `session_get` shows Line in scene 1 and Beat in scene 2; the launch answers with what plays or waits; the stop leaves nothing playing.

### controller-for-the-cutoff

Needs: hear-a-bass. A knob on a controller moves the cutoff; the binding is one undo step.

1. `midi_bind(machine="Bass", parameter="F1 cutoff", channel=1, controller=74)`
2. `midi_bindings_list(machine="Bass")`
3. `midi_unbind(machine="Bass", parameter="F1 cutoff")`
4. `midi_bindings_list(machine="Bass")`

Result: the first list has controller 74 on channel 1 for `F1 cutoff`, the second none.

### explain-a-control

A control explained from the machine's own text: what it does, what you will hear, then a change made and read back.

1. `machine_add(gear="Neo Learn FM", name="Teacher")`
2. `preset_load(machine="Teacher", preset="Keys — Glass Bell")`
3. `machine_describe(machine="Teacher")`
4. `param_set(machine="Teacher", changes=[{"param": "FM Depth", "value": "60%"}])`
5. `param_get(machine="Teacher", params=["FM Depth"])`

Result: step 3 gives `FM Depth` (id `fm_index`) a `how` and a `sound` sentence with its unit and range; step 5 reads 60 %.

### engine-swap

One Dye Essence slot changed from the default engine to a struck bar, heard and pitch-checked.

1. `machine_add(gear="Dye Essence", name="Dye")`
2. `param_set(machine="Dye", changes=[{"param": "A engine", "value": "Modal"}, {"param": "A geometry", "value": "Bar"}, {"param": "A exciter", "value": "Struck"}, {"param": "A force", "value": "60%"}])`
3. `pattern_create(machine="Dye", name="Hit", length_beats=4)`
4. `notes_write(machine="Dye", pattern="Hit", notes=[{"pitch": "A3", "start": 0, "length": 2}])`
5. `arrangement_place(machine="Dye", pattern="Hit", at=0)`
6. `render(from=0, to=4, solo=["Dye"])`
7. `analyze(source="r1", metrics=["peaks", "dynamics"])`
8. `param_get(machine="Dye", params=["A engine", "A geometry"])`

Result: the render is not silent, `analyze` reads a struck sound (very dynamic: a loud hit and a long, quiet ring) and `param_get` reads Modal and Bar.

### replace-a-classic

Needs: hear-a-bass. A classic reverb after the bass, moved onto its Neo successor: a dry run first, then the real move with the before and after renders compared, then undone.

1. `machine_add(gear="Pedal Plate", name="Room", insert_after="Bass")`
2. `machine_replace(machine="Room", dry_run=true)`
3. `machine_replace(machine="Room", with="Neo Verb", from=0, to=4)`
4. `machine_list()`
5. `undo()`
6. `machine_list()`

Result: the dry run names Neo Verb and changes nothing; step 3 reports a `comparison` of the two renders against the pair's tolerance (within it or not, and why); step 4 lists Room as Neo Verb; after the undo it is Pedal Plate again.

### check-the-load

Needs: hear-a-bass. Where the time goes: the device, the process, each machine.

1. `audio_status()`
2. `app_status()`
3. `render(from=0, to=4, cpu=true)`
4. `settings_get(section="audio")`
5. `settings_get(section="advanced", search="SubTick")`

Result: the render carries a `cpu` row for Bass in ms per second of audio; `audio_status` reports `dropouts`; the settings list names `audio.AudioThreads` and `advanced.SubTickResolution`.

## Checklist

- [ ] Every step's result was read before the next: a render that comes back silent is a fault to find (`arrangement_get`, `notes_read`), not a result.
- [ ] A recipe's change is backed out when it isn't wanted: `undo`, or `snapshot_restore(name=…)` after `snapshot_save(name=…)`.
- [ ] The claim in the end has a number behind it: `analyze(source=…, metrics=["loudness", "peaks"])` on the render.
