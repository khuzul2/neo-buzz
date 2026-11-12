# Mixing dynamics: compression and sidechain

Compressor starting points by source and the sidechain recipe that keeps the kick and bass apart, on Neo Comp. Gain staging, balance, EQ, space and stereo are in the mixing guide (`guide(topic="mixing")`).

## Compression starting points (Neo Comp)

**Threshold from a measurement.** The reduction at a level L over the threshold T is (L − T) × (1 − 1/ratio). Measure the part's loud level on its probe (`analyze(source=…, metrics=["dynamics"])`, `rms_percentiles_dbfs.p95`) and set T = p95 − reduction × ratio ÷ (ratio − 1): for 4 dB at 4:1, T = p95 − 5.3 dB. Then measure after the compressor: the crest factor falls by roughly the reduction on peaks.

| Part | Mode | Ratio | Attack | Release | Knee | Reduction | Mix | Start from |
|---|---|---|---|---|---|---|---|---|
| Kick | Clean | 4:1 | 10–30 ms | 50–100 ms | 6 dB | 3–6 dB | 100 % | |
| Snare | FET | 4:1 | 5–10 ms | 80–150 ms | 2 dB | 3–6 dB | 100 % | |
| Bass, even | Opto | 4:1 | 10 ms | 800 ms or Auto release | 18 dB | 3–6 dB | 100 % | "Opto — Bass Leveler" |
| Bass, aggressive | FET | 8:1 | 1 ms | 150 ms | 4 dB | 4–8 dB | 100 % | "FET — Bass Tamer" |
| Lead or vocal line | FET, then Opto | 4:1, then 3:1 | 0.25–3 ms, then 10 ms | 50–80 ms, then 1.5 s | 2, then 18 dB | 3–6, then 2–3 dB | 100 % | "FET — Vocal Grab", "Opto — Smooth Vocal" |
| Pads | Opto | 2:1 | 10 ms | 1.5 s | 18 dB | 1–3 dB | 100 % | |
| Plucks, keys | Clean | 3:1 | 5–15 ms | 100 ms | 6 dB | 2–4 dB | 100 % | "Clean — Gentle Leveler" |
| Drum bus | Glue | 4:1 | 3–10 ms | 100 ms or Auto | 3 dB | 2–4 dB | 100 % | "Glue — Drum Bus" |
| Parallel drums | FET | 8:1 | 1 ms | 80 ms | 0 dB | 10–15 dB | 25–40 % | "Parallel — New York Drums" |
| Mix bus | Glue | 2:1 | 10–30 ms | Auto release | 6 dB | 1–2 dB | 100 % | "Glue — Mix Bus" |

- Slow attack (10–30 ms) keeps transients; fast attack (under 1 ms) flattens them. Release short enough to recover before the next hit: under the note spacing.
- `Auto makeup` adds the gain a −12 dBFS signal loses; otherwise set `Makeup` to match the loudness before and after (compare the probe's integrated loudness).
- `SC low cut` 80–150 Hz on a bus stops the kick and bass from driving all the reduction.

Bass under control (Neo Comp after the bass):

| Parameter | Value | Why |
|---|---|---|
| Mode | Opto | smooth, program-dependent |
| Threshold | -20 dB | 3–6 dB of reduction on the loud notes |
| Ratio | 4:1 | |
| Knee | 18 dB | |
| Attack | 10 ms | the pluck stays |
| Release | 800 ms | |
| Auto makeup | On | |

## Sidechain with Neo Comp

Ducking the bass (or pads) under the kick with Neo Comp's second input.

1. `machine_add(gear="Neo Comp", name="Bass Duck", insert_after="Bass")`.
2. `preset_load(machine="Bass Duck", preset="Sidechain — Kick Duck")`, or set:

   | Parameter | Value | Why |
   |---|---|---|
   | Mode | Clean | |
   | Sidechain | External | detect from input 2 |
   | Threshold | -30 dB | below the kick's level at input 2 |
   | Ratio | 8:1 | |
   | Knee | 0 dB | |
   | Attack | 1 ms | ducks on the kick's attack |
   | Release | 150 ms | recovers before the offbeat at 124 BPM |

3. `connect(from="Kick", to="Bass Duck", input="Sidechain", gain_db=0)`. The kick keeps its own connection to Master.
4. Release against tempo: back up before the next 8th note. An 8th lasts 30000 ÷ BPM ms: 300 ms at 100 BPM, 242 at 124, 234 at 128, 214 at 140, 172 at 174. Release at 50–70 % of it.
5. Depth: bass 4–8 dB, pads and chords 2–5 dB, the audible "pump" 8–12 dB ("Sidechain — Pump": ratio ∞, release 300 ms).
6. Ghost trigger, to keep ducking where the kick stops (breakdowns): `machine_add(gear="Neo Poly", name="SC Kick", connect_to="none")` with the kick recipe from the sound-design guide, a note on every beat, and `connect(from="SC Kick", to="Bass Duck", input="Sidechain")`. Never connect it to Master.
7. Measure: `render(from=…, to=…, probes=["Bass Duck"])`, then `analyze(source="r5:Bass Duck", metrics=["dynamics"])`. `rms_over_time` should dip by the planned depth on each kick; the step is in `step_seconds` (render 2–4 bars so the steps are short).

The threshold follows the trigger: set it at the kick's peak level arriving at input 2 minus depth × ratio ÷ (ratio − 1). The kick's probe reads it after its Master connection; add the difference between the sidechain connection's gain and that one.

## Checklist

- [ ] The reduction is what the table names: `analyze(source="rN:Part", metrics=["dynamics"])` on the part, before and after, shows the crest factor moving by the amount you set.
- [ ] The compressor's values read back: `param_get(machine=…)` shows threshold, ratio, attack and release as chosen.
- [ ] The sidechain dip is there and musical: `analyze(source="rN:Part", metrics=["dynamics"])` on the ducked part shows the dip on each kick and the recovery before the next.
- [ ] Kick and bass no longer fight: `analyze_mix(section=…)` lists no masking pair between them.
