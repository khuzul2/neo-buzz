## How it sounds

A control machine that belongs to the Modern Sequence Editor view, with one parameter that does nothing measurable. It makes no sound. What it does for the editor beyond carrying it was not found.

## Use it for

- Nothing on its own: it appears in songs saved with that editor.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Unused switch | no | does nothing |

## Starting points

- Use it as the song already does: `machine_describe(machine="Modern Sequence Editor")` lists what is on it.
- To work on the arrangement here, `arrangement_get` and `arrangement_place`.

## Pitfalls

- Unverified: the editor's view is in ReBuzzGUI/ModernSequenceEditor; the Studio has its own arrangement view.
- It is a Windows machine; the Studio does not show its window.

## Pairs well with

- Nothing.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Modern Sequence Editor")`: its one parameter is `Unused switch`.
- [ ] `arrangement_get`: the song's arrangement is read through the tools.
- [ ] `param_get(machine="Modern Sequence Editor")`: it reads no.
