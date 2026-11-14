## How it sounds

A control machine that edits a text script for a track in its own window. It makes no sound itself, and its one parameter does nothing measurable. What the script language is was not found.

## Use it for

- Songs that already use it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Unused value | 0 | does nothing |

## Starting points

- Use it as the song already does: `machine_describe(machine="TrackScript")` lists what is on it.

## Pitfalls

- Unverified: its DLL names a script canvas and pattern text changes, but what scripts do was not measured.
- It references Windows presentation code: the Studio does not show its window.

## Pairs well with

- The machine its script writes to, if the song has one.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="TrackScript")`: its one parameter is `Unused value`.
- [ ] `param_get(machine="TrackScript")`: the value reads 0.
- [ ] `pattern_list(machine="TrackScript")`: any patterns the song holds for it.
