## How it sounds

A stereo gain control and a mute, nothing else. `Gain` is a percentage of the input: 100 % is unity, 200 % is +6 dB, 50 % is -6 dB, 0 % is silence. `Mute` fades the output over `Inertia` instead of cutting, so a mute never clicks. It does not colour the sound.

## Use it for

- A trim before or after another machine, a muteable send, and a level you automate.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Gain | 100% | unity; 50% is -6 dB, 200% is +6 dB |
| Mute | Off | switch on to fade out |
| Inertia | 20 ms | 0-500 ms; 0 is an instant cut |

## Starting points

- A -6 dB trim: Gain 50 %.
- A muteable send: Inertia 100 ms, then automate `Mute`.
- A level automation: `automation_write(machine="Gain", pattern="Fade", param="Gain", points=[{"at": 0, "value": "100%"}, {"at": 8, "value": "0%"}])`.

## Pitfalls

- Above 100 % a loud input can overload what follows: there is no limiter.
- It has no pan, width or polarity: Neo Utility has them.
- `Mute` fades over `Inertia`: set it short on drums.

## Pairs well with

- Pedal Gain Multi for a mixer; Neo Utility for pan, width and polarity.

## Successor

Neo Utility replaces Pedal Gain: `machine_replace(machine="Gain")` turns percent into decibels (100 % is 0 dB, 200 % is +6 dB, 0 % is silence) and Mute into silence. The match is within 0.1 dB.

## Checklist

- [ ] `analyze(source="Gain", metrics=["loudness"])`: the level moved by the amount you set.
- [ ] `param_get(machine="Gain")`: `Gain` reads in %, `Inertia` in ms.
- [ ] `param_set(machine="Gain", changes=[{"param": "Mute", "value": "On"}])` fades out over `Inertia`; `render` shows no click.
