## How it sounds

A plate reverb in the classic two-tank design: a quick, bright, dense wash with no obvious early reflections. `Size` scales the internal delays, `Decay` sets the tail, `Damping` darkens it, and a slow modulation keeps the tail from ringing on one pitch. `LowCut` keeps bass out of the tank. `Feedback` sends the output back in for a much longer, building tail (capped inside, below where the loop would grow).

## Use it for

- Vocals and snares, a bright short space, a send reverb, and large washes at high `Decay` and `Feedback`.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mix | 100% | on a send; 20-35 % as an insert (the default is 50 %) |
| Decay | 60% | tail length; 75 % is the default |
| Size | 100% | 10-200 %; small is tight, large is slow to build |
| Damping | 50% | higher is darker |
| PreDelayMs | 20 ms | keeps the dry sound clear before the space |
| LowCut | 120 Hz | high-pass into the tank so the tail stays clear |
| Feedback | 0% | 0 to 95 %; for ringing, building tails |

## Starting points

- Presets: "Plate — Vocal Bright", "Plate — Snare Short", "Room — Small Dark", "Hall — Big Wash", "Send — Plate 100% Wet", "Ambient — Long Shimmer".
- A send: a bus with Mix 100 %, fed by `connect(from="Vocal", to="Plate", gain_db=-10)`.
- Load with `preset_load(machine="Plate", preset="Plate — Snare Short")`.

## Pitfalls

- `Mix` is 50 % at the defaults: on a send, set it to 100 %.
- The profiler types it a delay (it hears two early echoes); it is a reverb.
- `Feedback` high plus `Decay` high builds a very long tail; the machine stays bounded, but the tail can mask the mix.

## Pairs well with

- Pedal EQ after it to dull or thin the wash; Pedal Chorus before it for width; Pedal Gate on a snare before it.

## Successor

Neo Verb replaces Pedal Plate: `machine_replace(machine="Plate")` puts it in Neo Verb's Plate mode with Decay converted from a loop gain in percent to seconds (scaled by Size). The match is within 1.5 dB of loudness; wet-only third octaves can differ by up to 6 dB.

## Checklist

- [ ] `analyze(source="Verb", metrics=["spectrum"])`: the tail is as bright or dark as `Damping` says.
- [ ] `render(probes=["Plate"])` of a short hit: the tail decays to silence inside your `tail_seconds`.
- [ ] `param_get(machine="Plate")`: `Mix` is 100 on a send and `PreDelayMs` in ms.
