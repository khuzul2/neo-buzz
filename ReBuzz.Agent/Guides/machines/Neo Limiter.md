## How it sounds

A true-peak lookahead limiter. `Input gain` drives the signal into the `Ceiling`, and peaks are found at 4× oversampling, as BS.1770 measures true peak.

- **Ceiling:** the output's true peak stays at most 0.002 dB over it, on squares, drums and noise pushed 12 dB into it.
- **Below the ceiling** it changes nothing: the output equals the input.
- **Distortion:** a 100 Hz sine limited by 6 dB has 0.3 % THD.
- **Release:** set times are measured exact. `Auto release` recovers fast from short peaks and slowly from sustained limiting.

## Signal flow

The signal goes through `Input gain`, then a 4× oversampled true-peak detector with `Stereo link`, then the gain computer (`Lookahead`, `Release` or `Auto release`), then the `Lookahead` delay. The output stays at or under `Ceiling`.

- `Ceiling` is in dB, meaning dBTP: −12 to 0 dB in 0.1 dB steps.
- `Release` is the time to recover 63 % of a reduction.
- Latency is `Lookahead` plus 11 samples. ReBuzz is told about it and compensates.

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
- Changing `Lookahead` changes the latency. Set it once.

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
