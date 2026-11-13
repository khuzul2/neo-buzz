## How it sounds

A plain gain stage with a bypass: it only changes the level, from silence through 0 dB at 80 up to +12 dB at 127. It does not colour the sound.

## Use it for

- A trim before or after another machine, a muteable level or an automated fade.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Gain | 0 dB | raw 80 is 0 dB; 127 is +12 dB; 0 is silence |
| Bypass | no | yes passes the signal unchanged |
| ATrackParam | 0 | a placeholder track parameter; it has no measured effect |

## Starting points

- A fade: `automation_write(machine="Gain", pattern="Fade", param="Gain", points=[{"at": 0, "value": "0 dB"}, {"at": 8, "value": "-24 dB"}])`.

## Pitfalls

- `Gain` is a table, not a straight line: about 0.3 dB a step near 0 dB and much coarser low down.
- +12 dB drives a normal signal past full scale (+2.8 dBFS in the profile).
- `ATrackParam` does nothing that was measured.

## Pairs well with

- Anywhere you need a trim; Neo Utility when you also need pan or width.

## Successor

Neo Utility replaces Jeskola Gain: `machine_replace(machine="Gain")` carries the gain in dB over (silence to +12 dB) and Bypass becomes the machine's bypass.

## Checklist

- [ ] `analyze(source="Gain", metrics=["loudness"])`: the level moved by the amount you set.
- [ ] `param_get(machine="Gain")`: `Gain` reads in dB.
- [ ] `param_set(machine="Gain", changes=[{"param": "Bypass", "value": "yes"}])` passes the signal; `render` agrees with the input.
