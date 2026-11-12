## How it sounds

The last machine of a master when the target is a 16 or 24 bit file: it rounds the audio to the target word length with dither, so the file made from it is exact. `Bits` (8–24) is the grid; `Dither` TPDF adds noise of 1 LSB (triangular) that makes the rounding error independent of the signal: no distortion on quiet passages and fades, only a steady hiss (about −98 dBFS in 16 bits). `Shaping` moves that noise: Light takes 7 dB out below 4 kHz, Psychoacoustic takes 16 dB out from 50 Hz to 3 kHz (23 dB at 4 kHz) and puts it above 8 kHz, at the cost of 12 dB more total noise power.

- The output is exactly on the grid: every sample is k / 2^(`Bits` − 1). Writing it as 16 or 24 bit PCM loses nothing.
- The noise generators are seeded at the start of every render: renders repeat bit for bit.
- `Auto blank`: after `Blank after` (0.7 s by default) of exact digital silence the output is silent (no hiss between tracks) and the first sound ends it.
- No latency. Put it after the limiter; nothing that changes the level may come after it.

## How to use it

- **A 16 bit master:** `Bits` 16, `Dither` TPDF, `Shaping` Off (preset "16 bit — TPDF (Standard)"). Use Psychoacoustic ("16 bit — TPDF, Psychoacoustic Shaping") for quiet, high-fidelity material.
- **A 24 bit master:** `Bits` 24, TPDF: the noise sits at −146 dBFS, below any converter. Dither is not needed at all for a 32-bit float file.
- **With the render tool:** `render(bits=16, dither="tpdf")` writes the master as 16 bit PCM through the same dither (`off`, `tpdf`, `light`, `psychoacoustic`); the measurements are of what is in the file. Leave Neo Dither out of the song in that case, or the audio is rounded twice.
- **Never:** dither twice, or put a gain, EQ or limiter after it.

## Gotchas

- `Shaping` Psychoacoustic at 16 bits adds noise above 10 kHz that is louder than plain TPDF; on very bright material or for further lossy encoding, use Light or Off.
- The shaping curves are designed for 44.1 and 48 kHz; at higher rates the same curve sits higher in frequency.
- Plain rounding (`Dither` Off) is only right for material that is already on the grid.

## Preset map

| Preset | What |
|---|---|
| "16 bit — TPDF (Standard)" | `Bits` 16, TPDF, no shaping |
| "16 bit — TPDF, Light Shaping" | `Bits` 16, TPDF, Light |
| "16 bit — TPDF, Psychoacoustic Shaping" | `Bits` 16, TPDF, Psychoacoustic |
| "24 bit — TPDF" | `Bits` 24, TPDF |
| "20 bit — TPDF" | `Bits` 20, TPDF |
| "16 bit — Rounding Only (No Dither)" | `Bits` 16, Dither Off |
