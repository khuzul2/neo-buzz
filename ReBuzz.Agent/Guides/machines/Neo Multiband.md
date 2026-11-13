## How it sounds

A multiband compressor and upward expander: the input split into 3 or 4 bands, each with its own dynamics, then summed. With every band at 1:1 and 0 dB it passes the input untouched (bit for bit, delayed by the latency), so it changes only what you ask. Above 1:1 a band compresses (infinity limits), below 1:1 it expands upward, 1:1 does nothing; `Range` caps the change either way. A band's static curve is Neo Comp's gain computer to 0.0003 dB.

| Phase | What it does | Use it for |
|---|---|---|
| Linear (default) | bands sum back exactly, no phase shift; about 96 ms of latency (4607 samples), compensated | mastering, buses where phase matters |
| Minimum | zero latency, Linkwitz-Riley 24 dB/oct crossovers with their phase shift | live playing |
| Dynamic | zero latency, no static phase shift: input plus each band's gain change times the band | mastering where latency matters; cuts are shallower than set (14 dB measures 8.7), so keep control gentle |

Path: crossovers (`Crossover 1` to `Crossover 3`, never closer than the one below), per band a detector (peak or RMS; `Stereo` L/R or M/S, `Link`), threshold, ratio, knee, `Range`, attack and release (63 % and 37 % times), `Makeup`, `Solo` and `Bypass`, the sum, then `Mix` (latency-aligned, so parallel compression never combs) and `Output`. `Lookahead` adds to the latency, fading over 10 ms when changed. `B1 upward ratio` to `B4 upward ratio` lift a band under its threshold (nothing under -80 dBFS). `B1 sidechain` (any band) External reads a machine on input 2 through the same crossovers: a kick on input 2 ducks the lows; nothing connected means silence.

## Use it for

- Fixing one band in a master (boomy kick, harsh 2-6 kHz, sibilance) while the rest is untouched.
- Gentle multiband glue, upward lift of air or detail, transient restore (ratio under 1:1), kick-keyed ducking of the lows.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bands | 4 bands | 3 bands ignore Crossover 3 |
| Crossover 1 | 100 Hz | the kick and bass band |
| B1 threshold | -30 dB | 2-3 dB of reduction; set it 3-6 dB under `b1_level_db` |
| B1 ratio | 3:1 | the other bands at 1:1 so only this one works |
| B1 attack | 20 ms | the kick's click passes |
| B1 release | 200 ms | |
| B1 makeup | 1.5 dB | loudness matched |
| Mix | 100% | 40 % under dry drums for parallel squash |

The defaults compress (every band 2:1 at -20 dB): set the bands you do not want to 1:1, or start from a preset.

## Starting points

- "Mastering — Gentle Glue (4 Bands)", "Mastering — Tight Low End" (band 1 only), "Mastering — De-Harsh 2–6 kHz" (band 3 only), "Mastering — Air Lift (Upward Expansion)", "Mastering — Transient Restore (Upward Expansion)", "Mastering — Quiet Detail Lift (Upward)".
- "Mastering — Zero-Latency Glue (Minimum Phase)", "Mastering — Dynamic-Phase Glue (Zero Latency)", "Mastering — Mid/Side Control (Unlinked)", "Mastering — Loud and Dense".
- "Bus — Drum Bus Punch (3 Bands)", "Bus — Mix Bus Glue (3 Bands)", "Bus — Kick Duck on the Lows (Sidechain)", "Bus — Bass Leveler".
- "Vocal — De-Esser Above 5 kHz" (Crossover 3 5 kHz, B4 6:1, attack 0.5 ms, range 9 dB), "Fix — De-Mud 200–500 Hz", "Fix — Boom Control Below 90 Hz", "Creative — Heavy Squash (Parallel 50 %)".

## Pitfalls

- Linear phase delays by about 96 ms. The engine delays other paths to match (machine delay compensation, on by default); with it off, a parallel path combs and a live input arrives late: use Minimum phase there. Renders start later: give them a tail.
- Linear-phase crossovers ring a little before a transient below about 100 Hz; Minimum rings after it.
- Thresholds read the band's peak, which is lower than the whole signal's: use `machine_readings` (`b1_level_db`, `b1_gr_db`, `b1_gr_peak_db`) to set them.
- Upward expansion raises peaks: keep Range 3-6 dB and a limiter after it. Mix is ignored while a band is soloed.
- A crossover set below the one before it sits at that one.

## Pairs well with

- Neo EQ before it, Neo Comp for broad glue, Neo Limiter and Neo Dither after it, Neo Analyzer to see the bands (`guide(topic="mastering-chain")`).

## Checklist

- [ ] `machine_add(gear="Neo Multiband", name="M Bands", insert_after="M EQ")`, then `preset_load(machine="M Bands", preset="Mastering — Gentle Glue (4 Bands)")`.
- [ ] `machine_readings(machine="M Bands")`: the working band's `b1_gr_peak_db` is 2-4 dB, the other bands about 0.
- [ ] `analyze(source="master", metrics=["loudness", "dynamics", "spectrum"])`: loudness within 0.5 LU after makeup, the fixed band 1-4 dB lower on its peaks.
- [ ] `latency_samples` in `machine_readings` is what you expect for the Phase you chose.
