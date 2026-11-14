## How it sounds

A control machine that draws envelopes for parameters over patterns, in its own window, and plays them into the machines they target. It makes no sound. Its one parameter decides whether a new envelope point starts from the target's current value or its default.

## Use it for

- Songs that already use it for drawn parameter envelopes.
- Reading how a song's automation was built.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| New point start | Current | Current keeps what you hear when a point is added; Default resets to the parameter's default |

## Starting points

- Use it as the song already does: `machine_describe(machine="EnvelopeBlock")` lists what is on it.
- For new automation, `automation_write` writes a movement into a pattern of the machine whose parameter it moves.

## Pitfalls

- The envelopes are in its window and the song's saved data; param_set cannot draw them.
- Unverified: how an existing EnvelopeBlock song plays in the Studio.

## Pairs well with

- The machines whose parameters it moves.

## Successor

None: `automation_write` and `modulate` do the same work, but they do not read an EnvelopeBlock's envelopes, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="EnvelopeBlock")`: its one parameter is `New point start`.
- [ ] `param_get(machine="EnvelopeBlock")`: it reads Current.
- [ ] `automation_read(machine="Bass")`: shows the movements already written into a pattern.
