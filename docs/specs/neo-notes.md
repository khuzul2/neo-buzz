# Neo Notes (NOT1)

Neo Notes is a note effect: notes in, changed notes out. Put it before an instrument (a note insert) or on a free note wire (docs/specs/note-wires.md). It follows Neo Chord (docs/specs/neo-chord.md) for note input and output, the note-off bookkeeping and the catalogue category, and Neo Arp (docs/specs/neo-arp.md) for synced timing and seeded humanize. It has no audio, so Mix and Amount (which belong to effects that process audio) do not apply; it is not a meter either. With every section Off it is an exact pass-through: the same events, on the same samples, in the same order.

Engine: `Machines/NeoNotes/Engine/NotesEngine.cs` (pure C#: one timeline, the pitch rules and the random stream). The scale table is Neo Chord's (`ChordTheory.Scales`, linked into the machine's projects as source, not copied). Machine: `NeoNotes.cs`. Editor: `NeoNotesEditor.cs`. Parameters: `Params.def` (38; Keep chords is appended at 37, so a song saved before takes it Off). Presets: `Presets.txt` (8).

## 1. Sections and their order

Seven sections, each with its own On switch, all Off by default, applied to every note on in this fixed order. A section that is Off does nothing at all.

1. **Transpose**: `Semitones` -48 to +48.
2. **Scale**: `Key` (C to B), `Scale type` (Chromatic, or Neo Chord's nine scales: major, natural, harmonic and melodic minor, dorian, phrygian, lydian, mixolydian, locrian), `Out of scale`: Nearest (the nearest scale note; a tie goes down), Up (the next scale note up), Down, or Filter (the note is dropped). A note already in the scale is never changed.
3. **Spread**: each note is moved by whole octaves. `Range` 0 to 3 octaves, `Pattern`: Up cycle (0, +1 … +range, 0, …), Down cycle (0, -1 … -range, 0, …), Up-down (0, +1 … +range, back to +1, 0, +1, …), Random (uniform from -range to +range, seeded). The pattern moves one step for every note that reaches this section, and starts again after a stop.
4. **Velocity**: `Curve` -100 % (soft) to +100 % (hard), `Out min` and `Out max` (1 to 127), `Random` ±0 to 50 % (seeded), and `Fixed` with its own `Fixed velocity`. The played velocity v maps to y0 = (v - 1) / 126; the curve is y = y0 + c · y0 · (1 - y0) (c is the curve from -1 to +1: 0 and 1 stay put, it is monotone, and 0 is the identity); the output is Out min + y · (Out max - Out min), where Out max is never taken below Out min. With the defaults (curve 0, 1 to 127) every velocity is unchanged. Fixed replaces all of this with `Fixed velocity`; Random then changes the result by up to ±the share; the result is always 1 to 127.
5. **Length**: `Mode`. As played: the note ends when its source does. Fixed: every note lasts one length, whatever the source does: `Fixed timing` Song tempo (`Fixed rate` 1/1 to 1/64, `Fixed feel` straight, dotted or triplet) or Free time (`Fixed time` 5 to 4000 ms). Clamp: `Clamp min` and `Clamp max` in ms; a note shorter than the minimum is held to it, a longer one ends at the maximum. Both are causal: the note off waits until the minimum has passed (it is sent later, never earlier than asked), and the maximum is a timer started with the note on.
6. **Echo**: `Repeats` 0 to 16, `Echo timing` Song tempo (`Echo rate`, `Echo feel`) or Free time (`Echo time` 10 to 2000 ms), `Velocity drop` 0 to 100 % (each repeat has the previous repeat's velocity times one minus the drop; the echo stops when a velocity would round to 0), `Pitch step` -12 to +12 st per repeat (the echo stops at the first repeat outside 0 to 127), `Echo length` 10 to 100 % of the note's length. The repeats are on the grid the note began on (repeat k is k times the time after the note on, rounded to a sample), and carry on after the key is released. A repeat copies the pitch and velocity the note was sent with before Humanize. Its length is `Echo length` of the shorter of the note's length and the time between repeats; if the note is still held when the repeat starts, its length is not known yet, and is at least the time between repeats, so that time is used.
7. **Humanize**: `Humanize time` 0 to 50 ms (a delay for every note, repeats included), `Humanize velocity` ±0 to 30 %, `Seed`, and `Keep chords` (a switch, Off by default; appended last, parameter 37). A note is only ever late, never early, so it stays causal. The note off is delayed by the same time as the note on, so the length is kept.
   * **Keep chords** (NOT1b): with it Off every note has its own delay, so a chord's notes land up to `Humanize time` apart (round 1's Loose Player sounded bad for this). With it On, the notes whose note on arrives on the same sample (the same tick, or the same offset within a buffer; a chord from Neo Chord arrives that way; the window is 0 samples) form a chord. The chord draws one delay, 0 to `Humanize time`, from the seeded stream (its first note's Humanize time draw); each note then adds its own spread of 0 to min(8 ms, `Humanize time`) from one more draw, in note order, so a chord's first and last note on are at most 8 ms apart and a chord is late by at most `Humanize time` + 8 ms. Velocity stays per note. Each note off is delayed by that note's own total delay, so lengths are kept. The repeats of an echo keep chords the same way: repeat k of every note of a chord shares one delay (the first note's draw) and adds its own spread. Off plays bit-identically to the build before it existed.

A pitch pushed outside 0 to 127 by Transpose, Scale (Up and Down at the ends of the keyboard) or Spread is dropped together with its note-off, and the note is not echoed. Nearest never leaves the keyboard.

## 2. Note-offs and stuck notes

Everything the machine sends goes through one timeline and every note on has its note off:

* The parameters are read when the note on arrives, and the pitch actually sent is kept with the note, so changing a parameter while a note is held never changes how that note ends. The note off goes to the pitch actually sent.
* As played and Clamp: the note off is queued when the source's note off arrives (Clamp also starts its maximum timer with the note on). Fixed: the note off is queued with the note on. Every echo repeat has both queued when it starts.
* Pitches are counted per channel, as Neo Chord counts them: a voice that starts adds one, one that ends takes one, and the note off goes out when the count reaches 0. So an echo over its own note, or a note delayed by Humanize past the end of the one before it on the same pitch, never leaves a note hanging. Unlike Neo Chord, a voice that starts on a pitch that is already sounding strikes it again (a note off and a note on on the same sample): an echo repeat or a late note must be heard.
* A note on for a key that is still held ends the old note first, on the same sample.
* Transport stop, the Stop command, and controllers 123 and 120 send every sounding pitch's note off at once, cancel every echo and delayed note still to come, and forget every held note. Controller 123 or 120 does this on its own sample, before the controller is passed on. A stop also starts the Spread pattern again.
* The notes that are held when the song stops and the notes still to come are never left sounding: the tests below check it.

Controllers, pitch bend and channel pressure pass through in order with the notes, on their sample.

## 3. Timing

Everything is in absolute sample time (a long), so a delay, a fixed length or an echo runs across block boundaries at any block size (1, 64, 512 and others) and lands on the same sample at every size. Song-tempo values are the note value in beats times the samples in a beat at the tempo when the note began (read from the host each block); a free time is its milliseconds at the sample rate, rounded to a sample. The output is in non-decreasing offset order (the wire's rule). With every section Off a note passes on the sample it came in at.

A note wire holds 1024 events a block. If more are due, the engine sends 1023 or 1024 and leaves the rest on the timeline for the next block, in order, at offset 0; nothing is dropped. The timeline holds 32768 events with a reserve of 4096 kept for note offs (a note on, a repeat or a controller is refused first, counted in `Dropped`, never a note off), and the input waits in 4096 places with a reserve of 256 for note offs (the note queue's reserve of docs/specs/notes.md G5). Up to 2048 notes (every key on every channel) can be held at once.

## 4. The random stream

One xorshift32 stream, started from `Seed` (state = 0x9E3779B9 · (seed + 1) + 0x7F4A7C15, then four draws are discarded) and started again when `Seed` changes and on a render or a stop. Every note on takes exactly four draws, in this order, whichever sections are On (so switching one section never moves another's numbers): Spread (Random), Velocity (Random), Humanize time, Humanize velocity. Every echo repeat takes two: Humanize time and Humanize velocity. With Keep chords On a note takes one more draw after those four (its own spread of 0 to 8 ms), and a repeat one more after its two; with it Off nothing changes. The same seed and the same notes give the same events, at any block size.

## 5. In the studio

* **Drop to insert**: its manifest says `note_in` and `note_out`, so dropped on an instrument track it goes into the track's note chain (docs/specs/neo-chord.md §4).
* The editor: a strip of the whole keyboard on top (the notes held on the input above, the notes sent below), then the seven sections in the order they work, each with its On switch first.

## 6. Gates and tests

`NeoBuzz.MachineTests/NeoNotesTests.cs` (with `NeoNotesRig.cs`, the harness, and the phrase), `NeoNotesPack.cs` (the listening pack and its click and repeat check):

* **All sections Off**: 4000 random well-formed notes with controllers, bend and pressure on three channels give event-identical output (same sample, same order) at block sizes 1, 64 and 512; so do all sections On at neutral settings.
* **Fuzz**: 10,000 random notes (pitches 0 to 127) with every section on and every parameter automated at random between blocks, at block sizes 1, 64 and 512, released and also stopped while held: 0 notes left hanging, no pitch started or ended twice, every velocity 1 to 127 and every pitch 0 to 127, at most 1024 events a block, offsets in order.
* **Scale**: every key, every scale and the modes Nearest, Up, Down and Filter on all 128 pitches against a brute-force oracle; every output pitch is in the scale; the scale table is checked against scales written from step patterns.
* **Timing**: echo (song tempo and ms, a held note and a short one), Fixed length (song tempo and ms), Clamp and Humanize (its delays computed from the stream) land on the computed sample, ±0, at block sizes 1, 64 and 512.
* **Determinism**: the same seed gives the same events (at any block size), another seed gives others, and the pack renders bit-identical twice.
* **Load**: an echo of 16 repeats on a 10-note chord (10 ms apart, 170 notes on and 170 off) drops nothing and stays out of the timeline's reserve; 2048 notes on one sample go out over two blocks, in order.
* **Allocation**: 0 bytes a block over 2000 blocks with dense input and every section on.
* **Keep chords** (`NeoNotesKeepChordsTests.cs`): 1,000 seeded 4-note chords at Humanize time 40 ms have a first-to-last note on gap of at most 8 ms (measured 7.94 ms) and a shared delay from 0.25 to 44.27 ms, spread evenly over eight 5 ms bins; the same at block sizes 1, 64 and 512; every note off is as late as its own note on; echo repeats of a chord stay within 8 ms; with it Off every preset and four busy inputs are bit-identical to the build before (golden hashes of be1648d1c); a state saved before has 37 values and loads with Keep chords Off, playing identically; a 10,000-event stress (overlapping chords, Echo and Clamp length, Humanize 50 ms) released and stopped leaves nothing sounding; 0 bytes a block.
* Spread patterns, velocity curve, range, fixed and random, notes outside the keyboard, a stop with echoes still to come, controller 123, retriggered keys, channels counted apart, a late humanized note over an early note off, the input's reserve for note offs, the presets (8, distinct, each doing what its name says by a clear margin on the pack's phrase).

## 7. Limits and decisions

* The Neo synths apply the events of a block at its start, so a delay or an echo repeat is exact on the wire and is heard to block resolution on a Neo synth (note-wires.md §11).
* The echo's Feedback is a loss: `Velocity drop` is how much quieter each repeat is (0 % repeats as loud as the note, 100 % repeats nothing).
* The nearest scale note in a tie goes down; Random Spread covers minus to plus the range, the cycles one direction.
* A very dense input with long echoes can use all 2048 sources; the note on is then refused and counted, and its note off is ignored. Nothing sticks.
