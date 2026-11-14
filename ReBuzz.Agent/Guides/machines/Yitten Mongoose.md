## How it sounds

A control machine with a window that works on several patterns together and a Bar length from 1 to 128. It makes no sound. Its own window cannot load in this host, so what it does was not measured.

## Use it for

- Songs that already use it.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Bar length | 16 | 1 to 128; what it measures here was not found |

## Starting points

- Use it as the song already does: `machine_describe(machine="Mongoose")` lists what is on it.

## Pitfalls

- Its window subscribes to sequence events that this host changed, so it cannot load here (a known break); the parameter is still usable.
- Unverified: what Bar length does, and what the multi-pattern view offers.

## Pairs well with

- Nothing; Jeskola Live has the same Bar parameter and the same open question.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Mongoose")`: its one parameter is `Bar length`.
- [ ] `param_get(machine="Mongoose")`: Bar length reads 16.
- [ ] `pattern_list(machine="Mongoose")`: any patterns the song holds for it.
