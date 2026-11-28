# NeoBuzz Live in five minutes

## Machines and the graph
- A song is a graph of **machines**. **Generators** make sound (synths, samplers, drum machines). **Effects** process the sound fed into them. **Master** is the output; whatever reaches it is heard.
- **Connections** carry audio from one machine's output to another's input, each with its own gain (dB) and pan. A machine can feed several others (sends) and take several inputs (they mix). Some effects have a second input, such as Neo Comp's "Sidechain" (`connect(…, input="Sidechain")`). A machine with several outputs lists them in `machine_list`; `connect(…, from_output="High")` wires one (number or name, default the first).
- **Control machines** (Neo LFO, Neo Envelope, Neo Step, Neo Macro) have no audio: they move other machines' parameters (`modulate`) and are added unconnected (`connect_to="none"`). **Neo Patch** is a synth built from nodes you wire (`patch_*` tools); **Neo Learn** machines are small synths with twelve controls, made for learning; **Dye Essence** has three engine slots, each one of seven engines.
- Machine names are unique in a song; tools address machines by name. Gear is the machine type ("Neo Poly"); a song can hold many machines of one gear.

## Parameters
- **Global parameters** shape the whole machine (cutoff, decay, mix). **Track parameters** belong to each of the machine's tracks (Note and Velocity on Neo synths: one track per voice played from a clip). Tools take `track` for those.
- Values are written in the parameter's unit. Neo machines publish exact units. Neo and Dye Essence controls also carry a `how` and a `sound` sentence (`machine_describe`): how the control changes the sound and what you will hear.
- Every Neo effect has **Amount** (0-200 %, default 100 %) and **Mix** (0-100 %, default 100 %), the last two parameters in its list. Amount is how far the effect moves the sound from neutral: 100 % is the settings as they are, 0 % is bit-exact dry (the input delayed by the machine's latency), 200 % exaggerates but stays stable; each machine's guide says what it scales. Mix blends the dry (delayed by the same latency, so no comb) against the wet. Use Amount to turn a preset up or down, Mix for parallel processing; neither changes a saved song at its defaults. Neo Tune's correction strength is `Correction`; Neo Plug FX has Mix only.

## Clips, the arrangement and the session grid
- A **clip** belongs to one machine and holds its notes and automation (a parameter's movement, played whenever the clip plays). Its length is in beats.
- The **arrangement** (sequence) places clips on a timeline, one lane per machine. A clip placed at beat 16 plays from there until it ends or the lane's next clip starts. The song plays from 0 to its end; the loop range is what plays on repeat. **Markers** name its sections ("Verse", "Drop").
- The **Session View** grid launches clips in time, a scene (a row) or a clip at a time: `session_get`, `session_clip_set`, `session_scene_set`; audio clips (`audio_import`, `audio_clip_set`, `audio_clip_get`) live on Neo Audio tracks. Renders play the arrangement, so a finished song lives there.

## Undo, history and snapshots
- Every changing call is one undo step (`undo`, `history_list`); `snapshot_save` keeps the whole song under a name and `snapshot_restore` puts it back, glitch-free while it plays. The user's edits share the same history.

## Time
- Tools speak **beats** from the song start (0 = the start), or "bar.beat" text ("17.1" = bar 17, beat 1; bars and beats count from 1). Inside a clip, times count from the clip's start.
- Ticks per beat (TPB, usually 4) is the clip grid: 4 means 16th notes. Notes can sit between ticks.
- Tempo is in BPM, whole numbers.

## Pitch
- MIDI numbers: 60 = C4 (middle C), 69 = A4 = 440 Hz. Names with sharps or flats: "F#3", "Bb2".
- The clip editor, the piano roll and the keyboards name notes as the user's Preferences ▸ General ▸ Note names says: C3 = 60 by default (the clip editor shows "C-3" for C4), scientific ("C-4"), or Buzz's own ("C-5"). The tools always use MIDI and scientific names; "C-5" style is refused so it can't be misread. When you tell the user a note, give the MIDI number or say which naming you mean.

## Levels
- Gains in dB: 0 dB leaves a level as is, -6 dB halves the amplitude, "-inf" is silent. A connection goes up to +12 dB.
- Pan from -1 (left) through 0 (center) to +1 (right).
- Loudness in LUFS (integrated over the render), peaks in dBTP (true peak) and dBFS (sample peak). Streaming masters sit near -14 LUFS with true peaks at or below -1 dBTP.

## Headless and studio
- **Headless** (`NeoBuzzLive.exe --agent`, or `rebuzz-headless --agent`): no window and no audio device. You hear only through `render`.
- **Studio**: the user's NeoBuzz Live with its window open; they see every change and share the undo history. Rendering pauses their audio output for its duration. The live tools (`transport`, `session_launch`, `meters_read`) play with them: `guide(topic="live-performance")`; if the sound drops out, `guide(topic="performance")`.

## Checklist
- [ ] You know the tempo, beats per bar and ticks per beat: `studio_status`.
- [ ] You know the machines and how they connect: `machine_list`.
- [ ] Parameter names and units come from the machine, not from memory: `machine_describe(machine=…)`.
- [ ] Positions are beats from the start (or "bar.beat"): `arrangement_get` shows them both ways.
- [ ] Pitches are MIDI numbers or scientific names: `notes_read` returns both.
