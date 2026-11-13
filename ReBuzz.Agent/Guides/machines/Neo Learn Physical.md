## How it sounds

A teaching physical-modelling synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A bank of 32 resonating modes is excited by a mallet, a pick or a bow; the material, the body shape, how hard it is played, the damping and the size are the whole sound.

- `Material` walks wood, nylon, glass, metal: stiffness, ring time and how fast the highs die. `Exciter Mode` crossfades mallet (0 %), pick (50 %) and bow or breath (100 %, sounding while the key is held).
- `Strike / Pressure` is how hard: harder is brighter. `Geometry` blends the mode spacing from a string through an open tube and a bar to a plate.
- `Damping` shortens the ring and mutes the upper modes first. `Body Size` moves the resonance: small is thin and bright, large is deep and dark.
- Each control has a How and a Sound sentence: `machine_describe` returns them with the id, and `param_set` takes the name or the id. 16 notes; no limiter.

## Use it for

- Learning physical modelling: change `Material` and `Geometry` on one note and hear the object change. Then marimbas, plucked strings, glass bowls, bells, bowed and breathy tones.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Material | 20% | wood to metal as it rises |
| Exciter Mode | 0% | mallet; 50 % pick; 100 % bow or breath |
| Strike / Pressure | 50% | harder is brighter |
| Geometry | 67% | 0 % string, about 67 % bar, 100 % plate |
| Damping | 30% | low lets a note ring for seconds |
| Body Size | 50% | small is thin, large is deep |
| Output | -6 dB | lower it if `peak_db` passes 0 dBFS |

## Starting points

- "Marimba — Rosewood", "Pluck — Nylon Guitar", "Bell — Bronze", "Glass — Crystal Bowl", "Bow — Cello Strings", "Breath — Open Pipe", "Kalimba — Thumb Piano", "Harp — Soft Pick", "Plate — Struck Steel".
- Bowed string: `Exciter Mode` 100 %, slow `Attack`, large `Body Size`.

## Pitfalls

- A bow or breath only sounds while the note is held: set `Sustain` high and write a long note.
- A mallet and a pick die away on their own; low `Damping` and a long `Release` let a note ring.
- High notes lose their upper modes above the Nyquist limit, so a metal bell played high has fewer partials.
- Unison 0 to anything above it jumps from 1 voice to 5. Only these 12 controls exist.

## Pairs well with

- Neo Verb for bells and bowls, Neo Ensemble for width, Neo EQ, Neo Comp on plucks; Neo FM6 for other bell and mallet tones.

## Checklist

- [ ] `machine_describe(machine="Learn")` shows the id, range, How and Sound of each control.
- [ ] `param_set(machine="Learn", changes=[{"param": "Damping", "value": "30%"}])`, then `notes_write` a long note and `render(probes=["Learn"])`.
- [ ] `machine_readings(machine="Learn")` gives `rms_db` near -22 dB and `peak_db` under 0 dBFS.
- [ ] `plot` the spectrum of the render and `compare` wood against metal: the modes move apart.
