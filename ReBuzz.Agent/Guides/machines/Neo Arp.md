## How it sounds

An arpeggiator: it takes the notes you hold and plays them one at a time, in a pattern, in time with the song. It has no audio of its own. Put it before an instrument as a note insert, or on a free note wire to an instrument: the instrument then plays the arpeggio instead of the held chord.

- `Pattern` is the order: Up, Down, Up-down (ends once or twice), Down-up, Converge, Diverge, As played, Random, Random walk, Chord (all notes together on each step) or Steps (your own 16-step row).
- `Octaves` (1 to 4) repeats the pattern higher, in the order `Octave mode` says; `Repeats` plays each note 1 to 4 times; `Transpose` moves everything ±24 semitones.
- `Timing` is Song tempo (`Rate` 1/1 down to 1/64, `Feel` straight, dotted or triplet) or Free time (`Free time` 20 to 2000 ms). Steps stay on the song's grid through tempo changes and jumps. `Swing` delays every second step.
- `Gate` is how long each note lasts as a share of a step (5 to 200 %; over 100 % the notes overlap).
- The step row: `Step count` (1 to 16), `Step 1` … `Step 16` (Off, On, Tie, Accent) and `Step 1 transpose` … (-12 to +12 st). It shapes the Steps pattern only. A Tie holds the note before it; an Accent is louder in Accent velocity mode.
- `Velocity` is As played, Fixed (`Fixed velocity`) or Accent (`Accent` lifts the marked steps toward 127). `Humanize time` and `Humanize velocity` add a repeatable looseness.
- `Latch` keeps the notes after you let go until you play a new chord; `Retrigger` restarts the pattern on each new note or each bar (Free running follows the song position).
- Nothing is sent while the song is stopped, and every note-on gets its note-off, also on stop and when notes are released.

## Use it for

- Runs and plucks from held chords, trance and synthwave arpeggios, rhythmic chord pulses, sequenced basslines with ties and accents, and random sparkle over a pad.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Pattern | Up | the order of the held notes |
| Rate | 1/16 | how long each step lasts at the song's tempo |
| Octaves | 1 | 2 or 3 widens the run |
| Gate | 80 % | short is plucky, 100 % touches the next note, over 100 % is legato |
| Swing | 0 % | late second steps |
| Velocity | As played | Fixed makes every note equal; Accent marks the Steps pattern's accents |
| Latch | off | on keeps the arpeggio after you let go |
| Retrigger | Free running | On new note or On bar always starts the figure from its first note |

## Starting points

- "Runs — Straight 16ths Up", "Runs — Triplet Cascade", "Waves — Up-Down Two Octaves", "Waves — Slow Converge".
- "Rhythm — Swing Eighths", "Rhythm — Gated Chord Pulse", "Random — Random Sparkle", "Steps — Step Bassline".
- A bassline: Pattern Steps, Step count 16, mark `Step 1` Accent, `Step 3` Tie, `Step 5` Off, and give `Step 4 transpose` +12 st; hold one note.

## Pitfalls

- With no notes held (and Latch off) there is nothing to play: it is silent by design.
- The step row does nothing for the other patterns, which play the held notes in their own order.
- Insert it before an instrument or wire it to one: it makes no sound by itself.
- Gate over 100 % makes the instrument overlap notes; a monophonic or legato instrument will slur them.
- Free time ignores the song's tempo and position.

## Pairs well with

- Neo Poly and Neo Organ as the instrument, Neo Delay and Neo Verb for space.

## Checklist

- [ ] `param_get(machine="Arp", params=["Pattern", "Rate", "Gate"])` shows what you meant.
- [ ] Hold a chord on the instrument's track, then `render(probes=["Poly"])`: a spectrogram shows the notes one at a time on the beat.
- [ ] After the song stops, no note is left sounding.
