## How it sounds

A control modulator with no audio of its own: it moves up to 8 parameters of other machines using random steps, probability-gated sample and hold, bounded 1/f-like drift or chaotic Lorenz trajectories. Each target keeps its base value and Neo Random modulates smoothly within the parameter's range and safe range, never recording undo steps.

- `Mode`: `Random` draws uniform steps (or raised-cosine curves as `Smooth` rises); `Sample and hold` gates updates by `Probability`; `Drift` wanders slowly with a bounded 1/f-like spectrum; `Chaos` blends from a calm periodic orbit (`0 %`) into a double-scroll Lorenz attractor (`100 %`).
- Speed: `Sync` `Free` uses `Rate` (`0.02` to `40 Hz`); synced divisions (`8 bars` down to `1/64` with `Feel` `Straight`, `Dotted` or `Triplet`) lock steps to the song's tempo.
- `Quantise` (`0` = off, `1` to `32`) snaps output onto N evenly spaced levels. `Depth`, signed `Bias` (`−100` to `+100 %`) and `Range` (`Unipolar` or `Bipolar`) shape the output before target slots.
- `Seed` (`1` to `9999`) repeats bit-for-bit on every playback and render; `0` picks a fresh sequence every time. `Retrigger` (`Beat`, `Bar`, `Note`) restarts the phase and seed (or forces a fresh step at `Seed 0`).
- New target slots added to Neo Random default to `uni` polarity in the editor and `modulate` tool; pass `polarity="bi"` per target when you want symmetric swing around the target's base.

## Use it for

- Organic filter wandering, tempo-locked random filter steps, sparse probability glitch accents, subtle stereo pan humanisation, 12-level stepped modulation, binary random gates and chaotic Lorenz sweeps.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Random | Random, Sample and hold, Drift (1/f walk) or Chaos (Lorenz) |
| Rate | 1.00 Hz | Free speed in Hz while Sync is Free |
| Sync | Free | Locks steps or orbit speed to 8 bars … 1/64 |
| Probability | 100 % | Chance a Sample and hold step changes value |
| Smooth | 0 % | 0 % crisp steps; 100 % smooth curves or continuous Lorenz flow |
| Quantise | 0 | 0 off; 2 for binary gates, 12 for chromatic-style steps |
| Seed | 1 | 1–9999 exact repeat; 0 new random sequence each run |
| Write glide | 30 ms | Least time a target takes to cross its whole range |

## Starting points

- Evolving pad filter: load preset `Gentle drift`, `modulate(source="Rnd", target_machine="Pad", param=<cutoff>, depth=35, polarity="bi")`.
- Synced filter steps: load `Random filter steps` (`Sync` `1/8`, `Smooth` `0 %`) on a synth cutoff with `depth=50` and `polarity="bi"`.
- Glitchy accents: load `Probability glitch` (`Sample and hold`, `1/16`, `Probability` `28 %`) on filter cutoff or delay feedback.
- Chaotic sweep: load `Lorenz swirl` (`Chaos`, `Chaos` `92 %`, `Smooth` `100 %`) on a resonant filter.

## Pitfalls

- New targets default to `uni` polarity: pass `polarity="bi"` (or click `bi` in the target table) when you want movement both above and below the base.
- `Seed 0` changes on every render; pick a non-zero `Seed` (`1`–`9999`) when you want a deterministic bounce or loop.
- `Retrigger` `Note` needs `listen_machine` set via `modulate(source=…, listen_machine=…)` and only sees song or `midi_play` notes.
- Two modulators on the same parameter fight; master volume is never pushed past 0 dB and tempo parameters are refused.

## Pairs well with

- Neo Poly, Neo Filter, Neo Wave, Neo Delay and Neo Utility as targets; Neo LFO for periodic waves and Neo Step for exact patterns.

## Checklist

- [ ] `modulation_list(machine="Rnd")` shows each target `active` with the expected range and polarity.
- [ ] `render(probes=["Pad"])` confirms audible, click-free movement matching the preset's mode and rate.
- [ ] Removing the target (`modulation_remove(source="Rnd", all=true)`) restores the parameter's base value.
