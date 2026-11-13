## How it sounds

A classic Buzz resonant filter: low-pass, high-pass or band-pass with cutoff and resonance on 0 to 128 scales. Resonance adds a peak at the cutoff and, near the top, rings very loudly. Mono.

## Use it for

- Cutoff sweeps and tone shaping in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Type | lowpass | lowpass, highpass or bandpass |
| Cutoff | 60 | 0 to 128; 128 is fully open for the low-pass |
| Resonance | 40 | 0 to 128; keep it below 118 |
| Bandwidth | 0 | 0 to 128; no change was measured at the defaults |

## Starting points

- Presets: "Low Pass — Dark", "Low Pass — Resonant", "High Pass — Thin", "Band Pass — Narrow"; load with `preset_load(machine="Filter", preset="Low Pass — Dark")`.
- A sweep: `automation_write(machine="Filter", pattern="Sweep", param="Cutoff", points=[{"at": 0, "value": "20"}, {"at": 8, "value": "110"}])`.

## Pitfalls

- `Resonance` at its top self-oscillates at about +54 dBFS: the safe range stops at 118.
- All three controls are bare 0 to 128 numbers: no Hz.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Distortion before it; Jeskola Delay after it.

## Successor

Neo Filter replaces Jeskola Filter: `machine_replace(machine="Filter")` makes a filter of the same type with its cutoff, Q and level fitted to the old response at each Cutoff and Resonance.

## Checklist

- [ ] `analyze(source="Filter", metrics=["spectrum"])`: the corner moves with `Cutoff`.
- [ ] `param_get(machine="Filter")`: `Resonance` is under 118.
- [ ] `render(probes=["Filter"])`: no runaway at a high `Resonance`.
