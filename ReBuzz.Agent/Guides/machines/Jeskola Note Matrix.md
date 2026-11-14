## How it sounds

A control machine with a single placeholder parameter (its own name is Dummy). It makes no sound of its own and its work happens in its window, which was not measured.

## Use it for

- Leave it alone unless a song you are reading already uses it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Dummy | 0 | a placeholder; it does nothing |

## Starting points

- Read what it does in a song that has it: `machine_describe(machine="Matrix")`.

## Pitfalls

- Unverified: nothing here was measured beyond its range.
- It is a control machine: it makes no sound itself and was only measured for its parameter range, so what it does here is unverified.

## Pairs well with

- Any machine it controls; look in the song.

## Checklist

- [ ] `machine_describe(machine="Matrix")`: its one parameter is `Dummy`.
- [ ] `param_get(machine="Matrix")`: `Dummy` is 0.
