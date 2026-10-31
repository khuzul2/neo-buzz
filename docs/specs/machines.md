# Neo machines — Specification

| | |
|---|---|
| Status | Draft v1 — M0 foundation, M1 effects, M2 synths in progress |
| Date | 2026-10-31 |
| Scope | New high-quality machines for sound design, written in C# with custom editors: first batch "core six" |

---

## 1. Summary

The package has 101 machines, but the synths are either 2000s-era native code or small, focused C# machines (the Pedal set), and none of the effects has a modern algorithmic reverb, a modelled saturator or a character compressor. No machine has a custom editor beyond a parameter list.

This project adds a family of machines, **Neo**, built on one shared DSP library and one shared editor kit, each measured by automated tests rather than judged by ear alone.

**First batch (core six)**

| Machine | Kind | One line |
|---|---|---|
| Neo Drive | effect | Saturator: tape, tube, transistor, diode and fold, oversampled and anti-aliased |
| Neo Comp | effect | Compressor: clean VCA, glue bus, FET and opto, with sidechain filter, lookahead and mix |
| Neo Verb | effect | Algorithmic reverb: hall, room, chamber and plate, modulated, with freeze and shimmer |
| Neo Poly | generator | Polyphonic virtual analog: 3 oscillators with sync, FM and ring, 2 filters, mod matrix |
| Neo Wave | generator | Wavetable synth: 2 morphing oscillators with warp modes, table import, mod matrix |
| Neo FM6 | generator | 6-operator FM: the 32 DX7 algorithms, DX7 envelopes, DX7 SysEx import |

## 2. Decisions

- **C#**, managed machines (`IBuzzMachine`), like the Pedal set: no native build, no 32-bit bridge.
- **Custom editor for every machine** (WPF, drawn in code), opened by the machine's GUI factory. The parameter window still works.
- **Every sound parameter is a Buzz parameter**: automatable in patterns, MIDI-learnable, stored in songs and presets by ReBuzz. Bulk data (imported wavetables) lives in the machine state.
- **Pitch**: a Buzz note's `octave × 12 + semitone` is its MIDI note, A-5 (MIDI 69) is 440 Hz. Same as the Pedal synths and ReBuzz's MIDI input.
- **Measured, not guessed**: each machine ships with tests that measure what the table below promises (aliasing, response, decay times, CPU).

## 3. Architecture

```
Machines/
  NeoBuzz.Dsp/        Pure DSP (no Buzz or WPF references), internal types, linked as source
  NeoBuzz.Machine/    Buzz glue shared by the machines (parameters, notes, MIDI), linked as source
  NeoBuzz.Editor/     WPF editor kit (knobs, switches, meters, graphs, preset bar), linked as source
  NeoDrive/ NeoComp/ NeoVerb/ NeoPoly/ NeoWave/ NeoFm6/
    Engine/           the machine's DSP: pure, tested on Linux
    Params.def        the parameter table (source of truth)
    *.cs              the machine class and its editor
NeoBuzz.MachineTests/ Linux NUnit project: links NeoBuzz.Dsp and every Engine/ folder
tools/gen-machine-params.py   Params.def → Engine/Params.g.cs + Machine.Params.g.cs
```

- **Linked as source, not a shared DLL.** ReBuzz loads managed machines with `Assembly.LoadFile`, so a shared library DLL would load once per machine folder and version clashes would be silent. Each machine compiles its own copy; types are `internal`.
- **Parameter table.** `Params.def` declares each parameter once: name, range, default, mapping (linear, exponential, dB, time, frequency, enum…) and unit. The generator writes the pure table (mapping and value text, used by the engine, the tests and the editor) and the machine's `[ParameterDecl]` properties. Continuous parameters use 0–1000 for fine control; discrete ones their own range.
- **Engine contract.** `Process(float[] left, float[] right, int n)` in ±1.0 floats, sample rate set by the machine from `MasterInfo`. No allocation and no locks on the audio thread; parameter changes are smoothed inside the engine.
- **Notes.** Synths have 16 tracks with Note and Velocity, plus MIDI (notes, mod wheel, pitch bend, sustain, aftertouch). Setters only queue; `Work` applies the queue at the block start, so the order ReBuzz calls setters in doesn't matter. Transport stop releases all voices.
- **Silence.** A machine returns `false` from `Work` once its output is silent (synth voices done, effect tail below −120 dB), so ReBuzz can skip downstream work.

### 3.1 Core fix

