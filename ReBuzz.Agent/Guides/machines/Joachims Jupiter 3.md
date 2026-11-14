## How it sounds

A three-band compressor and limiter for the master: a phase-correct split into low, mid and high, a threshold, attack, release, output and tube-like saturation for each band, one shared ratio, a narrower low band and a limiter with four characters.

## Use it for

- Mastering and bus compression.
- Tightening bass separately from the top.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Ratio | 4:1 | 1:1 to 30:1 |
| Low threshold | -18 dB | -100 to 0 dB; 0 does nothing |
| Mid threshold | -18 dB | -100 to 0 dB |
| High threshold | -18 dB | -100 to 0 dB |
| Low stereo width | 70% | 0 to 200 percent |
| Limiter mode | Smooth | off or four characters |

## Starting points

- A gentle master: `param_set(machine="Jupiter", changes=[{"param": "Ratio", "value": "3:1"}, {"param": "Low threshold", "value": "-12 dB"}, {"param": "Mid threshold", "value": "-12 dB"}, {"param": "High threshold", "value": "-12 dB"}])`.
- Set one band at a time with `Listen to` on LOW, MID or HIGH.

## Pitfalls

- It delays the sound by its FFT size, 32,768 samples (about 0.74 s) by default: the profile's short window heard silence, so its level at the defaults is unverified.
- At the defaults the thresholds are 0 dB, so nothing is compressed until you lower them.
- Put it last before the master output; it is not for low-latency work.
- The mid and high Output and Beefyness texts say low in the machine (copy mistakes).

## Pairs well with

- Joachims Hyperion before it.

## Successor

Neo Comp is the modern dynamics machine, but nothing maps the three-band compressor onto it: `machine_replace` has no rule for it, so set it up by hand.

## Checklist

- [ ] `analyze(source="Jupiter", metrics=["loudness"])`: after the 0.74 s delay the level is near the input's.
- [ ] `param_get(machine="Jupiter")`: `Ratio` and the thresholds are what you set.
- [ ] `render(probes=["Jupiter"])`: the limiter holds peaks below full scale.
