# NeoBuzz Live in five minutes

## Machines and the graph
- A song is a graph of **machines**. **Generators** make sound (synths, samplers, drum machines). **Effects** process the sound fed into them. **Master** is the output; whatever reaches it is heard.
- **Connections** carry audio from one machine's output to another's input, each with its own gain (dB) and pan. A machine can feed several others (sends) and take several inputs (they mix). Some effects have a second input, such as Neo Comp's "Sidechain" (`connect(…, input="Sidechain")`).
- Machine names are unique in a song; tools address machines by name. Gear is the machine type ("Neo Poly"); a song can hold many machines of one gear.

## Parameters
- **Global parameters** shape the whole machine (cutoff, decay, mix). **Track parameters** belong to each of the machine's tracks (Note and Velocity on Neo synths: one track per voice played from a pattern). Tools take `track` for those.
- Values are written in the parameter's unit. Neo machines publish exact units; older machines are read through the text they show for each value.

## Patterns, the arrangement and the session grid
- A **pattern** belongs to one machine and holds its notes and automation (a parameter's movement, played whenever the pattern plays). Its length is in beats.
- The **arrangement** (sequence) places patterns on a timeline, one lane per machine. A pattern placed at beat 16 plays from there until it ends or the lane's next pattern starts. The song plays from 0 to its end; the loop range is what plays on repeat. **Markers** name its sections ("Verse", "Drop").
- The **Session View** grid launches patterns as clips, in time, a scene (a row) or a clip at a time: `session_get`, `session_clip_set`, `session_scene_set`. Renders play the arrangement, so a finished song lives there.

## Time
- Tools speak **beats** from the song start (0 = the start), or "bar.beat" text ("17.1" = bar 17, beat 1; bars and beats count from 1). Inside a pattern, times count from the pattern's start.
- Ticks per beat (TPB, usually 4) is the pattern grid: 4 means 16th notes. Notes can sit between ticks.
- Tempo is in BPM, whole numbers.

## Pitch
- MIDI numbers: 60 = C4 (middle C), 69 = A4 = 440 Hz. Names with sharps or flats: "F#3", "Bb2".
- The Buzz pattern editor shows the same notes an octave higher ("C-5" for C4). The tools always use MIDI and scientific names; "C-5" style is refused so it can't be misread.

## Levels
- Gains in dB: 0 dB leaves a level as is, -6 dB halves the amplitude, "-inf" is silent. A connection goes up to +12 dB.
- Pan from -1 (left) through 0 (center) to +1 (right).
- Loudness in LUFS (integrated over the render), peaks in dBTP (true peak) and dBFS (sample peak). Streaming masters sit near -14 LUFS with true peaks at or below -1 dBTP.

## Headless and studio
- **Headless** (`NeoBuzzLive.exe --agent`, or `rebuzz-headless --agent`): no window and no audio device. You hear only through `render`.
- **Studio**: the user's NeoBuzz Live with its window open; they see every change and share the undo history. Rendering pauses their audio output for its duration.

## Checklist
- [ ] You know the tempo, beats per bar and ticks per beat: `studio_status`.
- [ ] You know the machines and how they connect: `machine_list`.
- [ ] Parameter names and units come from the machine, not from memory: `machine_describe(machine=…)`.
- [ ] Positions are beats from the start (or "bar.beat"): `arrangement_get` shows them both ways.
- [ ] Pitches are MIDI numbers or scientific names: `notes_read` returns both.
