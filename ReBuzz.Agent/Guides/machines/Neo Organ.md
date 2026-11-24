## How it sounds

A tonewheel organ: 91 shared sine wheels, one per note of its range, switched onto nine drawbar busses by the keys you hold. Each drawbar is one footage (a harmonic of the played note) and goes from 0 (off) to 8 (full) in steps of about 3 dB. Every key can sound at once and holding 61 keys costs about the same as holding one. Above the top wheel the high footages fold back an octave, as on the instrument.

| Drawbar | Partial |
|---|---|
| 16' | an octave below the note |
| 5 1/3' | a fifth above the note |
| 8' | the note itself |
| 4', 2', 1' | one, two and three octaves above |
| 2 2/3', 1 3/5', 1 1/3' | the 3rd, 5th and 6th harmonics |

Around the tone: Percussion (a single-trigger 2nd or 3rd harmonic, only when no other key is held), Key click, Leakage (crosstalk between wheels), a scanner Vibrato or chorus (V1 to V3, C1 to C3), a tube-style Drive before the speaker, and a rotary speaker with a horn and a drum that speed up and slow down with inertia. `Speed` is Slow, Fast or Stop: map a knob or pad to it to switch the speaker while you play. Every control has a How and a Sound sentence in `machine_describe`; `param_set` takes the name or the id.

## Use it for

- Gospel, church, jazz, blues, rock and reggae organ, flute stops, organ stabs and soft pads.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| 8' | 8 | the body of every registration; the other bars colour it |
| 16' and 5 1/3' | 8 | the jazz weight under the note |
| Percussion | on | a quick bell on the first key (single trigger); play legato for none |
| Vibrato type | C3 | chorus 3 is the widest; V1 is the gentlest |
| Drive | 0% | 30 to 70% for blues and rock grit |
| Speed | Fast | Slow is the chorale, Fast the tremolo; Stop holds the rotors |
| Depth | 75% | how much the speaker moves the sound |
| Volume | -6 dB | check `peak_db` with Drive up |

## Starting points

- "Full — Full Gospel" (every bar, chorus, driven, fast), "Full — Church Full", "Full — Fast Whirl".
- "Jazz — Jazz Trio", "Jazz — Smoky Blues", "Jazz — Warm Soul".
- "Soft — Soft Ballad", "Soft — Mellow Flutes", "Rock — Rock Drive".
- "Percussive — Bright Percussion", "Percussive — Reggae Bubble", "Percussive — Funk Stabs".
- A swell: set `Expression` low and automate it up; it sits before the drive, so the tone gets brighter as it opens.

## Pitfalls

- Freshly added only 8' and 4' are out: pull more drawbars for a full sound.
- Drawbar values are whole steps 0 to 8 (not percentages).
- Percussion triggers only on a key pressed with no other key held: legato playing gets none.
- `Rotary` off gives the plain dry organ in both channels; `Stop` brakes the rotors to rest, which sounds like a fixed room position.
- Speed changes take about a second to settle (the horn is quicker than the drum); that is the inertia, not a fault.

## Pairs well with

- Neo Delay and Neo Verb for space, Neo EQ to shape the highs, Neo Comp on stabs; the speaker already makes the stereo.

## Checklist

- [ ] `param_set(machine="Organ", changes=[{"param": "16'", "value": "8"}, {"param": "Speed", "value": "Fast"}])`, then `render(probes=["Organ"])`.
- [ ] `machine_readings(machine="Organ")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS with `Drive` up.
- [ ] `analyze(source="Organ", metrics=["spectrum"])`: each pulled bar shows its partial at the right pitch.
- [ ] `machine_describe(machine="Organ", group="Rotary")` lists the speeds, inertia, depth and balance.
