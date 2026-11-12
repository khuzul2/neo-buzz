# Mastering

Bring a finished mix to a platform's loudness and peak target without hurting it. Change one stage at a time, measure the whole song, and keep a change only when the numbers say it helped.

## The flow

1. **Inputs.** Ask where the master goes (a platform, a club, broadcast) and whether there is a reference: one mastered track in the style (a WAV file), or several averaged into a style target with `target_build(name="deep house", sources=["C:/Refs/a.wav", "C:/Refs/b.wav"])`. A style target is saved as a file and serves every song. Without a reference, the platform's target and the style table below are the target.
2. **Measure** the mix: `render()` the whole song, then `analyze(source=…, target=…)` with the reference's path or the style target's name. Besides loudness, peaks, dynamics, spectrum and stereo it reads `microdynamics` (LDR, the short-term punch, in four bands split at 120 Hz, 2 kHz and 10 kHz), `tonal` (the long-term curve at a sixth of an octave and, with a target, what an EQ would have to do to match it), `stereo`'s `ms_bands` (the side against the mid in the same four bands) and `platform` (what each streaming service will do to its level).
3. **Decide** from the numbers, stage by stage:
   - tone: `eq_fit(source=…, target=…)` turns the tonal difference into a few Neo EQ bands (Against a reference, below);
   - dynamics: PLR against the style's range or the target's, and LDR per band against the target's;
   - stereo: side/mid per band against the target's, the lows mono whatever the target does;
   - loudness: the limiter's gain loop (Reaching the target), and the `platform` read before you settle.
4. **Apply** one stage at a time, `snapshot_save` before each, measure the whole song after, `undo` a stage that measures worse.
5. **Report** before and after: the numbers that moved, and why each stage stayed.

In a live set (the Session View playing), suggest each move with its numbers and wait for a yes: don't move knobs the user didn't ask for.

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

What the services do (`analyze(source=…, metrics=["loudness", "peaks", "platform"])` previews it for a render):

| Service | Plays at | A louder master | A quieter master | Source |
|---|---|---|---|---|
| Spotify, normal (the default) | −14 LUFS | turned down | raised, but only as far as its true peak stays under −1 dBTP | published |
| Spotify, Loud | −11 LUFS | turned down | raised all the way, into a limiter (from −1 dBFS, 5 ms attack, 100 ms release) | published |
| Spotify, Quiet | −19 LUFS | turned down | raised, as normal | published |
| Apple Music (Sound Check) | −16 LUFS | turned down | as it is | observed |
| YouTube, TIDAL, Amazon Music | −14 LUFS | turned down | as it is | observed |
| Deezer | −15 LUFS | turned down | as it is | observed |

Spotify's own example: a −20 LUFS master with a −5 dBTP true peak plays at −16 LUFS (raised 4 dB, to −1 dBTP). For Spotify Loud without its limiter: −11 LUFS at −2 dBTP or lower. "Observed" values are what listeners measure, not a published rule, and change.

## Before you start

