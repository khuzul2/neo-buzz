## How it sounds

Eight bands of minimum-phase EQ with zero latency, measured against the drawn design (within 0.005 dB from 20 Hz to 16 kHz). Bells peak exactly at their frequency, shelves are at half their gain there and full beyond it, low and high cuts are Butterworth (-3 dB at the cutoff, 12, 24 or 48 dB/oct), the notch is deeper than -100 dB, and sweeps do not zipper. At its defaults it changes nothing; with every band off the output equals the input bit for bit.

Wide high bells and shelves above about 10 kHz bend away from an analog curve by up to about 3 dB near 16 kHz at 44.1 and 48 kHz: `analyze` the result if that matters.

`Mode` switches to a 31-band graphic EQ, and `Phase` Linear gives a zero-phase design with a curve layer; both are in `guide(topic="neo-eq-in-depth")`. In Parametric mode the graphic sliders do nothing; in Graphic mode the eight bands rest.

Each band has `B1 on` to `B8 on`, type (Bell, Low shelf, High shelf, Low cut, High cut, Notch), freq, gain (-24 to +24 dB, bells and shelves), Q (0.71 is flat) and slope (cuts). Bands 2-7 start on at 0 dB; bands 1 (Low cut) and 8 (High cut) start off.

Beyond that, each band can work on one channel (`B1 channel`: Stereo, Left, Right, Mid or Side; a Side band never changes what a mono listener hears), take another shape (`B1 shape`: Proportional Q, Baxandall, Vintage, All-pass) or move its gain with the level it hears (`B1 dynamic` Down or Up, with threshold, ratio, knee, range, attack, release and a Band or Sidechain detector: the machine's second input). Dynamic settings do nothing on cuts, notches or All-pass bands.

## Use it for

- Corrective EQ and tone shaping on any track, mono bass and width EQ (Mid and Side bands), dynamic de-essing and resonance control, hum notches, matching a reference (`eq_fit`).

## Key controls

| Parameter | Value | Why |
|---|---|---|
| B1 type | Low cut | high-pass everything that has no business in the sub |
| B1 freq | 30 Hz | rumble; 80-150 Hz on pads and returns |
| B1 slope | 24 dB/oct | |
| B3 freq | 320 Hz | mud; find the frequency first, then make the move |
| B3 gain | -3 dB | cut narrow, boost wide |
| B3 Q | 1.4 | 0.5-1 tone, 1.4-2 a region, 4-8 a resonance |
| Output | 0 dB | the editor's Match sets it from `gain_match_db` |

Find the frequency with `analyze_mix` (it names masking bands) or `plot(kind="spectrum", sources=["r1"])`. One labelled move per band. Check each move with `compare` against the render before.

## Starting points

- "Mixing — Vocal Clarity", "Mixing — De-mud", "Mixing — Kick Punch", "Mixing — Bass Tighten", "Mixing — Snare Crack".
- "Mastering — Gentle Smile", "Mastering — Air Shelf", "Mastering — Tame Harshness", "Mastering — Low Cut 25 Hz".
- "Fix — Rumble Filter", "Fix — Hum Notch 50 Hz", "Fix — Hum Notch 60 Hz".
- "Mastering — Mono Bass (Side Cut 100 Hz)" and "Mastering — Side Air" (Side bands).
- "Dynamic — De-ess": a dynamic band down on the sibilance, static gain 0 dB, `B6 freq` 6.5 kHz, `B6 threshold` -30 dB so only the esses pass it, range 8 dB. Also "Dynamic — Resonance Control (Mid)" and "Dynamic — Bass Ducks the Kick (Sidechain)".
- Graphic and linear-phase presets are listed in the in-depth guide.

## Pitfalls

- Gain does nothing on cuts and notches; slope matters only on cuts. A Q far from 0.71 on a cut adds a resonant bump at the cutoff.
- Boosts raise the level: watch the gain into what follows (a compressor's threshold, the master's headroom).
- The `G` sliders act only in Graphic mode and the `B` bands only in Parametric mode. Loading a preset sets the mode too.
- With a band's detector on Sidechain and nothing connected to input 2, the band stays static.
- Switching a band's type, slope, channel, shape or dynamic setting fades over 10 ms (no click). Static moves first, dynamic ones for what comes and goes.
- `machine_readings` gives `b1_dynamic_db` to `b8_dynamic_db` (the change now, negative is down), `gain_match_db`, `q_capped_bands` and `pre_ringing_db`.

## Pairs well with

- Neo Comp, Neo Multiband, Neo Drive and Neo Limiter after it in a chain (`guide(topic="mastering-chain")`); Neo Analyzer to check the result.
- `eq_fit(source="r4", target="C:/Refs/track.wav", machine="M EQ")` fits bells and shelves to a reference's tonal difference and writes them into bands 2-7 as one undo step; without `machine` it only returns the changes.

## Successor

Neo EQ replaces Pedal EQ, Jeskola EQ-3 and Joachims Hyperion (its graphic mode): `machine_replace(machine="EQ")` carries their curves over.

## Checklist

- [ ] `machine_readings(machine="M EQ")`: `gain_match_db` shows what the moves did to loudness; set Output to match before judging.
- [ ] `analyze(source="master", metrics=["spectrum", "tonal"])` before and after: only the intended region moved.
- [ ] `compare` the render with and without the machine: a cut that loses the part's character is too wide.
