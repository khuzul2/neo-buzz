## How it sounds

A tape machine: a record pre-emphasis (`Emphasis`, a high shelf), an oversampled, anti-aliased tape saturation (`Saturation`, `Bias`), the matching de-emphasis, the head bump and high-frequency loss of the tape `Speed`, an even-harmonic `Electronics` stage, optional `Hiss`, and `Wow` and `Flutter` from a modulated read. It rounds peaks, thickens the lows and softens the top; at a low `Saturation` it is mostly colour from the head bump and the loss.

- `Speed` 7.5, 15 or 30 ips moves the head bump (35, 70, 140 Hz), the high-frequency loss corner (6, 9.5, 14 kHz) and the hiss.
- `Saturation` is the gain into the curve: small signals pass unchanged, louder ones are rounded off earlier, odd harmonics first. `Bias` adds even harmonics.
- `Quality` High is 4x oversampled (constant, reported latency); Live has no oversampling and zero latency, for playing.
- `Delta` plays only what the tape adds. Every control has a How and a Sound sentence in `machine_describe`.

## Use it for

- Glue and warmth on a mix bus or master, crunch on drums, softening a bright source, parallel tape density, an aged cassette feel.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Speed | 15_ips | 7.5 ips is darker and bumpier, 30 ips clearer |
| Saturation | 4 dB | glue at 3-6 dB; 14-22 dB for crunch |
| Head bump | 1.5 dB | the low shelf, 0-6 dB |
| HF loss | 1.5 dB | the top roll-off, 0-12 dB |
| Electronics | 10% | a touch of second harmonic |
| Mix | 100% | 35 % with heavy saturation for parallel |
| Output | 0 dB | match the loudness before judging |

## Starting points

- "Mastering — Subtle Glue (15 ips)", "Mastering — Warm and Rounded (7.5 ips)", "Mastering — Open and Fast (30 ips)", "Mix Bus — Tape Glue".
- "Drums — Tape Crunch", "Vocal — Gentle Tape Warmth", "Parallel — Tape Crush", "Lo-Fi — Worn Cassette Feel" (`Wow` 12 ct, `Flutter` 6 ct, `Hiss` -60 dB).

## Pitfalls

- Tape colours the lows and highs even at a low `Saturation`: set `Head bump` and `HF loss` to 0 to hear the saturation alone.
- `Hiss` makes sound with no input; the machine never rests while it is on.
- `Wow` or `Flutter` above 0 delays everything by 5.2 ms (reported); use Live and no wow for zero latency.
- It is louder or quieter than the input: compare at matched loudness, not at the same fader.

## Pairs well with

- Neo Comp before it for control and Neo Limiter after it on a master; Neo EQ to trim what the tape adds; Neo Delay after it for wobbling repeats; Neo Drive for a harder edge.

## Checklist

- [ ] `machine_readings(machine="Tape")` gives `gain_match_db`: set `Output` to cancel it before judging, and `added_db` shows how much the tape changes.
- [ ] `machine_readings` gives `latency_samples`: 0 with Live and no wow, otherwise the reported delay.
- [ ] `analyze(source="Tape", metrics=["dynamics"])`: the crest factor falls a little, not a lot, for glue settings.
- [ ] `compare` a bypassed render (`machine_set(machine="Tape", bypass=true)`) with the processed one at matched loudness.