- The mix: true peak −3 dBTP or lower (−6 is better), 0 clipped samples, lows mono, a balanced tilt (the mixing guide's checks). If not, go back and fix the mix: mastering can't.
- Integrated loudness is a whole-song measure: `render()` the whole song for every decision on loudness. For quick iterations use the loudest section, `render(section="Drop")`, then confirm on the whole song.
- `snapshot_save(name="master 0")` before the first stage.

## The chain

Master has no insert slots: the chain sits in front of it.

1. `machine_list`: note every machine connected to Master and its gain.
2. `machine_add(gear="Neo EQ", name="M EQ", connect_to="Master")`.
3. For each of those machines: `disconnect(from="Bass Duck", to="Master")`, then `connect(from="Bass Duck", to="M EQ", gain_db=-4)` with the same gain it had.
4. Each next stage goes after the previous: `machine_add(gear="Neo Comp", name="M Glue", insert_after="M EQ")`, `machine_add(gear="Neo Drive", name="M Sat", insert_after="M Glue")`, optionally `machine_add(gear="Neo Clipper", name="M Clip", insert_after="M Sat")`, then `machine_add(gear="Neo Limiter", name="M Limit", insert_after="M Clip")` (after "M Sat" without the clipper).
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

Only one band misbehaves (a boomy kick, harsh 2–6 kHz, sibilance)? Neo Multiband instead of, or after, the glue: `machine_add(gear="Neo Multiband", name="M Bands", insert_after="M Glue")`, its other bands at 1:1 so only that band works, 2–4 dB of reduction on its peaks (its readings: `machine_readings(machine="M Bands")`). Its linear phase adds about 96 ms of latency and no phase shift; "Mastering — Tight Low End" and "Mastering — De-Harsh 2–6 kHz" are starting points (its manual: `guide(machine="Neo Multiband")`).

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

### 3b. Clipping (Neo Clipper, optional)

A clipper before the limiter shaves the first milliseconds of the loudest transients, which the limiter would otherwise pump on, so the master gets louder for the same limiting. Start from "Mastering — Transparent (Before a Limiter)" (+2 dB into −0.3 dB, a 3 dB knee, 4×) and raise `Drive` until `machine_readings(machine="M Clip")` shows `clip_db` 1–3 dB on the loudest hits. Then lower the limiter's `Input gain` by about the same and measure: PLR within 0.5 dB, the loudness at least as high. `Delta` On plays what it removes: clicks or a buzz mean too much. The clipper's "Match" (or `Output` set to minus `gain_match_db`) compares it at equal loudness. Three bands ("Mastering — Three Bands") clip the lows less if the kick breaks up.

### 3c. Low end and punch (Neo Low End, Neo Transient, optional)

Neo Low End shapes only the band below its crossover (`Punch`, `Sustain`, `Contrast`, `Balance` to the mids, mono below a frequency, `Peak control`): put it after "M EQ" when a kick or bass is muddy, uneven or too wide. Neo Transient shapes the attack and sustain of the whole signal (or four bands in Impact mode): small moves after "M Glue" when a mix lacks snap. Both report `gain_match_db` and a `Delta` listen; read `machine_readings` after a few seconds of playback.

### 3d. Stereo image and air (Neo Imager, Neo Exciter, optional)

Neo Imager sets the width of four bands and keeps them mono-safe: narrow the lows (`Width 1` 0 %), widen the highs a little, and read `correlation_N` and `width_db_N` from `machine_readings` after a few seconds. Neo Exciter adds only the harmonics a driven shaper makes, per band: a little `Amount 4` for air, `added_db` between −25 and −18 dB is gentle. Both go before the limiter; check the loudness they add with `gain_match_db`.

### 3e. One machine for a track (Neo Strip, optional)

Neo Strip puts a gate, an EQ, a compressor, a drive, a clipper and a limiter in one machine, each the engine of the Neo machine of that name. It is for tracks and buses before the master; the master chain above uses the separate machines, which have more controls. Its latency is the same whichever stages are on.

### 4. Limiter

Neo Limiter: `Ceiling` (dBTP), `Input gain` (how hard it is driven), `Auto release` or `Release`, `Lookahead` (default is fine), `Stereo link` 100 %. Set:

| Setting | Streaming | Club | Broadcast |
|---|---|---|---|
| Ceiling | −1.0 dBTP | −0.5 dBTP | −1.0 (EBU), −2.0 (ATSC) dBTP |
| Release | auto, or 50–200 ms | auto, or 30–100 ms | auto |
| Gain into it | until the integrated target | the same | the same |
| Reduction on the loudest peaks | 3–4 dB at most | 6–8 dB at most | little or none |

It has no target-LUFS mode: render, read `integrated_lufs`, and raise `Input gain` by the difference (it is close to dB for dB until the limiting is heavy), then render again.

Its styles set v2's stages (`guide(machine="Neo Limiter")`): "Style — Transparent" (5 ms lookahead, a slow release stage, transient unlink) distorts least, a third of Classic's IMD at the same reduction, and pumps less: the first choice for a dynamic master. "Style — Punchy" keeps hits forward. The two soft-clip styles ("Style — Dense (Soft Clip 9 dB, Upward)", "Style — Loud (Soft Clip 3 dB)") buy density with distortion: judge them by ear with `Unity listen` On (the output at the input's loudness), and turn it off before rendering.

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

### 5. Dither (Neo Dither), only for a 16 or 24 bit file

Neo Dither is the last machine after the limiter: `Bits` 16, TPDF, `Shaping` Off ("16 bit — TPDF (Standard)"). The simpler way is `render(bits=16, dither="tpdf")`, which writes the master as 16 bit PCM through the same dither and measures what is in the file; do not use both. A 32-bit float file needs no dither.

## Against a reference

- A reference measured on the fly: `analyze(source="r4", target="C:/Refs/track.wav")`. Several references, averaged and kept with their spread: `target_build(name="deep house", sources=[…])` (1 to 20, a few minutes each, in the style wanted), then `analyze(source="r4", target="deep house")`. A value inside the references' own range is a match.
- **Tone.** `tonal.target.diff_third_octave_db` is what an EQ would have to do (positive: boost), after moving the target to this master's level; `most_off` the worst places. Under 1 dB RMS from 50 Hz to 16 kHz is a close match: leave it. Then `eq_fit(source="r4", target="deep house", machine="M EQ")`: least squares on Neo EQ's own response, at most 4 bands (`bands`, up to 6) within ±4 dB each (`max_gain_db`), the wanted change clipped to ±3 dB (`limit_db`: this guide's EQ rule). It writes them into the Neo EQ's B2–B7 (Mode Parametric, the bands left over at 0 dB, B1 and B8, the cuts, untouched) as one undo step; without `machine` it returns the bands and their `param_set` changes. Its `rms_before_db` and `rms_after_db` say how much of the difference the bands take. Render and analyze again: the difference should be near `rms_after_db`, plus what the limiter adds.
- A change you want rather than a target's: `eq_fit(curve=[{"hz": 1000, "db": 0}, {"hz": 3500, "db": -3}, {"hz": 8000, "db": 0}], bands=1, machine="M EQ")`.
- **Loudness and PLR**: the target's, within its range. Don't chase a louder reference past what the mix holds: a PLR far under the style's range is crushed, however loud the reference.
- **Microdynamics.** An LDR 1.5 dB or more under the target's in a band: the master is flatter there (less limiting, a slower attack on the glue). Over it: punchier than the target there (more glue, or Neo Multiband on that band). Steady sound reads about 3 dB below 120 Hz and under 1.5 dB above: the low band's floor is higher.
- **Width.** Side/mid per band against the target's: Neo Utility's `Width` moves the side as a whole and `Bass mono` takes it out below a frequency; Neo Multiband in M/S (its `Stereo` M/S, `Link` 0 %) works band by band. Mid/side compression or saturation with any machine: between two Neo Utilities, the first "Mid/Side — Encode (Before a Stereo Machine)", the second "Mid/Side — Decode (After It)" (`guide(machine="Neo Utility")`). Never widen the lows.

## Reaching the target

1. Render (the loudest section first) and `analyze(source=…, metrics=["loudness", "peaks", "dynamics"])`.
2. Raise the gain into the limiter by the difference: target − measured. With Neo Drive before it: `param_set(machine="M Sat", changes=[{"param": "Output", "by": "+3 dB"}])`.
3. Render the whole song and measure again. A song with quiet parts reads 1–3 LU lower over the whole than in its loudest section; the whole-song number is the one that counts.
4. Repeat until within the tolerance: two or three passes. Once the limiter works hard, each dB of gain gives less than 1 LU.
5. Stop when the limiter's reduction or the PLR leaves the table's range: the target is too loud for this mix. Lower the target or go back and tame the kick and bass peaks in the mix.
6. Before you settle, `analyze(source=…, metrics=["loudness", "peaks", "platform"])`: what Spotify (normal and Loud), Apple Music and the others will do to it. A master that a service raises into its limiter, or turns down 4 dB or more, is louder or quieter than it needs to be there.

## Measure after every change

| After | Must hold |
|---|---|
| EQ | bands move only where intended, by about the gain set; loudness within 0.5 LU (or compensate) |
| Glue | PLR 0.5–2 dB lower; loudness matched; tilt unchanged |
| Saturation | PLR 0.5–1 dB lower; tilt within ±0.3 dB/oct; `high_mid` up 1 dB at most |
| Limiter | integrated on target; true peak at or under the ceiling; 0 clipped samples; PLR in the style's range |
| The whole chain | stereo correlation and per-band width within ±0.05 of the mix; lows mono; against a target, the tonal difference down, LDR and side/mid per band no further from it |

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
- [ ] Tonal balance as intended: `compare(a=…, b=…)` of the mix and the master shows level-matched bands within ±1 dB, and the tilt within ±0.5 dB/oct, unless that was the point. Against a reference or style target: `analyze(source=…, target=…)` reads the tonal difference under 1.5 dB RMS, or says why not.
- [ ] Each platform it's for plays it as intended: `analyze(source=…, metrics=["platform"])`.
- [ ] Stereo safe: `analyze(source=…, metrics=["stereo"])` shows correlation above 0 and `sub`/`low` above 0.9.
- [ ] The chain is the only way to Master: `machine_list`.
- [ ] Every stage earned its place: `snapshot_diff(a="master 0")`, each change with its before and after numbers in your report.
- [ ] Saved: `song_save(path=…)`.
