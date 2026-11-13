## How it sounds

It makes no sound itself. It moves one parameter of another machine with a low-frequency wave: sine, triangle, square, saw up, saw down or sample and hold. `Center` sets the middle of the swing and `Depth` its size, both as percentages of the target's own range, so the same settings fit any parameter. It runs free over `Period` ticks (16 ticks is a bar at 4 ticks per beat) or locks to the song's tempo with `Sync`.

## Use it for

- Filter sweeps, tremolo and vibrato on machines without their own LFO, slow drifts of a reverb mix or a delay time.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Shape | Sine | Sine, Triangle, Square, Saw Up, Saw Down, S&H |
| Period | 32 ticks | one cycle in free mode; 16 ticks is a bar |
| Sync | 1/4 | locks a cycle to a note length (1/1 to 1/8T); Free uses Period |
| Depth | 40% | the swing, as a percentage of the target's range |
| Center | 50% | the middle of the swing |
| Phase | 0° | staggers it against another LFO |
| Start | On | restarts the wave from phase 0 when switched on |

## Starting points

- A slow filter sweep: Shape Triangle, Period 64 ticks, Depth 40 %, Center 50 % on a filter cutoff.
- A rhythmic pulse: Shape Square, Sync 1/8, Depth 30 %.
- A drift: Shape S&H, Period 16 ticks, Depth 10 %, Center 50 %.

## Pitfalls

- The target machine and parameter are chosen in its own dialog (Configure target), and no tool sets them: ask the person to pick them, or it moves nothing.
- The LFO writes whole steps of the target's range, so a coarse target (a switch, a 12-step enum) moves in jumps.
- `Start` Off holds the target at `Center`.
- With `Bypass` On it stops writing and leaves the target where it was.

## Pairs well with

- Pedal Filter, Pedal Juno106 or Pedal SH101 as the target; Pedal Peer Control for several targets from one control.

## Checklist

- [ ] `param_get(machine="Filter")` twice, a few beats apart while the song plays: the target's value moved.
- [ ] `render(probes=["Filter"])`: the target's sound varies with the wave.
- [ ] `param_get(machine="LFO")`: `Depth` and `Center` in %, `Sync` as a note length.
