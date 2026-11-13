## How it sounds

It makes no sound itself. It is a general control machine: each of up to 64 tracks sends its `Value` (0 to 100 % in 65,535 steps) through a mapping curve to a parameter of another machine, or out as a MIDI controller, and `Inertia` glides every change. A track can be `Slaved` to the one before it, so a group of tracks follows one control. One track can drive several parameters.

## Use it for

- One slider that moves several parameters, smoothing a jumpy parameter, a non-linear response (a curve) and MIDI controller output.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Value | 50% | 0-100%; the track's output through its mapping curve (track parameter) |
| Slaved | No | Yes makes this track mirror the previous one |
| Inertia | 4 ticks | the glide, 0 (off) to 128 ticks |

## Starting points

- A macro: two tracks aimed at different parameters, the second `Slaved` to the first.
- A smoother: Inertia 8 ticks on a parameter that jumps between pattern rows.
- Drive it with `automation_write(machine="Peer", pattern="A", param="Value", track=0, points=[{"at": 0, "value": "0%"}, {"at": 8, "value": "100%"}])`.

## Pitfalls

- The targets, curves and MIDI settings are in the machine's own settings window, and no tool sets them: ask the person, or it moves nothing.
- It has no Note column: a MIDI note gets no sound (the profile warns of that), and it is not an instrument.
- Only 3 parameters are exposed: its behaviour mostly lives in the targets and curves.

## Pairs well with

- Pedal LFO for a modulated source; any machine with a parameter to move.

## Checklist

- [ ] `machine_describe(machine="Peer")` shows `Value`, `Slaved` and `Inertia`.
- [ ] `param_get(machine="Filter")` before and after a `Value` change: the target moved.
- [ ] `render` with the target playing: the sound changes when `Value` does.
