# Neo Follow — Specification

| | |
|---|---|
| Status | Implemented (FOL1, issue #199) |
| Date | 2026-11-28 |
| Kind | Effect (`Neo Follow.NET.dll`, box name `Follow`, 0 tracks, 2 inputs: the audio and a sidechain; 1 output: the audio, unchanged) |
| Scope | An audio follower that drives parameters: Envelope, Pitch and Onset, sharing `NeoModulatorBase` and `ModulatorEditor` with Neo LFO, Neo Envelope and Neo Random |

---

## 1. Design

Neo Follow is a control machine that listens to audio. Its engine (`Machines/NeoFollow/Engine/FollowEngine.cs`) turns what it hears into one control signal `0 … 1`; the shared bank (`NeoBuzz.Dsp/ModTargets.cs`) writes that signal to up to **8 targets** with depth (`−100 … +100 %`), polarity, optional range, curve and the write glide, exactly as for Neo Random (§1.1 of `neo-random.md`): writes are modulation, not edits (no undo steps), a removed target gets its base back, a list that grew keeps its old span (`LegacyChoiceSpans`), and the targets are saved and loaded by `NeoModulatorBase`.

### 1.1 The audio passes through: no Mix, no Amount
The audio is copied from the input to the output as it is (`Array.Copy` of the host's samples, no conversion): the output is **bit-identical** to the input in every mode, with the follower listening to the input or to the sidechain. It therefore belongs to the machines AGENTS.md §3 exempts from Mix and Amount (meters and analysers, which pass audio unchanged); it has neither. Nothing it does is heard unless a target is set.

### 1.2 Listen to
`Input` is the audio passing through (input 1); `Sidechain` is the machine wired to the second input (as Neo Comp's sidechain). With nothing wired it hears silence. A mono source is heard as left = right.

### 1.3 Modes
1. **Envelope.** The level of the signal through an attack and a release, then onto `0 … 100 %` between `Floor` and `Ceiling` (dB) and a `Curve`.
   - `Detector` **Peak** (the highest sample in a short window, then the ballistics) or **RMS** (the mean square, then the ballistics). The ballistics are one-pole time constants as in a dynamics detector (Giannoulis, Massberg and Reiss 2012): `Attack` (`0.1 … 500 ms`) and `Release` (`1 ms … 5 s`) are the time to cover 63 % of a step. For RMS the constants are those of the power, with a small averaging stage and a cascade correction so a step reads its set time.
   - `Floor` and `Ceiling` (`−80 … 0 dB`, at least 1 dB apart) map the level linearly in dB onto `0 … 1`; `Curve` (`−100 … +100 %`) bends it: `+` eases in (slow start), `−` eases out.
2. **Pitch.** The shared YIN tracker (`NeoBuzz.Dsp/PitchTracker.cs`, docs/specs/pitch.md §2; de Cheveigné and Kawahara 2002). `Pitch range` (`Voice`, `Guitar`, `Bass`, `Full`) sets the lowest and highest pitch it looks for; `Low note` and `High note` (MIDI, default C2 and C6) map the note onto `0 … 1` (in notes, linearly, cents included). Under the `Confidence` threshold (default 80 %), and in silence, the output **holds** its last value. `Pitch glide` (`0 … 500 ms`) slides between notes.
3. **Onset.** The shared onset detector (`NeoBuzz.Dsp/OnsetDetector.cs`, docs/specs/pitch.md §3; the max-filtered spectral flux of Böck and Widmer 2013 with the energy-rise functions of the shared detector). Each hit starts a **pulse**: full height, or with `Dynamic` on, scaled by the hit's level (`1` at −6 dB and louder, falling to `0.1` at −33 dB and quieter); the pulse holds for `Hold` (`0 … 2 s`), then falls with the `Decay` time constant (`5 … 2000 ms`). `Sensitivity` (`0 … 100 %`) moves the detector's threshold; at 100 % a sustained pad's beating can fire it.

### 1.4 Shape (all modes)
`Smooth` (a one-pole on the control, `0 … 1 s`), `Invert`, `Range` (`Unipolar`: `depth · x + bias`; `Bipolar`: `0.5 + depth · (x − 0.5) + 0.5 · bias`, clamped to `0 … 1`), `Depth` (`0 … 100 %`), `Bias` (`−100 … +100 %`) and `Write glide` (`1 ms … 1 s`, default `30 ms`).

### 1.5 Control delay (latency) per mode
Neo Follow reports no latency (the audio is not delayed). The **control delay** is the time between a change in the sound and the control showing it:

| Mode | Control delay | Why |
|---|---|---|
| Envelope | none of its own (the attack and release are the control's); at most the host's block | The detector reads the block it is given |
| Pitch | (window + one period) / 2 on a glide: about 21 ms at A4 and 33 ms at C2 (`Full`); a step to a new note is crossed in up to about twice that (40 ms measured for A3 → A4) | The estimate describes the audio half a window and half a period ago |
| Onset | about 10.7 ms (half a 21 ms frame), measured median 8.7 ms, worst 11.3 ms | The detector confirms a hit one hop after the frame that holds it; the pulse starts at the end of the block that holds the report |

The pitch output of a glide is compensated for this delay in the test (§3), not in the machine; the host's block (up to 256 samples here, 5.3 ms) comes on top in each mode.

---

## 2. Parameters (`Machines/NeoFollow/Params.def`, `StateVersion = 0`, 23 parameters)

| Group | Parameters |
|---|---|
| Source (2) | `Listen to` (`Input`, `Sidechain`), `Mode` (`Envelope`, `Pitch`, `Onset`) |
| Envelope (6) | `Detector` (`Peak`, `RMS`), `Attack` (`0.1 … 500 ms`, 10), `Release` (`1 ms … 5 s`, 200 ms), `Floor` (`−80 … 0 dB`, −60), `Ceiling` (`−80 … 0 dB`, −6), `Curve` (`−100 … +100 %`, 0) |
| Pitch (5) | `Pitch range` (`Voice`, `Guitar`, `Bass`, `Full`, default `Full`), `Low note` (36), `High note` (84), `Confidence` (`0 … 100 %`, 80), `Pitch glide` (`0 … 500 ms`, 30) |
| Onset (4) | `Sensitivity` (`0 … 100 %`, 50), `Decay` (200 ms), `Hold` (`0 … 2 s`, 0), `Dynamic` (On) |
| Shape (5) | `Smooth`, `Invert`, `Depth` (100 %), `Bias` (0 %), `Range` (`Unipolar`) |
| Behaviour (1) | `Write glide` (30 ms) |

Times are in milliseconds, not tempo: they are physical (a release is a time, not a note value). `Pitch range` is an addition to the brief's list: the shared tracker needs its range, and a wrong range costs octave errors. Presets and the editor store values by position; new parameters are appended with a new state version.

## 3. Acceptance gates and measured results (48 kHz, Linux x64; `NeoBuzz.MachineTests/NeoFollowTests.cs` unless noted)

| Gate | Target | Measured |
|---|---|---|
| Envelope step times (Peak and RMS, attack 5 … 100 ms, release 50 … 1000 ms) | within 10 % of the setting | Peak: +0.3 … +6.7 % (attack), 0.0 … +4.7 % (release); RMS: 0.0 … +3.3 % and −0.5 … +0.3 % |
| Level to output (5 points, −54 … −9 dB, curve −50 … +100 %) | within 0.5 dB | Peak 0.000 dB, RMS 0.021 dB |
| Pitch glide C2 → C6 (3 and 6 semitones a second) | within 5 cents | 1.96 and 4.21 cents against the note a control delay earlier |
| Pitch output against the Low and High mapping | within 0.5 % | 0.011 % of the range |
| Silence holds the last value | held | tone 0.6875; silence 0.688607 … 0.688607; noise 0.6886 … 0.6886 |
| Onsets on a Neo Drums loop (74 hits, 90, 120 and 150 BPM, sensitivity 50 %) | at least 95 %, each within 10 ms | 100 % (74 of 74), 0 extra; worst offset 8.67 ms |
| Sustained Neo Poly pad (12 s) | no trigger | 0 at sensitivity 50 % (54 at 100 %, a documented limit) |
| Pulse hold, decay, dynamic | hold and decay as set | hold 0.7 / 100.0 / 250.7 ms for 0 / 100 / 250; decay 200.0 / 100.0 / 398.7 ms for 200 / 100 / 400; soft hit 0.100 against 0.630 with Dynamic, 1.000 without |
| Control delay per mode | stated and measured | §1.5 |
| Shape (Invert, Depth, Bias, Range, Smooth) | exact | within 0.002; Smooth 100 ms covers 63 % in 100.0 ms |
| Targets | write, glide, ranges, base back, negative depth, kept span | worst write 17 against 17.0 allowed; `LegacyChoiceSpans` kept |
| Audio thread | 0 bytes per block, 2000 blocks, every mode and detector; no denormals; PortableMath only | 0 bytes; none; `AudioPathsUseNoLibmAndNoWidthDependentSums` passes |
| Block size independence | same result at 32, 64, 128, 256 | bit-equal levels, notes and onsets |
| Presets | audible and different | each moves its control by more than 0.5; pairwise mean difference above 0.04 |
| Pass-through bit-identical, targets saved and loaded | every mode | `ReBuzz.Studio.Tests/NeoEditorTests.cs` (`NeoFollowPassesTheAudioThroughBitIdenticalInEveryMode`, `NeoFollowWritesItsTargetsAndASongKeepsThemLikeNeoRandomsAreKept`) on the real machine |

Known limits: under RMS, attack times under about 10 ms read a steady tone slightly high (the power's ripple); Pitch needs one clear note at a time; a pulse starts at the end of the block that holds the report.

## 4. Editor, presets and pack

The editor is the shared modulator editor (the live output scope and the TARGETS table) with the mode tabs (`Envelope`, `Pitch`, `Onset`) over the mode's controls, the shared Shape and Behaviour sections, and a live meter of the control signal. Mac gallery shots, dark and light, are `neo-follow-editor` in the CI gallery.

**8 presets**: Envelope — Auto Wah, Pump (sidechain), Slow Swell, Drive by Level; Pitch — Filter Follows the Melody, Brighter When Higher; Onset — Hit Flash, Chop Gate. A **listening pack** (8 files, 15 s, the first 4 s with the follower off) is rendered by the explicit `NeoFollowPack` test into a local folder and never committed.

## 5. Literature

- Alain de Cheveigné and Hideki Kawahara, "YIN, a fundamental frequency estimator for speech and music", *Journal of the Acoustical Society of America* 111(4), 1917–1930, 2002 (pitch).
- Sebastian Böck and Gerhard Widmer, "Maximum Filter Vibrato Suppression for Onset Detection", *Proceedings of the 16th International Conference on Digital Audio Effects (DAFx-13)*, Maynooth, Ireland, 2013 (the max-filtered spectral flux, "SuperFlux").
- Juan Pablo Bello et al., "A Tutorial on Onset Detection in Music Signals", *IEEE Transactions on Speech and Audio Processing* 13(5), 1035–1047, 2005 (onset detection functions and adaptive thresholds).
- Dimitrios Giannoulis, Michael Massberg and Joshua D. Reiss, "Digital Dynamic Range Compressor Design — A Tutorial and Analysis", *Journal of the Audio Engineering Society* 60(6), 399–408, 2012 (level detectors and their ballistics).

The titles, authors, venues and years of all four were checked against their publishers' or archives' listings (JASA, the DAFx paper archive, the AES library) on 2026-11-28; the engine uses only these published ideas and our own DSP.
