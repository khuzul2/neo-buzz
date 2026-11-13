## How it sounds

A teaching vector synth: one Dye Essence patch driven by 12 controls, so every control does something you can hear and the Explain strip says what. Four sources sit at the corners of a diamond (west, east, north, south) and a cursor mixes them: in the middle you hear all four a little, at a corner only that one. An orbit generator moves the cursor around the point you set, along a circle, a figure of eight, a stepping diamond or a random drift, so the sound evolves with no LFO to set up. Four palettes decide what the corners are. Evolving pads, slow morphing textures, stepping sequences, hybrid keys.

## Use it for

Evolving pads, slow morphing textures, stepping sequences and hybrid keys.

## Key controls

- `Vector X (W-E)` and `Vector Y (S-N)` place the cursor. The mix always adds to the same total, so moving does not change the level much.
- `Corner Palette` chooses the four sources: Poly Classic (saw, square, choir, triangle sub), Cinema Horizon (strings, harmonic table, texture, sub with air), Cyber Pulse (pulse, digital table, sync sweep, square sub) or Organic Hybrid (organ, bell, formants, triangle sub with air). A step between four, not a ramp.
- `Orbit Rate` is how fast the cursor goes round (0.05 to 12 Hz).
- `Orbit Depth` is how far it travels from the point you set (0 % holds the cursor still).
- `Orbit Path` is the path: Circle, Figure 8, Diamond Step (jumps corner to corner) or Random Drift (a smoothed random walk, the same every time).
- `Attack`, `Decay`, `Sustain`, `Release` shape the level. `Unison / Spread`: 0 % is one voice per note; above it each note plays 5 detuned, spread voices (a step, not a ramp).
- `Output` is the level after synthesis (presets sit near -22 dB RMS). There is no limiter; the clip light shows over 0 dBFS. 16 notes.

## Starting points

- Evolving pad: Poly Classic, `Orbit Rate` 0.3 Hz, `Orbit Depth` 40 %, `Orbit Path` Circle, slow `Attack`.
- Stepping sequence: Cyber Pulse, `Orbit Path` Diamond Step, `Orbit Depth` 100 %, `Orbit Rate` 3 Hz.
- Hold a corner: `Orbit Depth` 0 %, then `Vector X` at -100 % for the west source alone.
- Drifting texture: Cinema Horizon, `Orbit Path` Random Drift, `Orbit Depth` 80 %.

## Pitfalls

- The cursor is one for every note: all held notes follow the same orbit. It runs free, not from each note's start.
- `Corner Palette` steps between sources: change it between notes rather than while one holds.
- A palette with samples or tables plays each corner at the note's pitch; the sub corner is an octave down.
- Unison 0 to anything above it jumps from 1 voice to 5, on all three upper corners.
- A machine of 12 controls: the other Dye Essence controls are fixed and cannot be set here.

## Pairs well with

Neo Verb for width, Neo Delay on stepping sequences, Neo Ensemble to thicken pads, Neo Filter for slow sweeps.

## Checklist

- [ ] Read each control's id, range and how/sound with `machine_describe` (ids are snake_case and also taken by `param_set`).
- [ ] Change one control at a time with `param_set`, play a long note (`notes_write`), then `render` with probes and check `machine_readings`.
- [ ] `plot` a spectrogram of the render to see the mix move with the orbit, and `compare` two palettes.
- [ ] Writes via `param_set` show on the Explain strip with the Agent badge.
