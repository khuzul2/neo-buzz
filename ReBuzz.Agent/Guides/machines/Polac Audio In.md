## How it sounds

A routing machine that brings an input of the audio device into the song as a source, with one Gain control. An older machine; the Studio records inputs another way.

## Use it for

- Live input in a song that already uses it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Input gain | 0 dB | silence to +12 dB |

## Starting points

- Read what it does in a song that has it: `machine_describe(machine="Audio In")`.

## Pitfalls

- Unverified: it was silent in the profile (no device input), so which input it takes was not checked.
- A note does nothing: it is a live source.

## Pairs well with

- Any effect you want to run the input through.

## Checklist

- [ ] `machine_describe(machine="Audio In")`: its one parameter is `Input gain`.
- [ ] `param_get(machine="Audio In")`: `Input gain` is near 0 dB at the default.
