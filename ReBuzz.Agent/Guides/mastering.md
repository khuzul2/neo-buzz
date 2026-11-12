# Mastering

Bring a finished mix to a platform's loudness and peak target without hurting it. Change one stage at a time, measure the whole song, and keep a change only when the numbers say it helped. The chain, stage by stage with settings, is in `guide(topic="mastering-chain")`.

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
