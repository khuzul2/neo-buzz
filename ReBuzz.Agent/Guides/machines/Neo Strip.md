## How it sounds

A channel strip in one machine: gate, EQ, compressor, drive, clipper and limiter, in that order. Each stage is the engine of the Neo machine of that name run with the strip's settings, so it sounds as that machine does (the EQ stage is Neo EQ with six of its bands, the compressor stage is Neo Comp, and so on). The strip with one stage on gives the same samples as that machine alone, delayed to line up.

- A stage that is off passes the audio untouched. All stages start off, and with every stage off the output is the input, delayed, bit for bit.
- The latency (the drive's oversampling and the clipper's and limiter's lookahead: 504 samples, about 11 ms at 44.1 kHz) is the same whichever stages are on, so switching a stage never shifts the track against the others. A stage switched on or off crossfades over 5 ms.
- Gate (`Gate on`): opens when the louder channel's peak passes `Gate threshold`, closes 3 dB lower after `Gate hold`, then lowers the signal by `Gate range` over `Gate release`. EQ (`EQ on`): `Low cut on`, a low shelf, two bells (`Low mid`, `High mid`, each frequency, gain and Q), a high shelf, `High cut on`. Compressor (`Comp on`): `Comp mode` Clean, Glue, FET or Opto. Drive (`Drive on`): five models with `Drive mix`. Clipper (`Clip on`): a hard clip at `Ceiling`, 4x oversampled. Limiter (`Limit on`): true-peak limiter at `Ceiling` (its range is -12 to 0 dB), after the clipper.

## Use it for

- One set of controls for one track: vocals, drums, bass and guitar channels, a quick master safety (clip and limit), a spoken-word clean-up.
- For a dynamic or linear-phase EQ, a sidechain or multiband work, use the machine itself: the strip has one detector and no sidechain input.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Gate threshold | -48 dB | with Gate on; Gate range 30 dB for spoken word |
| Low cut freq | 100 Hz | with Low cut on and EQ on |
| Comp mode | Glue | Opto for vocals, FET for bass and drums |
| Comp threshold | -18 dB | 1-3 dB of reduction |
| Comp ratio | 2:1 | gentle bus level; 4:1 for control |
| Drive | 4 dB | with Drive on; Drive mix sets the parallel share |
| Ceiling | -1 dB | shared by the clipper and the limiter (the limiter stops at -12 dB) |

## Starting points

- "Vocal — Up Front" (low cut 90 Hz, mud dip, presence, Opto, limiter), "Vocal — Spoken Word (Gate and Tidy)" (gate at -48 dB, 30 dB range, low cut 100 Hz).
- "Drums — Punch Bus", "Bass — DI Solid", "Guitar — Crunch and Air".
- "Master — Safety (Clip and Limit)" (clipper and limiter at -1 dB), "Master — Gentle Glue and Ceiling" (Glue 2:1, limiter).
- Turn on only the stages the part needs: EQ moves first, then compression, drive last.

## Pitfalls

- The clipper and the limiter share Ceiling.
- The latency is always there, even with every stage off.
- A gate or compressor that must hear another track needs Neo Gate or Neo Comp, not the strip.
- `machine_readings` gives `gate_db`, `comp_gr_db`, `limiter_gr_db`, `gain_match_db` and `latency_samples`; `Delta` plays only what the strip changes.

## Pairs well with

- Neo Analyzer after it on the bus; Neo Imager or Neo Multiband when the strip is not enough.

## Checklist

- [ ] `machine_add(gear="Neo Strip", name="Vox", insert_after="Vocal")`, then `preset_load(machine="Vox", preset="Vocal — Up Front")`.
- [ ] `machine_readings(machine="Vox")`: `comp_gr_db` 1-4 dB on the loud words, `limiter_gr_db` near 0.
- [ ] `analyze(source="Vocal", metrics=["loudness", "dynamics"])` compared with the input: level up to 3 dB, crest factor down.
