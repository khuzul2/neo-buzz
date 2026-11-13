## How it sounds

A teaching plucked-string synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A Karplus-Strong string, a delay line the length of one period that is fed a burst of noise and filters itself on every trip round, so the note rings at the pitch you play. Nylon and steel guitars, harps, harpsichords, upright piano, stiff bell-like chimes, sitar-style drones.

## Signal flow

- `Pick Position` is where the string is plucked: near the end is thin and nasal, near the middle hollow and round (a comb that removes the harmonics which have a node at that point).
- `String Damping` is how fast the highs die compared with the lows: low is a bright ring, high a dull, muted thud.
- `Pick Hardness` is the pluck's own brightness: soft is a finger, hard is a plectrum. Velocity adds to it.
- `Decay Time` is the time the fundamental takes to fall by 60 dB (T60), 0.05 to 20 s, the same at every note you play.
- `Stiffness` is the string's bending stiffness: partials stretch sharp of a harmonic series, from a nylon string (0 %) through a piano to a metal bar or bell (100 %).
- `Sympathetic` adds undamped strings tuned to the fifth and the octave that ring along with the note, like the open strings of a harp or sitar.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level on top of the ring (at 100 % `Sustain` the string rings by itself). `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Nylon guitar: `Pick Position` 25 %, `Pick Hardness` 40 %, `Decay Time` about 2.5 s.
- Harpsichord: `Pick Hardness` 95 %, `Pick Position` 15 %, short `Decay Time`.
- Upright piano: `Stiffness` 40 %, `Decay Time` 4 s, medium `String Damping`.
- Stiff chime: `Stiffness` 85 %, `String Damping` 10 %, `Decay Time` 8 s.
- Sitar drone: `Decay Time` long, `Sympathetic` 85 %, `Unison / Spread` 20 %.

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- `plot` a spectrum of the render to see the comb from `Pick Position`, and `compare` two settings.
- Check the ring time with `analyze` on a render of one note: the level should fall by 60 dB in about `Decay Time`.
- Writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- A long `Decay Time` with high `String Damping` cannot hold the time at the fundamental: damping wins and the note dies sooner.
- `Pick Position` at 50 % removes every even harmonic; that is the hollow sound, not a fault.
- Unison 0 to anything above it jumps from 1 voice to 5 (the string synth caps unison at 5 voices).
- `Stiffness` at the top makes the partials very sharp: the note stays pitched but sounds like a bar.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
