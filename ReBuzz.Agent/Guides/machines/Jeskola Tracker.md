## How it sounds

A Buzz tracker machine with up to 16 tracks and a single global parameter, Subdiv, for row timing. A note at its defaults gave no sound in the profile, even with a wave in slot 1, so what it plays is unverified.

## Use it for

- Leave it alone unless a song you are reading already uses it; Neo Sampler is the sample player to use.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Subdiv | 6 | 1 to 15; ticks are divided into this many parts |

## Starting points

- Read how a song drives it: `machine_describe(machine="Tracker")`.

## Pitfalls

- It made no sound from a note at its defaults, as MIDI or in its tracks.
- Unverified: how to make it play was not found.

## Pairs well with

- Neo Sampler for samples.

## Checklist

- [ ] `machine_describe(machine="Tracker")`: its one parameter is `Subdiv`.
- [ ] `param_get(machine="Tracker")`: `Subdiv` is in 1 to 15.
