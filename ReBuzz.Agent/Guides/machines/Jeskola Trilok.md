## How it sounds

A small synthesised bass drum: a trigger fires a short low thump that sweeps down in pitch, with its tone and decay. Simple and punchy; one hit at a time.

## Use it for

- Simple kicks in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| BD Trigger | On | an event: write it to play the drum |
| BD Tone | 64 | 0 to 127; higher is brighter |
| BD Decay | 64 | 0 to 127; about 17 ms to 1.3 s (150 ms at 64) |
| BD Volume | 128 | no effect: the machine never reads it |

## Starting points

- A kick hit: `automation_write(machine="Kick", pattern="Hits", param="BD Trigger", points=[{"at": 0, "value": "On"}])`.

## Pitfalls

- `BD Trigger` is an event, not held: it plays one hit per write.
- `BD Volume` has no effect, so level it with the machine's output.
- `BD Tone` at its top is hot (+2.5 dBFS peak).

## Pairs well with

- Jeskola Distortion for grit; Jeskola Filter to round it.

## Checklist

- [ ] `analyze(source="Kick", metrics=["loudness"])`: a hit is near -12 LUFS.
- [ ] `param_get(machine="Kick")`: `BD Decay` is what you set.
- [ ] `render(probes=["Kick"])`: the hit ends in silence.
