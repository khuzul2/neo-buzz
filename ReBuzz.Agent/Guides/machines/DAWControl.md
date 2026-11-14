## How it sounds

A control machine with a Bypass switch and its own window that responds to MIDI notes and controller changes as a DAW controller. It makes no sound. What it controls was not measured here.

## Use it for

- Songs that already use it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bypass | no | yes stops it acting |

## Starting points

- Use it as the song already does: `machine_describe(machine="DAWControl")` lists what is on it.
- In this app, transport and MIDI bindings are covered by `transport`, `midi_learn` and `midi_bind`.

## Pitfalls

- Unverified: its DLL names MIDI note and control-change handling and a DAW control type, and the app's preferences have a DAW controllers list; whether it acts on those assignments was not checked.
- Its window is Windows only; the Studio does not show it.

## Pairs well with

- A MIDI controller bound in the app's preferences.

## Successor

None: `transport`, `midi_learn` and `midi_bind` cover the job, but they are not a drop-in, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="DAWControl")`: its one parameter is `Bypass`.
- [ ] `midi_bindings_list`: lists what is bound in the app.
- [ ] `param_get(machine="DAWControl")`: `Bypass` reads no.
