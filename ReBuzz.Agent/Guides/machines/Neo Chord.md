## How it sounds

Neo Chord makes no sound of its own: it takes notes and sends chords. Put it before an instrument as a note insert (`note_chain(instrument="Lead", inserts=["Chord"])`) or on a note wire (`note_wire_connect(source="Chord", target="Lead")`, with the notes coming in on a wire of their own), and every note that reaches it plays as a chord on the instrument.

- Chord: `Chord` picks the shape (Off passes the note through unchanged), from Major, Minor, Power, Sus2, Sus4, Diminished, Augmented, sixths, sevenths, ninths and Add 9 to Stacked fourths, or Custom (up to four intervals, `Custom 1` to `Custom 4`, -24 to +24 semitones, written "+4 st", or Off). `Mode` Fixed puts the same shape on every note. Diatonic builds the chord from the scale degree played, in `Key` and `Scale` (the nine scales and modes), as triads or sevenths (`Diatonic chord`): in C major the white keys give C, Dm, Em, F, G, Am and the diminished chord on B. A note outside the scale plays the chord of the scale note just below it.
- Voicing: `Inversion` (0 to 3) moves the lowest notes up an octave, `Spread` is Close, Open, Drop 2 or Wide, `Octave shift` moves the whole chord, `Bass note` adds the root one or two octaves below, `Voices` limits the chord to 2 to 7 notes, dropping the highest first.
- Velocity: `Top voice` and `Bass voice` scale the velocity of the highest and lowest notes (percent of the velocity played), `Velocity spread` tilts the velocity from the bottom to the top of the chord.
- Strum: `Strum` Off plays the notes together; Up starts at the lowest note, Down at the highest, Alternate goes up on the first chord, down on the next and so on (one step for every new chord, not for every beat), Random uses a seeded shuffle. `Strum time` (0 to 250 ms) is the time from the first note to the last, `Strum curve` makes the notes come faster (plus) or slower (minus). `Humanize time` (0 to 30 ms) delays each note by a random amount, `Humanize velocity` varies it, and `Seed` fixes the random stream so a render repeats exactly.
- Every note it sends ends when the note that made it ends. A strummed note that has not started when the key is released never plays. If two chords share a note, it keeps sounding until the last of them ends and is struck once. The transport stopping, Stop and an all-notes-off message release everything. Changing a setting while a note is held changes the next chord, not the one sounding.

## Use it for

- Playing full chords from one key, strumming a pad or a pluck, keeping a whole clip in a key, and shaping the velocity and voicing of every chord with one machine.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Chord | Major 7 | the shape on every note; Off bypasses it |
| Mode | Diatonic | the chord follows the scale degree played |
| Key | C | the key's first note (with `Scale` Major) |
| Spread | Open | Close is dense; Open and Drop 2 are airy |
| Bass note | Root 1 oct down | the chord's root under the chord |
| Strum | Up | the order the notes start in |
| Strum time | 80 ms | the whole strum; 250 ms is a slow roll |
| Humanize time | 6 ms | a little looseness per note |

## Starting points

- preset="Chords — Major Triads", preset="Chords — Minor Sevenths", preset="Chords — Open Fifths", preset="Diatonic — Sevenths in C".
- preset="Strum — Slow Strum Up", preset="Strum — Down-Up Strum", preset="Voicing — Stacked Fourths", preset="Voicing — Wide Pad Voicing".
- Single notes in, a pad out: a Neo Poly pad with `Chord` Major 9, `Spread` Wide and `Strum` Up at 90 ms.

## Pitfalls

- It takes notes only: it does nothing if nothing sends it notes. As a note insert the track's clip notes and live MIDI go in; on a wire, the sender must be wired to it.
- It sits before the instrument: the instrument's own settings (its voice limit) still apply to the chord, so a 4-voice patch drops the fifth note of a ninth chord.
- The Neo synths apply the notes of a block at its start, so a strum is exact on the wire and as fine as the audio block size at the synth (usually 1 to 5 ms), which is far below the shortest strum step.
- Chord Off sends every note and velocity as played and ignores the voicing, velocity and strum settings.
- `Custom` takes up to four intervals above the played note, so a Custom chord has at most five notes; a value of Off leaves that interval out.
- A note that would fall outside the keyboard (0 to 127) is left out of the chord.
- Numbers for a choice are positions: write the text ("+7 st", "Minor 7"), not a number.

## Pairs well with

- Neo Poly, Neo Organ or Neo Wave after it for the sound.

## Checklist

- [ ] The chord is wired in: `note_wires` lists the insert or the wire.
- [ ] `param_set(machine="Chord", changes=[{"param": "Chord", "value": "Minor 7"}, {"param": "Strum", "value": "Up"}])`, then `midi_play` on the track and `render(probes=["Lead"])`: the instrument sounds the chord, not the single note.
- [ ] `machine_describe(machine="Chord", group="Strum")` lists the strum controls with their units.
