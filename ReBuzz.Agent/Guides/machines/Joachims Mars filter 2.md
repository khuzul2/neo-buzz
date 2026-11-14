## How it sounds

A distortion with 13 characters, from soft and round (Red crust, Deimos) through tube simulations (Cratered, God of iron, Scarred) to ring-mod and FM mangling (Battle axe, Crunch, Shatter), with a pre-gain and treble and bass attenuators.

## Use it for

- Crunch, fuzz and heavy distortion.
- Warm saturation.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Distortion character | Red crust | 13 characters; 0 is bypass |
| Input gain | 0 dB | -100 to +15.5 dB |
| Amount | 50 | 0 to 127 |
| Treble attenuator | -20 | 0 is off, lower attenuates |
| Bass attenuator | 0 | 0 is off, lower removes bass from the distortion |

## Starting points

- Presets: "Crunch — Red Crust", "Fuzz — God of Iron", "Warm — Rusty", "Heavy — Cannon"; load with `preset_load(machine="Mars", preset="Crunch — Red Crust")`.

## Pitfalls

- `Input gain` at its top (+15.5 dB) or `Amount` at its top peaks above 0 dBFS: +2.5 and +0.8 dBFS in the profile.
- Amount means something different per character; in Steel pain it does the same as Input gain.
- In Cannon the bass control keeps bass out of the distortion; Crunch adds a slight delay.
- Character 0 bypasses it, which is good for comparing.

## Pairs well with

- Jeskola Filter after it to tame the top.

## Successor

Neo Drive covers distortion, but nothing maps the 13 characters onto it: `machine_replace` has no rule for it, so set it up by hand.

## Checklist

- [ ] `analyze(source="Mars", metrics=["loudness"])`: the output is not above full scale.
- [ ] `param_get(machine="Mars")`: `Distortion character` is what you set.
- [ ] `render(probes=["Mars"])`: the distorted tail ends in silence.
