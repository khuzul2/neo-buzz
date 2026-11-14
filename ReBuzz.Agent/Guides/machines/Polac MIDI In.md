## How it sounds

A control machine that receives MIDI from a device into the song. It has no parameters and makes no sound; what it forwards was not measured.

## Use it for

- Leave it alone unless a song you are reading already uses it.

## Key controls

None: it has no parameters.

## Starting points

- Read what it does in a song that has it: `machine_describe(machine="MIDI In")`.

## Pitfalls

- Unverified: nothing here was measured beyond that it has no parameters and no sound.
- The app's MIDI settings and bindings cover this now.

## Pairs well with

- Any instrument the song plays with it.

## Checklist

- [ ] `machine_describe(machine="MIDI In")`: it lists no parameters.
- [ ] `param_get(machine="MIDI In")`: nothing to read.
