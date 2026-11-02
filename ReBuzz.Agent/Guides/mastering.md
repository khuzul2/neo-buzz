# Mastering

Bring a finished mix to a platform's loudness and peak target without hurting it. Change one stage at a time, measure the whole song, and keep a change only when the numbers say it helped.

## Targets

| Target | Integrated | True peak | Notes |
|---|---|---|---|
| Streaming (Spotify, YouTube, Tidal, Amazon) | −14 LUFS ±1 | −1 dBTP or lower | Louder masters are turned down to about −14. Spotify asks for −2 dBTP when a master is louder than −14 LUFS |
| Apple Music | −16 LUFS (Sound Check) | −1 dBTP or lower | A −14 master simply plays 2 dB quieter |
| Club, DJ | −9 to −7 LUFS (loudest EDM −6) | −0.5 dBTP or lower; −1 if it will be encoded | No normalisation in clubs |
| Broadcast, EBU R128 (Europe) | −23 LUFS ±0.5 | −1 dBTP or lower | |
| Broadcast, ATSC A/85 (US) | −24 LKFS ±2 | −2 dBTP or lower | |
| CD, download | −12 to −9 LUFS by genre | −1 dBTP or lower | Safe for later lossy encoding |
| Podcast, spoken word | −16 LUFS (−19 for mono) | −1 dBTP or lower | |

Dynamics by style. PLR is true peak minus integrated loudness (`analyze` reports it under `dynamics`); loudness range (LRA) under `loudness`.

| Style | Integrated | PLR | Loudness range |
|---|---|---|---|
| Ambient, acoustic, jazz | −18 to −14 LUFS | 12–18 dB | 8–15 LU |
| Pop, rock, synthwave | −14 to −9 | 8–12 | 4–8 |
| House, techno, DnB, hip-hop, trap | −10 to −6 | 6–9 | 3–6 |
| Lo-fi | −14 to −11 | 9–12 | 4–8 |

A −14 LUFS master at −1 dBTP has a PLR of 13 dB: plenty. Below 6 dB the master is crushed.

## Before you start

