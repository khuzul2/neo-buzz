## How it sounds

A tracker-style sample player in the manner of a classic module tracker: it plays waves from the wavetable per track, with a volume decay, a sample offset for slicing breaks, a whole-semitone tuning and two effect columns per note. With no wave loaded it is silent.

## Use it for

- Sample-based parts, breaks and one-shots played from patterns.
- Percussion slicing with Sample offset.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Wave | 1 | the wavetable slot a track plays (1 to 200) |
| Volume | 128 | 0 to 254, on a squared curve |
| Volume decay | 0 | 0 is none; higher shortens sustained samples |
| Sample offset | 0 | where in the sample playback starts |
| Offset quantize | perc | free curve or 2 to 64 slices |
| Tuning | 0 st | whole semitones, about -5 to +5 |

## Starting points

- Load a wave: `wave_import(path="/path/break.wav", slot=1)` and write notes with Wave 1.
- Plucks from a sustained wave: Volume decay 80.
- `notes_write(machine="Matilde", pattern="A", notes=[{"pitch": "C4", "start": 0, "length": 1}])`.

## Pitfalls

- Volume is squared: raw 128 is a quarter of full level, so use higher values.
- Silent without a wave in the track's slot.
- Its effect columns (arpeggio, slides, retriggers) are not in the parameter list: unverified whether `notes_write` reaches them.

## Pairs well with

- Neo Comp or Neo Transient on breaks; Neo Sampler for the same samples with modern controls.

## Successor

None: Neo Sampler plays waves too, but its controls and effect columns differ, so `machine_replace` does not apply to Matilde Tracker2.

## Checklist

- [ ] `machine_describe(machine="Matilde")`: four global and two track parameters.
- [ ] `wave_list`: the wave is loaded.
- [ ] `machine_readings(machine="Matilde")`: a note reads a level when the wave is in place.
