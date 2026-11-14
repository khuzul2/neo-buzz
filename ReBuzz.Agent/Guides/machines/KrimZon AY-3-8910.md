## How it sounds

A three-voice chip synth in the style of a classic sound chip: each voice plays a square-wave tone, noise or both at one of 16 volumes, with a hardware-style envelope, sub-note arpeggios, glide and vibrato. It is bright, thin and loud for its size, and plays 13 semitones above the written note.

## Use it for

- Chip-tune leads, arpeggiated chords and noise percussion.
- Retro game music over three tracks.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Final amp | 1x | the overall level, 0 to 2 |
| Tune | 1x | 0.5 to 2; 1 is in tune |
| Voice content | Tone | Tone, Noise or both |
| Volume | 15 | the chip's 0 to 15 steps |
| Sub-notes | 3 | arpeggio length from one written note |
| Envelope shape | 0: \____ | a single falling envelope |

## Starting points

- A chip lead: Voice content Tone, Volume 15, Vibrato a little.
- A hi-hat: Voice content Noise, Envelope shape 0: \____ and a short Envelope time.
- Write a chord over all three tracks with `notes_write(machine="AY", pattern="A", notes=[{"pitch": "C3", "start": 0, "length": 1}])`.

## Pitfalls

- Written notes sound 13 semitones high: write an octave and a semitone lower, or lower Tune.
- None in Voice content is silent.
- Unverified: how Sub-note length and Envelope time map to ticks or seconds.

## Pairs well with

- Jeskola Distortion for crunch, a short delay for width.

## Successor

None: no Neo machine imitates a sound chip, so `machine_replace` does not apply.

## Checklist

- [ ] `machine_describe(machine="AY")`: `Final amp` and `Tune`, then the track parameters.
- [ ] `machine_readings(machine="AY")`: a note reads near -21.5 LUFS.
- [ ] `param_get(machine="AY", track=0)`: Volume reads 15.
