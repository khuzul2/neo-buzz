## How it sounds

A teaching FM synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A sine carrier is phase-modulated by a second oscillator at an exact frequency ratio; the ratio, the depth, how fast the depth fades, a self-feedback loop and a metallic third oscillator are the whole sound. Electric-piano tines, glass bells, plucked and growling basses, bright leads, metal hits. Band-limited oscillators keep the top clean.

## Signal flow

- `Mod Ratio` sets the modulator's frequency as an exact multiple of the note (a ratio, not a transpose). It has detents at whole and half ratios: whole ratios give harmonic sidebands (a pitched, musical sound), half ratios fold sidebands between the harmonics (hollow, bell-like), and anything else is inharmonic (clangorous).
- `FM Depth` is the modulation index: more depth, more and louder sidebands, a brighter sound.
- `Mod Decay` is how long that depth takes to fade after the note starts: short is a struck attack that settles to a pure tone, long keeps the brightness.
- `Feedback` feeds the modulator back into itself: from sine toward saw-like buzz and then noise.
- `Metallic Edge` adds a third oscillator at a fixed high ratio (7x the note) on a quick envelope: the attack clang of a bell or a tine.
- `Velocity to FM` makes harder notes brighter (depth scaled by velocity).
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## How to use it

- Electric piano: `Mod Ratio` 1, `FM Depth` 25-40 %, `Mod Decay` about 0.5 s, `Metallic Edge` 30-50 %, `Velocity to FM` 60 %.
- Bell: `Mod Ratio` 3.5 or 5.5 (the half-ratio detents), `Mod Decay` long, `Sustain` 0, long `Decay`.
- Bass: `Mod Ratio` 0.5 or 1, `FM Depth` 40-60 %, short `Mod Decay`, `Feedback` 20-50 % for growl.
- Clean test tone: `FM Depth` 0, then raise it and listen as sidebands appear.

## Checklist

- Read each control's id, range and how/sound with `machine_describe` (the ids are snake_case and are also taken by `param_set`).
- Set `Mod Ratio` and `FM Depth` with `param_set`, play a pattern (`notes_write`), then `render` with probes and check `machine_readings` (rms_db, peak_db, active_voices).
- Compare two settings with `render` then `compare`, and `analyze` for spectral balance.
- Keep `peak_db` under 0 with `Output`; writes via `param_set` show on the Explain strip with the Agent badge.

## Gotchas

- Unison 0 to anything above it jumps from 1 voice to 5 (level stays even; the sound gets wider).
- High `FM Depth` with high `Feedback` is noise: lower `Output` and watch `peak_db`.
- Ratios between the detents are inharmonic and can sound out of tune with other parts; snap to a detent for pitched sounds.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.
