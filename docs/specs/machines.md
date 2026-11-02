# Neo machines — Specification

| | |
|---|---|
| Status | v1.2 — core six (M0–M2) and the mix and master batch (M3: Neo EQ, Neo Utility, Neo Limiter, Neo Analyzer) built: measured values below are from Linux CI, 2026-11-01 and 2026-11-02 |
| Date | 2026-11-02 |
| Scope | New high-quality machines for sound design, mixing and mastering, written in C# with custom editors: first batch "core six", second batch "mix and master" |

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
  NeoDrive/ NeoComp/ NeoVerb/ NeoPoly/ NeoWave/ NeoFm6/ NeoEq/ NeoUtility/ NeoLimiter/ NeoAnalyzer/
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
| Smoothing | one-pole and linear-ramp parameter smoothers (the ramp lands exactly on its target, for gains that must end bit-exact) |
| Oscillators | band-limited step/ramp tables (BLEP/BLAMP): saw, pulse with PWM, triangle, hard sync, through-zero FM |
| Wavetables | frames with per-octave band-limited mipmaps built by FFT; morphing reads |
| Filters | zero-delay-feedback SVF (LP/BP/HP/notch/peak/allpass/shelves), nonlinear ZDF ladder (LP24/LP12), one-pole, RBJ biquads, DC blocker; (M3) a double-precision TPT section for equalisers and crossovers (`SvfSection`, with its exact magnitude response) and Butterworth cascades (12/24/48 dB/oct) |
| Resampling | linear-phase FIR halfband (2×/4×/8×, fixed latency), polyphase IIR halfband (synths) |
| Modulation | analog-style ADSR, DX7 rate/level envelope, LFO (7 shapes, tempo sync, fade-in), 8-slot mod matrix |
| Voices | allocator: poly with stealing (released first, then oldest; stolen voices fade), mono/legato with note stack, glide, unison |
| Delay | fractional delay lines (cubic/allpass), modulated allpass diffusers |
| Analysis | peak/RMS meters for the editors; (A4) a lock-free triple buffer that hands whole snapshots from the audio thread to readers (`TripleBuffer`) |

**Editor kit (`NeoBuzz.Editor`)**: dark theme with one accent per machine; rotary knob (drag, Shift for fine, wheel, double-click for default, value shown while dragging); segmented switch; section frame; level and gain-reduction meters; graph surface for curves, envelopes, waves and algorithms; (M3) a frequency-response graph with draggable band handles (toolkit-free axes, hit-testing and drags in `Core/ResponseGraph.cs`, the pointer routed by `InteractiveGraph`); (A4) analysis graphs: a spectrum with third-octave bars, held peaks and a viewing tilt, loudness bars and readouts coloured against a target, a goniometer and a correlation bar (`Core/AnalysisGraphs.cs`); preset bar (ReBuzz presets: browse, previous/next, save). Controls bind to `IParameter` and follow automation.

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

Later: analog-matched (decramped) designs, so a bell or shelf above a few kHz keeps its analog shape up to Nyquist. The bilinear transform squeezes it: against the analog prototype, RBJ sections stay within 0.2 dB up to 16 kHz for bands below about 1 kHz, but a wide bell at 8 kHz differs by about 3 dB near 16 kHz. (Prototypes of a 5-point magnitude match and of Orfanidis' prescribed-Nyquist bell were 3–10× closer for bells but not robust near Nyquist, so they wait.) Also later: linear-phase mode, dynamic bands, mid/side bands.

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

## 6. Phases

| Phase | Content |
|---|---|
| M0 | Spec, core fix (§3.1), DSP library, editor kit, parameter generator, Linux test project |
| M1 | Neo Drive, Neo Comp, Neo Verb |
| M2 | Neo Poly, Neo Wave, Neo FM6 |
| M3 (MCP A4) | Neo EQ, Neo Utility, Neo Limiter, Neo Analyzer |

Each phase: pure tests (Linux CI), integration tests in ReBuzzTests (the machine loads, makes sound, its parameters and editor work, editor screenshots), then the package build.

M0–M3 are done. Every machine also ships factory presets (12–18 each), checked by a test: each preset sounds, at a consistent level (synths: loudest 50 ms of a chord between −30 and −6 dB RMS, peak below −1 dBFS).

## 7. Not in this batch

Additive and physical-modelling synths, convolution, tape delay, ensemble/phaser, multiband: candidates for the next batch.

The MCP server spec (docs/specs/mcp-server.md §8) sets the priorities by what an agent needs to mix and master: Neo EQ, Neo Limiter (true peak), Neo Utility and Neo Analyzer are built (M3); next Neo Delay, Neo Multiband, Neo Scope and Neo Sampler. Every Neo machine also publishes a manifest (its parameter table as JSON) so agents set values in real units.
