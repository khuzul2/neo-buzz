## How it sounds

The host's example of a multi-input, multi-output effect: a clean input, a noise input that gets noise added, and a delay input with feedback. The first output is the sum of all three, the others carry the noise and the delay alone. With only a clean input connected it passes the sound unchanged.

## Use it for

- Learning how a multi-input machine works, or testing a host's multi-channel routing.
- A delay and a noise generator on separate inputs, with a mix out.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Delay time | 0.3 s | 100 ms to 2 s for the delay input |
| Delay feedback | 50% | 0 to 100; at 100 the repeats never fade |
| Noise amount | 50% | 0 to 100; scaled by the noise input's own level |

## Starting points

- Connect a source to the first input for a clean pass-through; the other two inputs only matter when you connect them with `connect(from="Src", to="Multi IO", input=1)`.
- Keep Delay feedback under 80% on a long delay.

## Pitfalls

- It is an example, not a studio effect: its delay buffer resizes each time Delay time is set, so changing it during play can click.
- A silent noise input stays silent: the noise is scaled by that input's own level.
- At 100% feedback the level keeps building.

## Pairs well with

- A synth on the clean input and a noise source on the second.

## Successor

None: no Neo machine replaces it, so `machine_replace` does not apply; keep it in songs that use it.

## Checklist

- [ ] `machine_describe(machine="Multi IO")`: it lists `Delay time`, `Delay feedback` and `Noise amount`.
- [ ] `analyze(source="Multi IO", metrics=["loudness"])`: with only the clean input the level equals the input.
- [ ] `param_get(machine="Multi IO")`: the defaults are 0.3 s, 50% and 50%.
