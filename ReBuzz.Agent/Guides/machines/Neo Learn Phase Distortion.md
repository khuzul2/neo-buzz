## How it sounds

A teaching phase-distortion synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A sine oscillator whose read position is bent: instead of reading a cosine at an even speed, each cycle reads part of it much faster than the rest, which adds harmonics the way a filter sweep would, without a filter. Six geometries decide where the speed-up sits. A bend that decays on every note gives plucks and sweeps; a ring modulator, a hard-synced windowed resonance and a warm chorus finish it. Bright leads, resonant basses, glassy bells, metallic rings.

## Signal flow

- `Phase Bend` is how far the read position leaves an even speed: 0 % is a pure sine, 100 % reads the chosen geometry in full.
- `PD Wave` chooses the geometry: Sawtooth, Square, Narrow Pulse, Double Sine, Saw-Pulse or Trapezoid (a step between six, not a ramp).
- `Bend Decay` is how long the bend takes to relax after each note starts: it opens with the whole bend and settles to a quarter of it.
- `Resonance` mixes in a hard-synced, windowed sine above the note: the same sweep (and the same decay) moves its pitch up, so it sounds like a resonant filter opening.
- `Ring Mod` multiplies the bent wave with a second sine at an inharmonic ratio (2.756 times the note): bell-like sidebands.
- `Chorus` is a warm, slightly noisy stereo chorus on the output (the shared engine's `ChorusFx`).
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Bent saw lead: `PD Wave` Sawtooth, `Phase Bend` 70 %, `Bend Decay` 0.5 s, `Chorus` 30 %.
- Resonant bass: `Resonance` 80 %, `Bend Decay` 0.6 s, `Sustain` 55 %.
- Ring glass bell: `PD Wave` Double Sine, `Ring Mod` 70 %, `Sustain` 0 %, long `Decay`.
- Slow bend pad: `PD Wave` Saw-Pulse, slow `Attack`, `Unison / Spread` 35 %, `Chorus` 60 %.

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- Change one control at a time with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- `plot` a spectrum of a short render to see the harmonics fall as the bend relaxes, and `compare` two `PD Wave` settings.
- Writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- It runs at 4x oversampling (High quality), so it uses more CPU than the other Neo Learn synths without it.
- The bend's loudness does not rise with `Phase Bend`: it brightens more than it loudens.
- `Bend Decay` works on the start of each note: a held note keeps a quarter of the bend and `Resonance`'s sweep.
- `PD Wave`, 0 to 5, steps between geometries; automate `Phase Bend` instead for a smooth sweep.
- Unison 0 to anything above it jumps from 1 voice to 5.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
