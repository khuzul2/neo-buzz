## How it sounds

A Buzz stereo delay with a left and a right line whose echoes feed each other, so different lengths bounce between the speakers. Each track is its own pair of lines, up to 8. Mono in, stereo out.

## Use it for

- Ping-pong echoes, wide dotted-rhythm delays and a Haas-style widener.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| L. Length | 2 | left line, in the length unit (ticks by default) |
| R. Length | 4 | right line; unequal lengths ping-pong |
| Length unit | tick | tick follows the tempo; ms and sample are fixed |
| Feedback | 40% | -100 to +100 %; crosses between the lines |
| Wet out | 48% | echo level, 0 to 100 % |
| Dry thru | On | keeps the original sound |

## Starting points

- Presets: "Ping-Pong — Eighths", "Wide — Dotted", "Haas — Stereo Widen", "Send — Cross Echo"; load with `preset_load(machine="Cross", preset="Ping-Pong — Eighths")`.
- Dotted bounce: `L. Length` 3, `R. Length` 4, `Feedback` 40 %.

## Pitfalls

- `Feedback` near -100 % never decays: `param_set` keeps it inside the safe range.
- Each track adds its own pair of lines: raise tracks only as you need them.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Reverb after it for depth; Pedal EQ to cut the lows from the echoes.

## Successor

Neo Delay replaces Jeskola Cross Delay: `machine_replace(machine="Cross")` uses Neo Delay's ping-pong mode with the lengths as Sync and Feel (or a free Time) and Feedback carried over. The match depends on the length unit; the reply says what differs.

## Checklist

- [ ] `analyze(source="Cross", metrics=["loudness"])`: the echoes sit where `Wet out` puts them.
- [ ] `param_get(machine="Cross")`: `Length unit` is what you meant.
- [ ] `render(probes=["Cross"])`: the echoes decay to silence inside `tail_seconds`.
