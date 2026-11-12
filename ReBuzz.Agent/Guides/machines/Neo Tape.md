## How it sounds

A tape machine: the signal gets a record pre-emphasis (`Emphasis`, a high shelf), goes through an oversampled, anti-aliased tape saturation (`Saturation`, `Bias`), then the playback de-emphasis (the exact inverse of the pre-emphasis), the head bump and the high-frequency loss of the tape `Speed`, an even-harmonic `Electronics` stage, optional `Hiss`, and `Wow` and `Flutter` from a modulated read.

- `Speed` 7.5, 15 or 30 ips moves the head bump (35, 70, 140 Hz) and the high-frequency loss corner (6, 9.5, 14 kHz) and the hiss (3 dB more at 7.5, 3 dB less at 30). `Head bump` and `HF loss` are their amounts in dB.
- `Saturation` (0–30 dB) is the gain into the tape curve: small signals pass unchanged whatever it is, louder ones are rounded off earlier, odd harmonics first. `Bias` moves the working point: it adds even harmonics (its DC is removed). `Electronics` adds a second harmonic after the tape. `Emphasis` makes the highs saturate earlier without changing the small-signal response.
- `Quality` **High** is 4× oversampled (aliasing under −90 dB; latency constant, reported). **Live** has no oversampling and no compensation: zero latency, a little more aliasing and a softer top (−2 dB at 10 kHz): for monitoring and live play.
- `Wow` (cents) and `Flutter` (cents) bend the pitch slowly and quickly; the modulated read adds 5.2 ms of latency while either is above 0 (reported). `Mix` blends the dry in; at 0 % the output is the input, delayed, bit for bit. `Delta` plays only what the tape adds. `machine_readings`: `added_db`, `gain_match_db`, `latency_samples`.

## How to use it

- **Glue on a master:** `Speed` 15 ips, `Saturation` 4 dB, `Head bump` 1.5 dB, `HF loss` 1.5 dB, `Electronics` 10 %. Preset "Mastering — Subtle Glue (15 ips)". Match the loudness (`gain_match_db`) before judging.
- **Warm and rounded:** 7.5 ips, `Saturation` 6 dB, `Head bump` 2.5 dB, `HF loss` 4 dB. Preset "Mastering — Warm and Rounded (7.5 ips)".
- **Drums:** `Saturation` 14 dB, `Emphasis` 9 dB, `Mix` 70 %. Preset "Drums — Tape Crunch".
- **Aged sound:** `Wow` 12 cents, `Flutter` 6 cents, `Hiss` −60 dB. Preset "Lo-Fi — Worn Cassette Feel".
- Parallel: `Saturation` 22 dB with `Mix` 35 % (Preset "Parallel — Tape Crush") for density without losing the transients.

## Gotchas

- Tape colours the lows and highs a little even at a low `Saturation` (the head bump and the loss): set both to 0 to hear the saturation alone.
- `Hiss` makes sound with no input: the machine never rests while it is on.
- `Wow` above 0 delays everything by 5.2 ms (the engine compensates the path); switch `Quality` to Live and the wow off for zero latency.

## Preset map

| Preset | What |
|---|---|
| "Mastering — Subtle Glue (15 ips)" | 15 ips, `Saturation` 4 dB, bump and loss 1.5 dB |
| "Mastering — Warm and Rounded (7.5 ips)" | 7.5 ips, `Saturation` 6 dB, `HF loss` 4 dB, `Hiss` −76 dB |
| "Mastering — Open and Fast (30 ips)" | 30 ips, `Saturation` 3 dB |
| "Mix Bus — Tape Glue" | 15 ips, `Saturation` 7 dB, `Bias` +10 %, `Emphasis` 8 dB |
| "Drums — Tape Crunch" | `Saturation` 14 dB, `Emphasis` 9 dB, `Mix` 70 % |
| "Vocal — Gentle Tape Warmth" | `Saturation` 5 dB, `HF loss` 2 dB |
| "Lo-Fi — Worn Cassette Feel" | 7.5 ips, wow 12 ct, flutter 6 ct, `Hiss` −60 dB |
| "Parallel — Tape Crush" | 7.5 ips, `Saturation` 22 dB, `Mix` 35 % |
