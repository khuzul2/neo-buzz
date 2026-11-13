## How it sounds

A drawbar-style Buzz organ: six drawbars from 16 foot to 2 foot set the harmonics, with an attack and a release. Monophonic and hot at its defaults.

## Use it for

- Organ pads and stabs in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| 8" | 35 | 0 to 100; the note's own pitch |
| 16" | 100 | 0 to 100; the octave below |
| 4" | 30 | 0 to 100; the octave above |
| Attack | 15 ms | about 1 ms to 9 s |
| Release | 250 ms | about 1 ms to 9 s |
| 5 1/3" | 100 | 0 to 100; a fifth above |

## Starting points

- A soft pad: `param_set(machine="Organ", changes=[{"param": "Attack", "value": "300 ms"}, {"param": "Release", "value": "500 ms"}])`.

## Pitfalls

- Hot at its defaults: a note peaks at 0 dBFS.
- `Vib.Int.` and `Vib.Spd.` read "not impl. yet" and are not in the manifest.
- It is monophonic, and velocity does not change its level.

## Pairs well with

- Jeskola Chorus for width; Jeskola Reverb for space.

## Checklist

- [ ] `analyze(source="Organ", metrics=["loudness"])`: a note sits near -7 LUFS.
- [ ] `param_get(machine="Organ")`: the drawbars read 0 to 100.
- [ ] `render(probes=["Organ"])`: the peaks stay under full scale.
