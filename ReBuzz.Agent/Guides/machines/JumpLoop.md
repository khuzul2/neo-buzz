## How it sounds

A control machine that makes the song loop or jump: when playback reaches it, it jumps to a target position, given in ticks, beats or patterns, a set number of times, optionally holding for a few ticks first. It makes no sound.

## Use it for

- A section that repeats a few times in a live set.
- A hold-and-return that loops until stopped.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Target unit | Beat | Tick, Beat or Pattern |
| Jump count | 4 | 0 jumps without end |
| Reset count | Yes | start the count again at zero |
| Jump target | 0 | the position to return to, in the unit |
| Hold ticks | 0 | 0 to 20,000 ticks |

## Starting points

- Repeat the first four beats four times: Target unit Beat, Jump target 0, Jump count 4.
- Loop until stopped: Jump count 0.

## Pitfalls

- A count of 0 loops for as long as the song plays: stop it with `transport(action="stop")`.
- The count restarts when play is pressed.
- Unverified: exactly where in the song the jump is triggered and how Reset count behaves at zero.

## Pairs well with

- Any section of the song.

## Successor

None: `song_set(loop_start_beats=0, loop_end_beats=16, looping=true)` loops a range, but it is not a drop-in for a jump with a count, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="JumpLoop")`: five parameters, `Target unit` first.
- [ ] `param_get(machine="JumpLoop")`: Jump count reads 0 (without end).
- [ ] `transport(action="play")`: the song returns to the target when it reaches the machine.
