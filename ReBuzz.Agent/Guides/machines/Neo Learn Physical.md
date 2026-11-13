## How it sounds

A teaching physical-modelling synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A bank of 32 resonating modes is excited by a mallet, a pick or a bow (or a blend); the material, the shape of the body, how hard it is played, how much it is damped and its size are the whole sound. Marimbas, plucked strings, glass bowls, bells, bowed and breathy tones.

## Signal flow

- `Material` walks wood, nylon, glass, metal: it changes the stiffness (how far the modes stretch from a harmonic series), the ring time and how fast the highs die.
- `Exciter Mode` crossfades the energy source: 0 % is a mallet strike, 50 % a pick (a plucked release), 100 % a bow or breath (steady noise through the body, sounding while the key is held). Between, two sources are blended.
- `Strike / Pressure` is how hard: a harder mallet or pick is brighter; a firmer bow is more turbulent. Velocity adds to it.
- `Geometry` blends the mode spacing from a string (whole multiples) through an open tube (odd multiples) and a bar to a plate; it moves smoothly.
- `Damping` shortens the ring and mutes the upper modes first.
- `Body Size` moves the body's resonance: small is thin and bright, large is deep and dark.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level on top of the natural ring (at 100 % `Sustain` the body rings by itself). `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Marimba: `Material` 5 %, `Exciter Mode` 0 %, `Geometry` 67 % (bar), medium `Damping`.
- Nylon guitar: `Exciter Mode` 50 %, `Geometry` 0 % (string), `Material` 30 %.
- Bronze bell: `Material` 100 %, `Geometry` 100 %, low `Damping`, long `Release`.
- Bowed string: `Exciter Mode` 100 %, slow `Attack`, large `Body Size`.

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- `plot` a spectrum of the render to see the modes, and `compare` two settings.
- Writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- A bow or breath only sounds while the note is held; set `Sustain` high and write a long note with `notes_write`.
- A mallet and a pick die away on their own; a low `Damping` and long `Release` let a note ring for seconds.
- Unison 0 to anything above it jumps from 1 voice to 5.
- High notes lose their upper modes above the Nyquist limit, so a metal bell played high has fewer partials.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
