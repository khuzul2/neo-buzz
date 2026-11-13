# Mastering chain

The chain stage by stage with settings: corrective EQ, glue compression, saturation, optional clipping, low end, image and tape, the limiter and dither. The flow, targets, reference matching and what not to do are in the mastering guide (`guide(topic="mastering")`).

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

### 3f. Sibilance and tape (Neo De-esser, Neo Tape, optional)

A bright or sibilant master (or vocal bus) goes through Neo De-esser before the limiter: `Detector` Absolute at 8 – 10 kHz with `Action` Bell for a bright master, Relative at 6.5 kHz with `Action` Split for a vocal; find the band with `Listen`, then read `reduction_db` (3 – 6 dB on the worst passages). Neo Tape before the clipper or limiter adds a little glue and warmth: start from "Mastering — Subtle Glue (15 ips)", match the loudness with `gain_match_db`, and compare with it bypassed. Neo Gate (the successor of Pedal Gate) belongs on tracks, not on the master.

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

### 6. Hear it lossy (the Opus preview)

Streaming services re-encode the master, so check what a lossy codec does to it before you deliver: `render(opus_kbps=128)` (or `analyze(source="r4", opus_kbps=128)`) encodes and decodes it with Opus (64 to 256 kb/s) and measures the decoded audio: the true peak (a master at -1 dBTP can come back over 0), where the spectrum is cut off and how far down above it, and the loudness of the null residual (original minus decoded). Opus is a proxy for lossy coding in general (the platforms use Vorbis and AAC); it is not their encoder. With render, the decoded audio is the render `rN:opus`, which `analyze` and `compare` read. If the true peak comes back above -0.5 dBTP, lower the limiter's ceiling by the difference.

## Checklist

- [ ] Each stage's settings read back as chosen: `param_get(machine=…)` on every machine in the chain.
- [ ] Each stage was measured on the whole song: `analyze(source=…, metrics=["loudness", "peaks"])` after every change, with `snapshot_save` before it.
- [ ] The limiter holds the ceiling: `analyze(source=…, metrics=["peaks"])` shows a true peak at or under it and no clipped samples.
- [ ] The chain is the only way to Master and in the order written: `machine_list`.
