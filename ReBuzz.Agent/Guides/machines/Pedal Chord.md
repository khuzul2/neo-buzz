## How it sounds

It makes no sound itself. It turns each note you write on it into a chord or an arpeggio on a target generator: 51 voicings (triads, sevenths, ninths, shells, quartal and quintal stacks, clusters and more), a chord or one of five arpeggio modes, a step size, an octave range and an octave walk, swing, and humanised timing and velocity. In Chord mode it uses consecutive tracks of the target, one per chord tone; in the arpeggio modes it plays on one track.

## Use it for

- One-note chords and arpeggios from a single root note, drive a pad or a synth from a bass line, and quick harmonic ideas.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Chord | Minor | one of 51 voicings (Major, Minor, Dom 7, Maj 9, Sus4, Power, Quartal...) |
| Mode | Arp Up | Chord plays all notes at once; the arps play one at a time |
| Speed | 2 ticks | ticks between arpeggio steps; follows the tempo |
| Length | 0 ticks | 0 holds each note until the next one |
| Octaves | 2 | range of the arpeggio, 1-4 |
| Swing | 30% | 0 straight, 100 about a 2:1 triplet shuffle |
| Humanize | 10% | random timing drift per step |
| Velocity | 100 | what the target receives, 1-127 |

## Starting points

- A pad: Mode Chord, Chord Min 7, Length 0, a root note written every bar.
- An arpeggio: Mode Arp Up+Down, Chord Dom 7, Speed 2, Octaves 2, Swing 20 %.
- Write the root note: `notes_write(machine="Chord", pattern="A", notes=[{"pitch": "C3", "start": 0, "length": 1}])`.

## Pitfalls

- The target machine is chosen in the machine's own dialog (Target Settings), and no tool sets it: ask the person to pick it, or the machine plays nothing.
- The target must be connected to the master, or the chords are not heard.
- One instance controls one generator: add one per target.
- `Length` 0 never sends a note-off until the next note: set a length for staccato.

## Pairs well with

- Pedal Juno106 or Pedal FM as the target; Neo Poly as the target; Pedal LFO to sweep the target's filter.

## Checklist

- [ ] `notes_read(machine="Chord", pattern="A")` gives back the root notes you wrote.
- [ ] `arrangement_place(machine="Chord", pattern="A", at=[0])` puts the pattern in the song.
- [ ] `analyze(source="Pad", metrics=["pitch"])` on the target: the chord's notes sound.
