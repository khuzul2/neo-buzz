## How it sounds

A hard noise gate. It opens when the level reaches `Threshold` and closes when it falls `Hysteresis` below it; after the signal falls it holds open for `Hold`, then closes over `Release`. A closed gate sits at `Range` (-90 dB is nearly shut; 0 dB does nothing). `Detection` Peak follows transients, RMS is smoother. `Lookahead` delays the audio so the gate is open before a transient arrives. A second input, `Sidechain`, can supply the key.

## Use it for

- Tightening snares, kicks and toms, removing hiss or bleed between phrases, and rhythmic gating with an external key.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Threshold | -30 dB | 0.5 dB steps from -60 to 0; the default |
| Hysteresis | 6 dB | 6-10 dB stops chatter |
| Attack ms | 1 ms | fast keeps the start of a hit sharp |
| Hold ms | 60 ms | stops the gate cutting note ends |
| Release ms | 150 ms | short chops tails, long fades them |
| Range dB | -60 dB | -90 is nearly shut; -18 keeps a natural floor |
| Lookahead ms | 2 ms | gate open before the transient (adds delay) |

## Starting points

- A snare: Threshold -28 dB, Hysteresis 8 dB, Attack 0.5 ms, Hold 60 ms, Release 120 ms, Range -60 dB.
- A gentle gate between phrases: Range -18 dB, Hold 200 ms, Release 400 ms, Detection RMS.
- A key from another machine: `connect(from="Kick", to="Gate", input="Sidechain")` so the kick opens it.

## Pitfalls

- Its README says 1 dB steps from -80 dB; the code uses 0.5 dB steps from -60 to 0 dB, as the manual shows.
- With `Range` at its default of 0 the closed gate is nearly silent (-90 dB): a hard gate on a decaying note chops the tail. Use -18 dB or a longer `Hold`.
- With `Lookahead` above 0 the output is late by that time.
- A sidechain connection supplies the key, not the audio: the gate still passes its main input.

## Pairs well with

- Pedal Comp after it to level what stays; Pedal EQ before it to clean the key; Neo Transient to restore punch.

## Successor

Neo Gate replaces Pedal Gate: `machine_replace(machine="Gate")` carries every setting over in its units (Neo Gate's Ratio goes to infinity, the key filters are off, the sidechain stays internal). The match is 0.00 dB of loudness.

## Checklist

- [ ] `analyze(source="Snare", metrics=["dynamics"])`: the floor between hits dropped.
- [ ] `render(probes=["Gate"])`: no chatter on decaying notes; the gate stays open through each hit.
- [ ] `param_get(machine="Gate")`: `Threshold` in dB, `Hold ms` and `Release ms` in ms.
