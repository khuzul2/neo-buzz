# Neo Grain

Status: GRN1 (issue #185). A granular synth: clouds of grains cut from a built-in source or a wave of the song, played by notes. A normal instrument (a generator), clean room.
Live input and the audio of an audio clip as sources are later (section 9).
Code: `Machines/NeoGrain` (engine in `Engine/GrainEngine.cs`, the sources in `Engine/GrainSources.cs`, the song's wave in `GrainWaveSource.cs`, parameters in `Params.def`),
gates in `NeoBuzz.MachineTests/GrainTests.cs`, the listening pack in `GrainPack.cs`, guide `ReBuzz.Agent/Guides/machines/Neo Grain.md`.

## 1. What it is

Each note is a cloud. A grain is a short slice of the source, read at a pitch ratio, faded in and out by a window and placed in the stereo field; the cloud starts grains at the Density.
Everything is our own DSP from the published method (Roads, *Microsound*; Truax, "Real-time granular synthesis with a digital signal processor"; the WSOLA alignment of Verhelst and Roelands for section 4).
No other product's code, tables or presets were used, and Neo Learn Granular (a teaching synth on Dye Essence) shares nothing with it.

- **Voices and grains:** 16 note voices and one Drone voice; at most **128 grains** sounding in all, all preallocated (a grain that would be the 129th is refused and counted in `Dropped`).
- **Notes:** each note's cloud is transposed by the note (C3, MIDI 60, plays the source at its own pitch: the project's naming), has its own ADSR (Attack and Release in ms, Decay in ms, Sustain in %) and its own random stream.
  A stolen voice, and every voice on a hard All Notes Off, fades out in 5 ms (a raised cosine, at least 3 ms); the new note starts when the fade has landed.
- **Drone:** a switch that plays one cloud at C3 with no note while the song's transport runs (Attack and Release apply, it holds at full level); it stops with the transport.
- **Seeded randomness:** a note's stream starts from the Seed (1 to 100) and the note's number in the render, so a render repeats **bit for bit**; another seed gives another cloud.
- Parameters are in real units through `Params.def` (version 0, 20 parameters, new ones appended); the output is smoothed; a soft limiter (transparent below 0.7, ceiling 0.89) follows Volume.

## 2. The source

`Source` is Chord, Bells, Noise (built in) or Song wave. `Wave` is the slot (1 to 200) of the song's wavetable read when Source is Song wave. Both are plain parameters, so a saved song and a preset keep the source
reference as Neo Sampler keeps its zones' waves (the song saves the waves themselves). A slot with no wave, or one that cannot be read, gives the engine **no source: the cloud plays silence** and the editor says
"Wave N is empty: the cloud is silent" (it never crashes, and the wave arriving later starts the sound at once).

- **The built-in sources** are made by code once per process (48 kHz, 6 s, looping with no seam: every frequency is a whole number of cycles of the length): a **chord** (C major with a ninth over a low C, each note two detuned soft saws of 7 partials whose levels swell and fade, so scanning it changes its colour),
  **bells** (eight strikes of a pentatonic phrase on the free-bar mode ratios 1 : 2.756 : 5.404 : 8.933 : 13.34, Fletcher and Rossing, a strike's tail wrapping round to the start), and **noise** (white noise through two one-pole low-passes whose corner drifts between about 500 Hz and 3 kHz, cross-faded into a loop). No recording, no third-party audio.
- **A song wave** is read as Neo Sampler's `WaveSource` reads it (`GrainWaveSource.cs`): a timer (every 200 ms, and at once when ReBuzz says a wave changed) compares what the slot holds with what the engine has (the layer's buffer, length, format, rate) and makes room for a copy;
  `Work` copies up to 16 384 frames a block (no allocation, under the audio lock, so the wave cannot be freed meanwhile); the timer then builds the source (mono mix, up to 10 minutes) and hands it to the engine. `Busy` (renders ask first, on the song's thread) copies it at once.
  A grain keeps the source it started with, so a wave replaced while a cloud plays never pulls the samples from under a grain.
- The editor draws the source, the Position line and the **Spray range**, and a dot for each grain sounding (from the engine's published snapshot of the last block, 128 pairs of floats, no allocation per frame).

## 3. The grain

| Parameter | Range | What |
|---|---|---|
| Grain size | 5 to 500 ms | The grain's length in samples, `round(Size fs)`: within one sample of the setting |
| Density | 1 to 200 /s | Grains started a second by each note, on an exact clock (the sample each starts at is `round(k fs / Density)`), so the rate is exact whatever the block size |
| Position | 0 to 100 % | Where in the source the cloud reads, at the start of each note |
| Spray | 0 to 500 ms | Each grain's start is scattered uniformly plus or minus this many ms of the source |
| Scan speed | -200 to +200 % | How fast the position moves after the note starts: 100 % is the source's own speed whatever the pitch, 0 is frozen, negative runs backwards. The position wraps round the source |
| Window | Hann, Tukey, Gauss | Hann; Tukey with a taper of half the length; Gauss with sigma 0.14 of the length, lifted so it is exactly 0 at both ends |
| Align | 0 to 100 % | Section 4 |
| Pitch | -24 to +24 st | On top of the note; **Pitch spray** 0 to 1200 ct scatters each grain's pitch |
| Reverse chance | 0 to 100 % | The share of grains read backwards (the same segment, from its end) |
| Pan spread | 0 to 100 % | Each grain's pan is uniform in plus or minus this share, constant power |

**Level.** Grains that overlap add like uncorrelated sounds, so each grain is scaled by `1 / sqrt(max(1, Density x Size x 0.5))` and the cloud's RMS stays within 0.1 dB from 25 to 200 grains a second (measured 0.0579, 0.0586, 0.0579 at 25, 50 and 200).
Velocity scales the grain linearly.

## 4. Phase alignment

A synchronous train of identical grains is a comb: a sine cut into grains every T seconds sounds at the **multiples of 1/T nearest its pitch**, not at its pitch (the lines of the train's spectrum are `Density x m`, weighted by the grain's window spectrum). Without a remedy the pitch gate below
cannot be met by any granular engine with overlapping grains. With **Align** a new grain's start is moved, within plus or minus `min(20 ms, Size / 4) x Align` of the source, to where the waveform continues the grain the voice started last: the sum of squared differences over 32 samples between the new grain read from there and
the previous grain's continuation (both by linear reading), searched on a coarse grid (about 48 steps) and then frame by frame near the best, with a parabola through the best three for the fraction. The search costs no memory and
is deterministic. Align at 0 leaves the comb in, which suits noise and glitch (and is the character of an unaligned granulator).

## 5. Interpolation

A grain is read through a **12-tap Blackman-Harris windowed sinc** (half-width 6 samples), looked up in a table of 2048 steps a sample. When a grain is read faster than the source (a ratio above 1) the kernel is stretched by `0.95 / ratio` (up to a ratio of 8, so at most 96 taps), which lowers its cutoff so a
pitch up does not alias: content above the new Nyquist is filtered out, not folded back. No cubic or linear reading is used (linear reading is used only inside the alignment search).

## 6. Gates, measured (Linux CI numbers; local runs on the dev box are in the report)

| Gate | Measured |
|---|---|
| **Pitch:** a sine source played from C2 to C7, Size 60 ms, Density 50, no pitch spray | all six notes within **0.01 cent** (the printed 0.00 cents is the resolution of the parabolic peak) at Align 100 % |
| **Aliasing:** a sine source pitched up 24 semitones, loudest line outside the carrier | 1 kHz source: **-84.4 dB**; 2 kHz: **-78.8 dB**; 3 kHz: **-84.6 dB** under the carrier (gate: -60 dB) |
| **Density:** grains per second within 2 % | 1/s over 100 s: 1.010; 10/s: 10.05; 50/s: 50.00; 200/s: 200.10 |
| **Size:** each grain within 1 sample | 5, 37, 100 and 500 ms: every grain exactly `round(Size fs)` |
| **Clean:** no clicks at grain edges, note start, release, steal | project click detector: **0** in 27 busy takes (20 notes with 4 steals, releases, a moved Position and Size, Spray, Scan, Reverse, Pan; blocks 16, 64 and 256; the chord, bells and a song wave; Hann, Tukey and Gauss) |
| **Ear proxy:** max the second difference at grain boundaries not above that inside the grains | 0.000004 at the edges against 0.0081 inside (Hann), 0.000017 against 0.0126 (Tukey), at blocks 16, 64, 256 |
| **Repeatable:** two renders with the same seed bit-identical; the same at every block size (16, 64, 256) | yes; another seed differs |
| **Audio thread:** bytes allocated a block, 2000 blocks, 128 grains and 16 voices, parameters and notes changing | **0** |
| **CPU:** 128 grains on 16 voices, 48 kHz, one core | 0.17 % per grain unshifted (36 ns per grain-sample, 22 % in all), 0.22 % at +12 st, 0.32 % at +24 st (41 % in all) |
| **State:** every parameter survives a migration; a reopened state sounds the same; presets; catalogue | `GrainTests`, `PresetTests`, `StateVersionTests`, `machine_catalog.py` |

## 7. Presets

Eight, each on one of the built-in sources, so they sound the same in every song, and each peaking between -6 and -3 dBFS on the test phrase (the limiter is out of the way below -3.1 dBFS).

| Preset | Source | What it is |
|---|---|---|
| Pads — Frozen Pad | Chord | One held moment, 300 ms grains, Align 90 %, a slow swell |
| Pads — Dark Drone | Chord | An octave down, 450 ms grains, 1.5 s attack, 2.5 s release |
| Textures — Slow Scan Texture | Chord | Scan 40 %, Tukey, the harmony turning over |
| Textures — Wide Spray | Noise | Spray 500 ms, Scan 100 %, pan anywhere: an airy wash |
| Glitch — Glitter Cloud | Bells | 25 ms Gauss grains, 120 a second, an octave up, Spray 400 ms |
| Glitch — Stutter Grains | Bells | 60 ms Tukey grains, 8 a second, no spray: the same strike repeating |
| Shimmer — Reverse Shimmer | Bells | Every grain backwards and an octave up |
| Shimmer — Pitched Swarm | Chord | Pitch spray 700 ct, Gauss grains: a swarm of voices |

The closest two sit 5.9 dB apart (RMS over third-octave bands, `TheFactoryPresetsAreAudibleAndClearlyDifferent`). The **listening pack** (`GrainPack`, `GRAIN_PACK=<folder>`) renders each on one phrase of our own (four bars at 68 bpm, about 15.3 s, 48 kHz 16-bit WAV), local and never committed; click detector 0 on all eight.

## 8. The editor

The Source section (the source dropdown, the Wave knob, the drawing of the source with Position, the Spray range and live grain dots), Grain (Size, Density, Position, Spray, Scan, Align, Seed, Window), Pitch (Pitch, Pitch spray, Reverse chance, Pan spread), Envelope (the ADSR picture and four knobs) and Output (Volume, Drone, a meter), in the Synth identity colour, one source for WPF and Avalonia.

## 9. What is not here, and why

- **Live input.** The host cannot feed audio into a generator yet; it comes with host audio input into a generator (the same backlog item as Neo Mallet's resonator). Neo Grain has `InputCount = 0` and no Mix or Amount (those belong to effects).
- **The audio of an audio clip as a source.** The brief asked for it read-only through the existing public clip API. **There is none a machine can reach:** an audio clip's audio is a ReBuzz-internal `AudioSource` (`ReBuzz/Core/Clips/AudioClip.cs`, internal to the engine assembly) behind the session layout and the asset store, which since NA1 belongs to the song and not the wavetable; no interface in `BuzzGUI.Interfaces` returns it, and a task may not add one (the allowed paths are the machine's). The engine takes any `GrainSource`
  (`SetWave`), so a host that publishes clip audio later needs only a second `GrainWaveSource` and a Source choice (appended, so version 0 songs load unchanged). Until then a clip's audio reaches Neo Grain by being exported into a wave of the song.
- **Pitch bend, mod wheel and aftertouch** are not used.
