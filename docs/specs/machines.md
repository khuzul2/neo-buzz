# Neo machines — Specification

| | |
|---|---|
| Status | v1.3 — core six (M0–M2) and the mix and master batch (M3: Neo EQ, Neo Utility, Neo Limiter, Neo Analyzer) built: measured values below are from Linux CI, 2026-11-01 and 2026-11-02. Next batch (successors, machine-safety.md §4): Neo Ensemble and Neo EQ's graphic mode built, 2026-11-04 |
| Date | 2026-11-04 |
| Scope | New high-quality machines for sound design, mixing and mastering, written in C# with custom editors: first batch "core six", second batch "mix and master", third the successors of closed machines that misbehave |

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

**Second batch (mix and master, for the agent server: docs/specs/mcp-server.md §8.2)**

| Machine | Kind | One line |
|---|---|---|
| Neo EQ | effect | 8-band parametric EQ: bell, shelves, cuts at 12/24/48 dB/oct, notch; minimum phase; response graph with draggable bands over the spectrum |
| Neo Utility | effect | Gain, balance or mono pan, width, bass mono, polarity, swap and DC removal, each exact |
| Neo Limiter | effect | True-peak lookahead limiter: a ceiling in dBTP as BS.1770 measures it, auto or set release, stereo link |
| Neo Analyzer | effect | Spectrum (FFT with peak hold, third-octave bars), loudness (momentary, short-term, integrated LUFS, range, true peak) and stereo (correlation, width, goniometer) with the analysis library's numbers; readings for agents; audio untouched |

**Third batch (successors: machine-safety.md §4)**

| Machine | Kind | One line |
|---|---|---|
| Neo Ensemble | effect | Chorus (1–4 voices), flanger and phaser (2–12 stages) on one LFO, in Hz or synced to the tempo; feedback to ±99 % through a saturating loop that can't run away, however fast it sweeps. Succeeds Jeskola Flanger and Chorus, FSM Chorus2, Pedal Chorus |
| Neo EQ, graphic mode | effect | 31 constant-Q bands at the ISO third-octave centres, ±18 dB; the bands' overlap compensated, so the response passes through the sliders, and every slider at +X is exactly +X. Succeeds Joachims Hyperion |

## 2. Decisions

- **C#**, managed machines (`IBuzzMachine`), like the Pedal set: no native build, no 32-bit bridge.
- **Custom editor for every machine** (WPF, drawn in code), opened by the machine's GUI factory. The parameter window still works.
- **Every sound parameter is a Buzz parameter**: automatable in patterns, MIDI-learnable, stored in songs and presets by ReBuzz. Bulk data (imported wavetables) lives in the machine state.
- **Pitch**: a Buzz note's `octave × 12 + semitone` is its MIDI note, A-5 (MIDI 69) is 440 Hz. Same as the Pedal synths and ReBuzz's MIDI input.
- **Measured, not guessed**: each machine ships with tests that measure what the table below promises (aliasing, response, decay times, CPU).
- **Bit-identical everywhere**: a Neo machine renders the same bits on Windows x64, Linux x64 and macOS arm64 (mac-engine.md decision 6). Its audio code follows the portable maths rule (§4).

## 3. Architecture

