## How it sounds

A Buzz bass synth, one monophonic voice per track: sine, saw, pulse, triangle or noise into a resonant filter with an envelope. All controls are per track, on 0 to 100 percent.

## Use it for

- Basses and acid-style lines in old-school Buzz songs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Cutoff | 60% | 0 to 100 %; a closed filter is much quieter |
| Resonance | 30% | 0 to 100 %; a peak near 8 kHz |
| Envmod | 40% | how far the envelope opens the filter |
| Decay | 50% | how fast it closes again |
| Waveform | sawtooth | sine, sawtooth, pulse, triangle or noise |

## Starting points

- A bass: `param_set(machine="Bass2", changes=[{"param": "Waveform", "value": "sawtooth"}, {"param": "Cutoff", "value": "60%"}, {"param": "Envmod", "value": "40%"}])`.

## Pitfalls

- Cutoff swings the level by about 50 dB.
- Notes play as written; each track is a separate voice.
- Velocity does not change its level.

## Pairs well with

- Jeskola Distortion for grit; Jeskola Delay for echoes.

## Successor

Neo Poly replaces Jeskola Bass 2: `machine_replace(machine="Bass2")` plays its line as a mono legato bass with slides as glides and volume as velocity; cutoff, resonance and waveform carry over.

## Checklist

- [ ] `analyze(source="Bass2", metrics=["loudness"])`: a note sits near -10 LUFS.
- [ ] `param_get(machine="Bass2")`: `Cutoff` and `Resonance` read in %.
- [ ] `render(probes=["Bass2"])`: the filter is not closed by mistake.
