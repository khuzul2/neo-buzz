## How it sounds

A routing machine that sends its input to an output of the audio device, with one Volume control. An older machine for routing outputs by hand; the audio device settings and the master do this now.

## Use it for

- Leave it alone unless a song you are reading already uses it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Output volume | 0 dB | silence to +12 dB |

## Starting points

- Read what it does in a song that has it: `machine_describe(machine="Audio Out")`.

## Pitfalls

- Unverified: the profile has no measurement for it, so which device output it uses and what it does to the signal were not checked.
- Volume above 0 dB can overload the device.

## Pairs well with

- Any machine you route to a second output.

## Checklist

- [ ] `machine_describe(machine="Audio Out")`: its one parameter is `Output volume`.
- [ ] `param_get(machine="Audio Out")`: `Output volume` is near 0 dB at the default.
