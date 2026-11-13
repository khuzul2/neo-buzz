## How it sounds

A teaching wavetable synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A table of waveform frames is scanned: you pick the table and the frame, bend the phase of each cycle, and let a slow motion sweep the frame for you. Evolving pads, sync sweeps, pinched basses, organ keys, digital glass, gritty wobble leads. Band-limited tables keep the top clean.

## Signal flow

- `Wavetable` picks one of ten tables: Basic, PWM, Harmonics, Formants, Sync sweep, FM sweep, Organ, Resonant, Digital, Grit, plus User (an imported `.wav`: frames of 2048 samples, or a single cycle).
- `Frame Pos` is where in the table the sound reads, from the first frame (0 %) to the last (100 %), blended between neighbouring frames.
- `Phase Warp` bends the cycle's phase: 50 % is untouched, below it squeezes one way, above it the other, which adds harmonics and sync-like bite.
- `Motion Rate` and `Motion Depth` run a slow motion (LFO) on the frame position: rate in Hz, depth the width of the sweep.
- `Sub Anchor` mixes a sine one octave down so the low end stays solid however bright the table is.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Pad: slow `Attack` and `Release`, `Motion Depth` 40-70 %, `Motion Rate` 0.2-0.5 Hz, `Unison / Spread` 30 %.
- Sync lead: `Wavetable` Sync sweep, `Frame Pos` swept by `Motion Rate` about 1-3 Hz, `Phase Warp` above 60 %.
- Bass: `Sub Anchor` 40-80 %, `Frame Pos` low, short `Decay`.
- Your own table: use the machine's import (`wave_from_render` makes a wave from a render, then import it) and set `Wavetable` to User.

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- Set `Wavetable`, `Frame Pos` and `Phase Warp` with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- Compare two frames with `render` then `compare`, and `plot` a spectrum to see the harmonics move.
- Writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- Unison 0 to anything above it jumps from 1 voice to 5 (level stays even; the sound gets wider).
- `Phase Warp` at 50 % (raw 500) is neutral.
- With `Motion Depth` high the frame can reach the table's ends: some tables are loud there, so watch `peak_db`.
- User plays only after a wave has been imported into the machine; until then pick one of the ten built-in tables.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
