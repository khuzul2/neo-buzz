## How it sounds

A stereo imager in four bands. In each band the signal is split into its mid (what left and right share) and its side (what differs); `Width 1` … `Width 4` (0–200 %) set the side against the mid in that band. 0 % is mono, 100 % is as it came in, 200 % doubles the side. `Stereoize 1` … `Stereoize 4` add a decorrelated copy of the mid to the side, so a narrow band gets wider without changing the mono sum: what you hear in mono is exactly what it was. `Recover sides` is for narrowing: the side that narrowing takes away is turned 90 degrees and returned to the mid, so the level stays and nothing combs in mono.

- All four widths at 100 % and every `Stereoize` at 0: the output is the input, bit for bit, with no latency.
- The bands are zero-latency Linkwitz–Riley splits (`Crossover 1` – `Crossover 3`); the band sum is flat.
- `Delta` plays only what the imager changes; `Output` comes after it. The editor's "Match" sets `Output` from the gain-match reading.
- `machine_readings`: `width_db_1` … `width_db_4` (side against mid in each band at the output, dB of energy, over 3 s), `correlation_1` … `correlation_4` (−1 … +1, left against right), `gain_match_db`, `latency_samples` (always 0).

## How to use it

- **A mix that collapses in mono:** `analyze` the mix (`stereo.ms_bands`); the band with correlation under 0.2 or a negative one is the problem. Narrow it (`Width` 40–70 %) with `Recover sides` On.
- **Wider top, focused lows:** `Crossover 1` 150 Hz, `Width 1` 0 %, the top bands 110–140 %. Preset "Mastering — Widen the Highs".
- **Width without mono risk:** `Stereoize` 20–50 % in the bands that sound narrow; check `correlation_N` afterwards.
- **Mono compatibility check:** every width at 0 % with `Recover sides` On is a mono fold-down at the same level; compare its `gain_match_db` against the stereo version.

## Gotchas

- `Width` over 100 % makes the side louder: the master gets louder and a limiter after it works harder. Use `Match` or lower `Output`.
- A band at 0 % has no side at all; `Stereoize` in that band widens it again with the decorrelated mid.
- `Recover sides` only does something while a width is under 100 %.

## Preset map

| Preset | What |
|---|---|
| "Mastering — Widen the Highs" | `Width 3` 115 %, `Width 4` 135 % |
| "Mastering — Focus the Lows (Mono Below 150 Hz)" | `Crossover 1` 150 Hz, `Width 1` 0 % |
| "Mastering — Air and Width" | the top three bands 105 – 150 % |
| "Mastering — Stereoize the Top" | `Stereoize 3` 20 %, `Stereoize 4` 35 % |
| "Mixing — Narrow a Wide Mix, Keep the Level" | `Width 1` 0 %, the rest 70 %, `Recover sides` On |
| "Mixing — Mono Check (Everything at 0 %, Recovered)" | all widths 0 %, `Recover sides` On |
| "Mixing — Wide Pad" | `Width 1` 0 %, the others 150 – 190 %, `Stereoize 2` 15 % |
| "Mono-Safe Widening (Stereoize Only)" | `Stereoize 2` – `Stereoize 4` 25 – 50 % |
| "Lows Mono, Rest as It Is" | `Crossover 1` 120 Hz, `Width 1` 0 % |