- The mix: true peak −3 dBTP or lower (−6 is better), 0 clipped samples, lows mono, a balanced tilt (the mixing guide's checks). If not, go back and fix the mix: mastering can't.
- Integrated loudness is a whole-song measure: `render()` the whole song for every decision on loudness. For quick iterations use the loudest section, `render(section="Drop")`, then confirm on the whole song.
- `snapshot_save(name="master 0")` before the first stage.

## The chain

Master has no insert slots: the chain sits in front of it.

1. `machine_list`: note every machine connected to Master and its gain.
2. `machine_add(gear="Neo EQ", name="M EQ", connect_to="Master")`.
3. For each of those machines: `disconnect(from="Bass Duck", to="Master")`, then `connect(from="Bass Duck", to="M EQ", gain_db=-4)` with the same gain it had.
4. Each next stage goes after the previous: `machine_add(gear="Neo Comp", name="M Glue", insert_after="M EQ")`, `machine_add(gear="Neo Drive", name="M Sat", insert_after="M Glue")`, `machine_add(gear="Neo Limiter", name="M Limit", insert_after="M Sat")`.
5. `machine_list` again: only the last stage reaches Master. Render and confirm the loudness hasn't moved before you set anything.

Without Neo EQ, start the chain with the compressor. Without Neo Limiter, end it with Neo Comp as a limiter (below).

### 1. Corrective EQ (Neo EQ)

One move per band of Neo EQ: `B1` (Low cut) is the high-pass (`B1 on` On), bands 2–7 are bells and shelves (`B<n> type`, `B<n> freq`, `B<n> gain`, `B<n> Q`), `B8` (High cut) a low-pass if ever needed.

| Move | Setting | When |
|---|---|---|
| High-pass | 20–30 Hz, 12–24 dB/oct | always: rumble and DC |
| Low shelf | ±0.5–1.5 dB at 80–120 Hz | lows off against the reference or the style |
| Broad bell | ±0.5–2 dB, Q 0.5–1, 200–500 Hz | mud or thinness |
| Narrow cut | −1 to −3 dB, Q 3–6 | a ringing third octave 4 dB or more over its neighbours |
| High shelf | ±0.5–2 dB at 8–12 kHz | dull or harsh against the reference |

All moves together within ±3 dB. More means the mix needs fixing.

### 2. Glue compression (Neo Comp)

| Parameter | Value | Why |
|---|---|---|
| Mode | Glue | the bus character |
| Threshold | -18 dB | 1–2 dB of reduction on the loudest parts |
| Ratio | 2:1 | |
| Knee | 6 dB | |
| Attack | 30 ms | the drums' transients pass |
| Release | 300 ms | |
| Auto release | On | fast after hits, slow under sustained parts |
| SC low cut | 80 Hz | the kick alone doesn't drive it |
| Character | 20% | |
| Makeup | 1 dB | about the reduction: loudness matched within 0.3 LU |
| Mix | 100% | |

Gentler: "Mastering — Transparent" (Clean, 1.5:1, 1 ms lookahead, SC low cut 80 Hz). After it: PLR 0.5–2 dB lower, loudness matched, tilt unchanged.

### 3. Saturation (Neo Drive)

| Parameter | Value | Why |
|---|---|---|
| Mode | Tape | soft, even |
| Drive | 4 dB | |
| Character | 30% | |
| Tone | 0% | no tilt |
| Auto gain | On | level unchanged |
| Mix | 40% | parallel: density without dulling |
| Quality | 8× | the cleanest highs on a full mix |

After it: PLR 0.5–1 dB lower, tilt within ±0.3 dB/oct, `high_mid` up no more than 1 dB. Skip it on a mix that is already dense or saturated. A lighter touch: "Utility — Subtle Glue". Its `Output` (−24 … +24 dB) is a clean gain stage into the limiter.

### 4. Limiter

Neo Limiter: `Ceiling` (dBTP), `Input gain` (how hard it is driven), `Auto release` or `Release`, `Lookahead` (default is fine), `Stereo link` 100 %. Set:

| Setting | Streaming | Club | Broadcast |
|---|---|---|---|
| Ceiling | −1.0 dBTP | −0.5 dBTP | −1.0 (EBU), −2.0 (ATSC) dBTP |
| Release | auto, or 50–200 ms | auto, or 30–100 ms | auto |
| Gain into it | until the integrated target | the same | the same |
| Reduction on the loudest peaks | 3–4 dB at most | 6–8 dB at most | little or none |

It has no target-LUFS mode: render, read `integrated_lufs`, and raise `Input gain` by the difference (it is close to dB for dB until the limiting is heavy), then render again.

Without Neo Limiter, Neo Comp as the last stage (it catches peaks within about 1 dB, but isn't a true-peak limiter, so the threshold sits 1.5 dB under the ceiling):

| Parameter | Value | Why |
|---|---|---|
| Mode | Clean | |
| Threshold | -2.5 dB | for a −1 dBTP ceiling |
| Ratio | ∞:1 | limiting |
| Knee | 0 dB | |
| Attack | 0.05 ms | |
| Release | 80 ms | |
| Lookahead | 2 ms | sees the peaks coming |
| Makeup | 0 dB | |

## Reaching the target

1. Render (the loudest section first) and `analyze(source=…, metrics=["loudness", "peaks", "dynamics"])`.
2. Raise the gain into the limiter by the difference: target − measured. With Neo Drive before it: `param_set(machine="M Sat", changes=[{"param": "Output", "by": "+3 dB"}])`.
3. Render the whole song and measure again. A song with quiet parts reads 1–3 LU lower over the whole than in its loudest section; the whole-song number is the one that counts.
4. Repeat until within the tolerance: two or three passes. Once the limiter works hard, each dB of gain gives less than 1 LU.
5. Stop when the limiter's reduction or the PLR leaves the table's range: the target is too loud for this mix. Lower the target or go back and tame the kick and bass peaks in the mix.

## Measure after every change

| After | Must hold |
|---|---|
| EQ | bands move only where intended, by about the gain set; loudness within 0.5 LU (or compensate) |
| Glue | PLR 0.5–2 dB lower; loudness matched; tilt unchanged |
| Saturation | PLR 0.5–1 dB lower; tilt within ±0.3 dB/oct; `high_mid` up 1 dB at most |
| Limiter | integrated on target; true peak at or under the ceiling; 0 clipped samples; PLR in the style's range |
| The whole chain | stereo correlation and per-band width within ±0.05 of the mix; lows mono |

`snapshot_save(name="master 1 eq")` before each stage, `snapshot_diff(a="master 0")` to list what changed, `undo` a stage that measures worse. `compare(a=<the mix render>, b=<the master render>)` shows what the chain did: loudness, peaks, dynamics, stereo, and the bands level-matched (louder always seems better, so judge tone on those). `plot(kind="loudness", sources=[…])` shows loudness over the whole song.

## What not to do

- Don't push a streaming master far past −14 LUFS for loudness: services turn it down and the extra limiting costs punch. Dense genres can sit at −10 to −8 if they hold up, with the true peak at −1 dBTP (−2 for Spotify above −14 LUFS).
- Don't let anything that will be encoded (MP3, AAC, Opus) exceed −1 dBTP: encoding adds inter-sample peaks. Read true peak, not sample peak.
- Don't EQ more than ±3 dB in total, and don't boost narrow bands.
- Don't boost the lows on the master: +3 dB at 60 Hz costs limiter headroom on every kick.
- Don't widen the master or its lows. Correlation stays above 0, lows mono.
- Don't stack limiters, or clip and limit, without measuring each stage.
- Don't master a clipping mix.
- Don't judge loudness on a loop: integrated loudness and loudness range are whole-song measures.
- Don't put reverb, ducking or stereo effects on the master.

## Checklist

- [ ] Integrated loudness on target for the whole song: `analyze(source=…, metrics=["loudness"])` on a full `render()`.
- [ ] True peak at or under the ceiling and no clipping: `analyze(source=…, metrics=["peaks"])`.
- [ ] Dynamics in the style's range: PLR and loudness range from `analyze(source=…, metrics=["dynamics", "loudness"])`.
- [ ] Tonal balance as intended: `compare(a=…, b=…)` of the mix and the master shows level-matched bands within ±1 dB, and the tilt within ±0.5 dB/oct, unless that was the point.
- [ ] Stereo safe: `analyze(source=…, metrics=["stereo"])` shows correlation above 0 and `sub`/`low` above 0.9.
- [ ] The chain is the only way to Master: `machine_list`.
- [ ] Every stage earned its place: `snapshot_diff(a="master 0")`, each change with its before and after numbers in your report.
- [ ] Saved: `song_save(path=…)`.
