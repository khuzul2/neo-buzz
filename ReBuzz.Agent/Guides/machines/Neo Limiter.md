## How it sounds

A true-peak lookahead limiter. `Input gain` drives the signal into the `Ceiling` and peaks are found at 4x oversampling, as BS.1770 measures true peak.

- The output's true peak stays at most 0.002 dB over the ceiling (squares, drums and noise pushed 12 dB into it). Below the ceiling the output equals the input. A 100 Hz sine limited by 6 dB has 0.3 % THD.
- `Release` is the time to recover 63 % of a reduction; `Auto release` recovers fast from short peaks and slowly from sustained limiting.
- Latency is `Lookahead` plus 11 samples (58 more with `Soft clip` on), reported and compensated.

v2's stages are all off by default, so older songs sound as they did ("Style — Classic"). In signal order, before the limiter:
- `Upward` (up to 6 dB) lifts quiet passages: half a dB for each dB their 3 s RMS sits under `Upward threshold`, never below -60 dBFS.
- `Transients` (up to 6 dB) raises the first milliseconds of hits, so they keep their punch.
- `Soft clip` (3, 9 or 30 dB under the ceiling) clips 4x oversampled so the limiter does less; `Clip mix` blends it. It adds density and distortion: judge by ear.

In the limiter: `Slow stage` (with `Slow release`) holds part of the sustained reduction so dense material pumps less; `Transient unlink` lets a one-sided transient not duck the other side. After it: `Delta` plays only what limiting changes; `Unity listen` trims the output to the input's loudness (`gain_match_db`) to judge it at equal loudness: switch it off before rendering.

## Use it for

- The last loudness stage of a master (after Neo Clipper, before Neo Dither), peak safety on a bus, taming vocal or drum peaks.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Ceiling | -1 dB | streaming (-1 dBTP); -0.3 dB for CD, -2 dB for broadcast |
| Input gain | 4 dB | raise it up to the integrated target; about dB for dB until limiting is heavy |
| Lookahead | 2 ms | 4-5 ms is smoother on bass-heavy material |
| Auto release | On | fast on short peaks, slow under sustained limiting |
| Stereo link | 100% | the image stays put |

There is no loudness-target mode. Set the ceiling, render, read `integrated_lufs`, raise Input gain by the difference, render again and check the true peak and the reduction. Keep it to 3-4 dB of reduction for streaming, 6-8 dB for club; more should come from the mix.

## Starting points

- "Mastering — Streaming (−1 dBTP)", "Mastering — Transparent", "Mastering — Loud", "Mastering — Dense Electronic", "Mastering — CD (−0.3 dBTP)", "Mastering — Broadcast (−2 dBTP)".
- "Bus — Mix Bus Safety" (catches overs only), "Bus — Drum Peak Catcher", "Track — Vocal Peaks", "Track — Clean Bass", "Safety — Brickwall (−0.1 dBTP)", "Creative — Smash".
- Styles: "Style — Classic" (v1), "Style — Transparent" (least distortion), "Style — Punchy", "Style — Dense (Soft Clip 9 dB, Upward)", "Style — Loud (Soft Clip 3 dB)".

## Pitfalls

- Input gain raises everything, not only the peaks: check `range_lu` and the crest factor after.
- Stereo link below 100 % lets channels limit separately and can shift the image.
- Changing Lookahead, or switching Soft clip, changes the latency: set them once.
- Unity listen on in a render leaves the master at the input's loudness.
- Soft clip trades distortion for density: compare by ear at equal loudness.

## Pairs well with

- Neo Clipper before it, Neo Dither after it, Neo Analyzer last; the chain is in `guide(topic="mastering-chain")`.

## Checklist

- [ ] `machine_readings(machine="M Limit")`: `gain_reduction_db` about 3-4 dB on the loudest peaks.
- [ ] `analyze(source="master", metrics=["loudness", "peaks"])`: true peak at or under the ceiling, loudness at the target.
- [ ] `param_set(machine="M Limit", changes=[{"param": "Unity listen", "value": "Off"}])` before rendering.
