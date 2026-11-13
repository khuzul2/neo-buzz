## How it sounds

The last machine of a master when the target is a 16 or 24 bit file: it rounds the audio to the target word length with dither, so the file made from it is exact. `Bits` (8-24) is the grid; `Dither` TPDF adds noise of 1 LSB (triangular) that makes the rounding error independent of the signal: no distortion on quiet passages and fades, only a steady hiss (about -98 dBFS in 16 bits). `Shaping` moves that noise: Light takes 7 dB out below 4 kHz, Psychoacoustic takes 16 dB out from 50 Hz to 3 kHz (23 dB at 4 kHz) and puts it above 8 kHz, at the cost of 12 dB more total noise power.

- The output is exactly on the grid: every sample is k / 2^(`Bits` - 1), so 16 or 24 bit PCM loses nothing.
- The noise generators are seeded at the start of every render: renders repeat bit for bit.
- `Auto blank`: after `Blank after` (0.7 s by default) of exact digital silence the output is silent (no hiss between tracks); the first sound ends it.
- No latency.

## Use it for

- The final stage of a 16 or 24 bit master, after the limiter.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bits | 16 | the word length of the file: 16 for CD and most masters, 24 for an archive |
| Dither | TPDF | Off is plain rounding, right only for material already on the grid |
| Shaping | Off | Light or Psychoacoustic for quiet, high-fidelity material |
| Auto blank | On | silence stays silent between tracks |
| Blank after | 0.7 s | how long digital silence must last before the output mutes |

## Starting points

- "16 bit — TPDF (Standard)": Bits 16, TPDF, no shaping. "16 bit — TPDF, Light Shaping" and "16 bit — TPDF, Psychoacoustic Shaping" for quiet material.
- "24 bit — TPDF" (noise at -146 dBFS, below any converter) and "20 bit — TPDF". "16 bit — Rounding Only (No Dither)" for material already on the grid.
- Alternative: `render(bits=16, dither="tpdf")` writes the master as 16 bit PCM through the same dither (`off`, `tpdf`, `light`, `psychoacoustic`); leave Neo Dither out of the song then.

## Pitfalls

- Never dither twice, and never put a gain, EQ or limiter after it. A 32-bit float file needs no dither.
- Psychoacoustic at 16 bits adds more noise above 10 kHz than plain TPDF: on very bright material, or before lossy encoding, use Light or Off.
- The shaping curves are designed for 44.1 and 48 kHz; at higher rates the same curve sits higher.

## Pairs well with

- Neo Limiter directly before it (`guide(topic="mastering-chain")`); Neo Analyzer to read the file's level.

## Checklist

- [ ] `machine_list`: Neo Dither is the last machine before Master, with nothing after it.
- [ ] `render(bits=16, dither="off")` (or the machine, not both): the file is written once, not rounded twice.
- [ ] `analyze(source="master", metrics=["peaks", "loudness"])` after it: peak under the ceiling.
- [ ] `machine_readings(machine="Dither")`: `latency_samples` is 0.
