## How it sounds

A harmonic exciter, the successor of Oomek Exciter. The input is split into four bands; each band goes through an oversampled, anti-aliased shaper (`Mode`: Tape, Tube, Transistor, Diode or Warm) driven by `Drive`; only what the shaper adds, its harmonics, is added back to the untouched audio, by `Amount 1` … `Amount 4`. The audio itself never goes through a filter: with every `Amount` at 0 % the output is the input, delayed (bit for bit).

- **Tape** adds odd harmonics (3rd, 5th), smooth. **Tube** is asymmetric: 2nd and 3rd. **Transistor** is harder (more upper harmonics). **Diode** is soft and asymmetric (2nd, 3rd). **Warm** adds even harmonics only (2nd, 4th): an octave of body without edge.
- `Drive` (0–36 dB) sets how hard the shaper works: more drive, more harmonics and of higher order. `Amount` sets how much of them is added.
- `Shelf freq` and `Shelf gain` (±12 dB) shape the added harmonics only: brighter or duller excitation without touching the audio.
- `Auto gain` keeps the output's loudness close to the input's (within ±6 dB). `Stereo` M/S excites mid and side separately: harmonics of the side widen, of the mid centre.
- The latency is constant, the oversampling's, and reported. `Delta` plays only what the exciter adds. `machine_readings`: `added_db` (the harmonics' level against the input, 3 s), `gain_match_db`, `latency_samples`.

## How to use it

- **Air on a master:** `Mode` Tape, `Drive` 12 dB, `Amount 4` 15–25 % (the band above 8.7 kHz). Preset "Mastering — Air". Check `added_db`: −25 to −18 dB is audible but gentle.
- **Presence on vocals:** `Mode` Tube, `Amount 3` and `Amount 4` 25–35 %. Preset "Vocal — Bright and Present".
- **Bass that carries on small speakers:** `Mode` Warm, move the crossovers low (`Crossover 1` 100 Hz, `Crossover 2` 400 Hz, `Crossover 3` 2 kHz) and add 40 % in band 2. Preset "Bass — Add Harmonics to Be Heard on Small Speakers".
- **Too bright:** lower `Shelf gain` before lowering `Amount`.
- `Delta` On to hear exactly what is being added; a buzz or hiss in it means too much `Drive` for the material.

## Gotchas

- An exciter adds energy: the master gets brighter and a little louder. `Auto gain` or `Match` keeps a fair comparison.
- The bands' default crossovers (800 Hz, 3 kHz, 8 kHz) put `Amount 4` on the top octave; only band 4 is on by default.
- Replacing Oomek Exciter with `machine_replace` carries its Drive, its cuts and its Amount into the matching settings.

## Preset map

| Preset | What |
|---|---|
| "Mastering — Air" | Tape, `Drive` 12 dB, `Amount 4` 20 % |
| "Mastering — Presence and Air" | Tube, `Amount 3` 15 %, `Amount 4` 25 %, `Shelf gain` +2 dB |
| "Mastering — Subtle Warmth" | Warm, `Amount 2` 20 %, `Amount 3` 15 %, `Amount 4` 10 % |
| "Vocal — Bright and Present" | Tube, `Drive` 14 dB, `Amount 3` 35 %, `Amount 4` 25 %, `Auto gain` On |
| "Drums — Crack and Sizzle" | Transistor, `Drive` 16 dB, `Amount 3` 30 %, `Amount 4` 40 % |
| "Bass — Add Harmonics to Be Heard on Small Speakers" | Warm, low crossovers, `Amount 2` 45 % |
| "Mid/Side — Air on the Sides" | Tape, M/S, `Amount 4` 35 % |
| "Old Exciter (Oomek-style)" | Tube, `Drive` 18 dB, `Amount 2` – `Amount 4` 20 – 60 % |
