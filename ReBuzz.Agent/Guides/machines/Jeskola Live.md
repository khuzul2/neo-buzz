## How it sounds

A control machine with a single parameter, Bar (1 to 128, 16 by default). It makes no sound of its own. What the parameter does was not measured, so this page describes only what can be seen.

## Use it for

- Leave it alone unless a song you are reading already uses it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bar | 16 | 1 to 128; the machine's own text gives no description |

## Starting points

- Read what it does in a song that has it: `machine_describe(machine="Live")`.

## Pitfalls

- Unverified: nothing here was measured beyond its range.
- It is a control machine: it makes no sound itself and was only measured for its parameter range, so what it does here is unverified.

## Pairs well with

- Any machine it controls; look in the song.

## Checklist

- [ ] `machine_describe(machine="Live")`: its one parameter is `Bar`.
- [ ] `param_get(machine="Live")`: `Bar` is in 1 to 128.
