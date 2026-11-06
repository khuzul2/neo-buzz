## How it sounds

A multiband compressor and upward expander: the input split into 3 or 4 bands, each with its own dynamics, then summed. With every band at 1:1 and 0 dB it passes the input untouched (in linear phase, delayed by the latency, bit for bit), so it only changes what you ask it to.

| Phase | What it does | Use it for |
|---|---|---|
| Linear (the default) | the bands sum back to the input exactly, with no phase shift; about 96 ms of latency (4607 samples at 44.1 and 48 kHz), which NeoBuzz Live compensates | mastering, and any bus where phase matters (parallel paths, mid/side) |
| Minimum | zero latency, the same band shapes (Linkwitz–Riley, 24 dB/oct, −6 dB at a crossover), the crossovers' phase shift | live playing, buses where the latency would be in the way |

Each band's ratio works above its threshold: above 1:1 it compresses (∞:1 limits), below 1:1 it expands upward (0.5:1 doubles how far a peak rises over the threshold), 1:1 does nothing. `Range` caps the change either way. Measured: a band's static curve is Neo Comp's gain computer to 0.0003 dB; attack and release times within 2.5 % of their settings; with every band at 1:1 the input comes out bit for bit, 4607 samples later at 44.1 and 48 kHz.

## Signal flow

