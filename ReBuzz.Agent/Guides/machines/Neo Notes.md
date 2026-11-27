## How it sounds

Neo Notes makes no sound of its own: it takes notes and sends changed notes. Put it before an instrument as a note insert (`note_chain(instrument="Lead", inserts=["Notes"])`) or on a note wire (`note_wire_connect(source="Notes", target="Lead")`, with the notes coming in on a wire of their own), and the instrument plays what comes out. It has seven sections, each with its own On switch (a parameter named for the section: `Transpose`, `Scale`, `Spread`, `Velocity`, `Length`, `Echo`, `Humanize`), all off at first, so it passes every note through untouched until you switch one on. They work in that order.

- Transpose: `Semitones` moves every note ±48. A note that leaves the keyboard (0 to 127) is dropped, with its note-off.
- Scale: `Key` and `Scale type` (Chromatic or one of nine scales); `Out of scale` says what a note outside the scale does: Nearest scale note (a tie goes down), Up to the next, Down to the next, or Filter (the note is dropped).
- Spread: `Range` (0 to 3 octaves) and `Pattern`: Up cycle, Down cycle, Up-down, or Random (from minus to plus the range, seeded). Each note moves by whole octaves, one step on from the note before.
- Velocity: `Curve` (-100 % soft to +100 % hard), `Out min` and `Out max` (the range the curve maps onto), `Random` (± up to 50 %), and `Fixed` with `Fixed velocity` (every note the same; the curve and range are skipped).
- Length: `Mode` As played, Fixed or Clamp. Fixed lasts `Fixed timing` Song tempo (`Fixed rate` 1/1 to 1/64 with `Fixed feel` straight, dotted or triplet) or Free time (`Fixed time`, 5 to 4000 ms). Clamp holds a short note to `Clamp min` and ends a long one at `Clamp max` (ms).
- Echo: `Repeats` (0 to 16) at `Echo timing` Song tempo (`Echo rate`, `Echo feel`) or Free time (`Echo time` 10 to 2000 ms). Each repeat loses `Velocity drop` of its velocity (the echo stops when it would fall below 1), moves by `Pitch step` (-12 to +12 st; one that leaves the keyboard ends the echo) and lasts `Echo length` of the note's length (never more than the time between repeats). The echo goes on after the key is released.
- Humanize: `Humanize time` (0 to 50 ms) only ever delays a note, and its note-off with it, so lengths are kept; `Humanize velocity` varies the velocity; `Seed` fixes the random stream (Random spread, Random velocity, Humanize), so a render repeats exactly.
- Every note it sends ends: when its source ends, by its length, or when the song stops (the transport stopping, Stop and an all-notes-off message release everything and cancel echoes still to come). Changing a setting while a note is held never changes how that note ends. A note sent while its pitch is still sounding is struck again.

## Use it for

- Keeping a part in key, turning a major line minor, scattering a line across octaves, flattening or sharpening the dynamics of a part, staccato or legato whatever was played, tempo-synced echoes with rising or falling pitch, and loosening a machine-made part.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Scale | On | the section switch; with `Key` C and `Scale type` Natural minor every note goes into C minor |
| Out of scale | Nearest | Filter plays only the notes already in key |
| Pattern | Down cycle | the order the octave shifts follow (`Spread` on, `Range` 3) |
| Curve | +100 % | hard; -100 % is soft |
| Mode | Fixed | with `Fixed rate` 1/32: staccato at any tempo |
| Repeats | 4 | with `Echo rate` 1/8 and `Echo feel` Dotted: the classic dotted echo |
| Pitch step | +7 st | each repeat a fifth higher |
| Humanize time | 10 ms | a little looseness, repeatable with `Seed` |

## Starting points

- preset="Scale — Minor Snap", preset="Spread — Octave Cascade", preset="Echo — Dotted Eighth Echo", preset="Echo — Rising Fifths".
- preset="Velocity — Hard Hitter", preset="Velocity — Soft Touch", preset="Length — Staccato", preset="Humanize — Loose Player".
- A tight bassline: switch `Length` on, `Mode` Clamp, `Clamp min` 60 ms, `Clamp max` 300 ms, and `Velocity` on with `Fixed` and `Fixed velocity` 110.

## Pitfalls

- It takes notes only: it does nothing if nothing sends it notes. As a note insert the track's clip notes and live MIDI go in; on a wire, the sender must be wired to it.
- Every section is off until its switch is on: setting `Repeats` without `Echo` on changes nothing.
- The sections work in the fixed order Transpose, Scale, Spread, Velocity, Length, Echo, Humanize: a transposed note is moved into the scale afterwards; an echo repeats the pitch and velocity the note was sent with.
- `Velocity` Fixed ignores `Curve`, `Out min` and `Out max`; `Out max` is never taken below `Out min`.
- With `Mode` Fixed, the source's note-off changes nothing: the note lasts the fixed time. Clamp's maximum ends a held note even if the key is still down.
- An echo repeat of a note that is still sounding is struck again; a repeat longer than the time between repeats is cut to it.
- The Neo synths apply the notes of a block at its start, so delays and repeats are exact on the wire and as fine as the audio block size at a synth.
- Numbers for a choice are positions: write the text ("Down cycle", "1/8"), not a number.

## Pairs well with

- Neo Chord before it (chords, then the echo), Neo Arp before it, and Neo Poly, Neo Organ or Neo Wave after it for the sound.

## Checklist

- [ ] The insert or wire is there: `note_wires` lists it.
- [ ] `param_set(machine="Notes", changes=[{"param": "Echo", "value": "On"}, {"param": "Repeats", "value": 4}, {"param": "Echo feel", "value": "Dotted"}])`, then `midi_play` on the track and `render(probes=["Lead"])`: the instrument repeats each note.
- [ ] `machine_describe(machine="Notes", group="Echo")` lists the echo controls with their units.
