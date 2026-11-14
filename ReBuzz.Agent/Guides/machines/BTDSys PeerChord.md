## How it sounds

A control machine that turns each note written to its track into a chord or an arpeggio on a target machine: 54 chord types, strum and delay modes, a note cut, arpeggio modes and an octave spread. It makes no sound itself; the target plays the notes.

## Use it for

- One-note chords and arpeggios for a pad, a pluck or a bass.
- Strums and spreads that sound played by hand.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Chord type | min | one of 54 chords; none sends the note alone |
| Strum mode | Strum Dn | Off, Simple, Random, Strum Dn or Strum Up |
| Strum delay | 32/128 ticks | the gap between chord notes |
| Arpeggio mode | Off | Up, Down, Up/Down, Down/Up, Random or Brownian |
| Arpeggio steps | 4 | 2 to 16 |
| First target track | 0 | the first of up to six target tracks |
| Velocity | 50% | from the target's lowest to its highest |

## Starting points

- A pad: Chord type min, Strum mode Off, a root note each bar.
- A strum: Strum mode Strum Dn, Strum delay 12/128 ticks.
- Write the root note: `notes_write(machine="PeerChord", pattern="A", notes=[{"pitch": "C3", "start": 0, "length": 1}])`.

## Pitfalls

- The target machine is chosen in the machine's own window and no tool sets it: ask the person to pick it, or nothing plays.
- A chord needs up to six free consecutive tracks on the target.
- Octave controls hold two shifts each, -8 to +7; custom note offsets 0 to 47 with 48 meaning off.

## Pairs well with

- A polysynth with enough tracks; Pedal Chord does the same with a source and its own page.

## Successor

None: Pedal Chord is the repo's own chord and arpeggio machine, but a different one, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="PeerChord")`: 20 track parameters, `Chord type` first.
- [ ] `param_get(machine="PeerChord", track=0)`: Chord type reads none until set.
- [ ] `notes_read(machine="PeerChord", pattern="A")`: the root notes written are back.
