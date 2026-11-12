## How it sounds

A de-esser: a band-pass detector on the sibilance band (`Frequency`, `Width` octaves wide) reads how hard the "s" and "t" sounds are, and the machine takes that band down when it passes `Threshold`, by (1 − 1/`Ratio`) of the excess, at most `Max reduction`, with `Attack` and `Release`.

- `Detector` **Relative** reads the band's level against the whole signal's: it follows a singer who sings louder or softer (`Threshold` −15 dB means the band within 15 dB of the whole signal counts as sibilance). **Absolute** reads the band in dBFS: for cymbals, hats and bright masters.
- `Action` **Split** takes only the band down (minimum phase, no latency of its own: the rest of the spectrum is left bit for bit). **Split linear** does the same with a linear-phase band (latency 255 samples, constant). **Shelf** and **Bell** act on the whole signal as a dynamic high shelf or peak filter at `Frequency`: gentler, more natural on a dense mix.
- `Lookahead` (0–5 ms) delays the audio so the reduction is already in place when the sibilant arrives (the latency is reported). With nothing to reduce the output is the input, delayed, bit for bit.
- `Listen` plays the band the detector hears: sweep `Frequency` until the harsh sound is loud, then switch it off. `Delta` plays only what is taken out. `machine_readings`: `reduction_db`, `gain_match_db`, `latency_samples`.

## How to use it

- **A vocal:** `Detector` Relative, `Frequency` 6.5 kHz (female 7–8 kHz, male 5–6 kHz; find it with `Listen`), `Threshold` −15 dB, `Ratio` 3–4, `Max reduction` 6–8 dB. Preset "Vocal — Gentle De-ess". Read `reduction_db` during the loud passages: 3–6 dB is about right, more than 10 means the threshold is too low.
- **A bright master:** Absolute, `Frequency` 9 kHz, `Width` 2 octaves, `Action` Bell, `Max reduction` 4 dB. Preset "Mastering — Tame a Bright Master".
- **Hats and cymbals on a drum bus:** Absolute, `Frequency` 8.5 kHz, a fast `Attack`. Preset "Drums — Harsh Hats and Cymbals".
- **Transparent on a solo vocal:** `Action` Split linear: no phase shift in the split (at the cost of 255 samples of latency, reported to the engine).

## Gotchas

- A de-esser works on the level of the band, not on the word: a `Ratio` too high with a low `Threshold` lisps. Lower `Max reduction` first.
- Shelf and Bell change the whole signal's top end while they reduce; Split changes only the band. If a vocal sounds dull in the "s" sounds, use Split.
- Latency is the lookahead (and 255 samples more in Split linear): `Lookahead` 0 and Split give zero latency for live use.

## Preset map

| Preset | What |
|---|---|
| "Vocal — Gentle De-ess" | Relative, 6.5 kHz, ratio 3, up to 6 dB |
| "Vocal — Female, Strong" | Relative, 7.5 kHz, ratio 6, up to 12 dB, lookahead 1.5 ms |
| "Vocal — Male, Warm Shelf" | Relative, 5.5 kHz, a dynamic high shelf |
| "Vocal — Transparent (Linear Phase Split)" | Relative, 6.5 kHz, Split linear |
| "Mastering — Tame a Bright Master" | Absolute, 9 kHz, 2 octaves, Bell, up to 4 dB |
| "Drums — Harsh Hats and Cymbals" | Absolute, 8.5 kHz, 2 octaves, fast attack |
| "Find the Sibilance (Listen)" | Listen On, to find the frequency |
