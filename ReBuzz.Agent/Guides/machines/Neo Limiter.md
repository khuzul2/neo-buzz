## How it sounds

A true-peak lookahead limiter. `Input gain` drives the signal into the `Ceiling`, and peaks are found at 4× oversampling, as BS.1770 measures true peak.

- **Ceiling:** the output's true peak stays at most 0.002 dB over it, on squares, drums and noise pushed 12 dB into it.
- **Below the ceiling** it changes nothing: the output equals the input.
- **Distortion:** a 100 Hz sine limited by 6 dB has 0.3 % THD.
- **Release:** set times are measured exact. `Auto release` recovers fast from short peaks and slowly from sustained limiting.

## v2's stages

Each is off by default, so a song made before them sounds as it did ("Classic", v1's bits). In signal order, before the limiter:

- `Upward` (up to 6 dB) lifts quiet passages: half a dB for each dB their 3 s RMS sits under `Upward threshold`, never below −60 dBFS (no noise floor raised). A −30 dBFS passage under a −20 dB threshold rises 5.4 dB; a −14 dBFS one not at all.
- `Transients` (up to 6 dB) raises the first milliseconds of hits before limiting, so they keep their punch: +6 dB lifts a kick's first 2 ms by 5.7 dB and leaves its tail.
- `Soft clip` (3, 9 or 30 dB: where the curve starts bending under the ceiling) clips 4× oversampled before the limiter, which then does less; `Clip mix` blends it. The ceiling still holds (the limiter is after it: true peak within 0.03 dB). It adds 58 samples of latency while on. It distorts a full-scale test tone far more than limiting does (see the styles): use it for density, by ear.

In the limiter:

- `Slow stage` (with `Slow release`): a slow second release holds part of the sustained reduction, so dense material pumps less between hits (a 1 kHz tone's sidebands under a kick: −19.8 → −22.7 dB at 60 %).
- `Transient unlink`: on hits the stereo link drops, so a one-sided transient doesn't duck the other side (−6.1 dB linked, −3.4 dB at 100 %); sustained sound stays linked.

After it, for listening:

- `Delta` plays only what the limiting changes, at the input's level.
- `Unity listen` trims the output to the input's loudness (the gain-match reading, `gain_match_db`), to judge what limiting does at equal loudness. Turn it off before rendering.

## Signal flow

The signal goes through `Input gain`, then v2's `Upward`, `Transients` and `Soft clip` when on, then a 4× oversampled true-peak detector with `Stereo link` (and `Transient unlink`), then the gain computer (`Lookahead`, `Release` or `Auto release`, `Slow stage`), then the `Lookahead` delay, then `Delta` and `Unity listen`. The output stays at or under `Ceiling`.

- `Ceiling` is in dB, meaning dBTP: −12 to 0 dB in 0.1 dB steps.
- `Release` is the time to recover 63 % of a reduction.
- Latency is `Lookahead` plus 11 samples (plus 58 with `Soft clip` on). NeoBuzz Live is told about it and compensates.

## How to use it

Use it last on the master, or to catch peaks on a bus. There is no loudness-target mode. To reach a loudness:

1. Set the `Ceiling` for the destination, below.
2. Render, and read `integrated_lufs`.
3. Raise `Input gain` by the difference. It works about dB for dB until the limiting is heavy.
4. Render again and check the true peak and the reduction.

| Parameter | Value | Why |
|---|---|---|
| Ceiling | -1 dB | streaming (−1 dBTP); −0.3 dB for CD, −2 dB for broadcast |
| Auto release | On | |
| Lookahead | 2 ms | default; 4–5 ms is smoother on bass-heavy material |
| Stereo link | 100% | the image stays put |
| Input gain | 4 dB | up to the integrated target |

Keep it to 3–4 dB of reduction on the loudest peaks for streaming, 6–8 dB for club. More than that, and compression or the mix should do the work.

## Gotchas

- `Input gain` raises everything, not only the peaks. Check the loudness range (`range_lu`) and the crest factor after.
- `Stereo link` below 100 % lets the channels limit separately, which can shift the image on one-sided peaks.
- Changing `Lookahead` changes the latency, and so does switching `Soft clip` on or off. Set them once.
- `Unity listen` on in a render leaves the master at the input's loudness: turn it off first.
- The soft-clip styles trade distortion for density: on an SMPTE test tone they pass 1 % IMD before any limiting, where Classic reaches it at 4 dB of reduction and Transparent at 19 dB. Compare by ear at equal loudness (`Unity listen`).

## Preset map

| Preset | What |
|---|---|
| "Mastering — Streaming (−1 dBTP)" | +4 dB into −1 dBTP, auto release |
| "Mastering — Transparent" | +2 dB, 5 ms lookahead |
| "Mastering — Loud" | +8 dB, 3 ms lookahead |
| "Mastering — Dense Electronic" | +10 dB into −0.5 dBTP, 1 ms lookahead |
| "Mastering — CD (−0.3 dBTP)" | +4 dB into −0.3 dBTP |
| "Mastering — Broadcast (−2 dBTP)" | +2 dB into −2 dBTP |
| "Bus — Mix Bus Safety" | no drive: catches overs only |
| "Bus — Drum Peak Catcher" | +3 dB into −3 dB, set release, 1 ms lookahead |
| "Track — Vocal Peaks" | +6 dB into −6 dB |
| "Track — Clean Bass" | +4 dB into −3 dB, set release, 5 ms lookahead: low distortion |
| "Safety — Brickwall (−0.1 dBTP)" | −0.1 dBTP, 0.5 ms lookahead: the last guard |
| "Creative — Smash" | +12 dB, set release, 0.5 ms lookahead: an effect |
| "Style — Classic" | v1: +6 dB into −1 dBTP |
| "Style — Transparent" | 5 ms lookahead, a 50 % slow stage, 50 % transient unlink: the least distortion (IMD 0.4 % at 6 dB of reduction, Classic 1.4 %) |
| "Style — Punchy" | +3 dB transients, 50 % unlink, a 30 % slow stage |
| "Style — Dense (Soft Clip 9 dB, Upward)" | upward 3 dB under −18 dB, a 9 dB soft clip, a 40 % slow stage: dense, distorts |
| "Style — Loud (Soft Clip 3 dB)" | a 3 dB soft clip, +1.5 dB transients, a 30 % slow stage: loud, distorts |
