## How it sounds

A noise gate and downward expander. The gate opens when the key's level reaches `Threshold` and closes when it falls `Hysteresis` under it; after the signal falls it stays open for `Hold`, then closes over `Release`. `Attack` and `Release` are linear gain ramps (the time for the whole opening or closing). A closed gate sits at `Range` (-90 dB is nearly shut, 0 dB does nothing).

- `Ratio` below infinity makes it an expander: under the close threshold every dB of level is (ratio - 1) dB quieter, down to `Range`; at infinity the closed gate goes straight to `Range`.
- `Detection` Peak follows transients (1 ms attack, 10 ms release); RMS averages 10 ms, smoother on sustained sources.
- The key: `Sidechain` Internal (the input) or External (a machine on input 2); `Key low cut` and `Key high cut` filter it so rumble or cymbal spill does not open the gate; `Listen` plays the filtered key.
- `Lookahead` (0-25 ms) delays the audio so the gate is open before the transient arrives (latency, reported). `Delta` plays only what the gate takes out.

## Use it for

- Tightening snares, kicks and toms, removing hiss or bleed between phrases and takes, rhythmic gating with an external key.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Threshold | -28 dB | opens the gate; watch `open` in `machine_readings` |
| Hysteresis | 8 dB | 6-10 dB stops chatter before you lengthen Hold |
| Range | -60 dB | -90 is nearly shut; -18 keeps a natural floor |
| Attack | 0.2 ms | fast keeps the hit's start sharp |
| Hold | 60 ms | stops the gate cutting note ends |
| Release | 120 ms | short chops tails, long fades them |
| Lookahead | 2 ms | gate open before the transient (adds latency) |

## Starting points

- "Drums — Snare, Tight" (-28 dB, hold 60 ms, lookahead 2 ms, Key low cut 150 Hz to keep the kick's spill out).
- "Drums — Kick, Clean the Spill", "Drums — Toms, Gentle Expander" (Ratio 4, Range -30 dB: less obvious than a hard gate).
- "Vocals — Soft Gate Between Phrases" (Ratio 3, Range -18 dB, long Hold and Release), "Bass — Tighten the Tail" (RMS), "Noise — Hiss Between Takes".
- A duck-and-open effect: Sidechain External with a kick into input 2 opens a gate on a pad on every kick.

## Pitfalls

- A hard gate on a decaying note chops the tail: use Range -18 dB or an expander ratio, or a longer Hold.
- With Lookahead above 0 the audio is late by it and the engine compensates; with 0 there is no latency.
- With Sidechain External and nothing on input 2 the key is silent and the gate stays shut.

## Pairs well with

- Neo Comp after it to level what stays; Neo EQ before it to clean the key; Neo Transient to restore punch.

## Successor

Neo Gate replaces Pedal Gate: `machine_replace(machine="Gate")` carries every setting over in its units (Bypass becomes the machine's bypass); "Pedal Gate — Same Settings" is its defaults.

## Checklist

- [ ] `machine_readings(machine="Gate")`: `open` flips with the hits, `gain_db` sits at Range between them, no chatter.
- [ ] `Delta` on (`param_set(machine="Gate", changes=[{"param": "Delta", "value": "On"}])`) contains only noise and tails, not the wanted notes.
- [ ] `analyze(source="Snare", metrics=["dynamics"])`: the floor between hits dropped.
