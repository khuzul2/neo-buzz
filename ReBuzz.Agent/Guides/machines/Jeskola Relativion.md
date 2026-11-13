## How it sounds

A control machine that works with note ranges: a root note, a lowest and a highest note, and a controller root and range. It makes no sound of its own, and what it controls was not measured, so its behaviour is unverified.

## Use it for

- Leave it alone unless a song you are reading already uses it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Root Note | C-5 | notes run C-0 to G-10 (0 to 127) |
| Lowest Note | C-5 | the lowest note it reacts to |
| Highest Note | C-10 | the highest note it reacts to |
| Ctrl. Root | C-5 | the middle of the controlled movement |
| Ctrl. Range | 24 | 0 to 48 steps |
| Volume | C-0 | its default; what it does is unverified |

## Starting points

- Read what it does in a song that has it: `machine_describe(machine="Relativion")`.

## Pitfalls

- Unverified: nothing here was measured beyond ranges and texts.
- It is a control machine: it makes no sound itself and was only measured for its parameter range, so what it does here is unverified.
- A seventh parameter exists in the machine but is not in the manifest.

## Pairs well with

- Any machine it controls; look in the song.

## Checklist

- [ ] `machine_describe(machine="Relativion")`: six parameters, notes named C-0 up.
- [ ] `param_get(machine="Relativion")`: `Ctrl. Range` is in 0 to 48.
