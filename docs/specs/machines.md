# Neo machines — Specification

| | |
|---|---|
| Status | v1 — core six built (M0–M2): measured values below are from Linux CI, 2026-11-01 |
| Date | 2026-11-01 |
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
- **Notes.** Synths have 16 tracks with Note and Velocity, plus MIDI (notes, mod wheel, pitch bend, sustain, aftertouch). Setters only queue; `Work` applies the queue at the block start, so the order ReBuzz calls setters in doesn't matter. Transport stop releases all voices; Stop again once stopped silences everything (a panic, for sounds that ring on after release). Changing the voice mode releases held notes.
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

| Criterion | Target | Measured |
|---|---|---|
| Alias floor, 3.1 kHz sine at +30 dB drive, Transistor, 4× | ≤ −90 dB | −100.2 dB (Tape −96.2, Tube −91.0, Diode −105.1, Fold −130.0; Transistor 2× −77.1, 8× −123.5) |
| Mix 0 % | output equals the input delayed by the reported latency | passes |
| Drive 0 dB, −12 dBFS | THD ≤ 1 % (Tube ≤ 2 %: its even-order character is the point) | Tape 0.34 %, Transistor 0.37 %, Tube 1.44 % |
| Tube | 2nd harmonic ≥ 3rd harmonic (even-order character) | passes |
| Auto gain | output RMS within ±1.5 dB of input across 0–36 dB drive (pink noise; Fold to 24 dB) | passes |
| Latency | constant across Quality settings, reported to ReBuzz | 62 samples at every setting; ReBuzz sees it |

### Neo Comp (compressor)

Log-domain gain computer with soft knee; modes: **Clean** (feed-forward VCA, peak/RMS), **Glue** (bus VCA, stepped ratios, auto release), **FET** (feedback, very fast attack, program-dependent release, light harmonics), **Opto** (program-dependent attack, two-stage release). Parameters: Mode, Threshold, Ratio, Knee, Attack, Release, Auto release, Makeup, Auto makeup, Lookahead, SC low cut, Stereo link, Character, Mix, Output, Sidechain (internal/external input 2).

| Criterion | Target | Measured |
|---|---|---|
| Static curve (Clean, ratio 4, knee 0, threshold −20 dB, steady sine) | within ±0.25 dB of ideal from −60 to 0 dBFS | 0.015 dB (FET 0.015, Glue 0.073) |
| Attack / release (Clean) | time to 63 % of the gain change within ±10 % of the setting | within 0.5 % (1 ms … 2 s) |
| Below threshold (Clean, Character 0) | THD+N ≤ −100 dB | passes |
| Stereo link 100 % | identical gain on both channels | passes |
| Mix 0 % | output equals the input delayed by the latency | passes |
| Lookahead | peaks above threshold are caught (overshoot ≤ 1 dB with ratio ∞, attack 0) | passes; ReBuzz sees the new latency |

### Neo Verb (reverb)

Pre-delay → input diffusion (allpasses) → early reflections → 16-line feedback delay network with a Householder mix, modulated delays, and per-line low/high shelving set from the decay times so that T60 is exact per band → width, tone EQ → mix. Freeze sets the feedback to unity and mutes the input; shimmer pitch-shifts the feedback +12 semitones.

Modes: Hall, Room, Chamber, Plate. Parameters: Mode, Pre-delay, Size, Decay, Low decay ×, High damping, Diffusion, Mod depth, Mod rate, Early, Late, Width, Low cut, High cut, Freeze, Shimmer, Mix.

| Criterion | Target | Measured |
|---|---|---|
| T60 at 1 kHz (Hall, 0.5–10 s) | within ±10 % of the setting (Schroeder integration, T30 fit) | −5.2 % … +1.6 % |
| Echo density | late impulse response kurtosis ≤ 4 within 100 ms (Hall), 50 ms (Plate) | ≤ 3.33 in every mode |
| Stereo | late-tail interchannel correlation \|ρ\| ≤ 0.3 at width 100 % | 0.009 |
| Freeze | tail energy stays within ±1 dB over 10 s | −0.27 dB |
| Stability | every parameter at its extreme for 60 s: bounded, no NaN | passes |
| Mix 0 % | output equals the input | passes |

### Neo Poly (virtual analog)

16 notes × unison up to 7 (32-voice pool), mono/legato/glide. Per voice: 3 oscillators (shape morph sine→triangle→saw→square→pulse, PWM, Osc 2 sync to Osc 1, Osc 1 → Osc 2 linear FM, ring), noise, drift; filter 1 (ladder LP24/LP12, SVF LP/BP/HP/notch) with drive, filter 2 (SVF), serial/parallel; amp, filter and mod envelopes; 2 LFOs; 8-slot mod matrix; velocity, key tracking, mod wheel, pitch bend.

