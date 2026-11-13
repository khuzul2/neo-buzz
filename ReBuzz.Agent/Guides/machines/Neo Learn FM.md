## How it sounds

A teaching FM synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. A sine carrier is phase-modulated by a second oscillator at an exact frequency ratio; the ratio, the depth, how fast the depth fades, a self-feedback loop and a metallic third oscillator are the whole sound. Band-limited oscillators keep the top clean.

- `Mod Ratio` is the modulator's frequency as an exact multiple of the note, with detents at whole and half ratios: whole ratios give harmonic sidebands (pitched, musical), half ratios fold sidebands between the harmonics (hollow, bell-like), anything else is inharmonic (clangorous).
- `FM Depth` is the modulation index: more depth, more and louder sidebands. `Mod Decay` is how long that depth takes to fade: short is a struck attack that settles to a pure tone.
- `Feedback` runs from sine toward saw-like buzz and then noise. `Metallic Edge` adds a third oscillator at 7x the note on a quick envelope: the clang of a bell or tine. `Velocity to FM` makes harder notes brighter.
- Each control has a How and a Sound sentence: `machine_describe` returns them with the id, and `param_set` takes the name or the id. 16 notes; no limiter.

## Use it for

- Learning FM: raise `FM Depth` from 0 and hear sidebands appear. Then electric-piano tines, glass bells, plucked and growling basses, bright leads and metal hits. Neo FM6 is the full six-operator version.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mod Ratio | 1 | whole ratios are pitched; 3.5 or 5.5 give bells |
| FM Depth | 30% | brightness; 0 % is a pure sine |
| Mod Decay | 500 ms | short is a struck attack, long keeps the brightness |
| Feedback | 0% | 20-50 % for growl; high values turn to noise |
| Metallic Edge | 40% | the attack clang of a tine or bell |
| Velocity to FM | 60% | hard notes brighter |
| Output | -6 dB | lower it if `peak_db` passes 0 dBFS |

## Starting points

- "Keys — Tine Piano", "Keys — Glass Bell", "Bass — FM Pluck", "Bass — Growl", "Lead — Bright Glass", "Pad — Slow Bells", "Percussion — Metal Hit", "Lead — Clean Sine".
- Bell: `Mod Ratio` 3.5, long `Mod Decay`, `Sustain` 0 %, long `Decay`. Bass: `Mod Ratio` 0.5 or 1, `FM Depth` 40-60 %, short `Mod Decay`.

## Pitfalls

- Unison 0 to anything above it jumps from 1 voice to 5 (the level stays even; the sound gets wider).
- High `FM Depth` with high `Feedback` is noise: lower `Output` and watch `peak_db`.
- Ratios between the detents are inharmonic and can sound out of tune with other parts.
- Only these 12 controls exist: the other Dye Essence controls are fixed and cannot be set here.

## Pairs well with

- Neo Ensemble for chorus, Neo Verb for bells, Neo Comp on basses, Neo EQ; Neo FM6 when you want six operators and 32 algorithms.

## Checklist

- [ ] `machine_describe(machine="Learn")` shows the id, range, How and Sound of each control.
- [ ] `param_set(machine="Learn", changes=[{"param": "FM Depth", "value": "30%"}])`, then `notes_write` a chord and `render(probes=["Learn"])`.
- [ ] `machine_readings(machine="Learn")` gives `rms_db` near -22 dB and `peak_db` under 0 dBFS.
- [ ] `plot` the spectrum of the render and `compare` two depths: more depth, more upper partials.
