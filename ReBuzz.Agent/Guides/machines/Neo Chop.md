## How it sounds

An effect that cuts up the audio of a track in time with the song, with no latency. Six modes, any number on at once: `Stutter`, `Repeat`, `Retrigger`, `Tape`, `Gate`, `Pan`. The four cutting modes share one read of the audio and take over by priority: Tape, then Stutter, then Repeat, then Retrigger (highest first). The gate shapes the result and the pan comes last. Every cut is crossfaded over `Smoothness` (2-20 ms), so nothing clicks.

- Each cutting mode has a trigger: `Synced` (from the song position; each window ends exactly on its period), `Note` (a note through a note wire, live MIDI or the track's note column starts it on the note's own sample) or `Both`. Nothing runs while the song is stopped, except a gate or pan set to Free. Four beats to the bar is assumed.
- `Stutter` repeats the last slice (`Slice` 1/4 to 1/64, `Slice feel` straight, dotted or triplet) `Repeats` times (1-16, or Hold: until the note ends), each repeat shifted by `Pitch per repeat` (-12 to +12 st), `Decay per repeat` (dB) and `Filter per repeat` (octaves: left a low-pass that closes, right a high-pass that opens). `Stutter every` is the period (1 beat to 8 bars); the stutter fills the last `Repeats` x slice of it.
- `Repeat` loops a `Grid` slice for a `Length` (1/16 to 4 bars) at the end of every `Interval` (1-8 bars), with a `Chance` (the same bars on every play) and a `Variation` that cuts some loops shorter.
- `Retrigger` keeps jumping back to the start of the beat or bar (`Back to`) every `Retrigger grid`, for a `Retrigger length` at the end of each period.
- `Tape` winds the audio down to a stop (`Stop time` in ms or `Stop sync` as a note value, `Stop curve` bends it), holds it silent until the period's end (or the note's end) and spins it back up over `Start time` or `Start sync`.
- `Gate` has 16 steps (`Step 1`-`Step 16`: Off, On, Accent) at `Gate rate`, `Gate attack` and `Gate release` in ms, `Gate depth` (how far an off step closes) and `Accent` in dB. `Gate lock` Free runs it off the song.
- `Pan` swings the balance by `Pan depth` at `Pan sync` (a note value of the song, or Free at `Pan rate` Hz) in a `Pan shape`, starting `Pan phase` degrees into its cycle.
- `Note picks slice` lets a note's key choose the slice or grid (C quarter, D eighth, F sixteenth, G# thirty-second, and the dotted and triplet values between).
- `Amount` (0-200%, 100% is the settings as they stand) scales the pitch, decay, filter, repeat chance, gate depth and pan depth. `Amount` 0 or `Mix` 0 passes the input bit for bit.

## Use it for

- Fills and builds at the end of a bar or phrase (stutter, repeat, retrigger); a tape stop for a break; a rhythmic gate to pump a pad; a slow auto-pan to widen a part. Note-triggered, it is a performance effect played from a keyboard or a clip.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Stutter every | 1 bar | the stutter fills the end of every bar; 2 beats makes it constant |
| Slice | 1/16 | 1/8 is a stammer, 1/32 and shorter a buzz; Slice feel Triplet rolls |
| Repeats | 8 | 4 is a short fill, Hold freezes while a note is down |
| Pitch per repeat | +2 st | a rising roll; negative falls; 0 keeps the pitch |
| Length | 1/2 | with Interval 1 bar, a Repeat fill in the second half of every bar |
| Stop time | 700 ms | a tape stop; 200 ms is a brake, 2 s a slow sink |
| Gate rate | 1/8 | the 16 steps then make a pattern of two bars (1/16: one bar) |
| Smoothness | 4 ms | longer is softer (and slurs fast repeats), 2 ms is the tightest |
| Amount | 100% | the whole strength; 50% halves every shift and depth |
| Mix | 100% | blend the dry in to keep the groove under a cut |

## Starting points

- "Stutter — Sixteenth Stutter", "Stutter — Triplet Glitch", "Repeat — Bar-End Repeat", "Retrigger — Half-Bar Retrigger".
- "Tape — Tape Stop at the Bar", "Gate — Pumping Step Gate", "Pan — Wide Auto-Pan", "Fill — Chopped Vocal Feel".

## Pitfalls

- A fresh instance stutters at the end of every bar; presets turn off the modes they do not use. Check the mode row first.
- Nothing happens with the song stopped (except a Free gate or pan), and a cut waits until there is enough audio before it: the first bar after a start has nothing to repeat.
- A mode set to `Note` does nothing without a note: connect a note wire or play a key. A held stutter or tape stop ends only on the note's end.
- Cutting modes take over in priority order: a Tape stop silences a Stutter that overlaps it.
- The gate steps follow the song at `Gate rate`; 16 steps of 1/16 are one bar, of 1/8 two bars.

## Pairs well with

- Neo Delay and Neo Verb after it for tails on the repeats; Neo Comp after the gate for a pump; a clip or note source into its note wire for played fills.

## Checklist

- [ ] `machine_describe(machine="Chop")`: the mode switches and triggers; turn off the modes you do not want before judging.
- [ ] Play the song across the bar line where the window ends, and `render` it: the cut should land on the beat.
- [ ] `analyze(source="Chop", metrics=["loudness"])`: gate and tape change the level; match `Mix` and `Amount` when comparing.
- [ ] `compare` a bypassed render (`machine_set(machine="Chop", bypass=true)`) with the processed one.
