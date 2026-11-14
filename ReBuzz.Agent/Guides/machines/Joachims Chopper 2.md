## How it sounds

A gapper: it cuts the sound into rhythmic chops, each fired by writing Trigger, with a band-pass filter on the gap, a de-clicking attack, a time-travel control that replays earlier sound and an internal random pattern. Version 2.2.

## Use it for

- Stutter and rhythmic gating.
- Glitchy rearrangements with Travel back.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Dry/wet mix | 100% | 0 to 100 percent |
| Gap filter frequency | 1 kHz | about 40 Hz to 10 kHz |
| Gap filter bandwidth | 100 | 1 to 128; 128 is the filter off |
| Attack | 5 ms | 0 to 50 ms |
| Travel back | 0 | 0 to 128 ticks |
| Command | 1 | 1 to 255, see Pitfalls |

## Starting points

- Chop on the beat: write `Gap trigger` in the pattern (`automation_write`) on each row you want a chop.
- A narrow pitched blip: `Gap filter bandwidth` 20, `Gap filter frequency` 800 Hz.

## Pitfalls

- Nothing chops until Trigger is written; at the defaults it passes the sound (-0.8 dB in the profile).
- Command and Value work together: Command 1 resets the random pattern, 5 sets filter steepness, 11 gate or ducking mode, 16 what Assignable does; Value is the amount.
- Unverified: the decay each Trigger step gives (its labels all read short).
- Its Rows per beat attribute should stay at 4 unless the pattern editor differs.

## Pairs well with

- Jeskola Delay after it for echoing chops.

## Checklist

- [ ] `analyze(source="Chopper", metrics=["loudness"])`: the output is about the input level with the mix at 100 percent.
- [ ] `param_get(machine="Chopper")`: `Dry/wet mix` is what you set.
- [ ] `render(probes=["Chopper"])`: gaps are silent and chops do not click.
