## How it sounds

A thick, loud subtractive synth: up to 16 unison oscillators with their own pitch LFOs and chord shapes, a dual filter with 39 modes, pre and post distortion, two per-track LFOs and exponential envelopes. It is monophonic and sounds an octave below the written note.

## Use it for

- Fat unison leads and pads, detuned stacks and chord-shaped basses.
- Filter sweeps and wobbles with the filter envelope and the LFOs.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Oscillator count | 12 | 1 to 16; more voices cost CPU |
| Chord shape | Phat | the pitch layout of the oscillators |
| Cutoff | 25% | filter cutoff |
| Resonance | 4% | keep low to stay safe |
| Filter envelope amount | 26% | how far the filter envelope opens the cutoff |
| Filter mode | LP | low pass; 39 modes in all |
| Amp sustain | 50% | the held level |

## Starting points

- A supersaw lead: Oscillator count 12, Chord shape Straight, Unison depth about 20%, Cutoff 60%.
- A pad: Amp attack and Amp release long, Chord shape Rich.
- Write notes an octave up: `notes_write(machine="Green Milk", pattern="A", notes=[{"pitch": "C4", "start": 0, "length": 1}])`.

## Pitfalls

- Loud at its defaults: one note measures about -9 LUFS with a peak near -2 dBFS; lower the master first.
- It plays 12 semitones low (profile): write notes an octave higher.
- Many oscillators with distortion cost CPU; lower Oscillator count if the song stutters.
- Unverified: the time parameters above raw 128, where the machine gives no text, and the LFO speeds and delays, which are plain raw numbers here.

## Pairs well with

- Neo Verb and Neo Delay on its tail; a compressor to tame the peaks.

## Successor

None: Neo Poly is a different synth, so `machine_replace` does not apply to Green Milk.

## Checklist

- [ ] `machine_describe(machine="Green Milk")`: 38 parameters with units.
- [ ] `machine_readings(machine="Green Milk")`: a note reads near -9 LUFS.
- [ ] `analyze(source="Green Milk", metrics=["loudness"])`: the output stays under full scale.
