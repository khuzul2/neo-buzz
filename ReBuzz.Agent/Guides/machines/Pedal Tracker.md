## How it sounds

A sample player in the manner of a classic tracker. Up to 16 tracks each play a wave from the song's wavetable and carry a volume and two effect columns (a command and an argument each): pitch slides, tone portamento, vibrato, sample offset, volume slides, retriggers, note cuts and delays. Playback reads the sample with a choice of interpolation up to a 32-point windowed sinc, with all bit depths and sample rates the wavetable can hold. It is silent until a slot holds a wave.

## Use it for

- Chip-style and tracker-style music, chopped breaks, one-shot drums played from patterns, and any sample that needs pitch slides or vibrato from the pattern.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Wave | 1 | the wavetable slot the track plays, 1-200 (track parameter) |
| Volume | 0 dB | track volume, square-law: raw 128 is unity, 254 is +11.9 dB |
| Cmd | 4 | first effect command: 1 slide up, 2 slide down, 3 portamento, 4 vibrato, 9 sample offset |
| Arg | 55 | the command's argument: for vibrato, speed and depth in two digits |
| Master | 100% | the whole machine's level; up to 200% |
| Interpolation | Sinc 16 | None, Linear, Cubic (the default), B-Spline, Sinc 16, Sinc 32 |
| Mute Inertia | 50 ms | fade of every mute |

## Starting points

- Load a wave: `wave_import(path="C:/Samples/kick.wav", slot=1, name="Kick")`, then write notes with `notes_write(machine="Tracker", pattern="A", notes=[{"pitch": "C3", "start": 0, "length": 0.5}])`.
- Vibrato on a lead: Cmd 4 and Arg 36 (speed 3, depth 6).
- Mute a track without losing its place: set `Mute Track 3` to On; it fades over `Mute Inertia` while its voice keeps running.

## Pitfalls

- No wave, no sound: a track plays only a slot that holds a wave.
- `Volume` is a square law: halving the number does not halve the level.
- Higher `Interpolation` costs more CPU with many tracks.
- The Cmd and Arg columns are raw numbers; their values are not shown in units.

## Pairs well with

- Pedal Filter or Pedal Comp after it; Pedal Gain Multi to mix several trackers.

## Successor

No `machine_replace` pair: a tracker chooses a wave per note and plays effects per row, which Neo Sampler's fixed key zones cannot hold. Neo Sampler (zones, loops, layers) is the replacement for new work.

## Checklist

- [ ] `wave_import` returns the slot and the wave length; `machine_describe(machine="Tracker")` lists the tracks.
- [ ] `render(probes=["Tracker"])` of a short pattern: it sounds; the peak is under 0 dBFS.
- [ ] `notes_read(machine="Tracker", pattern="A")` gives back the pitches you wrote.
