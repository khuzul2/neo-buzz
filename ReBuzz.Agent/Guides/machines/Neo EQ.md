## How it sounds

Eight bands of minimum-phase EQ with zero latency, measured against the drawn design: the response is within 0.005 dB of it from 20 Hz to 16 kHz.

- Bells peak exactly at their frequency.
- Shelves are at half their gain at the frequency, and full gain beyond it.
- Low and high cuts are Butterworth, −3 dB at the cutoff, at 12, 24 or 48 dB/oct.
- The notch is deeper than −100 dB.
- Sweeps don't zipper.
- At its defaults it changes nothing. With every band off, the output equals the input bit for bit.

Near the top of the spectrum, wide high bells and shelves above about 10 kHz bend away from an analog EQ's curve, by up to about 3 dB near 16 kHz at 44.1 and 48 kHz. If that matters, `analyze` the result.

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

## Gotchas

- `B<n> gain` does nothing on cuts and notches. `B<n> slope` matters only on cuts.
- A Q far from 0.71 on a cut gives it a resonant bump at the cutoff.
- Boosts raise the level: watch the gain into what follows (Neo Comp's threshold, the master's headroom).

## Preset map

| Preset | What |
|---|---|
| "Mixing — Vocal Clarity" | 90 Hz high-pass, a small mud cut, +2 dB at 3 kHz and on the top shelf |
| "Mixing — Kick Punch" | +3 dB at 60 Hz, −4 dB box cut at 350 Hz, +3 dB click |
| "Mixing — Bass Tighten" | 35 Hz high-pass, a low-mid cut, +2 dB growl at 800 Hz |
| "Mixing — Snare Crack" | +2 dB body at 200 Hz, −3 dB box at 500 Hz, +3 dB crack at 5 kHz |
| "Mixing — Acoustic Guitar" | 80 Hz high-pass, −2 dB boom at 200 Hz, a top-shelf lift |
| "Mixing — De-mud" | −3 dB at 300 Hz |
| "Mixing — Telephone" | 400 Hz to 3 kHz at 48 dB/oct, mids lifted: an effect |
| "Mastering — Gentle Smile" | +1 dB at 80 Hz, −0.7 dB at 300 Hz, +1.5 dB shelf at 12 kHz |
| "Mastering — Air Shelf" | +2 dB shelf at 12 kHz |
| "Mastering — Tame Harshness" | −1.5 dB at 3.5 kHz, −1 dB at 7 kHz |
| "Mastering — Low Cut 25 Hz" | the master's high-pass, 48 dB/oct |
| "Fix — Rumble Filter" | a 40 Hz high-pass |
| "Fix — Hum Notch 50 Hz" | notches at 50, 100 and 150 Hz |
| "Fix — Hum Notch 60 Hz" | notches at 60, 120 and 180 Hz |
