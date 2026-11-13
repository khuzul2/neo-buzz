## How it sounds

A stereo imager in four bands. In each band the signal is split into its mid (what left and right share) and its side (what differs); `Width 1` to `Width 4` (0-200 %) set the side against the mid: 0 % is mono, 100 % as it came in, 200 % doubles the side. `Stereoize 1` to `Stereoize 4` add a decorrelated copy of the mid to the side, so a narrow band gets wider without changing the mono sum. `Recover sides` is for narrowing: the side that narrowing takes away is turned 90 degrees and returned to the mid, so the level stays and nothing combs in mono.

- All four widths at 100 % and every Stereoize at 0: the output is the input, bit for bit, with no latency.
- The bands are zero-latency Linkwitz-Riley splits (`Crossover 1` to `Crossover 3`); the band sum is flat.
- `Delta` plays only what the imager changes; `Output` comes after it (the editor's Match sets it from `gain_match_db`).

## Use it for

- Widening the highs while keeping the lows centred, repairing a mix that collapses in mono, widening a pad without mono risk, a mono fold-down check.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Crossover 1 | 150 Hz | the lows below it can be made mono on their own |
| Width 1 | 0% | mono lows; the other bands stay at 100-140 % |
| Width 4 | 120% | wider highs; over 100 % makes the side louder |
| Stereoize 4 | 20% | widens a narrow band without changing the mono sum |
| Recover sides | On | keeps the level when a band is narrowed |
| Output | 0 dB | lower it, or use Match, if widening made it louder |

## Starting points

- "Mastering — Widen the Highs" (Width 3 115 %, Width 4 135 %), "Mastering — Focus the Lows (Mono Below 150 Hz)", "Mastering — Air and Width", "Mastering — Stereoize the Top".
- "Mixing — Narrow a Wide Mix, Keep the Level" (Width 1 0 %, the rest 70 %, Recover sides On), "Mixing — Wide Pad".
- "Mixing — Mono Check (Everything at 0 %, Recovered)": every width 0 % with Recover sides On is a mono fold-down at the same level.
- "Mono-Safe Widening (Stereoize Only)" and "Lows Mono, Rest as It Is" (Crossover 1 120 Hz, Width 1 0 %).

## Pitfalls

- Width over 100 % makes the side louder: the master gets louder and a limiter after it works harder. Use Match or lower Output.
- A band at 0 % has no side at all; Stereoize widens it again with the decorrelated mid.
- Recover sides only does something while a width is under 100 %.
- A mix that collapses in mono: `analyze` the mix (`stereo.ms_bands`); the band with correlation under 0.2 or negative is the one to narrow (Width 40-70 %). Check `correlation_1` to `correlation_4` in `machine_readings` after.

## Pairs well with

- Neo Utility for bass mono and polarity; Neo Exciter in M/S for air on the sides; Neo Analyzer for `correlation` and `width`; Neo Scope's vector display.

## Checklist

- [ ] `machine_readings(machine="Imager")`: `correlation_1` to `correlation_4` all above 0.2 after the change.
- [ ] `analyze(source="master", metrics=["stereo"])`: width as planned, the mono fold-down not thinner.
- [ ] `machine_readings(machine="Imager")`: `width_db_1` to `width_db_4` moved by the amount you set; `latency_samples` is 0.