| Criterion | Target | Measured |
|---|---|---|
| Saw alias floor at 1.76 kHz | ≤ −80 dB | −101 dB at 3.7 kHz (oscillator alone: −133 dB) |
| Hard sync alias floor | ≤ −70 dB | −92.2 dB |
| Ladder self-oscillation pitch, 100 Hz–5 kHz | within ±25 cents of the cutoff | within 0.1 cent |
| SVF −3 dB point (LP, Q 0.707) up to 15 kHz | within ±2 % | passes |
| Envelope times | within ±5 % of the setting | passes |
| Voice stealing | no step larger than −40 dBFS at the steal (fade) | passes |
| Silence | `Work` returns false within one block after the last release ends | passes |
| CPU | 16 voices, all oscillators, ladder: ≥ 10× real time on the CI runner (was 20×: shared runners vary, and 10× is ample for 16 voices) | 12.2× … 17.1× |

### Neo Wave (wavetable)

Two wavetable oscillators (frame position with morphing, warp: bend ±, squeeze, mirror, asym, phase modulation, window sync), sub oscillator, noise; unison; filters, envelopes, LFOs and mod matrix shared with Neo Poly. Rendered at 2× with an IIR halfband decimator so mipmaps keep the full audible band. Built-in tables are generated (no binary assets): basic shapes, PWM, harmonic sweep, formants, sync sweep, FM sweep, organ, digital. Import: WAV (single cycle, or frames of 2048 such as Serum tables).

| Criterion | Target | Measured |
|---|---|---|
| Alias floor (saw table, 1.76 kHz) | ≤ −80 dB | −92 … −101 dB from 55 Hz to 3.7 kHz |
| Bandwidth | harmonics present up to 18 kHz at every pitch below 9 kHz | the harmonic nearest 18 kHz within 1.4 dB of ideal at 110 Hz … 3.5 kHz |
| Frame morph | no step: adjacent-sample difference bounded while sweeping the position | largest step 0.28 of the peak |
| Import | a 2048 × N WAV round-trips into N frames; a single cycle of any length is resampled to one frame | passes; the user table also survives the song's XML (ReBuzz test) |
| CPU | 8 voices, 2 tables, sub, ladder, 2×: ≥ 4× real time | 15.3× … 32.7× |

### Neo FM6 (6-operator FM)

DX7 architecture: 6 operators, 32 algorithms, feedback, per-operator ratio/fixed frequency, detune, rate/level envelopes, rate and level scaling, velocity, amp-mod sensitivity; pitch envelope; LFO; plus four operator waveforms (sine, half sine, abs sine, pulse sine), operator on/off, unison, glide and a tone control. 2× oversampling (switchable to 1×, "Raw", for the original's aliasing). Parameters keep the DX7's own values (0–99 levels and rates, coarse 0–31, …), so patches and DX7 knowledge carry over. DX7 behaviour follows the measurements published with Music Synthesizer for Android (msfa) and Dexed: fixed-frequency operators follow pitch bend only, tremolo starts unattenuated, operators above Nyquist fold back, a held note follows level edits; the pitch envelope's rate table is fitted, not exact. DX7 SysEx: 32-voice banks and single voices in, single voices out; a loaded bank is kept in the song.

| Criterion | Target | Measured |
|---|---|---|
| Algorithms | each of the 32 matches the DX7 chart (carriers, modulators, feedback) | passes |
| Frequency | ratio and fixed modes within 0.1 cent of the DX7 formulas | within 0.05 cent; detune ±7 = ±6.8 cents at A4 |
| Modulation | sidebands of a sine modulator follow Bessel functions of the operator's index | within 0.01 dB (index 1.0 and π/2) |
| Envelope | level steps 0.75 dB; decays are straight lines in dB; flat segments hold for their rate | straight within 0.15 dB; a rate-40 hold lasts 0.51 s |
| Velocity and scaling | velocity and keyboard level scaling follow the DX7 tables | velocity 127 vs 64 at sensitivity 7: 15.80 dB, as the table |
| SysEx | a 32-voice bank imports; parameters round-trip unchanged | bank → voices → parameters → voice is exact; damaged checksums are refused |
| Alias floor | single sine, 2×: ≤ −90 dB; full-depth 1:1 FM at C7, Clean: ≤ −80 dB | sine −135 dB; FM at C7 −93.4 dB (Raw −3.9 dB) |
| CPU | 16 voices × 6 operators, 2×: ≥ 10× real time on the CI runner | 10.7× |

## 6. Phases

| Phase | Content |
|---|---|
| M0 | Spec, core fix (§3.1), DSP library, editor kit, parameter generator, Linux test project |
| M1 | Neo Drive, Neo Comp, Neo Verb |
| M2 | Neo Poly, Neo Wave, Neo FM6 |

Each phase: pure tests (Linux CI), integration tests in ReBuzzTests (the machine loads, makes sound, its parameters and editor work, editor screenshots), then the package build.

All three phases are done. Every machine also ships factory presets (14–18 each), checked by a test: each preset sounds, at a consistent level (synths: loudest 50 ms of a chord between −30 and −6 dB RMS, peak below −1 dBFS).

## 7. Not in this batch

Additive and physical-modelling synths, convolution, tape delay, ensemble/phaser, multiband, limiter: candidates for the next batch.

The MCP server spec (docs/specs/mcp-server.md §8) sets the next batch's priorities by what an agent needs to mix and master: Neo EQ, Neo Limiter (true peak), Neo Utility and Neo Analyzer first; then Neo Delay, Neo Multiband, Neo Scope and Neo Sampler. Every Neo machine also publishes a manifest (its parameter table as JSON) so agents set values in real units.
