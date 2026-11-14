## How it sounds

A second Buzz resonant filter: low-pass, high-pass or band-pass with cutoff and resonance on 0 to 128 scales, and no bandwidth control. It behaves like Jeskola Filter and rings very loudly near the top of its resonance. Mono.

## Use it for

- Cutoff sweeps and tone shaping in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Type | 0 | 0 low-pass, 1 high-pass, 2 band-pass |
| Cutoff | 60 | 0 to 128; 128 is fully open |
| Resonance | 40 | 0 to 128; keep it below 118 |

## Starting points

- A resonant sweep: `automation_write(machine="Filter2", pattern="Sweep", param="Cutoff", points=[{"at": 0, "value": "20"}, {"at": 8, "value": "110"}])`.

## Pitfalls

- `Resonance` near the top self-oscillates (+52 dBFS peak in the profile): the safe range stops at 118.
- Bare 0 to 128 scales: no Hz.
- It is mono: NeoBuzz Live feeds it left + right summed, so a centred signal comes out about 6 dB louder.

## Pairs well with

- Jeskola Distortion before it; Jeskola Delay after it.

## Successor

Neo Filter replaces Jeskola Filter 2: `machine_replace(machine="Filter2")` makes a filter of the same type with its cutoff, Q and level fitted to the old response.

## Checklist

- [ ] `analyze(source="Filter2", metrics=["spectrum"])`: the corner moves with `Cutoff`.
- [ ] `param_get(machine="Filter2")`: `Resonance` is under 118.
- [ ] `render(probes=["Filter2"])`: no runaway.
