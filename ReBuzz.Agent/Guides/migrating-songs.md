# Migrating old songs to Neo

A song that arrives with classic machines (Jeskola, FSM, Pedal, Oomek, Rout and others) plays as it always did. Moving it onto the Neo successors is optional, and always a choice for the person: the successors are lighter, safer and understood by every tool, but they sound like themselves, not like the originals. `machine_replace` does the move for the pairs that have a mapping, measures what changed and can be undone whole. For the rest you build the sound by ear.

## Find what can move

1. `machine_list` shows what the song holds. `gear_list(search="Jeskola")` rows carry `successor` ("Neo Verb (machine_replace)") for machines that have one; `machine_describe` on a machine names its successors too.
2. A machine's manual (`guide(machine="Pedal Plate")`) says which pair applies and what carries over, or that there is no pair and which Neo machine to build the sound on.

## The pairs

| Neo successor | Machines that move onto it |
|---|---|
| Neo Poly | Jeskola Bass, Jeskola Bass 2, Jeskola Bass 3 (a mono legato bass) |
| Neo Drums | FSM KickXP, Rout 909 |
| Neo Sampler | Pedal M1 |
| Neo Utility | Jeskola Gain, Pedal Gain |
| Neo Filter | FSM WahMan, Jeskola Filter, Jeskola Filter 2, Pedal Filter |
| Neo EQ | Jeskola EQ-3, Joachims Hyperion, Pedal EQ |
| Neo Comp | Pedal Comp |
| Neo Gate | Pedal Gate |
| Neo Drive | Jeskola Distortion |
| Neo Exciter | Oomek Exciter |
| Neo Ensemble | FSM Chorus2, Jeskola Chorus, Jeskola Flanger, Pedal Chorus |
| Neo Delay | FSM PanzerDelay, Jeskola Cross Delay, Jeskola Delay, Jeskola NiNjA dElaY |
| Neo Verb | Jeskola Reverb, Pedal Plate |
| Neo LFO | Pedal LFO (the wave and phase only; its targets are set again with `modulate`) |

Anything not in the table has no mapping (Pedal FM and Pedal SH101, for example): its manual names the Neo synth to build the sound on.

## Move one machine

1. Look first: `machine_replace(machine="Room", dry_run=true)` reports the settings the successor gets, where each came from, the tolerance it will be held to, and what has no counterpart. It changes nothing.
2. Move it: `machine_replace(machine="Room", with="Neo Verb")` (`with` is only needed when there are several successors). The successor takes the old machine's place: name, connections, position. Every parameter goes through real units (a level in percent to dB, a table to Hz), the patterns' columns and notes move (ramps kept; tracker notes as notes, slides as glides), and the arrangement and the Session View follow.
3. It then renders the song before and after (the first 60 s, or `from`, `to` or `section`) and compares them against the pair's tolerance: loudness within a number of dB and each third octave within a number of dB, level-matched. The result's `comparison` says whether it is within and why not. The bass and drum pairs set no band limit, so they are compared on loudness.
4. A snapshot is taken first ("before replacing …"), and the move is one undo step: `undo` brings the original back exactly.

`keep_old=true` leaves the old machine muted beside the new one for an A/B; `name` names the successor; `compare=false` skips the renders.

Read the result's `notes`: they say what could not be reached or has no counterpart on the Neo machine. A comparison "not compared" means nothing played through the machine in the range: render a part where it plays (`from`, `to`) and compare again.

## A whole song

1. `snapshot_save(name="before migration")` and `render` the song; keep the render id as the reference.
2. Go in signal order, sources first and the master chain last. One `machine_replace` at a time, reading each `comparison`.
3. After each move that changed the sound beyond its tolerance, decide with the person: keep it, retune the successor with `param_set`, or `undo`.
4. At the end `render` again and `compare(a=<reference>, b=<new render>)`: the bands are level-matched, so a changed tone shows even when the loudness agrees. Check the master's true peak (`analyze`, peaks) has not moved.
5. `song_save` under a new name; the original file stays as it was.

## When there is no pair

- Build the sound on the Neo machine the manual names, by ear against the original: `render(solo=[…])` the old machine, then the new one, and `compare`.
- The notes still have to move: `notes_read` on the old machine's pattern, `notes_write` on the new one (`apply_pitch_offset` on both takes care of a generator's own pitch convention). Then `arrangement_place` the new pattern where the old one was.
- Leave the old machine in the song, muted, until the person has heard the new one.

## Do not

- Do not replace anything without telling the person; a successor changes the sound.
- Do not migrate a song that is playing live in front of an audience.
- Do not trust a "within tolerance" for a machine used as a creative effect (a long delay, a flanger): listen.

## Checklist

- [ ] You know the song before you change it: `machine_list`, and a reference `render` kept.
- [ ] Each move was looked at first: `machine_replace(machine=…, dry_run=true)`.
- [ ] Each `machine_replace` result's `comparison` and `notes` were read, and what is outside its tolerance was decided with the person.
- [ ] A way back exists: `snapshot_save(name=…)` before, `undo` per move.
- [ ] The whole song measures as before: `compare(a=…, b=…)` of the two renders, and `analyze(source=…, metrics=["peaks"])` on the new one.
