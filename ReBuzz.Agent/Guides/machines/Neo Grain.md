## How it sounds

A granular synth. Every note is a cloud of grains: tiny slices of a source, each 5 to 500 ms long, started 1 to 200 times a second, each read from Position in the source plus a random Spray, faded in and out by a window (Hann, Tukey or Gauss). The note transposes the cloud (C3 plays the source at its own pitch); Pitch adds semitones, Pitch spray detunes each grain, Reverse chance plays a share of them backwards, Pan spread scatters them left and right. Scan speed moves the position through the source after the note starts: 100 % is the source's own speed, 0 holds one moment still (a frozen pad), negative runs backwards. An ADSR shapes each note; Drone plays one cloud at C3 with no notes while the song runs. 16 voices and at most 128 grains sounding in all; a stolen voice fades in 5 ms. A seed makes the randomness repeat exactly.

The source is built in (Chord, Bells, Noise, made by code) or a wave of the song: set Source to Song wave and Wave to the slot (1 to 200); an empty slot is silent. Align moves each grain's start within 20 ms to where the sound already playing continues best, so a tonal source stays one clean tone at the note's pitch; with Align at 0 the grain rate adds a comb of sidebands and a tone turns buzzy.

It is a normal instrument: a generator that plays by notes (keyboard, MIDI, a note wire) with no audio input. The audio of an audio clip and the live input as sources are planned for later.

## Use it for

- Frozen pads and drones from any sound, glittering clouds, slow evolving textures, stutters, reversed washes and swarms.

## Key controls

| Parameter | Value | Why |
|---|---|---|
| Source | Chord | Chord, Bells, Noise, or Song wave with Wave naming the slot |
| Grain size | 100 ms | 5 ms is grainy and buzzy, 500 ms a smooth blend |
| Density | 20 /s | grains per second per note; the level is kept steady as it grows |
| Position | 50% | where in the source the grains are read |
| Spray | 80 ms | random scatter of the start: 0 repeats one slice |
| Scan speed | 0% | 0 is frozen, 100 the source's own speed, negative runs backwards |
| Align | 60% | up for tonal sources, 0 for noise and glitch |
| Pitch | 0 st | on top of the note; Pitch spray is a random detune in cents |

## Starting points

- "Pads — Frozen Pad", "Pads — Dark Drone", "Textures — Slow Scan Texture", "Textures — Wide Spray".
- "Glitch — Glitter Cloud", "Glitch — Stutter Grains", "Shimmer — Reverse Shimmer", "Shimmer — Pitched Swarm".

## Pitfalls

- A Song wave source with an empty slot plays silence; the editor says so. Load a sound into that slot first.
- Short grains at low Density leave gaps: raise Density or Grain size for a continuous cloud.
- A dense cloud on many voices hits the 128-grain limit: the newest grains are refused, so a chord of long dense notes thins out.
- Pitching a source up by more than about two octaves above its own brightness can alias: the interpolator low-passes a grain read faster than the source, which also dulls it.
- Drone sounds only while the song plays; it uses Attack and Release and holds at full level.
- It has no audio input, no Mix and no Amount: it only plays notes.

## Pairs well with

- Neo Verb for space, Neo Delay and Neo Ensemble for width, Neo EQ to shape the cloud.

## Checklist

- [ ] `param_set(machine="Grain", changes=[{"param": "Source", "value": "Bells"}, {"param": "Density", "value": "60"}])`, then `render(probes=["Grain"])`.
- [ ] `machine_readings(machine="Grain")` gives `rms_db` and `peak_db`: `peak_db` under 0 dBFS with a chord.
- [ ] `analyze(source="Grain", metrics=["spectrum"])`: a tonal source with Align up shows lines at the note's harmonics and no comb at the Density.
