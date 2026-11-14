## How it sounds

An analog-style drum machine with eleven voices: bass drum, snare, three toms, rim shot, clap, closed and open hi-hat, crash and ride. Each voice has a level; most have tune and decay controls, and the snare has tone and snap. It plays from trigger columns in its tracks, not from notes, so it is silent until a trigger fires.

## Use it for

- Four-on-the-floor kicks, snares, claps and hi-hat patterns for house, techno and breaks.
- Songs that already carry its trigger patterns.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bass drum level | 100% | 0 to 198%; raw 128 |
| Snare level | 100% | the snare against the kick |
| Closed hi-hat level | 80% | keep hats under the snare |
| Bass drum tune | 32 | 0 to 63; lower is deeper |
| Bass drum decay | 32 | 0 to 63; longer is boomier |
| Shuffle | 0 ticks | 0 to 0.5 tick of swing; 0 is straight |

## Starting points

- A kick and clap groove: Bass drum level 100%, Snare level 100%, Bass drum decay 32.
- Swing it: Shuffle 0.25 ticks with Shuffle step 2.
- Its triggers are pattern columns: `notes_write` does not play it, so build the pattern in a song that carries them.

## Pitfalls

- Silent at its defaults until a trigger fires (the profile found no note column): a note does nothing.
- Levels reach about 200%: keep every voice under 120% to leave headroom.
- Tune and decay are bare 0 to 63 scales with no unit in the machine's texts.

## Pairs well with

- Neo Drums for a modern kit; a compressor on the bus for glue.

## Successor

Neo Drums replaces Rout 909: `machine_replace(machine="Rout")` plays its eleven triggers on Neo Drums' voices, carries the levels over in decibels, and sets the tunes, decays, attack, tone and snap to the same settings in Neo Drums' units.

## Checklist

- [ ] `machine_describe(machine="Rout")`: its parameters are the eleven levels, the tunes, decays and the shuffle.
- [ ] `param_get(machine="Rout")`: every level reads 100%.
- [ ] `machine_replace(machine="Rout", dry_run=true)`: shows what Neo Drums would get.
