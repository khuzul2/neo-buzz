## How it sounds

A control machine that holds a rich text note in the song, edited in its own window. It makes no sound and sends nothing. Its one parameter does nothing measurable.

## Use it for

- Notes on how a song is built, pinned in the machine view.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Unused value | 0 | does nothing |

## Starting points

- For notes about the song, `song_notes_write(text="...")` is the app's own place and `song_notes_read` reads them.
- Use it as the song already does: `machine_describe(machine="Info")` lists what is on it.

## Pitfalls

- The text is the window's own state; no tool reads or writes it.
- Its window is Windows only.

## Pairs well with

- Nothing; it is a note.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Info")`: its one parameter is `Unused value`.
- [ ] `song_notes_read`: the app's own song notes.
- [ ] `param_get(machine="Info")`: the value reads 0.
