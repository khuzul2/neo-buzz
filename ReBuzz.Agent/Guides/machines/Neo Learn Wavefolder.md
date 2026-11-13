## How it sounds

A teaching wavefolder: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A sine (with a second sine frequency-modulating it) is pushed through one to six folding stages: when the wave gets higher than the fold, it bends back, and every fold adds harmonics instead of clipping. A low-pass gate (a filter and volume together, as in a vactrol) shapes each note. Metallic basses, bongo-like plucks, sizzling leads, glassy bells.

## Signal flow

- `Wavefold` is the drive into the folds: at 0 % the sine passes unchanged, more and more folds add brighter harmonics.
- `Symmetry` shifts the wave before it is folded: away from the middle adds even harmonics and a hollow, reedier tone.
- `Fold Stages` is how many folds are in series (1 to 6): each stage folds the result of the one before, so the sound gets denser and noisier.
- `Timbre FM` is phase-locked frequency modulation of the sine, before the folds, from a second sine at the same pitch.
- `Low-Pass Gate` is the gate's mode: low is a plain filter, high is a gate where level and brightness fall together.
- `Gate Strike` is how much each note strikes the gate open from velocity, then it falls back: high is a bongo-like ping.
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Metal sub bass: `Wavefold` 45 %, `Fold Stages` 2, play low notes.
- Bongo ping: `Low-Pass Gate` 85 %, `Gate Strike` 85 %, `Sustain` 0 %, medium `Decay`.
- Sizzle lead: `Wavefold` 75 %, `Fold Stages` 4, `Symmetry` 20 %.
- Rust pad: `Fold Stages` 5, slow `Attack`, `Unison / Spread` 35 %.

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- `plot` a spectrum of the render to see harmonics grow with `Wavefold`, and `compare` two settings.
- Writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- The folds run at 4x oversampling (High quality) so it uses more CPU than the other Neo Learn synths.
- On very high notes the total fold gain is held back so the folds stay below the audible alias floor (as far as the cap allows, about 30 dB or more under the note); low and middle notes fold in full.
- The output of a fold does not rise with `Wavefold`: it folds back, so turning up the drive brightens more than it loudens.
- A closed gate (`Low-Pass Gate` high, `Gate Strike` 0 %) can be nearly silent; add `Gate Strike`.
- Unison 0 to anything above it jumps from 1 voice to 5.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
