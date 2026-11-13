## How it sounds

Eight bands of minimum-phase EQ with zero latency, measured against the drawn design: the response is within 0.005 dB of it from 20 Hz to 16 kHz.

- Bells peak exactly at their frequency.
- Shelves are at half their gain at the frequency, and full gain beyond it.
- Low and high cuts are Butterworth, −3 dB at the cutoff, at 12, 24 or 48 dB/oct.
- The notch is deeper than −100 dB.
- Sweeps don't zipper.
- At its defaults it changes nothing. With every band off, the output equals the input bit for bit.

Near the top of the spectrum, wide high bells and shelves above about 10 kHz bend away from an analog EQ's curve, by up to about 3 dB near 16 kHz at 44.1 and 48 kHz. If that matters, `analyze` the result.

`Mode` switches it to a 31-band graphic EQ (`guide(topic="neo-eq-in-depth")`). In `Parametric` mode, the default, the graphic sliders do nothing; in `Graphic` mode the eight bands rest.

Each band can also work on one channel, take another shape, or move its gain with the level it hears (a dynamic EQ): see "Channels, shapes and dynamic bands" below. At their defaults (Stereo, Standard, dynamic Off) the bands sound exactly as before.

## Signal flow

Bands 1 to 8 in series. Each band has these parameters:

- `B1 on` … `B8 on`: in or out.
- `B1 type` … `B8 type`: Bell, Low shelf, High shelf, Low cut, High cut or Notch.
- `B1 freq` … `B8 freq`: Hz.
- `B1 gain` … `B8 gain`: −24 to +24 dB in 0.1 dB steps, for bells and shelves.
- `B1 Q` … `B8 Q`: the width of a bell or notch, or the resonance of a shelf or cut. 0.71 is flat.
- `B1 slope` … `B8 slope`: for cuts.

Bands 2–7 start on at 0 dB, so they change nothing until given a gain. Bands 1 (a Low cut) and 8 (a High cut) start off.

## How to use it

- **Find the frequency first, then make the move.** `analyze_mix` names masking bands. `plot(kind="spectrum", sources=[…])` shows resonances.
- **One labelled move per band**, e.g. "B3: Bell, 320 Hz, −3 dB, Q 1.4".
- **Cut narrow, boost wide.**
  - Q 0.5–1: tone.
  - Q 1.4–2: a region.
  - Q 4–8: a ringing resonance.
  - Notch: hum.
- **High-pass everything that has no business in the sub** (pads, keys, reverb returns), with band 1:

| Parameter | Value | Why |
|---|---|---|
| B1 on | On | |
| B1 type | Low cut | |
| B1 freq | 30 Hz | rumble; 80–150 Hz on pads and returns |
| B1 slope | 24 dB/oct | |
| B3 type | Bell | |
| B3 freq | 320 Hz | mud |
| B3 gain | -3 dB | |
| B3 Q | 1.4 | |

- **Check after each move:** render and `compare` against the render before. A cut that also loses the part's character is too wide.
- **Matching a reference or a curve:** `eq_fit(source="r4", target="C:/Refs/track.wav", machine="M EQ")` fits a few bells and shelves to the tonal difference on this EQ's own response and writes them into B2–B7 (Mode Parametric, those bands set to Stereo, Standard and static, the ones left over at 0 dB, B1 and B8 untouched) as one undo step; `eq_fit(curve=[{"hz": 200, "db": 0}, {"hz": 400, "db": -2}, {"hz": 800, "db": 0}], bands=1, machine="M EQ")` does it for a change you describe. Without `machine` it only returns the bands and their `param_set` changes. Its `rms_after_db` is what the bands leave.

## Channels, shapes and dynamic bands

- `B<n> channel`: Stereo (both, the default), Left, Right, Mid ((L+R)/2) or Side ((L−R)/2). A Mid or Side band leaves the other half alone: a Side band never changes what a mono listener hears (L+R, within the last bit of rounding), a Mid band never changes the width. Typical: a Side low cut at 100–150 Hz (mono bass), a Side high shelf (air in the width only), a Mid bell on a vocal's harshness.
- `B<n> shape`:
  - Standard: the type as it is.
  - Proportional Q: a bell narrows as its gain grows (Q × 10^(|gain|/40), about twice as narrow per 12 dB): wide for small moves, focused for big ones.
  - Baxandall: a shelf becomes first order, 6 dB/oct, half its gain at its frequency; `Q` does nothing. The broad, gentle tilt of a hi-fi tone control.
  - Vintage: a shelf plus its complementary dip, a bell of half its gain the other way, Q 1, an octave above a low shelf (below a high shelf), as a Pultec's boost-and-cut trick: a boost under 60 Hz with the mud above it pulled down.
  - All-pass: the band only turns the phase (second order at its frequency, `Q` its width), whatever the type: to nudge a bass against a kick without changing either's level.
- `B<n> dynamic`: Off (static), Down or Up. A dynamic band hears its own shape (a band-pass at its frequency and Q for a bell, everything under a low shelf's frequency, over a high shelf's) and, above `B<n> threshold`, moves its gain down (or up) by 1 − 1/ratio dB for each dB over, at most `B<n> range`, with `B<n> knee`, `B<n> attack` and `B<n> release` (the same curve as Neo Comp's). Bells and shelves only; the gain stays within ±30 dB. Measured: the static curve within 0.01 dB of that rule, attack and release within 10 % of their times from 10 ms (the detector adds about 0.5 ms at 4 kHz, 0.9 ms at 1 kHz).
- `B<n> detector`: Band (its own input) or Sidechain: the machine's second input (connect a machine to Neo EQ's input 2), heard through the same shape and channel. Typical: the bass's low shelf ducking under the kick.
- `machine_readings` gives `b1_dynamic_db` … `b8_dynamic_db` (the change now, negative = down) and `gain_match_db`.
- `Delta`: hear only what the EQ changes (the output minus the input). `Output` comes after it. The editor's Match sets `Output` from `gain_match_db` (one undo step): judge a move at equal loudness.

A de-esser on a vocal (or a bus): a dynamic band down on the sibilance, nothing static:

| Parameter | Value | Why |
|---|---|---|
| B6 freq | 6.5 kHz | the "s"; sweep 5–9 kHz to find it |
| B6 Q | 2 | |
| B6 gain | 0 dB | static: nothing happens between the esses |
| B6 dynamic | Down | |
| B6 threshold | -30 dB | so only the esses pass it: read `b6_dynamic_db` while they play |
| B6 range | 8 dB | |
| B6 attack | 1 ms | |
| B6 release | 60 ms | |

Static moves first, dynamic ones for what comes and goes (a resonance on some notes, harshness on loud passages, boom when the bass player digs in): a static cut there thins every other moment.

Graphic mode, linear phase with the curve layer, and the map of the factory presets are in `guide(topic="neo-eq-in-depth")`.

## Gotchas

- `B<n> gain` does nothing on cuts and notches. `B<n> slope` matters only on cuts.
- A Q far from 0.71 on a cut gives it a resonant bump at the cutoff.
- Boosts raise the level: watch the gain into what follows (Neo Comp's threshold, the master's headroom).
- The `G …` sliders only act in Graphic mode, the `B…` bands only in Parametric mode. Loading a preset sets the mode too.
- A dynamic setting on a cut or a notch does nothing (they have no gain to move); nor on an All-pass band.
- With `B<n> detector` Sidechain and nothing connected to input 2, the band stays static.
- Switching a band's type, slope, channel, shape or dynamic fades it out and in over 10 ms (no click).

