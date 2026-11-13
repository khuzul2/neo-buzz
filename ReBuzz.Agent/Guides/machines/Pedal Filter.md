## How it sounds

A stereo multimode filter: low pass, high pass, band pass, band reject (notch) or comb, at 12 or 24 dB per octave. A tanh saturator (`Drive`, 1x to 10x) sits before the filter. An LFO, free-running or locked to the song's tempo, sweeps the cutoff up to 4 octaves, and `Phase Offset` runs the left and right LFOs apart for movement across the stereo field. In comb mode `Cutoff` is the comb's pitch and `Resonance` its feedback; `Slope` then picks flanging (FIR) or ringing (IIR).

## Use it for

- Sweeps, auto-wah, filtered builds, telephone and radio tones, comb resonance, and a drive-into-filter edge on drums or bass.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Mode | Low Pass | the default; High Pass, Band Pass, Band Reject, Comb |
| Cutoff | 2 kHz | 20 Hz to 18 kHz; this is the default and cuts the highs |
| Resonance | 30% | 0 wide to 100 near self-oscillation |
| Slope | 24 dB / IIR | steeper; the first choice is 12 dB / FIR |
| LFO Depth | 1 oct | 0 to 4 octaves around the cutoff |
| LFO Rate | 0.5 Hz | or `Tempo Sync` On with `LFO Div` 1/4 |
| Mix | 100% | below 100% keeps the dry body |

## Starting points

- Presets: "Low Pass — Warm 24 dB", "Low Pass — Slow Sweep", "High Pass — Thin Out", "Band Pass — Radio", "Auto-Wah — Sixteenths", "Comb — Metallic Flange".
- An auto-wah: Band Pass, Cutoff 1 kHz, Resonance 60 %, LFO Depth 2 oct, Tempo Sync On, LFO Div 1/8.
- Load with `preset_load(machine="Filter", preset="Auto-Wah — Sixteenths")`.

## Pitfalls

- At the defaults it is a 2 kHz low pass: it cuts the highs (about 9.5 dB on noise). Set `Cutoff` before judging.
- High `Resonance` and `Drive` together peak hot (about +16 dBFS with every parameter at its maximum): lower the track level.
- Above about 2 kHz at 24 dB the response closes toward the Nyquist limit; Neo Filter keeps the analog slope there.

## Pairs well with

- Neo Verb or Pedal Plate after it; Pedal Comp before it to even out a sweep; Pedal Drum Matrix for harsher effects.

## Successor

Neo Filter replaces Pedal Filter: `machine_replace(machine="Filter")` carries mode, slope, cutoff, resonance, drive and the LFO over, with Pedal's level compensation. The match is within 1 dB of loudness and 3 dB per third octave up to 12 kHz; very high cutoffs at 24 dB or a notch near 8 kHz can differ by up to 5 dB.

## Checklist

- [ ] `analyze(source="Synth", metrics=["spectrum"])`: the cutoff sits where you set it and the sweep moves it.
- [ ] `param_get(machine="Filter")`: `Cutoff` in Hz, `LFO Depth` in octaves.
- [ ] `analyze(source="Synth", metrics=["peaks"])`: no clipping at high `Resonance` or `Drive`.
