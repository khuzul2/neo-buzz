## How it sounds

A utility that records what is connected to it into a wavetable slot, with a Stop and Record mode, an auto-stop switch and an overwrite switch. It passes its input through unchanged while it records.

## Use it for

- Capturing a loop or a live take straight into a wave slot.
- Sampling the output of a machine for a sampler to play.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Recording mode | Stop | set Record to start a take |
| Auto stop | Enabled | ends the take on its own |
| Wavetable slot | 1 | 1 to 200; where the take is saved |
| Overwrite existing | Yes | No protects a slot that already has a wave |

## Starting points

- Pick a free slot with `wave_list`, set Wavetable slot, then `param_set(machine="EasyRec", changes=[{"param": "Recording mode", "value": "Record"}])`.
- For a bounce of the whole song, `wave_from_render` does it without a recording machine.

## Pitfalls

- Overwrite existing Yes can erase a wave: check `wave_list` first.
- Unverified: the condition that ends a take with Auto stop, and whether it records in the Studio.

## Pairs well with

- Any source you want to sample; a sampler in the next track.

## Successor

None: `wave_from_render` and the Session View's take recording cover the same need, but they are not a replacement for it, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="EasyRec")`: four parameters, `Recording mode` first.
- [ ] `wave_list`: the slot holds a wave after a take.
- [ ] `analyze(source="EasyRec", metrics=["loudness"])`: its output equals its input.
