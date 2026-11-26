# Neo Chord (CHD1)

Neo Chord turns every note it gets into a chord. It is the first shipped machine that both takes and sends notes (docs/specs/note-wires.md): put it before an instrument (a note insert) or on a free note wire. It has no audio, so Mix and Amount (which belong to effects that process audio) do not apply; it is not a meter either, and its Chord Off setting is an exact pass-through (same sample, same velocity).

Engine: `Machines/NeoChord/Engine/` (pure C#: `ChordTheory.cs` the tables and the builder, `ChordEngine.cs` the event processing). Machine: `NeoChord.cs`. Editor: `NeoChordEditor.cs` (one source for the studio and the classic app). Parameters: `Params.def` (23, state version 0). Presets: `Presets.txt` (8).

## 1. What it does

* **Chord**: Off, Major, Minor, Power, Sus2, Sus4, Diminished, Augmented, Major 6, Minor 6, Dominant 7, Major 7, Minor 7, Minor 7 flat 5, Diminished 7, Add 9, Minor add 9, Dominant 9, Major 9, Minor 9, Stacked fourths, Custom (four intervals, -24 to +24 semitones or Off). The chord is built on the note played.
* **Mode**: Fixed (above) or Diatonic: Key, one of nine scales (major, natural, harmonic and melodic minor, dorian, phrygian, lydian, mixolydian, locrian), triads or sevenths. The note played picks the scale degree; a note outside the scale takes the scale note just below it.
* **Voicing**: Inversion 0 to 3, Spread (Close, Open, Drop 2, Wide), Octave -2 to +2, Bass (Off, the root one or two octaves down), Voices 2 to 7 (the highest go first). Notes outside 0 to 127 are left out; the same pitch twice is one note.
* **Velocity**: the top voice and the bass voice scale the velocity (0 to 200 %), and Spread tilts it across the chord.
* **Strum**: Off, Up, Down, Alternate, Random; time 0 to 250 ms for the whole chord, a curve (-100 to +100 %), humanize in time (0 to 30 ms) and velocity (0 to 30 %), seeded (Seed 0 to 99): the same notes give the same result every time.

## 2. Note-offs and stuck notes

Every note sent ends when the note it came from ends. Each pitch has a reference count per channel, so two chords that share a pitch do not retrigger it and it ends when the last one ends. A note-off for a chord still strumming cancels the voices that have not started (they are never sent). Transport stop, the Stop command, controller 123 and 120 send all offs. Changing a parameter while a note is held never changes how that note ends (the chord is remembered per source note).

## 3. Timing

Everything is in absolute sample time, so a strum runs across block boundaries at any block size (16, 64, 256 and others), and Off passes the event on the sample it came in at. Voices due in a block are sent earliest first, before the event that is being handled, and the output is in non-decreasing offset order (the wire's rule). A wire holds 1024 events a block; a strummed chord of seven voices on a very dense input could in theory overflow it, and the engine drops the newest voices in that case (counted, never a stuck note).

## 4. In the studio

* **Drop to insert**: a machine whose manifest says `note_in` and `note_out` (read at drop time, before the machine exists) dropped on an instrument track goes into the track's note chain, last, before the instrument, as one undo step (`DropAction.InsertNote`, `DropRouter`). Audio drops are unchanged.
* **Tracks mode** draws a note insert left of its instrument in chain order (note-wires.md §11).
* **machine_list** shows `note_in`, `note_out` and `note_insert_of`.
* The editor: chord and mode on top, a three-octave keyboard that lights the last chord sent, then Voicing, Velocity and Strum.

## 5. Gates and tests

`NeoBuzz.MachineTests/NeoChordTests.cs`: theory (every chord, nine scales in twelve keys, snapping, inversion and spread as exact arrays), no stuck notes (10,000 events at three block sizes against an oracle), timing, strum shapes and humanize determinism, the allocation gate (0 bytes a block over 2000 blocks with 64 note-ons a block), presets (distinct, audible through Neo Poly), state. `ReBuzz.Studio.Tests`: the drop (one undo step, undo restores the track), the chain and free wire in the real engine, machine_list.

## 6. Limits

The Neo synths apply the events of a block at its start, so a strum's offsets inside a block are exact on the wire but are heard to block resolution on a Neo synth (note-wires.md §11). A very long strum on a very fast input keeps many voices pending; the voice slots (256 held source notes) bound it.
