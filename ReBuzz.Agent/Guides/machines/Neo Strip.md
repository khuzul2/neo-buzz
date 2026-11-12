## How it sounds

A channel strip in one machine: gate, EQ, compressor, drive, clipper and limiter, in that order. Each stage is the engine of the Neo machine of that name run with the strip's settings, so it sounds as that machine does: the EQ stage is Neo EQ with six of its bands, the compressor stage is Neo Comp, and so on. Measured: the strip with one stage on gives the same samples as that machine alone, delayed to line up.

- A stage that is off passes the audio untouched. With every stage off the output is the input delayed by the strip's latency, bit for bit (all stages start off).
- The latency (the drive's oversampling, the clipper's and the limiter's lookahead: about 11 ms at 44.1 kHz in all: 504 samples) is the same whichever stages are on, so switching a stage never shifts the track against the others.
- A stage switched on or off crossfades over 5 ms.

## Signal flow

Gate, EQ, compressor, drive, clipper, limiter, then `Output`.

- **Gate** (`Gate on`): opens when the louder channel's peak passes `Gate threshold`, closes 3 dB lower after `Gate hold`, and then lowers the signal by `Gate range` (80 dB is nearly silence) over `Gate release`. `Gate attack` is the time to open.
- **EQ** (`EQ on`): `Low cut on` (12 dB/oct, `Low cut freq`), a low shelf, two bells (low mid and high mid, each with frequency, gain and Q), a high shelf, and `High cut on` (12 dB/oct). A band with no gain changes nothing.
- **Compressor** (`Comp on`): `Comp mode` Clean, Glue, FET or Opto, threshold, ratio, attack, release and makeup (no lookahead, no sidechain input).
- **Drive** (`Drive on`): Tape, Tube, Transistor, Diode or Fold, `Drive` amount and `Drive mix` (auto gain holds the level).
- **Clipper** (`Clip on`): a hard clip at `Ceiling`, 4× oversampled.
- **Limiter** (`Limit on`): the true-peak limiter at `Ceiling` (dBTP; its range is −12 to 0 dB), `Limiter release`. After the clipper when both are on.
- `Output`, `Delta` (hear only what the strip changes) and the editor's Match.

## How to use it

- **One set of controls for one track:** turn on the stages the part needs, set the EQ moves first, then the compressor, drive last. Start from a preset.
- **A bus or a master:** compressor Glue at 2:1, the clipper and the limiter at one ceiling (preset "Master — Safety (Clip and Limit)").
- **A spoken voice:** the gate at −48 dB with 30 dB of range, a low cut at 100 Hz (preset "Vocal — Spoken Word (Gate and Tidy)").
- For anything the strip's controls cannot do (a dynamic or linear-phase EQ, a sidechain, multiband), use the machine itself.
- `machine_readings` gives `gate_db`, `comp_gr_db`, `limiter_gr_db`, `gain_match_db` and `latency_samples`.

## Gotchas

- The clipper and the limiter share `Ceiling`; the limiter's range stops at −12 dB.
- The latency is always there, even with every stage off.
- Neo Strip has one detector and no sidechain: a gate or compressor that must hear another track needs Neo Comp.

## Preset map

| Preset | What |
|---|---|
| "Vocal — Up Front" | low cut 90 Hz, mud dip, presence, Opto compression, limiter |
| "Vocal — Spoken Word (Gate and Tidy)" | gate, low cut, mud dip, Clean compression, limiter |
| "Drums — Punch Bus" | low shelf, Glue compression, tape drive in parallel, clipper |
| "Bass — DI Solid" | low cut 35 Hz, mud dip, FET compression, tube drive in parallel |
| "Guitar — Crunch and Air" | low cut, mid dip and presence, transistor drive, limiter |
| "Master — Safety (Clip and Limit)" | clipper and limiter at −1 dB |
| "Master — Gentle Glue and Ceiling" | tone shelves, Glue compression 2:1, limiter |
