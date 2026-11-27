## How it sounds

An eight-lane Euclidean and probability rhythm generator on note wires: it makes no sound itself and sends note-on and note-off events to any instrument with a note input, such as Neo Drums or Neo Poly. Each lane distributes its hits evenly across 1 to 32 steps via Bjorklund's algorithm, with a second Euclidean accent ring over the same steps.

- **Eight independent lanes (`Lane 1` … `Lane 8`):** each lane has `Steps` (1 to 32), `Pulses` (0 to steps), `Rotation` (0 to steps − 1), `Chance` (0 to 100 %), `Accent pulses` and `Accent rot`, `Note` (MIDI 0 to 127 or note name such as `C2`), `Velocity` and `Accent vel` (1 to 127), `Gate` (5 to 100 % of the step slot), `Ratchet` (1 to 4 repeats inside the step) with `Ratchet chance` (0 to 100 %), clock `Divider` (`1x` to `4x` slower), MIDI `Channel` (1 to 16), and `Mute`.
- **Clock and feel:** global `Rate` (`1/1` down to `1/64`) and `Feel` (`Straight`, `Dotted`, `Triplet`) stay sample-locked to the song position through mid-bar starts, tempo changes and loop jumps. `Swing` delays odd steps by up to 75 %; `Humanize time` (0 to 20 ms) and `Humanize velocity` (0 to 30 %) loosen rigid timing and dynamics.
- **Probability, fill and seed:** `Fill chance` adds extra random hits on empty steps of unmuted lanes. `Seed` (`1` to `9999`) makes every random choice repeatable across runs; `Seed` `0` (`Free`) varies on each playback.
- **Routing:** wire Neo Pulse to a target with `note_wire_connect`. To send lane 3 to a different synth than lane 1, add a second Neo Pulse instance (both stay sample-locked to the song clock) and wire each to its own instrument, or fan out one instance when targets respond to disjoint notes or MIDI channels.
- Nothing is sent while the song is stopped, and every note-on gets its matching note-off on mute, edit, loop jump, stop or panic.

## Use it for

- Euclidean drum grooves (four-on-the-floor with dense hats, West African 12-step bell timelines, syncopated son rhythms), odd-meter percussion, ratcheting hi-hat flurries, polymetric polyrhythms, and evolving pentatonic synth lines.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Rate | 1/16 | base step length synced to song tempo (`1/1` to `1/64`) |
| Swing | 0 % | delays odd steps up to 75 % for a shuffled groove |
| Fill chance | 0 % | adds random hits on empty steps of unmuted lanes |
| Seed | 1 | `1`–`9999` repeats identically; `0` (`Free`) varies every play |
| L1 steps | 16 | step ring length (`1` to `32`); unequal lengths create polymeters |
| L1 pulses | 4 | Euclidean hit count distributed evenly across `L1 steps` |
| L1 chance | 100 % | per-hit firing probability (`0` to `100 %`) |
| L1 note | C2 | outgoing MIDI note (`36` = Kick on Neo Drums, or any synth pitch) |

## Starting points

- Drum kits (wire to Neo Drums): "Four on the floor — Euclidean hats", "West African — Bell E(7,12)", "Clave — Son E(5,16)", "Percussion — Sparse probability", "Odd meter — E(5,8) groove", "Hats — Ratchet flurry", "Polyrhythm — 3 vs 4 vs 5".
- Melodic synth lines (wire to Neo Poly): "Melodic — Pentatonic lanes".
- Quick 7-over-12 bell groove: `Rate` `1/16`, `L1 steps` `12`, `L1 pulses` `3`, `L1 note` `36`, `L5 steps` `12`, `L5 pulses` `7`, `L5 note` `56`.

## Pitfalls

- Neo Pulse makes no audio by itself: connect it with `note_wire_connect(source="Pulse", target="Drums")` to hear it.
- With `Fill chance` above `0 %`, any unmuted lane with `Pulses` `0` will still receive fill hits on its empty steps; switch `Mute` on for unused lanes when using fills.
- Nothing is sent while the transport is stopped; start playback or run `render` to hear the rhythm.
- `Pulses` and `Rotation` clamp internally to `Steps` and `Steps − 1`, so shortening `Steps` never goes out of range.

## Pairs well with

- Neo Drums for Euclidean drum kits and percussion, Neo Poly or Neo FM6 for polymetric synth lines, and Neo Delay or Neo Verb after the target instrument.

## Checklist

- [ ] `note_wire_connect(source="Pulse", target="Drums")` wires Neo Pulse to the target instrument.
- [ ] `preset_load(machine="Pulse", preset="Four on the floor — Euclidean hats")` and `render(probes=["Drums"])` confirms beats land cleanly on the grid.
- [ ] `param_get(machine="Pulse", params=["Rate", "Swing", "Seed"])` verifies clock and seed settings.
- [ ] `transport(action="stop")`, then `machine_readings(machine="Drums")`: no note is left sounding.
