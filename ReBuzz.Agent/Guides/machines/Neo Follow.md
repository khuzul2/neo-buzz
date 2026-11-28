## How it sounds

A control machine that listens to audio and moves up to 8 parameters of other machines with what it hears. It never changes the audio: the signal passes through bit-identical in every mode, so it has no Mix or Amount. Put it on a track to follow that track, or switch `Listen to` to `Sidechain` and wire another machine into its second input to let one track move another.

- `Mode` `Envelope` follows the level: `Detector` `Peak` answers to every spike, `RMS` follows loudness. `Attack` (`0.1` to `500 ms`) and `Release` (`1 ms` to `5 s`) are the time to cover 63 % of a step. `Floor` and `Ceiling` (`-80` to `0 dB`) turn the level into 0 to 100 %, and `Curve` bends it.
- `Mode` `Pitch` follows the note it hears: `Low note` and `High note` become 0 and 100 %, `Pitch range` sets the lowest and highest pitch it looks for, `Confidence` holds the last value when the sound is unclear or silent, and `Pitch glide` slides between notes.
- `Mode` `Onset` fires a pulse on each hit: `Sensitivity` sets how easily it fires, `Hold` keeps the pulse at full, `Decay` lets it fall, and `Dynamic` scales the pulse with the hit's strength.
- Shared: `Smooth`, `Invert`, `Depth`, signed `Bias`, `Range` (`Unipolar` or `Bipolar`) and `Write glide`, the least time a target takes to cross its whole range.
- Delay: Envelope has none of its own; Pitch trails the audio by about 20 to 40 ms (longer for low notes); Onset by about 10 ms. Leave it where a filter or volume moves, not where tight timing matters.

## Use it for

- Auto-wah (drums open a filter), ducking (a kick on the sidechain lowers a pad's volume), a filter that follows a melody, drive that rises with playing level, a filter flash on every hit, or a gate-like chop of a pad on a rhythm.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Listen to | Input | Input is the audio passing through; Sidechain is the second input |
| Mode | Envelope | Envelope, Pitch or Onset |
| Attack | 10 ms | Time to cover 63 % of a rise |
| Release | 200 ms | Time to cover 63 % of a fall |
| Ceiling | -6 dB | The level that gives 100 % |
| Sensitivity | 50 % | Higher fires on softer hits |
| Decay | 200 ms | How long a hit's pulse takes to fall |
| Write glide | 30 ms | Keeps target moves smooth |

## Starting points

- Auto-wah: load preset `Envelope — Auto Wah` on the drums' track, then `modulate(source="Follow", target_machine="Filter", param=<cutoff>, depth=70, polarity="uni")`.
- Ducking: load `Envelope — Pump`, wire the kick into the second input, and `modulate(source="Follow", target_machine="Pad", param=<volume>, depth=-60, polarity="uni")`.
- Melody filter: load `Pitch — Filter Follows the Melody` on a lead and target a filter cutoff.
- Hit flash: load `Onset — Hit Flash` on drums and target a filter cutoff with `depth=100`.

## Pitfalls

- The audio is never processed here: to hear anything, target a parameter of another machine.
- Pitch needs one clear note at a time; chords, noise and drums give an unclear sound, so the output holds the last value.
- A negative `depth` ducks (the control rises, the target falls); the target's base value is where it rests.
- `Sensitivity` `100 %` can fire on the beating of a sustained pad: lower it for pads.
- Two modulators on the same parameter fight; tempo parameters and the master volume are refused.

## Pairs well with

- Neo Drums, Neo Poly and Neo Filter as sources and targets; Neo LFO and Neo Random for movement that does not depend on the audio.

## Checklist

- [ ] `modulation_list(machine="Follow")` shows each target `active` with the expected range and polarity.
- [ ] `render(probes=["Pad"])` confirms audible, click-free movement that follows the source.
- [ ] Removing the target (`modulation_remove(source="Follow", all=true)`) restores the parameter's base value.