```
Machines/
  NeoBuzz.Dsp/        Pure DSP (no Buzz or WPF references), internal types, linked as source
  NeoBuzz.Machine/    Buzz glue shared by the machines (parameters, notes, MIDI), linked as source
  NeoBuzz.Editor/     WPF editor kit (knobs, switches, meters, graphs, preset bar), linked as source
  NeoDrive/ NeoComp/ NeoVerb/ NeoPoly/ NeoWave/ NeoFm6/ NeoEq/ NeoUtility/ NeoLimiter/ NeoAnalyzer/ NeoEnsemble/
    Engine/           the machine's DSP: pure, tested on Linux
    Params.def        the parameter table (source of truth)
    Presets.txt       factory presets in units
    *.cs              the machine class and its editor
NeoBuzz.MachineTests/ Linux NUnit project: links NeoBuzz.Dsp, every Engine/ folder, the editor kit's Core/ and the analysis library's loudness, streaming meters, FFT, spectrum and stereo files
tools/gen-machine-params.py   Params.def → Engine/Params.g.cs + Machine.Params.g.cs + <Name>.machine.json
tools/gen-machine-presets.py  Presets.txt → <Name>.prs.xml
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
| Portable maths | `PortableMath` (double, as `Math`) and `PortableMathF` (float, through the double ones, as `MathF`): exp, expm1, log, log1p, log2, log10, pow, 2^x, 10^x, sin, cos, tan, tanh, asinh, atan, atan2, built only from IEEE basic operations, so the same bits on every CPU and OS. Within 1 ulp (Atan2 1.2, Asinh 1.3, Tanh 1.8); exp and 2^x table-driven, the rest after fdlibm |
| Smoothing | one-pole and linear-ramp parameter smoothers (the ramp lands exactly on its target, for gains that must end bit-exact) |
| Oscillators | band-limited step/ramp tables (BLEP/BLAMP): saw, pulse with PWM, triangle, hard sync, through-zero FM |
| Wavetables | frames with per-octave band-limited mipmaps built by FFT; morphing reads |
| Filters | zero-delay-feedback SVF (LP/BP/HP/notch/peak/allpass/shelves), nonlinear ZDF ladder (LP24/LP12), one-pole, RBJ biquads, DC blocker; (M3) a double-precision TPT section for equalisers and crossovers (`SvfSection`, with its exact magnitude response) and Butterworth cascades (12/24/48 dB/oct) |
| Resampling | linear-phase FIR halfband (2×/4×/8×, fixed latency), polyphase IIR halfband (synths) |
| Modulation | analog-style ADSR, DX7 rate/level envelope, LFO (7 shapes, tempo sync, fade-in), 8-slot mod matrix |
| Voices | allocator: poly with stealing (released first, then oldest; stolen voices fade), mono/legato with note stack, glide, unison |
| Delay | fractional delay lines (cubic/allpass), modulated allpass diffusers |
| Analysis | peak/RMS meters for the editors; (A4) a lock-free triple buffer that hands whole snapshots from the audio thread to readers (`TripleBuffer`) |

**The portable maths rule** (every machine, every audio path: the shared library, the Buzz glue, each `Engine` folder, and coefficients computed from parameters while playing):
- **No libm.** `Math.Sin`, `Exp`, `Pow`, `Tanh` … and every `MathF` transcendental function call the platform's C runtime, and the UCRT, glibc and Apple's libm round the last bit differently. Use `PortableMath` and `PortableMathF`. Exact operations stay: `Sqrt`, `Abs`, `Floor`, `Ceiling`, `Round`, `Min`, `Max`, `Clamp`, conversions.
- **No width-dependent sums.** `Vector<T>` is 8 floats on x64 (AVX) and 4 on arm64, and `Vector.Sum` and `Dot` leave the order to the implementation. No `FusedMultiplyAdd` either (one rounding instead of two, and the C runtime's `fma` on a CPU without the instruction). Element-wise vector maths is fine; a sum runs in one fixed order: scalar, or explicit lanes added in a fixed order. Neo Limiter's true-peak filter has eight lanes, one `Vector256` on x64 or two `Vector128` halves elsewhere: the same additions, so the same bits.
- **Denormals** are IEEE on all three (no flush-to-zero mode), and feedback state is flushed with `DspMath.Flush` (below 1e-18 in float, 1e-30 in double), the same way everywhere.
- **Checked** by `PortableMathTests` (it scans those sources for libm calls and width-dependent sums, and pins a checksum of the maths, the same on every platform) and by the null test (mac-engine.md §6), which fails on any differing sample. Analysis and editors (Neo Analyzer's measurements, `NeoBuzz.Analysis`, the editor kit) don't touch the audio and may use `Math`.

**Editor kit (`NeoBuzz.Editor`)**: dark theme with one accent per machine; rotary knob (drag, Shift for fine, wheel, double-click for default, value shown while dragging); segmented switch; section frame; level and gain-reduction meters; graph surface for curves, envelopes, waves and algorithms; (M3) a frequency-response graph with draggable band handles (toolkit-free axes, hit-testing and drags in `Core/ResponseGraph.cs`, the pointer routed by `InteractiveGraph`); (A4) analysis graphs: a spectrum with third-octave bars, held peaks and a viewing tilt, loudness bars and readouts coloured against a target, a goniometer and a correlation bar (`Core/AnalysisGraphs.cs`); preset bar (ReBuzz presets: browse, previous/next, save). Controls bind to `IParameter` and follow automation.

The kit has two backends, and each machine's editor is one source file for both. WPF (`NeoBuzz.Editor/*.cs`, `WpfCanvas`) builds the Windows machines. Avalonia (`NeoBuzz.Editor.Avalonia/`: `AvaloniaCanvas` on Avalonia's `DrawingContext`, and the same controls with the same API) builds their studio flavour (`Machines/Studio`, studio-mvp.md S1). `EditorContext` and `ParamBinding` are shared (`Params/`). In the studio, a gesture's edits (a drag, a click, a preset) are one step on the song's undo history (`ParamEdits`).

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

Decay is the T60 at 1 kHz: each line's gain divides out its shelves' gain there. That is exact while 1 kHz is in the flat mid band. The lower High damping, the more the high shelf already cuts at 1 kHz, and dividing that out lifts the slowest band: it rings longer than set (2.2× in Ambient — Long Dark Hall, damping 2.5 kHz), and below about 1.4 kHz at the defaults (2.9 kHz with Low decay ×4) it grew without bound (the profiler: +347 dBFS). So each line's peak loop gain over frequency is computed exactly from its shelves (a ratio of quadratics in tan²(ω/2)) and compared with the slowest band's set decay. Where that band would ring more than 3× longer than set, or never die, the gain comes down until it rings as set, blending in continuously from that edge: then the band below the damping corner keeps the set Decay (the lows the set Low decay, when that is longer), and no band rings more than 3× its setting. With Freeze off every setting decays: every line's loop gain stays below 1 at every frequency, and the orthonormal mix keeps the network's below it. What changed: only settings that rang more than 3× too long or diverged, which need High damping below about 1.85 kHz at the defaults (up to 3.7 kHz with Low decay ×4 and the longest Decay; never with Low decay ×0.25). The defaults and every factory preset are untouched, bit for bit.

Modes: Hall, Room, Chamber, Plate. Parameters: Mode, Pre-delay, Size, Decay, Low decay ×, High damping, Diffusion, Mod depth, Mod rate, Early, Late, Width, Low cut, High cut, Freeze, Shimmer, Mix.

| Criterion | Target | Measured |
|---|---|---|
| T60 at 1 kHz (Hall, 0.5–10 s) | within ±10 % of the setting (Schroeder integration, T30 fit) | −5.2 % … +1.6 % |
| Echo density | late impulse response kurtosis ≤ 4 within 100 ms (Hall), 50 ms (Plate) | ≤ 3.33 in every mode |
| Stereo | late-tail interchannel correlation \|ρ\| ≤ 0.3 at width 100 % | 0.009 |
| Freeze | tail energy stays within ±1 dB over 10 s | −0.27 dB |
| Stability | every parameter at its extreme for 60 s: bounded, no NaN | passes |
| Loop gain | every line below 1 at every frequency, over Mode × Size × Decay × Low decay × High damping (20 412 settings, ends included) | at most 0.99991; no band longer than 3.000× its set decay |
| Extremes | each parameter alone at each end, and the lowest damping with the longest decays (sizes, modes, full modulation): bounded over 30 s of noise and decaying after it | passes (Shimmer included) |
| Shimmer | 30 % and 100 % across Mode × Size (ends) at the longest Decay, 100 % at the default and shortest: 30 s of noise at −20 dBFS RMS, then 40 s of tail: finite, 6 dB under the engine's guard, and over the last 10 s decaying faster than the edge (3× the slowest set decay), held or not | passes; peaks up to +42.0 dBFS (Plate, size 0, 30 s, 100 %: held inside its edge, it amplifies that much) |
| Shimmer guard | a loop inside its edge never wakes it (bit for bit); Ambient — Shimmer is held, its tail falling between the edge and twice the rate aimed at; the seven settings closest to the edge in the sweep below, over 90 s of tail (a straight line through it): less than 3× longer than set | asleep for 60 s at 40 %/2.5 s, 100 %/0.2 s, 100 %/1.5 s Room, 60 %/2.5 s Plate; Ambient — Shimmer held at 19 % shimmer (of 45 %), falling 2.1 dB/s (edge 1.85); closest 2.95× (Plate, size 0, 30 s, 100 %) |
| Freeze, extremes | Mode, Size, modulation and Shimmer at each end, Decay max, every parameter at its maximum, four worst combinations: frozen 60 s with the input playing, never grows | passes (every one falls slightly, through the interpolation) |
| High damping 1 kHz | Decay 2 s, Low decay ×1: no octave longer than the set Decay; the band below the corner at it | 125 Hz 2.19 s, 250 Hz 2.01 s, 500 Hz 1.65 s, 1 kHz 1.17 s (diverged before) |

| Mix 0 % | output equals the input | passes |

**Shimmer** has its own loop: the tail, an octave up, back into the network. Above an edge that falls with the decay it grows by itself, slowly at first: its slowest mode can take 10–40 s to come through the rest of the tail, so the few-second measurements made before (an edge of about 93 % at the default Decay, 48 % for Ambient — Shimmer) were far too high. Where the guard below settles 100 % (Hall, size 50 %, each loop ringing about 2–2.4× longer than set): about 46 % at the default Decay (2.5 s), 20 % at 9 s, 8 % at 30 s. The factory preset Ambient — Shimmer (45 % at 9 s, Low decay ×1.2) is above its edge: from a 2 s burst its tail falls to −58 dB in 15 s, then grows by about 1.9 dB/s, past 0 dBFS within a minute; it settles at 19 %. Small rooms with a long decay are the hardest: there the loop can amplify by tens of dB on its way up, and some first grow by tens of dB and only then decay.

The **shimmer guard** keeps the loop to the rule the network keeps without it (Loop gain, above): no tail rings more than 3× longer than its slowest set decay (Decay, or Low decay where that is longer). The user chose this over the first guard's fixed aim, a loop decaying at 0.5 dB/s, which let a held tail ring for 2 minutes at any Decay, and a loop just inside the old edge longer still. A shadow of the network and the shimmer loop runs beside it with no input: the same lines, shelves, gains, modulation and shifter taps, so it has exactly the tail's own dynamics. Seeded with noise and renormalised, it measures how fast the loop decays. While the loop rings inside the edge, nothing changes, bit for bit. When the loop's rate, averaged over 2 s (starting from the network's own, so it comes at the loop's from below), stays slower than the edge for 1.5 s, or its level grows by itself (averaged over 2 s, 6 dB above its lowest; or 15 dB at once, or 6 dB at more than 12 dB/s, for a fast runaway), the shimmer's gain steps down by the excess it measured, then follows continuously, in proportion to the excess relative to the rate it aims at (so 1 s and 120 s decays settle alike), until the loop decays as if it rang 2.4× longer than set; it goes back to the setting when the loop decays faster than that at the setting for 2 s. Where the network alone already rings close to the edge, the shimmer can be held down by up to 60 dB. The loop's slowest mode can lie below what reaches the output (with Low decay long, down to DC, which the shifter passes unshifted): the guard holds that too, so there the audible tail rings shorter than the edge allows (Low decay ×4 at 30 s: about 1×). The shadow ignores the input, so whether and when the guard acts depends only on the settings. Of 237 settings (Decay × Mode × Size × Shimmer at 30, 60 and 100 %; Low decay and High damping at their ends; Ambient — Shimmer; 40 random ones) rendered for 100 s (10 s of pink noise at −20 dBFS RMS, then silence; the tail measured by a straight line through its 1 s levels from 20 s to its last second above −140 dB): the guard left 139 alone, the longest ringing 1.34× longer than set; it held 98, 93 of them within the first 20 s (from 0.8 s; the latest at 74 s). Of those whose tail lasted past 30 s, every one rings less than 3× longer than set: 31 under 2×, 39 at 2–2.5×, 22 at 2.5–2.8×, 6 at 2.8–2.95× (small rooms at 30 s, the slowest to settle). Peaks, during the noise: up to +38.4 dBFS (Room, size 0, 9 s, 100 %). The defaults and the other 13 presets have no shimmer, so the guard never runs for them. Plain Freeze is untouched; with Freeze on the shimmer fades out, so it can't feed a frozen tail. The shadow costs a second network while Shimmer is on.

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

### Mix and master batch (M3, MCP A4)

The three machines an agent mixes and masters with (docs/specs/mcp-server.md §8.2). Their parameters speak the agent's units (Hz, dB, Q, %, ms), with exact steps where an agent names values: gains in 0.1 dB steps, frequencies in 0.07 % steps (a requested "320 Hz" lands within 0.04 % and reads back as "320 Hz"), Q in 0.3 % steps. The ceiling is in dB of true peak (dBTP), written as dB, which the agent server already parses.

### Neo EQ (parametric equalizer)

Eight bands in series, then the output gain. Each band is one second-order section, or a cascade of them for the steeper cuts: a topology-preserving (TPT) state-variable filter in double precision (NeoBuzz.Dsp `SvfSection`), whose response is the RBJ cookbook biquad's: the bilinear transform of the analog prototype, prewarped at the band frequency. Minimum phase, zero latency. One function (`EqDesign`) designs the sections the engine runs, the curve the editor draws and the reference the tests measure against.

| Type | Prototype |
|---|---|
| Bell | RBJ peaking: the gain at the frequency, half the gain (in dB) at the edges of a bandwidth set by Q; boost and cut mirror each other |
| Low shelf, High shelf | RBJ shelves: half the gain at the frequency; Q 0.71 is the steepest shelf without overshoot, higher Q adds a dip and a bump |
| Low cut, High cut | Butterworth, 12, 24 or 48 dB/oct (1, 2 or 4 sections): −3 dB at the frequency with Q 0.71 (maximally flat); another Q scales the resonance of the sharpest section |
| Notch | RBJ notch: a null at the frequency, bandwidth from Q |

Parameters per band (B1 … B8): On, Type, Freq (20 Hz – 20 kHz), Gain (±24 dB, bells and shelves), Q (0.1 – 30), Slope (12/24/48 dB/oct, cuts). Then Output (±24 dB). Frequency, gain and Q glide (20 ms one-pole in log frequency, dB and log Q; the sections are recomputed every sample while they move and land exactly on the design); type, slope and on/off changes fade the band out over 5 ms, switch, and fade it back in. The defaults spread the bands over the spectrum: B1 low cut 30 Hz and B8 high cut 18 kHz start off; B2 low shelf 100 Hz, bells at 250 Hz, 600 Hz, 1.5 kHz and 4 kHz, and B7 high shelf 8 kHz start on at 0 dB, which is exactly transparent, so an agent's first move is just a gain.

The editor draws the response over 20 Hz – 20 kHz with a numbered handle per band (drag: frequency and gain, Shift for fine; wheel: Q; double-click: on/off; dragging a band that is off switches it on), the selected band's own curve shaded, and the output spectrum behind it (4096-point FFT, 1/6 octave, tilted +3 dB/oct so pink noise reads flat). Below, a band selector and the selected band's controls. The graph's axes, hit-testing and drags are toolkit-free (NeoBuzz.Editor `Core/ResponseGraph.cs`), tested on Linux.

| Criterion | Target | Measured |
|---|---|---|
| Response = design | measured response (the engine's impulse response, DTFT at 160 frequencies) within ±0.1 dB of the drawn design curve, 20 Hz – 16 kHz, at 44.1 and 48 kHz: a mix curve (all 8 bands), a mastering curve, extremes (Q 30 at 20 Hz, ±24 dB, 48 dB/oct cuts) | worst 0.005 dB (48 kHz), 0.002 dB (44.1 kHz): the extremes at 20 Hz |
| Labels | a bell's gain at its frequency equals the setting (±0.01 dB); a shelf is at half its gain at its frequency (±0.01 dB) and within 0.1 dB of its gain 4 octaves past it; a notch is ≥ 60 dB deep at its frequency | bells exact to 0.0001 dB; shelves 3.000 / 6.000 dB (+6 low) and −3.000 / −6.000 dB (−6 high); notch −170 dB |
| Cut slopes | Q 0.71: −3.01 dB at the cutoff (±0.05 dB) for every slope; the slope over the second octave past the cutoff (where the asymptote has taken over and float audio still resolves 48 dB/oct) is 12, 24 or 48 dB/oct within ±3 % | −3.007 dB at every slope; low cuts 11.80, 24.08, 48.18 dB/oct; high cuts 12.10, 24.67, 49.38 dB/oct (1 kHz) |
| Parameter moves | a +12 dB bell swept 100 Hz – 5 kHz and its gain swept ±12 dB, one value per 256-sample block as automation arrives, on a 220 Hz sine: energy above 2 kHz ≤ −90 dB relative to the sine (the same moves on a plain biquad switched per block measure ≥ −70 dB, so the test hears zipper noise when there is some) | −124.8 dB (switched biquad: −54.0 dB); type and on/off switches: no step larger than the signal's own |
| Flat | every band at 0 dB or off: output equals the input (±1e-6) | defaults: identical; all bands off: bit for bit |
| CPU | 8 bands with 48 dB/oct cuts at both ends, stereo: ≥ 50× real time on the CI runner (was 100×: shared runners vary, and 50× is 2 % of a core) | 144× |

Later: analog-matched (decramped) designs for the parametric bands, so a bell or shelf above a few kHz keeps its analog shape up to Nyquist (the graphic mode's bells, below, already do). The bilinear transform squeezes it: against the analog prototype, RBJ sections stay within 0.2 dB up to 16 kHz for bands below about 1 kHz, but a wide bell at 8 kHz differs by about 3 dB near 16 kHz. (Prototypes of a 5-point magnitude match and of Orfanidis' prescribed-Nyquist bell were 3–10× closer for bells but not robust near Nyquist, so they wait.) Also later: linear-phase mode, dynamic bands, mid/side bands.

#### Graphic mode (machine-safety.md §4: H2, the successor of Joachims Hyperion)

`Mode` (Parametric, the default, or Graphic) and 31 sliders, `G 20 Hz` … `G 20 kHz`, follow Output in the parameter table, so songs and presets keep their values. The sliders sit at the ISO 266 third-octave centres (1000 · 10^(k/10) Hz, k = −17 … 13, named by their nominal values) and run from −18 to +18 dB in 0.1 dB steps (Hyperion's range). In graphic mode the eight bands rest; Output applies in both modes; a mode change crossfades the two over 5 ms. In Parametric mode the engine runs as it did before graphic mode was added, bit for bit.

- **Constant-Q bells.** Each band is the analog bell H(s) = (s² + (A/Q) s + 1) / (s² + s/(AQ) + 1), A = 10^(g/40), Q = 1/(2^(1/3) − 2^(−1/3)) ≈ 2.15: half its gain in dB at 2^(±1/3) times its centre, 2/3 octave apart, whatever the gain, and a cut the mirror of a boost.
- **No cramping near Nyquist.** The digital bell matches that prototype at DC, at its centre, at its lower half-gain point and at Nyquist (Orfanidis' prescribed-Nyquist-gain design, solved for the lower edge instead of a bandwidth, written so nothing cancels as the gain goes to 0, where it is exactly 1); a cut is the boost's inverse. The bilinear bell the parametric bands use is 0 dB at Nyquist and misses the prototype by up to 14.8 dB at 20 kHz.
- **The response follows the sliders.** The bands overlap, so their gains are solved for. The sliders' median is applied as a plain gain and the bands make up the rest, fitted by weighted least squares at the 31 centres (weight 1) and at two points between each pair (weight 0.15; targets on straight lines in dB between the sliders): a first estimate from the bands' interaction at 12 dB, precomputed per sample rate (after Liski and Välimäki's accurate cascade graphic equalizer), then three Gauss–Newton steps on the exact response. It runs only when a slider moves, at the start of the block, never per sample. Zigzag sliders (±18 dB alternating) need bands near ±54 dB; they are clamped at ±60 dB.
- **Running.** TPT state-variable sections in double precision, one per band for both channels, each written as its state-space update on the SVF's own states (`StereoBell`: `SvfSection`'s coefficients, one multiply and one add per step on the chain through the state). The bands' gains glide (a 20 ms one-pole in dB) in 16-sample segments that run across blocks, the five coefficients moving in straight lines within each, so fast automation stays smooth and stable and the output doesn't depend on how the host splits it. A band settled at exactly 0 dB passes the signal untouched (its sections keep running, so it moves off cleanly), so every slider at 0 dB passes the input bit for bit; tails below 1e-30 are 0. Bands at or above 0.46 fs are left out (none at 44.1 kHz and above). No allocation after the machine is made.
- One function (`GraphicDesign` and `GraphicSolver`, `Engine/GraphicEq.cs`) designs what the engine runs, the curve the editor draws and the reference the tests measure against.

The editor keeps its size: a Mode switch sits beside the band controls. In graphic mode the graph shows the 31 sliders on their tracks with the response through them over the output spectrum: drag a slider's cap (Shift: fine), or press beside it and drag across the sliders to draw a curve; the wheel moves the slider under the pointer by 0.5 dB (Shift: 0.1 dB), a double-click puts it at 0 dB. Below, the selected slider's knob and Flat. The sliders' input is toolkit-free (NeoBuzz.Editor `Core/GraphicSliders.cs`). Presets (7): Graphic — Flat, Loudness (Smile), Telephone, Air, Low Cut, Mid Scoop, Vocal Presence.

| Criterion | Target | Measured |
|---|---|---|
| Response = sliders | at every centre within ±1 dB of its slider: 1000 random settings across ±18 dB (neighbours up to 36 dB apart), a loudness smile and both zigzags, at 44.1 and 48 kHz | within 0.188 dB (mean 0.025 dB); the worst the zigzag at 20 Hz |
| Flat | every slider at +X (±0.1 … ±18 dB): flat +X within ±0.5 dB at 600 frequencies, 20 Hz – 20 kHz | exact (0.0000 dB): the median is the gain, the bands rest at 0 dB |
| Response = design | the engine's impulse response (DTFT, 160 frequencies, 20 Hz – 20 kHz) against the solver's curve: a random setting, the smile, the zigzag, every slider at +12 dB with Output −6 dB | within 0.00001 dB |
| Constant-Q | each bell alone at ±1 … ±40 dB: its half-gain points 2/3 octave apart at every gain | the lower point exact (0.0000 cent); the width varies with the gain by at most 0.003 octave for the 23 bands whose upper point lies below 0.1 fs, 0.033 octave up to 0.4 fs, where Nyquist warps the upper point |
| Near Nyquist | the 12.5, 16 and 20 kHz bells at ±6 … ±18 dB against the analog prototype, 20 Hz – Nyquist: within 8 % of the gain | 44.1 kHz: within 0.84, 1.03 and 1.03 dB (the bilinear bell: 5.3, 8.6, 14.8 dB); 48 kHz: 0.75, 0.98, 0.91 dB (4.4, 7.2, 12.1 dB); at most 7.0 % of the gain |
| Extremes | every slider at +18 dB, at −18 dB, and at +18 dB with Output +24 dB: exactly +18, −18 and +42 dB, bounded (Hyperion: +221 dBFS, §1) | +18.0000, −18.0000 and +42.0000 dB on pink noise, every sample within 2·10⁻⁸ of the input times the gain; the zigzag's curve peaks at 18.2 dB anywhere up to Nyquist; the Linux profile's every parameter at its maximum: +32.8 dBFS from noise peaking near −9 dBFS (Hyperion: +225.6 dBFS before the guard) |
| Fast automation | every slider jumping every block (32 and 256 samples) between the zigzags, both ends and random settings, the mode switched now and then, 20 s of pink noise: finite, under +24 dB over the input's peak | +11.2 dB (blocks of 32), +14.4 dB (256) |
| Slider moves | the sliders from 100 to 500 Hz moving through ±12 dB every block, on a 220 Hz sine: energy above 2 kHz ≤ −90 dB relative to the sine | −93.3 dB |
| Mode switches | a 100 Hz sine, +12 dB in graphic mode and −12 dB in parametric, switched back and forth: no step larger than the boosted sine's own (×1.2) | 0.0130 against 0.0130 |
| Blocks | blocks of 256 and of 117 samples give the same output, with the sliders and the mode moving | identical |
| Silence after noise | the zigzag, 2 s of noise then 20 s of silence: no subnormal number out; silence costs no more than noise | none; 0.94× |
| Audio thread | 2000 blocks with a slider moving every block (the solver running) and the mode switching; in the studio, a graphic Neo EQ with a slider moved every buffer, each work algorithm | 0 bytes; 0 bytes |
| Flat passes | every slider at 0 dB: the input bit for bit (negative zero and subnormals included), also after a slider has moved away and back | bit for bit; latency 0 |
| Parametric unchanged | the parametric mode's renders (the defaults, the three test curves, the 14 factory presets' stored values, a bell automated every block, type and on/off switches; 44.1 and 48 kHz) against origin/main's before graphic mode | 40 of 40 bit-identical |
| CPU | 31 bands busy, stereo at 48 kHz: ≥ 30× real time (3.3 % of a core: 31 sections, where the parametric figure is for 14); a slider move's solve | 119× and 99× (two Linux runs), 59× and 67× on the macOS arm64 runner; a solve 0.15–0.23 ms (at most 4.3 % of a 256-sample block) |

As built:

- **No bandwidth or Q control.** With the overlap compensated, the response at the centres is the sliders whatever the bands' width; the width only sets how the curve runs between centres and how hard the bands work against each other. 2/3 octave is the compromise measured: 1/2-octave bells ripple 0.8 dB between centres on a plateau 18 dB from the median (2/3 octave: 0.44 dB), wider ones need larger gains and fit the zigzags worse. Hyperion's Bandwidth folds into the sliders' values instead (machine-safety.md §4).
- **A slider is a third of an octave wide.** One slider at +X leaves its neighbours' centres at 0 dB, as an accurate graphic EQ's must: its curve is 0.36 octave wide at half its gain at +3 dB, 0.28 at +18 dB, and dips about 0.8 dB below 0 either side at +12 dB. Between centres the curve is free: two neighbours at +12 dB with the rest at 0 bulge to +13.2 dB between them (the Windows test measures 13.16 dB at 220 Hz, the design's figure to 0.000001 dB); four in a row stay within 0.75 dB of +12.
- **Outside 20 Hz – 20 kHz** the response settles at the sliders' median (the plain gain), not at 0 dB: the Telephone preset (median −18 dB) cuts DC and the top octave too.
- **Bands near ±54 dB ring.** Their poles' Q is about 2.15 · 10^(54/40) ≈ 48; the curve stays within the sliders, but such a setting (sliders alternating 36 dB apart) rings for seconds at the lowest bands.
- The Linux profile (`docs/machine-health.md`): hot as before, with B4–B7 gain and Output at their maximum; no parameter alone diverges or fails to decay, so no safe range.
- Hyperion's parameters on graphic mode, for `machine_replace` (U5): machine-safety.md §4.

### Neo Utility (gain, pan, width)

DC removal → polarity invert → channel swap → width (mid/side) → bass mono → pan → gain. Zero latency. Every move is a linear ramp that lands exactly on its value (gain, pan and width over 20 ms, switches over 5 ms), and a stage at its identity is skipped, so settled values are exact and the defaults pass the input bit for bit.

Parameters: Gain (−∞, −60 … +24 dB in 0.1 dB steps), Pan (−100 … +100 %), Pan law (**Balance**: the far side fades linearly to silence, 0 dB at centre, for stereo sources; **Mono pan**: sums to mono, then the sine/cosine law, −3 dB per side at centre), Width (0 – 200 %: the side signal times the width; 0 % is mono), Bass mono (Off/On) and Bass mono freq (20 – 500 Hz: the side signal through a 4th-order Butterworth high-pass; the mid is untouched, so the lows become mono), Invert L and Invert R (the inputs, before the swap), Swap, DC removal (2nd-order Butterworth high-pass at 5 Hz). The editor shows the stereo image (width and pan as a wedge, the mono lows, polarity and swap tags) and the live output correlation.

| Criterion | Target | Measured |
|---|---|---|
| Gain | settled output/input = setting within ±0.001 dB, −60 … +24 dB; −∞ is silence | exact (0.00000 dB); −∞ silent |
| Pan | Balance: far side 1 − \|pan\|, near side 0 dB; Mono pan: L = cos θ and R = sin θ times the mono sum, θ = (pan + 1) · 45°; each within ±0.001 dB | within 0.0001 dB; the silent side exactly 0 |
| Width | the side/mid ratio scales by the setting (±0.01 dB); 0 %: L = R exactly; 100 %: the input unchanged | within 0.00005 dB; 0 % mono; 100 % bit for bit |
| Bass mono | side −3 dB at the frequency (±0.1 dB), ≥ 45 dB down two octaves below, within 0.1 dB two octaves above; the mid untouched (±1e-6) | 120 Hz: −3.008 dB, −48.2 dB, −0.0001 dB; mid untouched |
| Invert, swap | exact, sample for sample | exact |
| DC removal | a DC offset falls below −100 dB within 1 s; 20 Hz passes within ±0.05 dB | below −100 dB after 534 ms; 20 Hz −0.017 dB |
| Moves | no steps: a −24 dB gain move and a polarity flip on DC move at most 0.001 per sample | 0.00049 per sample |
| Default | the input bit for bit, zero latency | bit for bit, latency 0 |

### Neo Limiter (true-peak limiter)

Input gain → 4× true-peak detector → the reduction each sample needs to stay under the ceiling (dB ≥ 0) → stereo link → hold over the lookahead (a sliding maximum) → release → two box filters over the lookahead → gain on the audio, delayed to match → output.

- **Detector**: the BS.1770 interpolator NeoBuzz.Analysis measures true peak with (windowed sinc, 24 taps per phase, Kaiser β 8, the same phase positions), run sample by sample, so the limiter holds exactly what the meter reads. A sample answers for the interpolated points on both sides of it.
- **Lookahead**: the hold keeps the largest reduction of the last `lookahead` samples; the two box filters (lengths summing to the lookahead plus one) average it into a smooth ramp that reaches the needed value exactly when the peak's sample leaves. Each value averaged is at least what that sample needs, so the guarantee holds by construction, not by tuning.
- **Release**: one-pole in dB; the Release time is the time constant (63 % recovered). **Auto release** slides the rate from five times faster (an isolated peak) to the Release time (sustained limiting), by the ratio of the reduction's 0.3 s average to its value now: transients recover quickly, dense material slowly, with no more ripple on low frequencies than a set release.
- **Latency**: lookahead + 11 samples (the interpolator), reported to ReBuzz; it follows the Lookahead setting. Fully linked (the default), one gain chain serves both channels.

Parameters: Input gain (−12 … +24 dB), Ceiling (−12 … 0 dB true peak, 0.1 dB steps), Release (1 ms … 1 s), Auto release, Lookahead (0.5 – 5 ms), Stereo link (0 – 100 %). The editor shows input, output true peak and gain reduction over the last four seconds against the ceiling, and input, output true-peak and gain-reduction meters.

| Criterion | Target | Measured |
|---|---|---|
| True peak | output true peak (4× oversampled, NeoBuzz.Analysis `Loudness.TruePeak`) ≤ ceiling + 0.1 dB: naive square waves (997 Hz, 5.5 kHz), a drum loop and pink noise, each pushed +12 dB over full scale; ceilings −1.0 and −0.1 dBTP; lookahead 0.5 and 2 ms; auto and set release | worst +0.002 dB (drums, 0.5 ms); squares +0.000 dB |
| Below the ceiling | a sine 6 dB under the ceiling: output equals the input delayed by the latency (±1e-6); THD+N ≤ −100 dB | identical (difference 0); THD+N −153 dB |
| Limiting distortion | 100 Hz sine limited by 6 dB, Auto release: THD ≤ 1 % | 0.32 % (Release 100 ms set: 0.31 %) |
| Release | a 12 dB reduction recovers exponentially with the Release time as its time constant (80 % → 29 %), ±10 % (Auto off) | 10.00, 100.00 and 498.9 ms for 10, 100 and 500 ms |
| Auto release | an isolated 5 ms peak recovers 63 % in under 50 ms; 2 s of limiting in 80 – 130 ms (Release 100 ms) | 24 ms; 108 ms |
| Stereo link | 100 %: identical gain on both channels | identical |
| Latency | lookahead + 11 samples, reported to ReBuzz, follows the setting | 107 samples at 2 ms, 48 kHz; an impulse arrives exactly then; ReBuzz sees the new latency |
| CPU | stereo, limiting a drum loop: ≥ 50× real time on the CI runner (was 100×: shared runners vary, and 50× is 2 % of a core) | 120× |

Not built: a target-LUFS mode. Integrated loudness needs the whole programme, so a live machine can only guess it, and limiting makes loudness grow slower than the input gain; the agent instead renders, measures (`analyze`) and sets Input gain, which is exact.

### Neo Analyzer (spectrum, loudness, stereo; MCP A4)

An effect whose output is its input, bit for bit, with zero latency. It measures what passes with the analysis library the agent server's `analyze` uses (docs/specs/mcp-server.md §7), so the editor, `machine_readings` and `analyze` give the same numbers.

- **Loudness**: NeoBuzz.Analysis `LoudnessMeter`, the streaming form of `Loudness.Measure`: the same K-weighting and 100 ms steps, momentary (400 ms) and short-term (3 s) every step, and since the reset the BS.1770 gated integrated loudness and the EBU Tech 3342 loudness range. These two come from histograms of 0.01 LU bins (−70 … +30 LUFS) that keep each bin's exact energy, so memory doesn't grow with time and only the bin a gate falls in is approximate.
- **True peak**: `TruePeakMeter`, `Loudness.TruePeak`'s interpolator and shortcut run sample by sample (the points around a sample are known 11 samples later). Shown as the recent true peak (falling 20 dB/s), a held marker and the maximum since the reset.
- **Spectrum**: an 8192-point FFT (at 44.1 and 48 kHz; about 0.17 s) every 2048 samples, Hann, both channels in one complex transform, the channels' mean power per bin on the library's scale (the bins sum to the mean square, so levels are plain RMS dB). Speed averages it exponentially over 0.1, 0.3, 1 or 3 s. The 31 third-octave bars and the curve (48 points per octave) integrate the average over bands, as `Spectrum.Integrate` does. The curve's bands are 1/24, 1/12, 1/6 or 1/3 octave wide (Smoothing), so a tone reads its level at every smoothing, and at 1/3 octave the curve passes through the bars. Held peaks hold for Peak hold, then fall 20 dB/s. Tilt (0, 3 or 4.5 dB/oct around 1 kHz) is for viewing only: at 0 pink noise draws flat, at 3 a typical finished mix (about −3 dB/oct in third octaves) does.
- **Stereo**: correlation and width (side/mid energy, `Stereo.Measures`) over 400 ms for the meter and over 3 s for the readings; a goniometer of the last 1024 samples.
- **Threads**: the audio thread fills a snapshot and publishes it through a lock-free triple buffer (NeoBuzz.Dsp `TripleBuffer`): one atomic exchange per publish, never a lock or an allocation. Readers (the editor, the agent server) copy the newest snapshot out, taking a lock among themselves only.
- **Readings** (docs/specs/mcp-server.md §3.5): `Readings` returns a new read-only dictionary of 48 values, keyed as the manifest's `readings`: `momentary_lufs`, `short_term_lufs`, `integrated_lufs`, `range_lu`, `true_peak_dbtp`, `momentary_max_lufs`, `short_term_max_lufs`, `seconds`, `correlation`, `width`, the named bands `sub_db` … `air_db` and the third octaves `band_20hz_db` … `band_20000hz_db` (both over 3 s). They stay after the transport stops, so a render leaves its measures.

Parameters: Smoothing, Speed, Peak hold (0.25 – 20 s), Tilt, Target (the loudness the meters are coloured against, written in dB: within 1 LU green, under blue, up to 3 LU over amber, beyond red) and Reset (On starts the integrated loudness, range, maxima and held peaks again; `ResetReadings()` and the editor's Reset button do it without the parameter). Presets: Mix (−18), Mastering (−14, slow, tilted 4.5), K-14, Broadcast R128 (−23), Tones (1/24 octave, fast). The editor shows the spectrum, the loudness meters and readouts, and the goniometer with the correlation bar.

Levels follow the library: plain RMS dB, so a full-scale sine reads −3 dB. A sine at −20 dB RMS reads −20 dB in its band; one peaking at −20 dBFS reads −23.01 dB, as `analyze` reads it.

| Criterion | Target | Measured |
|---|---|---|
| Pass-through | output = input bit for bit; latency 0 | bit for bit at the machine's Work (1,024 loud random samples); inside ReBuzz a −20 dBFS sine reaches the master within 0.01 dB; latency 0 |
| Same numbers as the library | over whole signals (a 1 kHz tone, pink noise stepping by 10 dB, noise bursts with silences, a quarter-rate sine at 45°; in blocks of 256 and of 117): integrated loudness and the momentary and short-term maxima within 0.05 LU of `Loudness.Measure`, loudness range within 0.05 LU, true peak within 0.05 dB | integrated and maxima within 0.0000 LU, range within 0.0003 LU, true peak within 0.0000 dB; every momentary and short-term value within 9e-11 LU. Inside ReBuzz a stereo 1 kHz sine at −20 dBFS reads −20.0 LUFS integrated and −20.0 dBTP |
| A tone in its band | a 1 kHz sine at −20 dB RMS: −20 dB in its third octave, in the mid band and on the curve (±0.05 dB); two third octaves away, over 40 dB down | −20.000 dB in the third octave, the mid band and on the curve; the next third octaves −103.4 and −105.9 dB |
| Bands | named bands within 0.5 dB of `Spectrum.Measure` (3 s of pink noise and tones); third octaves of pink noise within 0.5 dB, 100 Hz – 20 kHz | named bands within 0.07 dB; third octaves within 0.21 dB (the bars, 12 s) and 0.47 dB (the 3 s readings) |
| Correlation | 1 for mono and −1 for anti-phase (±1e-9); 0 ± 0.02 for unrelated noise, width 1 ± 0.05 | mono 1.000000 (width 0), anti-phase −1.000000 (width ∞), unrelated noise 0.0009 (width 0.998); the 400 ms meter 0.006 |
| Threads | no allocation while processing (parameter changes and a reset included); no torn snapshot with a writer and two readers (200,000 publishes) | 0 bytes; no torn snapshot |
| CPU | stereo at 48 kHz: under 2 % of real time on the CI runner (TieredCompilation off, as the other tests) | 0.46 % (220× real time) |

### Neo Ensemble (chorus, flanger, phaser; machine-safety.md §4)

The first successor: it replaces Jeskola Flanger (which diverges at feedback +100 %, machine-safety.md §3.1) and Jeskola Chorus, FSM Chorus2 and Pedal Chorus. Three modes on one LFO, each channel its own loop, the right channel's LFO `Spread` on from the left's:

- **Chorus**: 1–4 voices read a delay line at delays swept exponentially an octave either side of `Delay` at 100 % `Depth`, their LFOs evenly round the cycle (with several voices the right channel's fall between the left's: `Spread` is a share of the gap). The wet signal is their average through `Tone`, times √voices, so the moving part keeps its power.
- **Flanger**: one voice, swept two octaves either side of `Delay`; with feedback a comb of resonant peaks (the "jet").
- **Phaser**: 2–12 first-order allpasses at one frequency swept three octaves either side of `Centre`; the wet signal is their output, and the feedback comes back one sample later.

`Mix` crossfades dry and wet linearly: 50 % gives the deepest comb or notches, 100 % vibrato. `Output` is the gain after it. Zero latency; at `Mix` 0 % and `Output` 0 dB the input passes bit for bit.

**Why the feedback can't run away.** A loop gain below 1 at every frequency is proof enough for a fixed loop (Neo Verb's), but these loops are swept, and a swept loop can pump: the first build, with the phaser's allpasses as TPT one-poles (2·LP − x), never decayed at +99 % with a 20 Hz LFO sweeping three octaves around 50 Hz, though its frozen loop gain was 0.99. So each loop is built so that it can't grow however it is swept:

- Chorus, Flanger: the line takes v = HP(x) + S(fb · L(R(v))). R averages the voices' reads with linear interpolation, each a convex combination of two past samples; L is the `Tone` one-pole y = x + a(y₁ − x), 0 ≤ a < 1, a convex combination too; S is the saturator, |S(u)| ≤ |u|. So with no input each sample is at most |fb| ≤ 0.99 times the largest in the last delay's worth: the loop shrinks by at least 0.99 per pass, whatever the sweep does. The wet signal reads the same line with a Catmull-Rom cubic for clean highs, outside the loop. A high-pass inside the loop would break the convexity (its taps sum to 2 in magnitude), so the 10 Hz high-pass that keeps positive feedback from booming below the audible range filters what enters the line.
- Phaser: u = x + S(fb · HP(L(w))) one sample late, w = A^N(u). Each stage is a normalised lattice, [y, s′] = [[k, c], [c, −k]]·[x, s] with c = √(1 − k²), an orthogonal matrix: swept at any speed it neither adds nor loses energy. L and the 10 Hz loop high-pass have gain at most 1 at every frequency, and |S(u)| ≤ |u|: the loop's energy gain is at most |fb| ≤ 0.99 (the small-gain theorem), so the tail always decays.
- Frozen, the loop's gain at every frequency is the product of factors each at most 1 (the linear read's at every fraction, `Tone`'s at every setting, the high-pass's, an allpass stage's, the saturator's slope) times |fb|. The tests check each factor and the product over 864 settings.
- The saturator S is the identity up to full scale, then 1 + r/(1 + r) above it (r the excess): its slope is 1 at the knee and falls after it, and what goes round never passes +6 dBFS whatever comes in. Below full scale the loops are exactly linear, so the response and the notches are as designed.

Parameters: Mode (Chorus, Flanger, Phaser), Rate (0.02–20 Hz), Sync (Free, 1/32 … 16 bars: one LFO cycle per division of the song's tempo), Shape (Sine, Triangle; a change crossfades over 50 ms), Depth (0–100 %), Spread (0–180°), Delay (0.2–30 ms, the geometric centre of the sweep; Chorus, Flanger), Voices (1–4; Chorus), Centre (50 Hz – 10 kHz; Phaser), Stages (2–12; Phaser), Feedback (−99 … +99 % in 0.1 % steps), Tone (500 Hz – 20 kHz, −3 dB at the setting exactly; on the wet signal and in the loop for Chorus and Flanger, in the loop for Phaser; open at its top), Mix (0–100 %), Output (−12 … +12 dB in 0.1 dB steps). The LFO and the delays (or the stages' coefficient) move in straight lines over 8-sample segments that run across blocks, so the output doesn't depend on how the host splits it; Delay, Centre, Depth and Spread glide over 50 ms, Tone over 20 ms, Mix, Output and Feedback ramp per sample. Mode, Voices (in Chorus) and Stages (in Phaser) fade the wet signal and the feedback out over 5 ms, switch and fade back in; the line is always written, so a mode comes back in on the input as it is. Presets (14): Chorus — Classic, Wide Stereo, Subtle Doubling, Poly Synth I; Ensemble — Strings, Thick (3 Voices); Flanger — Slow Jet, Tape Style (Negative), Metallic; Phaser — 4-Stage Classic, 8-Stage Deep, Slow Sweep (4 Bars); Rotary — Leslie-like Fast; Vibrato — Pitch Wobble.

The editor shows the effect, the LFO and its sync, a picture of the sweep and the sound and output controls with meters. The picture: for the flanger and the phaser, the response now (each channel at its own LFO phase) inside the band the sweep covers, 20 Hz – 20 kHz; for the chorus, each voice's delay over one LFO cycle with where the LFO is; under both, each voice's LFO position. One function (`EnsembleDesign`) gives the delays and coefficients the engine runs and the response the editor draws and the tests measure against.

| Criterion | Target | Measured |
|---|---|---|
| Phaser notches | Depth 0, no feedback, Mix 50 %: each notch at fs/π · atan(tan(πfc/fs) · tan((2k + 1)π/2N)) within 0.2 %, at least 40 dB deep; 2, 4, 6, 8 and 12 stages | within 0.0000 %; at least 112 dB deep |
| Flanger comb | Depth 0, Mix 50 %, no feedback: notches at (k + ½)/τ within 0.5 % (or 4 Hz), up to 10 kHz, at 0.37, 1 and 2.5 ms; feedback ±70 %: the peaks at k/τ (+) or (k + ½)/τ (−) within 0.5 % | worst 0.40 % (the lowest notch about 3 Hz higher: the input high-pass); peaks within 0.02 % (+70 %) and 0.30 % (−70 %) |
| Response = design | the engine's impulse response (DTFT, 160 frequencies, 20 Hz – 16 kHz) against `EnsembleDesign`: chorus voices, flanger and phaser with feedback both ways, Tone low | within 0.0003 dB |
| Chorus depth | one voice, wet only, a 1 kHz sine: the largest pitch deviation up and down, in cents, within 3 % (or 0.3 cent) of f · (1 − τ′(t)) from the setting | 0.5 Hz, 20 %: +5.34 / −5.36 (expected +5.33 / −5.35); 0.5 Hz, 60 %: +17.12 / −17.29 (+17.11 / −17.28); 2 Hz, 30 %: +32.19 / −32.80 (the same); triangle, 1 Hz, 50 %: +23.57 / −23.89 (+23.58 / −23.90) |
| Voices, spread | four voices' delays a quarter cycle apart; Spread 0°: both channels identical; 180°: the right channel's delay mirrors the left's about Delay; the wet's correlation for pink noise falls as Spread opens | within 1e-5 (octaves); correlation 1.000, 0.189 and 0.159 at 0°, 90° and 180° (two voices: 0.390, 0.342) |
| Tempo sync | 1/4 at 120 BPM is 2 Hz, at 90 BPM 1.5 Hz; 2 bars at 120 BPM 0.25 Hz; 1/8T at 150 BPM 7.5 Hz | the LFO's phase within 1e-6 cycle |
| Loop gain | below 1 at every frequency (258), at 12 points of the LFO's cycle, both channels, over Mode × Delay or Centre × Depth × Voices or Stages × Feedback × Tone (864 settings, ends included), and never above \|Feedback\|; each factor at most 1 | at most 0.990000; above \|Feedback\| by 1e-8 at most (0.99 in float) |
| Extremes | each parameter alone at each end (Feedback at +99 % unless it is the parameter), then in each mode ±99 % with full depth at the fastest and slowest LFO and the shortest and longest delay (lowest and highest Centre), every voice and stage, and every parameter at each end at once: 30 s of pink noise above 30 Hz at −20 dBFS RMS, then 10 s of silence: finite, under +18 dBFS before Output's gain (the guard trips at +60), the gain within 1.5 dB from the second 10 s to the third (LFOs of 5 s or less) and the tail decaying at least half as fast as the bound | passes; peaks up to +10.1 dBFS (Chorus, +99 %, 0.1–0.4 ms, four voices: the saturator holds it; every parameter at its maximum +6.2 dBFS before Output's +12 dB); the gain moves 1.3 dB at most; the slowest tail −13.4 dB in 7 s (Flanger, +99 %, 7.5–120 ms swept at 20 Hz; the bound asks 2.5) |
| Mix 0 % | output = input bit for bit in every mode at full feedback, depth and rate (negative zeros, subnormals) | bit for bit; latency 0 |
| Blocks | blocks of 256 and of 117 samples give the same output | identical |
| Silence after noise | Feedback 90 %, 2 s of noise then 20 s of silence: no subnormal number in the lines, filters or stages; silence costs no more than noise | none; 0.75–0.96× |
| Smoothing | every parameter jumping between its ends every 1024 samples, on a 220 Hz sine, in each mode: the largest step between samples within 1.5× the steady signal's at either end | at most 1.13× (Rate, the pitch it bends) |
| Presets | each on pink noise at −18 dBFS RMS: within ±6 dB of the input, peaks under +3 dBFS | −3.9 … +0.2 dB; peaks −9.6 … −5.3 dBFS |
| Audio thread | a Neo Poly pattern playing through it in the studio (a flanger with feedback, its LFO synced, its depth moved every buffer), each work algorithm | 0 bytes per buffer |
| CPU | stereo at 48 kHz: ≥ 50× real time on the CI runner | four chorus voices 161×, flanger 342×, twelve phaser stages 227× |

As built:

- **Not built: through-zero flanging.** It needs the dry signal delayed by the sweep's longest delay, so the wet can pass it: latency, against zero latency and a dry signal untouched at Mix 0 %. "Flanger — Tape Style (Negative)" sweeps the delay close to zero (0.2 ms) with negative feedback instead.
- The phaser's stages were TPT allpasses at first; the extremes test found the pumping above, and they became lattices (the same response). The delay loop read with the cubic at first (its taps sum to up to 1.25 in magnitude, so the convexity argument didn't hold); the loop now reads with straight lines and the wet with the cubic. The loop's high-pass moved to the line's input, where it doesn't break the bound.
- The Linux profile (`docs/machine-health.md`): hot only with Output at +12 dB (+1.6 dBFS) and with every parameter at its maximum at once (+18.5 dBFS, Output +12 dB included); no parameter at either end diverges or fails to decay, so no safe range.
- Parameter roles for `machine_replace` (U5) and how the closed machines map onto it: machine-safety.md §4.

## 6. Phases

| Phase | Content |
|---|---|
| M0 | Spec, core fix (§3.1), DSP library, editor kit, parameter generator, Linux test project |
| M1 | Neo Drive, Neo Comp, Neo Verb |
| M2 | Neo Poly, Neo Wave, Neo FM6 |
| M3 (MCP A4) | Neo EQ, Neo Utility, Neo Limiter, Neo Analyzer |
| Successors (machine-safety.md §4) | Neo Ensemble; Neo EQ's graphic mode |

Each phase: pure tests (Linux CI), integration tests in ReBuzzTests (the machine loads, makes sound, its parameters and editor work, editor screenshots), then the package build.

M0–M3 are done, and of the successors Neo Ensemble and Neo EQ's graphic mode. Every machine also ships factory presets (12–18 each), checked by a test: each preset sounds, at a consistent level (synths: loudest 50 ms of a chord between −30 and −6 dB RMS, peak below −1 dBFS).

## 7. Not in this batch

Additive and physical-modelling synths, convolution, tape delay, multiband: candidates for the next batch (ensemble and phaser: Neo Ensemble, built).

The MCP server spec (docs/specs/mcp-server.md §8) sets the priorities by what an agent needs to mix and master: Neo EQ, Neo Limiter (true peak), Neo Utility and Neo Analyzer are built (M3); next Neo Delay, Neo Multiband, Neo Scope and Neo Sampler. Every Neo machine also publishes a manifest (its parameter table as JSON) so agents set values in real units.
