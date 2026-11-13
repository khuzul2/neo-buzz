## How it sounds

A stereo waveshaping distortion with five curves: Soft (a rounded tanh), Hard (a hard clip), Tube (lopsided, rich in even harmonics), Fold (a wavefolder that rings metallically as the drive passes 0 dB) and Crush (a coarse 4-bit quantiser, lo-fi). A tilt EQ comes before the shaper, a DC bias makes the clipping lopsided, and the dry/wet `Mix` sits after it. Two texture controls: `Hysteresis` smears the release like magnetic tape (rounder tails, fatter bass) and `Dust` adds signal-following noise that is distorted with the sound. There is no oversampling: high notes driven hard alias.

## Use it for

- Warming and thickening drums and bass (Soft, Tube), harsh leads (Hard, Fold), lo-fi textures (Crush), and tape-like smear.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Drive | 12 dB | -24 to +24 dB in 0.5 dB steps; 0 dB is the default |
| Shape | Soft | Soft, Hard, Tube, Fold or Crush |
| Tone | 0% | tilt around 1 kHz, -100% darker to +98% brighter; the default is flat |
| Bias | 0% | DC offset for even harmonics; try 20% with Tube |
| Mix | 40% | blends dry and shaped |
| Output | -6 dB | pulls the driven level back down |
| Hysteresis | 4 ms | the release smear; 0 is off |

## Starting points

- Warm drums: Shape Soft, Drive 9 dB, Mix 50 %, Output -4 dB.
- Valve-like bass: Shape Tube, Drive 12 dB, Bias 20 %, Tone -20 %.
- Lo-fi: Shape Crush, Drive 6 dB, Mix 60 %, Dust 30 %.

## Pitfalls

- `Output` at its maximum (+24 dB) is hot (+14.6 dBFS peak in the profile): drive and output stack, so lower `Output` as you raise `Drive`.
- No oversampling: Hard, Fold and Crush on high notes alias audibly.
- Its own value texts are bare numbers; the manifest gives dB and percent from the code.
- The profiler types it a utility (it measures clean at 0 dB drive); it is a distortion.

## Pairs well with

- Neo EQ after it to tame the highs; Pedal Comp after it to level the result; Pedal Filter for a filtered fuzz.

## Successor

No `machine_replace` pair. Neo Drive covers the same job (distortion with tone shaping and anti-aliasing) and Neo Tape the tape-like warmth; set them up by ear.

## Checklist

- [ ] `analyze(source="Drums", metrics=["spectrum"])`: harmonics grew where you expected and the top end is not harsh.
- [ ] `analyze(source="Drums", metrics=["peaks"])`: under 0 dBFS after `Output`.
- [ ] `param_get(machine="Shaper")`: `Drive` and `Output` read in dB.
