## How it sounds

A control machine that holds a text note in the song, edited in its own window. It makes no sound and sends nothing; its one parameter does nothing.

## Use it for

- A reminder pinned beside a machine in the machine view.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Unused switch | Off | does nothing |

## Starting points

- For notes about the song as a whole, write them with `song_notes_write(text="...")` instead; it is the app's own place and the agent can read it back with `song_notes_read`.
- Use it as the song already does: `machine_describe(machine="QuickNote")` lists what is on it.

## Pitfalls

- The text is the machine's own state; the agent cannot read or write it through a tool.
- It has no audio: the profile heard silence.

## Pairs well with

- Nothing; it is a note.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="QuickNote")`: its one parameter is `Unused switch`.
- [ ] `song_notes_read`: the song's own notes are there instead.
- [ ] `param_get(machine="QuickNote")`: the switch reads Off.
