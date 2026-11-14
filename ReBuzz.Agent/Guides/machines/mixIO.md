## How it sounds

A multi-input mixer: every track has a gain from silence to +12 dB, a stereo width, mute and solo, and gliding times for each. With one clean input at its defaults it is a pass-through (the profile: 0 dB, no latency); at +12 dB it overloads.

## Use it for

- Balancing several inputs of a multi-channel setup.
- Muting and soloing inputs with a smooth fade.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Gain | -0.18 dB | about 0 dB at the default; +12 dB is the top |
| Stereo width | 100% | mono at 0, unchanged in the middle |
| Gain glide | 1 ticks | how long a gain change takes |
| Mute | OFF | silences the input |
| Solo | OFF | plays only the soloed inputs |

## Starting points

- Keep Gain at its default and balance by ear; Mute an input with Mute ON.
- Smooth fades: Gain glide 4 ticks.

## Pitfalls

- Gain at +12 dB peaks above full scale: keep it lower.
- Unverified: how its 32 tracks map onto several connected inputs, and what the Pan glide smooths (the profile found no pan parameter).
- Several tracks mean several inputs only in a multi-input setup; with one input only track 0 matters.

## Pairs well with

- Neo Imager for width and Neo Utility for gain on a single stream.

## Successor

Neo Utility covers a single stream's gain and width: `machine_replace(machine="mixIO")` does not apply to a multi-input mixer, so use it only on a one-input mixIO.

## Checklist

- [ ] `machine_describe(machine="mixIO")`: eight track parameters, `Gain` first.
- [ ] `param_get(machine="mixIO", track=0)`: Gain reads about 0 dB.
- [ ] `analyze(source="mixIO", metrics=["loudness"])`: at the defaults the level equals the input.
