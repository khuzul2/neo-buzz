## How it sounds

A Buzz waveshaper effect, per track: an input gain of plus or minus 24 dB into one of four shapes. It is louder than its input, rings for a while and added noise or offset to silence in the profile. What each shape does is unverified.

## Use it for

- Distortion and waveshaping where a classic Buzz sound is wanted.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| gain | 0 dB | input gain, -24 to +24 dB |
| shape | 1 | 1 to 4; the shapes were not told apart |

## Starting points

- Try the four: `param_set(machine="Shaper", changes=[{"param": "shape", "value": "2"}])`, then `analyze` each.

## Pitfalls

- It made sound from silence (-52 dBFS peak: noise, hum or DC): check it with no input.
- About +13 dB louder than its input at the defaults.
- Both controls are track parameters, not global ones.

## Pairs well with

- Jeskola Filter after it to tame the top.

## Checklist

- [ ] `analyze(source="Shaper", metrics=["loudness"])`: the output is not above full scale.
- [ ] `param_get(machine="Shaper")`: `gain` reads in dB.
- [ ] `render(probes=["Shaper"])` with no input: it stays silent, or note the offset.