`MachineCore.parametersChanged` is keyed by parameter, so when one parameter changes on several tracks in the same tick only the last track reaches a managed machine: a chord on tracks 1–3 plays one note. (The Pedal synths work around it by reading ReBuzz's private fields.) M0 keys it by parameter and track.

## 4. Shared foundation (M0)

**DSP library (`NeoBuzz.Dsp`)**

| Block | What |
|---|---|
| Math | fast tanh/exp2, dB, note → Hz, denormal-safe helpers, xorshift noise (white, pink) |
| Smoothing | one-pole and linear-ramp parameter smoothers |
| Oscillators | band-limited step/ramp tables (BLEP/BLAMP): saw, pulse with PWM, triangle, hard sync, through-zero FM |
| Wavetables | frames with per-octave band-limited mipmaps built by FFT; morphing reads |
| Filters | zero-delay-feedback SVF (LP/BP/HP/notch/peak/allpass/shelves), nonlinear ZDF ladder (LP24/LP12), one-pole, RBJ biquads, DC blocker |
| Resampling | linear-phase FIR halfband (2×/4×/8×, fixed latency), polyphase IIR halfband (synths) |
| Modulation | analog-style ADSR, DX7 rate/level envelope, LFO (7 shapes, tempo sync, fade-in), 8-slot mod matrix |
| Voices | allocator: poly with stealing (released first, then oldest; stolen voices fade), mono/legato with note stack, glide, unison |
| Delay | fractional delay lines (cubic/allpass), modulated allpass diffusers |
| Analysis | peak/RMS meters for the editors |

**Editor kit (`NeoBuzz.Editor`)**: dark theme with one accent per machine; rotary knob (drag, Shift for fine, wheel, double-click for default, value shown while dragging); segmented switch; section frame; level and gain-reduction meters; graph surface for curves, envelopes, waves and algorithms; preset bar (ReBuzz presets: browse, previous/next, save). Controls bind to `IParameter` and follow automation.

## 5. Machines and acceptance criteria

Tests run at 48 kHz unless noted. "Alias floor" is the loudest component that is not a harmonic, 20 Hz–20 kHz, relative to the fundamental, from a coherent 65,536-point FFT.

### Neo Drive (saturator)

Drive → pre-emphasis → oversampled shaper with first-order anti-derivative anti-aliasing (ADAA) → de-emphasis → DC block → tone → output; dry/wet mix delayed to match the latency.

Modes: **Tape** (pre/de-emphasis, soft symmetric curve, head bump), **Tube** (asymmetric, bias that sags with level → even harmonics), **Transistor** (harder knee), **Diode** (asymmetric exponential), **Fold** (sine wavefolder). Parameters: Mode, Drive (0–36 dB), Bias, Character, Tone, Low cut, Output, Auto gain, Mix, Quality (2×/4×/8×).

| Criterion | Target |
|---|---|
| Alias floor, 3.1 kHz sine at +30 dB drive, Transistor, 4× | ≤ −90 dB |
| Mix 0 % | output equals the input delayed by the reported latency |
| Drive 0 dB, Tape/Tube/Transistor | THD ≤ 1 % at −12 dBFS |
| Tube | 2nd harmonic ≥ 3rd harmonic (even-order character) |
| Auto gain | output RMS within ±1.5 dB of input across 0–36 dB drive (pink noise) |
| Latency | constant across Quality settings, reported to ReBuzz |

### Neo Comp (compressor)

Log-domain gain computer with soft knee; modes: **Clean** (feed-forward VCA, peak/RMS), **Glue** (bus VCA, stepped ratios, auto release), **FET** (feedback, very fast attack, program-dependent release, light harmonics), **Opto** (program-dependent attack, two-stage release). Parameters: Mode, Threshold, Ratio, Knee, Attack, Release, Auto release, Makeup, Auto makeup, Lookahead, SC low cut, Stereo link, Character, Mix, Output, Sidechain (internal/external input 2).

| Criterion | Target |
|---|---|
| Static curve (Clean, ratio 4, knee 0, threshold −20 dB, steady sine) | within ±0.25 dB of ideal from −60 to 0 dBFS |
| Attack / release (Clean) | time to 63 % of the gain change within ±10 % of the setting |
| Below threshold (Clean, Character 0) | THD+N ≤ −100 dB |
| Stereo link 100 % | identical gain on both channels |
| Mix 0 % | output equals the input delayed by the latency |
| Lookahead | peaks above threshold are caught (overshoot ≤ 1 dB with ratio ∞, attack 0) |

### Neo Verb (reverb)

Pre-delay → input diffusion (allpasses) → early reflections → 16-line feedback delay network with a Householder mix, modulated delays, and per-line low/high shelving set from the decay times so that T60 is exact per band → width, tone EQ → mix. Freeze sets the feedback to unity and mutes the input; shimmer pitch-shifts the feedback +12 semitones.

Modes: Hall, Room, Chamber, Plate. Parameters: Mode, Pre-delay, Size, Decay, Low decay ×, High damping, Diffusion, Mod depth, Mod rate, Early, Late, Width, Low cut, High cut, Freeze, Shimmer, Mix.

| Criterion | Target |
|---|---|
| T60 at 1 kHz (Hall, 0.5–10 s) | within ±10 % of the setting (Schroeder integration, T30 fit) |
| Echo density | late impulse response kurtosis ≤ 4 within 100 ms (Hall), 50 ms (Plate) |
| Stereo | late-tail interchannel correlation \|ρ\| ≤ 0.3 at width 100 % |
| Freeze | tail energy stays within ±1 dB over 10 s |
| Stability | every parameter at its extreme for 60 s: bounded, no NaN |
| Mix 0 % | output equals the input |

### Neo Poly (virtual analog)

16 notes × unison up to 7 (32-voice pool), mono/legato/glide. Per voice: 3 oscillators (shape morph sine→triangle→saw→square→pulse, PWM, Osc 2 sync to Osc 1, Osc 1 → Osc 2 linear FM, ring), noise, drift; filter 1 (ladder LP24/LP12, SVF LP/BP/HP/notch) with drive, filter 2 (SVF), serial/parallel; amp, filter and mod envelopes; 2 LFOs; 8-slot mod matrix; velocity, key tracking, mod wheel, pitch bend.

| Criterion | Target |
|---|---|
| Saw alias floor at 1.76 kHz | ≤ −80 dB |
| Hard sync alias floor | ≤ −70 dB |
| Ladder self-oscillation pitch, 100 Hz–5 kHz | within ±25 cents of the cutoff |
| SVF −3 dB point (LP, Q 0.707) up to 15 kHz | within ±2 % |
| Envelope times | within ±5 % of the setting |
| Voice stealing | no step larger than −40 dBFS at the steal (fade) |
| Silence | `Work` returns false within one block after the last release ends |
| CPU | 16 voices, all oscillators, ladder: ≥ 20× real time on the CI runner |

### Neo Wave (wavetable)

Two wavetable oscillators (frame position with morphing, warp: bend ±, squeeze, mirror, asym, phase modulation, window sync), sub oscillator, noise; unison; filters, envelopes, LFOs and mod matrix shared with Neo Poly. Rendered at 2× with an IIR halfband decimator so mipmaps keep the full audible band. Built-in tables are generated (no binary assets): basic shapes, PWM, harmonic sweep, formants, sync sweep, FM sweep, organ, digital. Import: WAV (single cycle, or frames of 2048 such as Serum tables).

| Criterion | Target |
|---|---|
| Alias floor (saw table, 1.76 kHz) | ≤ −80 dB |
| Bandwidth | harmonics present up to 18 kHz at every pitch below 9 kHz |
| Frame morph | no step: adjacent-sample difference bounded while sweeping the position |
| Import | a 2048 × N WAV round-trips into N frames; a single cycle of any length is resampled to one frame |

### Neo FM6 (6-operator FM)

DX7 architecture: 6 operators, 32 algorithms, feedback, per-operator ratio/fixed frequency, detune, rate/level envelopes, rate and level scaling, velocity, amp-mod sensitivity; pitch envelope; LFO; plus extra operator waveforms (sine, half, abs, quarter, and others). 2× oversampling (switchable to 1× for the grit). DX7 SysEx import: 32-voice banks and single voices.

| Criterion | Target |
|---|---|
| Algorithms | each of the 32 matches the DX7 chart (carriers, modulators, feedback) |
| Frequency | ratio and fixed modes within 0.1 cent of the DX7 formulas |
| Envelope | level steps 0.75 dB; decay times follow the DX7 rate table (within ±10 %) |
| SysEx | a 32-voice bank imports; parameters round-trip unchanged |
| Alias floor (algorithm 32, single sine, 2×) | ≤ −90 dB |
| CPU | 16 voices, 2×: ≥ 10× real time on the CI runner |

## 6. Phases

| Phase | Content |
|---|---|
| M0 | Spec, core fix (§3.1), DSP library, editor kit, parameter generator, Linux test project |
| M1 | Neo Drive, Neo Comp, Neo Verb |
| M2 | Neo Poly, Neo Wave, Neo FM6 |

Each phase: pure tests (Linux CI), integration tests in ReBuzzTests (the machine loads, makes sound, its parameters and editor work, editor screenshots), then the package build.

## 7. Not in this batch

Additive and physical-modelling synths, convolution, tape delay, ensemble/phaser, multiband, limiter: candidates for the next batch.

The MCP server spec (docs/specs/mcp-server.md §8) sets the next batch's priorities by what an agent needs to mix and master: Neo EQ, Neo Limiter (true peak), Neo Utility and Neo Analyzer first; then Neo Delay, Neo Multiband, Neo Scope and Neo Sampler. Every Neo machine also publishes a manifest (its parameter table as JSON) so agents set values in real units.