Input → crossovers (`Crossover 1`, `Crossover 2`, and with 4 bands `Crossover 3`; never closer than the one below) → per band: detector (the band's peak; `Stereo` L/R or M/S, `Link`) → threshold, ratio, knee, `Range` → attack and release → `Makeup` → `Solo`, `Bypass` → the bands summed → `Mix` against the dry signal (latency-aligned, so parallel compression never combs) → `Output`.

- Band 1 is everything below `Crossover 1`, band 2 between `Crossover 1` and `Crossover 2`, and so on; the top band is everything above the last crossover.
- `Attack` is the time to 63 % of the gain change, `Release` back to 37 %, either way (compression or expansion).
- `Solo` on any band plays only the soloed bands (`Mix` is ignored meanwhile); `Bypass` passes a band untouched.
- Changing `Phase` while playing fades out, switches and fades back in (into linear phase, after its latency); NeoBuzz Live realigns the other paths.
- `machine_readings(machine=…)` gives each band's gain change now (`b1_gr_db` …), its largest over the last 3 s (`b1_gr_peak_db`) and its peak level (`b1_level_db`, what the threshold compares against), and `latency_samples`.

## How to use it

- In a master chain after the EQ, before the limiter: `machine_add(gear="Neo Multiband", name="M Bands", insert_after="M EQ")`, then a preset (`preset_load(machine="M Bands", preset="Mastering — Gentle Glue (4 Bands)")`).
- Thresholds from the band's level: render, `machine_readings(machine="M Bands")`, then set each band's threshold 3–6 dB under its `bN_level_db` for 2–4 dB of reduction at 2:1 to 3:1. Check with `machine_readings` again: the reduction sits in `bN_gr_peak_db`.
- Fix one band and leave the rest: set the others to 1:1 (`B2 ratio` "1:1") and only the band that misbehaves compresses.
- Put a crossover at the problem: a de-esser is the top band above 5–8 kHz, mud control a band around 200–500 Hz, boom control band 1 below 80–100 Hz.
- Upward expansion (ratio under 1:1, `Range` 3–6 dB) restores transients a mix lost, or lifts the air band only when it is there.
- Check: render, `analyze(source=…, metrics=["loudness", "dynamics", "spectrum"])` against the input: loudness within 0.5 LU after makeup, the fixed band 1–4 dB lower on its peaks.

Mastering, gentle control of the lows (one band working):

| Parameter | Value | Why |
|---|---|---|
| Bands | 4 bands | |
| Crossover 1 | 100 Hz | the kick and bass |
| B1 threshold | -30 dB | 2–3 dB of reduction on the kick |
| B1 ratio | 3:1 | |
| B1 attack | 20 ms | the kick's click passes |
| B1 release | 200 ms | |
| B1 range | 12 dB | |
| B1 makeup | 1.5 dB | loudness matched |
| B2 ratio | 1:1 | untouched |
| B3 ratio | 1:1 | |
| B4 ratio | 1:1 | |

De-esser on a vocal bus:

| Parameter | Value | Why |
|---|---|---|
| Crossover 3 | 5 kHz | the top band is the sibilance |
| B1 ratio | 1:1 | |
| B2 ratio | 1:1 | |
| B3 ratio | 1:1 | |
| B4 threshold | -42 dB | |
| B4 ratio | 6:1 | |
| B4 attack | 0.5 ms | catches the "s" |
| B4 release | 60 ms | |
| B4 range | 9 dB | never more than 9 dB down |

Parallel multiband squash on drums:

| Parameter | Value | Why |
|---|---|---|
| Bands | 3 bands | |
| Crossover 1 | 120 Hz | |
| Crossover 2 | 3 kHz | |
| B1 threshold | -40 dB | |
| B1 ratio | 8:1 | |
| B2 threshold | -42 dB | |
| B2 ratio | 8:1 | |
| B3 threshold | -44 dB | |
| B3 ratio | 8:1 | |
| B1 makeup | +10 dB | |
| B2 makeup | +10 dB | |
| B3 makeup | +10 dB | |
| Mix | 40% | the squashed bands under the dry drums, in phase |

## Gotchas

- The defaults compress (every band 2:1 at −20 dB): set the bands you don't want to 1:1, or start from a preset.
- Linear phase delays by about 96 ms. NeoBuzz Live delays the other paths to match when its machine delay compensation is on (an engine setting, off by default: Preferences ▸ Engine on Windows, Preferences ▸ General in the studio); with it off, a parallel path around it combs, and a live input monitored through it arrives late. Use `Phase` Minimum there, or turn the compensation on. Renders start that much later: give them the tail.
- The linear-phase crossovers ring a little before a transient below about 100 Hz (pre-echo, the price of no phase shift); minimum phase rings after it instead.
- `Crossover 3` only matters with 4 bands. A crossover set below the one before it sits at that one.
- Thresholds read the band's peak, not the whole signal's: a band holds only part of the energy, so its level (`bN_level_db`) is lower than the input's.
- Upward expansion raises peaks: keep `Range` small (3–6 dB) and a limiter after it.
- `Mix` is ignored while a band is soloed; solo is for listening, not for the final setting.

## Preset map

| Preset | What |
|---|---|
| "Mastering — Gentle Glue (4 Bands)" | 1.5:1 everywhere, soft knees, slow on the lows, faster on top |
| "Mastering — Tight Low End" | only band 1 (under 100 Hz), 3:1 |
| "Mastering — De-Harsh 2–6 kHz" | only band 3 (2–6 kHz), 3:1, fast, at most 6 dB |
| "Mastering — Air Lift (Upward Expansion)" | band 4 above 10 kHz expands upward, at most 3 dB |
| "Mastering — Transient Restore (Upward Expansion)" | three bands at 0.85:1, at most 3 dB: the peaks back |
| "Mastering — Loud and Dense" | 3:1 everywhere, +6 dB makeup |
| "Mastering — Mid/Side Control (Unlinked)" | mid and side compressed each by their own level |
| "Mastering — Zero-Latency Glue (Minimum Phase)" | the gentle glue, minimum phase |
| "Bus — Drum Bus Punch (3 Bands)" | slow attack on the lows, faster above, makeup |
| "Bus — Mix Bus Glue (3 Bands)" | 2:1, 30 ms attack on three bands |
| "Bus — Bass Leveler" | the lows and low mids levelled |
| "Vocal — De-Esser Above 5 kHz" | band 4 above 5 kHz, 6:1, fast, at most 9 dB |
| "Vocal — Smooth Mids 300 Hz–3 kHz" | only the middle band, 2.5:1 |
| "Fix — De-Mud 200–500 Hz" | only band 2 (200–500 Hz), 4:1, at most 6 dB |
| "Fix — Boom Control Below 90 Hz" | only band 1 (under 90 Hz), 6:1, at most 9 dB |
| "Creative — Heavy Squash (Parallel 50 %)" | 10:1 on every band, +12 dB makeup, half wet |
| "Utility — Linear-Phase Split (Transparent)" | every band 1:1: the input, delayed, bit for bit |
