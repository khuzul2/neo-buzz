## How it sounds

A resonant filter with 17 filter types and an LFO with 17 shapes that sweeps the cutoff. Its settings are on bare 0 to 240 scales. Close to its sibling FSM Philta; its own code was not available here, so its scales are unverified.

## Use it for

- Filter sweeps and rhythmic filter movement.
- Vowel-like tones with the mixed filter types.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 100 | 0 to 240; low is dark |
| Resonance | 80 | 0 to 240; its effect varies by filter |
| Filter type | 6L Multipeak | 17 filters; the first is a six-pole low-pass |
| LFO rate | 1 Hz | about 0.05 to 39 Hz, the top steps locking to the tempo |
| LFO depth | 60 | 0 to 240 cutoff steps |
| LFO shape | sine | 17 waves |

## Starting points

- A slow sweep: `param_set(machine="Philthy", changes=[{"param": "Filter type", "value": "6L Multipeak"}, {"param": "LFO rate", "value": "0.5 Hz"}, {"param": "LFO depth", "value": "120"}])`.

## Pitfalls

- Unverified: its cutoff and phase scales (they were not measured in Hz here); use your ears and `analyze(metrics=["spectrum"])`.
- It is mono: NeoBuzz Live feeds it left + right summed; it sounds 7.4 dB quieter at the defaults.
- It adds 10 samples of latency.
- All its controls are track parameters of its single track.

## Pairs well with

- Jeskola Gain after it to bring the level back.

## Successor

No Neo successor rule: Neo Filter covers the same ground but nothing maps it, so build the sweep there by hand.

## Checklist

- [ ] `analyze(source="Philthy", metrics=["loudness"])`: about 7 dB below the input at the defaults.
- [ ] `param_get(machine="Philthy")`: `Filter type` and `Cutoff` agree with what you set.
- [ ] `render(probes=["Philthy"])`: the sweep moves without clicks.
