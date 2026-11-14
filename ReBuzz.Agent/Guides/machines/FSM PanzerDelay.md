## How it sounds

A stereo multi-tap delay: each track is a tap with a left and right line, its own level, pans, feedback and stereo spread, plus a filter on the echoes. Lengths count in ticks by default, so the echoes follow the tempo.

## Use it for

- Dub echoes and wide, filtered delays.
- Ping-pong repeats.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Delay time unit | Ticks | ticks follow the tempo |
| Left delay length | 3 | in the unit; 3 ticks is a dotted eighth |
| Right delay length | 3 | in the unit |
| Echo level | -8 dB | -24 to 0 dB |
| Feedback | -8 dB | -24 to -0.1 dB; stay below -3 dB |
| Echo filter type | LP | none, low-pass, high-pass or band-pass |

## Starting points

- Presets: "Echo — Dark Eighths", "Ping-Pong — Dotted", "Tape — Warm Long", "Slapback — Wide", "Send — Echo Only"; load with `preset_load(machine="PanzerDelay", preset="Ping-Pong — Dotted")`.
- A send: `Dry level` -24 dB and a wet `Echo level` 0 dB.

## Pitfalls

- `Feedback` at its top (-0.1 dB) piles up: +10.5 dBFS after 30 s in the profile.
- The echo filter's coefficients assume 44.1 kHz, so its cutoff moves with the sample rate.
- Dry left and right pan default hard left and hard right: move both to the middle for a mono dry sound.
- Each track is a tap, four at most.

## Pairs well with

- Jeskola Reverb after it.

## Successor

Neo Delay replaces FSM PanzerDelay: `machine_replace(machine="PanzerDelay")` makes a Neo Delay in Stereo mode with FB PingPong as Cross, the first track's lengths as Sync and Feel, Feedback in dB and the echo filter as its low or high cut. Extra taps and the pans have no counterpart.

## Checklist

- [ ] `analyze(source="PanzerDelay", metrics=["loudness"])`: the output is where you expect with the dry level at 0 dB.
- [ ] `param_get(machine="PanzerDelay")`: `Delay time unit` and the lengths agree.
- [ ] `render(probes=["PanzerDelay"])`: the repeats decay to silence.
