## How it sounds

It is a set of exact, colourless tools, and at its defaults it passes the input bit for bit:

- `Gain` in 0.1 dB steps; at its bottom, silence.
- `Pan`, with a `Pan law`:
  - Balance fades the far side linearly, 0 dB at centre, for stereo sources.
  - Mono pan sums to mono, then pans.
- `Width`: the side signal times the width. 0 % is mono, 100 % unchanged, 200 % twice the side.
- `Bass mono` makes everything below `Bass mono freq` mono. It removes the side signal there with a 4th-order Butterworth high-pass: −3 dB at the frequency, −48 dB two octaves below.
- `Invert L` and `Invert R` flip a channel's polarity; `Swap` swaps the channels (after the inversions).
- `DC removal`: a 5 Hz high-pass. 20 Hz passes within 0.02 dB.

Gain, pan, width, polarity and swap measure exact.

## Signal flow

Input → `Invert L`/`Invert R` → `Swap` → `DC removal` → `Width` and `Bass mono` → `Pan` → `Gain` → output. Zero latency.

## How to use it

Use it for gain staging and stereo fixes, one parameter per move:

- **Trim a track to its level:** `Gain` −6 dB, then render and check.
- **Bass and kick mono below 120 Hz:**

| Parameter | Value | Why |
|---|---|---|
| Bass mono | On | |
| Bass mono freq | 120 Hz | 80 Hz on the master |

- **A pad wider or narrower:** `Width` 120–140 % or 50–80 %. Check the mix in mono afterwards (`analyze(metrics=["stereo"])`, correlation above 0).
- **Out of phase:** `analyze_mix` reports a low end out of phase, or the correlation reads below 0. Flip one channel (`Invert R` On) and measure again.

## Gotchas

- `Width` above 100 % lowers the mono sum's correlation: keep basses at 100 % or below.
- `Pan` with Balance on a mono source only turns one side down. Use Mono pan for mono sources.
- `Gain` is after everything else: it doesn't change what the stereo tools see.

## Preset map

| Preset | What |
|---|---|
| "Gain — Trim −6 dB" | −6 dB |
| "Gain — Boost +6 dB" | +6 dB |
| "Gain — Stage to −18 dBFS (−12 dB)" | −12 dB, for hot sources |
| "Stereo — Mono" | width 0 % |
| "Stereo — Narrow 50 %" | width 50 % |
| "Stereo — Wide 140 %" | width 140 % |
| "Mastering — Bass Mono 120 Hz" | lows mono |
| "Mastering — Clean Up (Bass Mono 80 Hz, DC)" | lows mono and DC removal |
| "Mixing — Mono Pan Left 30 %" | mono pan |
| "Mixing — Mono Pan Right 30 %" | mono pan |
| "Mixing — Balance Left 20 %" | balance |
| "Fix — Flip Polarity" | both channels inverted |
| "Fix — Right Channel Out of Phase" | right inverted |
| "Fix — Swap Channels" | left and right swapped |
| "Fix — DC Offset" | DC removal |
