# Mixing and mastering tools — Specification

| | |
|---|---|
| Status | v0.5 — M0 (engine and library prerequisites) built on `machines/mastering-m0`, 2026-11-06; M1 (the assistant's measurements and tools, Neo Utility's matrix, the Delta and gain-match helpers) built on `machines/mastering-m1`, 2026-11-06; M2 (Neo Clipper, Neo Limiter v2, the editor's Match) built on `machines/mastering-m2`, 2026-11-06; M3 (Neo EQ v2; Neo Clipper's and Neo Limiter v2's CPU targets) built on `machines/mastering-m3`, 2026-11-06; M4 next |
| Date | 2026-11-06 |
| Scope | Mixing and mastering machines after iZotope Ozone 12 (and the best of FabFilter, Neutron, soothe and others), the engine work they need, and an assistant that is our agent plus deterministic measurements |
| Design basis | The analysis "Mixing and mastering machines for NeoBuzz Live, after iZotope Ozone" (2026-11-06): today's inventory (machines.md §5), the Ozone 12.2 module inventory, proposal cards with techniques and measurements, effort and risks. Its findings are summarised here where they decide something |

This file is how a fresh agent picks the work up: the rules every phase follows (§2), the phases with their branches and exit criteria (§3), the engine and library design of M0 (§4), a card per machine with its measured targets (§5), the agent's tools and guides (§6), and what was built, phase by phase (§8). Keep it current: update §8 and the status line at the end of every phase.

---

## 1. Summary

What we have already covers Ozone's core chain: Neo EQ (parametric and 31-band graphic, minimum phase), Neo Comp, Neo Multiband (linear or minimum phase, upward expansion), Neo Drive, Neo Utility and a true-peak Neo Limiter (+0.002 dB worst overshoot), with Neo Analyzer and the agent's `analyze`. What is missing, in order of value for a tracker musician: a clipper and the maximizer's extras (soft clip, transient emphasis, upward compression); dynamic and mid/side EQ bands; transient shaping and low-end control; linear-phase and match EQ; a multiband imager; an exciter, tape and dither; and last, a spectral resonance suppressor.

Three engine limits come first, because every machine with latency is wrong without them: ReBuzz's machine delay compensation is off by default; a latency change reallocates and resets the compensation buffers (a dropout); and a bypassed machine counts as having no latency, so bypassing a 96 ms machine moves the mix by 96 ms. M0 fixes all three and moves Neo Multiband's audio-thread FFT and partitioned convolution into the shared library, with the new blocks the later machines share (STFT, transient split, 90° allpass pair).

Out of scope, by the rules and by need: Ozone's machine-learning modules (Stem EQ, Unlimiter, Master Rebalance, Stem Focus, Assistive Vocal Balance). A NeoBuzz Live song has its stems; the agent rebalances real tracks. Nothing is called "AI": the assistant is our agent plus measurements it can explain.

## 2. Rules for every machine and change

These are the repo's rules (machines.md §2–§4, mcp-server.md §12) as they apply here; a phase isn't done until each holds.

| Rule | What it means here |
|---|---|
| Same bits everywhere | `PortableMath`/`PortableMathF` only in audio paths and in coefficients computed while playing; fixed-order sums (explicit lanes); no `FusedMultiplyAdd`; the null test (`tools/null-test.py`) bit-identical for every existing case, and new cases for every new machine |
| Audio thread | No allocation after construction (one per sample rate is fine); long loops `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`; checked with `AllocationCheck` and `DOTNET_JitDisasmSummary=1` over the machine's renders |
| Songs keep their sound | Existing machines' parameter order, ranges and saved data never change; new parameters only at the end, with defaults that keep the old behaviour, so old songs render bit for bit unless a change is a deliberate, reported fix |
| Measured | A measurement suite per machine with stated targets (§5): the analysis's R=D, Alias, TP, Zip, Blk, Mix0, Lat, Sil, Alloc, CPU and Pre, plus the machine's own; measured values written into machines.md §5 when built. Where a measurement is only a proxy (transparency at high loudness), listening notes on a fixed corpus as well |
| One source | Editors from one source file for WPF and Avalonia (the Neo editor kit); manifests generated from `Params.def` with real units; factory presets in units (`Presets.txt`); an agent guide per machine; null-test cases |
| Successors | Where a classic machine has a Neo successor, a `machine_replace` pair in real units with its stated tolerance (mcp-server.md §12.10): Oomek Exciter → Neo Exciter, Pedal Gate → Neo Gate |
| Dependencies | Free/MIT-compatible only (Concentus, BSD, for the Opus preview); no ML models |
| Names | The app's name in visible text from `ReBuzz.Agent/Product.cs` ("NeoBuzz Live") |

House conventions for the new mixing and mastering machines (decided in M1, used from M2 on):

- **Delta.** A `Delta` switch (Off/On) among the last parameters of each dynamics and EQ machine: the output becomes wet − dry (the dry delayed by the machine's latency), so the user hears what the machine changes (Ozone's Delta buttons). Off by default. Built in M1 as `NeoBuzz.Dsp.DeltaListen`: the dry through a ring every block (on or off, so a switch has it at once), the switch gliding over 10 ms, off exactly the wet signal. As built in M2 it is the output minus the input (at a Mix under 100 %, the change that mix makes), before Output; Neo Limiter's is at the input's level (÷ its input gain), so it isn't 24 dB louder than what went in.
- **Gain match.** A reading `gain_match_db` (short-term loudness out − in, K-weighted, over 3 s) on each of those machines, and in the editor a "Match" action that sets `Output` by it: one undoable parameter change, never automatic gain riding. Built in M1 as `NeoBuzz.Analysis.GainMatch` (the K-weighting of `Loudness`, 30 steps of 100 ms, unknown until 3 s have passed and while either side is under −70 LUFS; `GainMatch.Match` gives the Output setting on its 0.1 dB steps); the editor's Match action came with Neo Clipper (M2). Neo Limiter has the reading and Unity listen instead of an Output to match.
- **Zero latency available.** Every machine with latency has a zero-latency mode or setting (live Session View play and monitoring), and reports its latency exactly; a latency that depends on a setting changes only through the engine's crossfade (§4.1).
- **Transient/Sustain.** Where a machine offers Ozone's Transient/Sustain channel modes, they come from the shared `TransientSplit` (§4.2), so they mean the same everywhere.

## 3. Phases

Each phase is its own branch from `origin/main` (`machines/mastering-m0`, `-m1`, …), green on Fork CI, Fork Mac and a package run on its final head (which merges the latest `origin/main`), reported to the lead, who merges; the next phase starts from the merged main. The build order follows the analysis (§5.2 there): infrastructure first, then what makes the agent a better assistant cheaply, then loudness, EQ, drums, and the riskiest last.

| Phase | Branch | Delivers | Done when |
|---|---|---|---|
| **M0** | `machines/mastering-m0` | Engine: delay compensation on by default; glitch-free, deterministic latency changes; latency-preserving bypass. Library: Neo Multiband's audio-thread FFT, partitioned convolver and stepped FIR design in NeoBuzz.Dsp (Neo Multiband bit for bit); an STFT/WOLA block; `TransientSplit`; a 90° allpass pair | §4's tests pass (a parallel path around a latent machine nulls; latency changes and bypass don't gap, click or move the alignment; renders with latency changes deterministic); null test bit-identical; Multiband's suite unchanged |
| **M1** | `machines/mastering-m1` | Neo Utility mid/side matrix modes; the Delta and gain-match convention; analysis metrics (per-band microdynamics, mid/side ratio in 4 bands, long-term fine-resolution curve against a target, platform normalisation preview); agent tools `eq_fit` and `target_build`; the upgraded mastering guide flow (§6) | Utility's matrix round trip bit for bit; the metrics against constructed signals; `eq_fit` fits known curves within its stated error; the A6 master task still passes, plus the new eval briefs scored |
| **M2** | `machines/mastering-m2` | Neo Clipper; then Neo Limiter v2 (soft clip, transient emphasis, upward compression, two-stage release, unity-gain listen) with v1's behaviour kept as a style and old songs unchanged | §5.1 and §5.2 targets; Limiter v1 renders bit for bit at the new parameters' defaults |
| **M3** | `machines/mastering-m3` | Neo EQ v2: per-band Stereo/L/R/M/S, Baxandall, Pultec-style and proportional-Q shapes, all-pass, dynamic bands with an external sidechain | §5.3; the parametric and graphic modes' existing renders bit for bit |
| **M4** | `machines/mastering-m4` | Neo Transient (with a multiband Impact mode); Neo Low End | §5.4, §5.5 |
| **M5** | `machines/mastering-m5` | Neo EQ linear-phase mode and match-curve layer; Neo Imager; Neo Exciter (successor of Oomek Exciter); Neo Dither and a bit-depth option for renders; Neo Strip | §5.6–§5.10 |
| **M6** | `machines/mastering-m6` | Neo Tape; Neo De-esser; Neo Gate (successor of Pedal Gate, sharing the de-esser's detector); Neo Multiband extensions (dynamic phase, lookahead, upward compression, external sidechain); the Opus preview (Concentus) | §5.11–§5.15 |
| **M7** | `machines/mastering-m7` | Neo Spectral (short-time FFT resonance suppressor), only after a prototype's measurements show it pays (artefacts and CPU, §5.16) | §5.16 |

Not planned unless measurements call for them: Neo Limiter's multiband (IRC 5-like) mode; multi-output routing and a band splitter (Neo Split/Merge); Neo Comp's vari-mu mode; a Neo Align (phase alignment of two inputs). Each is in the analysis with its cost.

## 4. M0: engine and library prerequisites

### 4.1 Delay compensation

**Today** (origin/main, 2026-11-06). `ReBuzzCore.UpdateMachineDelayCompensation` walks the graph from Master and gives each input connection the delay that lines it up with the latest input of its machine (`MachineConnectionCore.UpdateLatencyBuffers`). It is off by default (`EngineSettings.MachineDelayCompensation`, `[BuzzSetting(false)]`). Every recompute, whatever triggered it (a connection anywhere, a bypass, any latency change), resets every compensated connection's ring positions, and a changed delay allocates a new ring under the audio lock: the audio in flight is lost (a gap, then stale samples). A machine's own latency change, polled on the audio thread at its tick, is applied on the UI thread through the dispatcher: when it lands depends on the UI thread, so a render where a latency changes (Neo Limiter's lookahead automated) isn't deterministic once compensation is on. A bypassed machine counts as latency 0.

**M0.**

| Item | Design |
|---|---|
| On by default | `MachineDelayCompensation` defaults to true. Settings store only values a user changed (`Settings.Save` writes modified settings), so installs that never touched it get it on; a stored choice (on or off) is kept. Songs don't store it. Old songs render as before unless a parallel path (sends, a dry/wet split, a sidechain) holds a machine with latency: those now play aligned, which is the fix. The null test's cases are serial chains, so they stay bit-identical |
| A latency plan | Built on the UI thread (under the audio lock) whenever the graph, a bypass, an override or the setting changes: the machines that reach Master in topological order with their input connections, as arrays. Applying it (each machine's arrival time = the latest of its inputs' + its own latency; each input's delay = that latest − its own) allocates nothing, so it also runs on the audio thread |
| Deterministic latency changes | A machine whose latency changes (polled at its tick, as today) marks the plan dirty on the audio thread; the plan is applied at once, before the block's work. The same samples every run, whatever the UI thread is doing. The UI's `Latency` property follows through the UI event queue (no allocation on the audio thread) |
| Delay rings | Each compensated connection keeps a ring (`LatencyRing`) sized to a power of two above its delay plus a block, grown off the audio thread (the UI thread allocates and swaps under the lock, keeping what's in flight; the audio thread asks through the UI event queue when a delay outgrows it, and meanwhile uses what fits). A ring is touched only when its delay changes |
| Glitch-free changes | A changed delay crossfades from the old tap to the new over 10 ms (raised cosine): a short comb, never a gap or a click. The new tap needs history: the ring starts writing when a change is asked for, and the crossfade starts once it holds the new delay's worth. When everything in flight on that connection is silent (a new connection, a silent path), the change is instant and exact. With the delay at 0 and nothing pending, the connection keeps today's direct path (bit for bit) |
| Latency-preserving bypass | A bypassed machine keeps its latency in the plan. The engine delays its dry input by that latency (a `LatencyRing` per machine), so bypass doesn't move the mix. A bypassed effect with latency keeps working (its output discarded), so its state is current when it comes back; switching crossfades wet and delayed dry over 5 ms. A bypassed effect without latency is skipped as before (no work, instant switch) |
| Not changed | Automation on a machine with latency is heard early by the path's latency (machines.md, Neo Multiband); the Session View's heard time counts the device's output latency, not the machines' (a latent master chain is heard later than launches assume; both pre-existing, listed in §7) |

**Tests** (Linux and macOS: `ReBuzz.SessionTests` for the pure ring and plan, `ReBuzz.Studio.Tests` for the engine with Neo machines; Windows: `ReBuzzTests`).

| Criterion | Target |
|---|---|
| Parallel path | a source split into Neo Multiband (linear phase, unity) and Neo Utility (inverted), both into Master, compensation at its default: the two cancel (≤ −240 dB, the meter's floor); with compensation off: they don't (the comb is measured) |
| Latency change | the same graph with Neo Limiter (lookahead 0.5 → 5 ms, automated) in place of the multiband, on a sine: no sample-to-sample step larger than the sine's own (×1.2) anywhere, the null restored within 10 ms + the new delay of the change |
| Ring | a delay change on a running signal: the output is the old tap, a raised-cosine blend, then the new tap, exactly; a silent connection switches at once; growing the ring keeps what's in flight bit for bit |
| Plan | diamonds, chains and fan-outs: the delays equal the recursive walk's; applying it allocates nothing |
| Determinism | a latency change reaches the delays on the audio thread before the block's work, with the UI thread not run (so a render doesn't depend on it); renders start from a plan rebuilt for the graph as it is |
| Bypass | bypassing the latent machine in the parallel graph: the null holds (the dry path is delayed by its latency); bypass and un-bypass while playing: no step larger than the signal's own; a bypassed machine without latency renders as before, bit for bit |
| Unchanged | the null test bit-identical; ReBuzzTests' existing latency tests pass |

### 4.2 Shared DSP blocks (NeoBuzz.Dsp)

| Block | Design | Tests |
|---|---|---|
| `ComplexFft` (moved) | Neo Multiband's single-precision radix-2 FFT for the audio thread (twiddles from the portable maths, element-wise vectors, `TransformPart` for transforms spread over blocks), moved verbatim from `Machines/NeoMultiband/Engine/Fft.cs` into the shared library | Multiband's FFT tests, unchanged |
| `PartitionedConvolver` | Uniformly partitioned overlap-save convolution of stereo audio (left + i·right in one complex FFT of 2P points) with F real FIRs on a frequency-domain delay line of K partitions; two sets of partition spectra (playing, next) for crossfaded switches; outer partitions under 1e-9 of a FIR's peak skipped. Moved from `LinearSplit` | Neo Multiband bit for bit (its suite and the null test's five cases) |
| `SteppedFirDesign` | Linear-phase FIRs designed from zero-phase magnitudes on a grid of 2N points (two FIRs per transform), made symmetric and cut at ±D, then each partition's spectrum, in steps of bounded work taken at fixed samples, so no block carries a whole design and the switch falls on the same sample however the host splits the audio. Moved from `LinearSplit`; the magnitudes come from an interface (Multiband's crossovers now, Neo EQ's curve in M5) | Multiband's stepped-design tests, unchanged |
| `Stft` | Short-time Fourier transform with weighted overlap-add: frame N (a power of two), hop N/4, √Hann analysis and synthesis windows (periodic Hann, so their product overlap-adds to a constant), stereo packed into one complex FFT; a spectral callback per hop; hops on fixed samples of an internal ring, independent of host blocks; latency N − 1, reported | Unit callback: output = input delayed by N − 1 within −120 dB (null); blocks of 256/128/117/16 identical; no allocation; portable maths (the same bits) |
| `TransientSplit` | Two peak followers on the linked level: fast (attack 0.1–1 ms, release 10–30 ms) and slow (attack 10–50 ms, release 100–300 ms). Transient weight t = 1 − slow/fast while fast > slow, else 0; sustain s = 1 − t. Consumers apply g = 1 + (a − 1)·t + (b − 1)·s, so a = b = 1 is exactly unity (bit for bit) | Unity bit for bit; a burst's first 5 ms weighted ≥ 0.8, a steady sine's weight 0 within 0.01 after 100 ms; blocks; allocation |
| `QuadratureAllpass` | A 90° phase-difference pair: two cascades of second-order allpass sections in z⁻², from the polyphase halfband design already in `Halfband.cs` (after L. de Soras' HIIR), frequency-shifted by a quarter of the rate; its band from a transition width | Phase difference within ±0.5° from 20 Hz to 20 kHz at 44.1, 48 and 96 kHz (the order the design needs, reported); magnitude 1 within 1e-6; deterministic |

## 5. Machine cards and targets

Tests at 48 kHz unless noted; the abbreviations are the analysis's measurement suite: **R=D** response = design (impulse response, DTFT at 160 frequencies); **Alias** (coherent 65,536-point FFT); **TP** true-peak overshoot (`Loudness.TruePeak`); **Zip** zipper energy above 2 kHz on a 220 Hz sine, parameters stepping per block (≤ −90 dB); **Blk** blocks of 256/128/117/16 identical; **Mix0** neutral settings bit for bit; **Lat** latency reported = measured, and nulls against a parallel inverted path in the engine; **Sil** no subnormals, idle after the tail, NaN leaves no state; **Alloc** 0 bytes per block; **CPU** real-time factor on the CI runner; **Pre** factory presets within ±6 dB, peaks < +3 dBFS.

### 5.1 Neo Clipper (M2)

Hard, soft (cubic, tanh) and polynomial-knee clipping into a ceiling in dBFS; oversampling 1/2/4/8× (`FirOversampler`) with first-order ADAA; up to 3 bands on the zero-latency Linkwitz–Riley split; Stereo/M/S; Mix; constant latency. Neutron 5's clipper is the reference for features.

| Criterion | Target |
|---|---|
| Ceiling | hard mode: every sample ≤ ceiling exactly; soft and knee modes: ≤ ceiling |
| Alias | 3.1 kHz at +12 dB into the knee, 4×: ≤ −90 dB; each factor reported |
| TP | the true-peak overshoot reported (a clipper doesn't promise it; the guide puts a true-peak limiter after it) |
| THD | vs knee, reported; below the knee −100 dB THD+N |
| Mix0, Blk, Lat, Alloc | as above; CPU ≥ 100× |

### 5.2 Neo Limiter v2 (M2)

New stages, in signal order, as parameters after the existing ones: upward compression (a slow RMS detector, ≤ 6 dB below the threshold), transient emphasis (`TransientSplit` before the limiter), soft clip (4× `FirOversampler` + ADAA, knee 3/9/30 dB under the ceiling, wet/dry, constant latency; the limiter's guarantee still holds after it), a two-stage release (the existing auto release plus a slow stage that tracks sustained reduction), transient/sustain unlink, and Unity listen (the output trimmed by the gain reduction, for judging what limiting does at equal loudness). `Style` presets of the stages; "Classic" (the default) is v1, so old songs render bit for bit.

| Criterion | Target |
|---|---|
| TP | existing cases plus soft clip at each knee, 44.1/48/96 kHz: worst ≤ ceiling + 0.1 dB |
| Alias | the clip stage ≤ −90 dB (3.1 kHz at +12 dB into the knee) |
| IMD | SMPTE 60 Hz + 7 kHz (4:1) at 3, 6 and 9 dB of reduction, each style against v1: reported, and no style worse than v1 at the same reduction unless it says so |
| Pumping | AM sidebands of a steady 1 kHz tone while a 50 Hz kick drives 6 dB of reduction: reported per style |
| Loudness at a distortion budget | integrated LUFS reached at a fixed IMD per style: a regression table |
| Classic | v1's renders bit for bit (the defaults of the new parameters) |
| Listening | ABX notes on a fixed corpus at −9 and −7 LUFS (transparency at high loudness has no sufficient measurement) |
| Blk, Alloc | as above; CPU ≥ 50× |

### 5.3 Neo EQ v2 (M3)

Per-band channel: Stereo, L, R, M, S (M and S chains run only when a band uses them). New shapes: Baxandall shelves, a Pultec-style "Vintage" shelf (a shelf with its complementary dip), proportional Q (Q grows with |gain|), all-pass. Dynamic bands: a detector on the band's own shape (internal, or input 2 as the sidechain), threshold, ratio, knee, range, attack and release through `Dynamics.cs`, up or down; the gain moves on Neo Filter's 16-sample grid with per-sample coefficient ramps. Minimum phase, zero latency.

| Criterion | Target |
|---|---|
| R=D | at rest per channel mode within ±0.01 dB, M/S included |
| M/S | an S-only band leaves L+R bit for bit |
| Dynamics | static curve per dynamic band vs `Dynamics` within 0.25 dB; attack and release 63 % within ±10 % |
| Zip | with the dynamic gain moving: ≤ −90 dB |
| Unchanged | the parametric and graphic modes' existing renders bit for bit |
| Blk, Alloc, sidechain from input 2 | as above; CPU 8 dynamic bands ≥ 30× |

### 5.4 Neo Transient (M4)

`TransientSplit` with Attack and Sustain gains (±12 dB), detector speed (Punchy, Balanced, Smooth = the followers' time constants), optional lookahead (latency reported), Stereo/M/S, Mix, Delta. Impact mode: 3–4 Linkwitz–Riley bands, per band the microdynamics ratio (a short/long envelope ratio, after Ozone's LDR) moved by one bipolar knob, auto gain per band.

| Criterion | Target |
|---|---|
| Attack | a synthetic drum (exponential bursts): Attack +X dB raises the first 5 ms peak by X ± 0.5 dB |
| Steady | a steady sine within 0.01 dB |
| Unity | Attack = Sustain = 0 dB: bit for bit |
| Zip, Blk, Alloc, CPU | as above |

### 5.5 Neo Low End (M4)

A low band (20–300 Hz, Linkwitz–Riley 24, minimum phase or linear) through `TransientSplit`: Punch, Sustain, Contrast (gain from the deviation from the band's slow envelope: keep the strongest and pull the rest, or the reverse; after Low End Focus), Balance (the low band against the mid band toward a ratio), Peak control (a soft clipper on the low band only), mono below a frequency.

| Criterion | Target |
|---|---|
| Split | the bands sum flat (Multiband's test) |
| Isolation | only < 300 Hz changes (above it untouched within 1e-6 in the linear option) |
| Punch | on a kick sample: the peak change as set ± 0.5 dB; the low band's crest change reported |
| Blk, Alloc, CPU | as above |

### 5.6 Neo EQ linear phase and match curve (M5)

The whole curve's zero-phase magnitude (from `EqDesign`) designed into one FIR by `SteppedFirDesign` and run by `PartitionedConvolver`; latency fixed per sample rate (D = 4095 + P at 44.1/48 kHz), reported; redesigned in steps with a crossfade. A "curve" layer: an arbitrary smoothed dB curve (from `eq_fit` or a reference capture) added to the magnitude before the design. Low narrow bells ring longer than the FIR (a bell's time constant is about Q/(π·f)): Q·(1/f) is capped in linear mode and the editor says so, or a hybrid (minimum phase below about 100 Hz). Phase blend per band waits.

| Criterion | Target |
|---|---|
| R=D | within 0.05 dB from 30 Hz; the magnitude = the minimum-phase mode's within ±0.05 dB |
| Phase | group delay constant (±0.5 sample) wherever the magnitude is above −60 dB; pre-ringing energy before an impulse reported per setting |
| Lat | engine null −240 dB |
| Blk, Alloc while redesigning | as above; CPU ≥ 100× |

### 5.7 Neo Imager (M5)

Four bands on `MinimumSplit` (zero latency) or `LinearSplit`; width per band 0–200 %; Stereoize (a decorrelated mid added to the side: L+R unchanged, mono-safe by construction); Recover Sides (the side removed by narrowing returned to the mid through `QuadratureAllpass`, so it doesn't comb in mono); per-band correlation and width readings.

| Criterion | Target |
|---|---|
| Width | S/M ratio per band within 0.01 dB of the setting |
| Stereoize | L+R bit for bit against the input's, delayed by the split's latency |
| Recover Sides | mono energy within 0.5 dB of the input's when narrowing to 0 % |
| Readings | correlation vs `Stereo.Measures` |
| Blk, CPU | as above |

### 5.8 Neo Exciter (M5; successor of Oomek Exciter)

Up to 4 Linkwitz–Riley bands, per band an oversampled ADAA shaper from Neo Drive's `Shapers.cs` (tape, tube, transistor, diode, and "warm": even harmonics only), Amount and Mix per band, a post high shelf, auto gain, Stereo/M/S. The `machine_replace` pair from Oomek Exciter in real units.

| Criterion | Target |
|---|---|
| Alias | ≤ −90 dB at 4× |
| Harmonics | profile per mode (even/odd ratio) reported; Warm: odd harmonics ≥ 40 dB under the even |
| Isolation | a band at Amount 0 leaves the input within 1e-6 |
| Mix0, Lat (constant), Pre, successor tolerance | as above |

### 5.9 Neo Dither and render bit depth (M5)

TPDF ±1 LSB from two xorshift uniforms seeded per render (renders stay bit-identical); error-feedback noise shaping with fixed FIR shapes per sample rate (off, light, psychoacoustic); auto-blank after 0.7 s of digital silence; output exactly on the target grid. `render(bits=16|24, dither=…)` for the agent, and a dither option in the HD recorder (libsndfile's int16 scaling verified first).

| Criterion | Target |
|---|---|
| Linearity | a −90 dBFS 1 kHz sine at 16 bits: no harmonics above the noise |
| Noise | TPDF power = LSB²/4 (±0.2 dB); shaped spectrum = design (±1 dB) |
| Grid | every output sample on the grid |
| Blk | as above |

### 5.10 Neo Strip (M5)

Gate → EQ (4 bands + cuts) → compressor → drive → limiter/clip, from the existing engines linked as source, one editor, presets, Delta: one machine per track, the agent's default for a channel.

### 5.11 Neo Tape (M6)

Record pre-emphasis → saturation (Neo Drive's tape curve, ADAA, oversampled) with bias → playback de-emphasis → head bump (a resonant low shelf whose frequency scales with speed: 7.5/15/30 ips) and HF loss by speed → an even-harmonic electronics stage → optional noise → wow and flutter (Neo Delay's modulated read).

| Criterion | Target |
|---|---|
| R=D | small-signal response per speed ±0.1 dB |
| THD | vs level, reported; Alias ≤ −90 dB |
| Wow | depth in cents vs the setting |
| Mix0 | as above |

### 5.12 Neo De-esser (M6)

Detector: the high band's level relative to the full band (vocal) or absolute (all-round), a split band (minimum or linear) or wide band reduction as a dynamic shelf or bell, lookahead (latency reported), Listen.

| Criterion | Target |
|---|---|
| Reduction | vs design within 0.5 dB; nothing below the threshold |
| Blk, Lat | as above |

### 5.13 Neo Gate (M6; successor of Pedal Gate)

Hysteresis, hold, range, lookahead, key filter, external sidechain, downward expansion ratio; the `machine_replace` pair from Pedal Gate.

### 5.14 Neo Multiband extensions (M6)

New parameters at the end: lookahead (latency added, reported), an RMS detector, upward compression (a negative slope below the threshold, in `Dynamics.cs`), an external sidechain per band, and a "Dynamic" phase mode: y = x + Σ (g_k − 1)·band_k (minimum phase bands, the raw input as the dry: no static phase shift and no latency; our analogue of Pro-MB's dynamic phase, not its method). Existing renders bit for bit at the defaults.

### 5.15 Opus preview (M6)

Concentus (pure C#, BSD) encodes and decodes a render at 64–256 kb/s; `analyze` measures the decoded audio (true peak, loss above the codec's cutoff, the null residual's loudness). A proxy for lossy coding in general (the platforms use Vorbis and AAC), as a `render`/`analyze` option rather than a real-time machine.

### 5.16 Neo Spectral (M7, gated)

STFT (`Stft`: N = 2048 at 44.1/48 kHz, 4096 at 88.2/96, hop N/4): per bin level against a spectral envelope smoothed in log frequency, excess above a threshold reduced with depth, sharpness and selectivity (soothe2's controls), attack and release per bin (faster at high frequency), the mask smoothed across frequency. Modes: Suppress, Balance (toward pink ± tilt above 300 Hz), Stabilize (toward a target curve), Cut-only; action region, M/S or L/R with link, sidechain, Mix, Delta. Two ways of applying the mask (multiply with a smoothed mask; or a short FIR per hop through the convolver) are prototyped and measured before one is chosen. Built only if the prototype meets these:

| Criterion | Target |
|---|---|
| Null | unit mask ≤ −120 dB; Mix 0 % bit for bit |
| Suppression | a +12 dB, Q 20 resonance on pink noise brought within 1 dB of the envelope; plain pink noise changed < 0.5 dB per third octave at the default depth |
| Artefacts | pre-echo before an impulse ≤ −60 dB (FIR option) or reported; time-domain aliasing energy and residual spectral flux on noise reported |
| CPU | ≥ 20× real time (the analysis estimates 30–60×, unmeasured) |
| Blk, Alloc, same bits | as above |

## 6. The agent: tools, metrics and guides (M1)

The "assistant" is the `master` prompt and guide run by our agent with deterministic measurements; every move is a real machine parameter with undo, and the agent explains each with the numbers that led to it.

| Piece | What |
|---|---|
| `analyze` metrics | `microdynamics`: per-band short/long envelope ratio (LDR-like) in 4 bands; `stereo`: mid/side power ratio in Ozone's 4 bands (split at 120 Hz, 2 kHz, 10 kHz); `tonal` (as built a metric of its own, `spectrum` unchanged): a long-term 1/6-octave curve, level-matched, against a target (a reference file or a stored target); `platform`: the playback gain each service would apply and the true peak after it (Spotify −14 default, −11 loud with its limiter, −19 quiet; Apple −16; YouTube −14), measured as BS.1770 |
| `eq_fit` | Fits up to N Neo EQ bands to a target difference curve (smoothed to 1/3 octave, clipped to ±3 dB total by default): least squares on `EqDesign` with Gauss–Newton (as `GraphicSolver`), regularised; returns editable bands and the residual |
| `target_build` | Averages the user's own reference WAVs into a style target (spectrum curve, PLR, LRA, per-band width, microdynamics), as built saved as a file (`*.target.json`) rather than in the song, so one target serves every song; no curves derived from commercial records ship with the app |
| Guide | `mastering.md`'s flow: inputs (platform, reference, or style target) → measure → decide (tonal via `eq_fit`, dynamics via PLR and microdynamics, stereo per band, loudness by the limiter's gain loop, delivery checks) → apply one stage at a time with a snapshot, measure, revert if worse → report before and after. Live: suggest, don't move knobs unasked |
| Eval | A6 extended: "match a reference" (third-octave difference after level match ≤ 1.5 dB RMS, 50 Hz–16 kHz; width per band ±0.1; PLR in range), "master for Spotify Loud", "fix harshness" (2–6 kHz excess reduced without a tilt change) |

## 7. Risks and open questions

- **Transparent limiting is the hard part, not compliance.** Our proxies (IMD, pumping sidebands, loudness at a distortion budget) are necessary, not sufficient: listening notes on a fixed corpus, and v1 kept as a style.
- **Linear phase rings before transients and adds latency.** A full chain of linear EQ (96 ms) + Multiband (96 ms) + Spectral (43 ms) + limiter is about 0.24 s: fine offline, in the way live. Minimum phase stays the default; every latent machine has a zero-latency setting.
- **Automation on latent machines** is heard early by the path's latency (pre-existing; documented, not compensated).
- **The Session View's heard time** uses the device's output latency only; a master chain with latency is heard later than launches and takes assume (pre-existing; a follow-up can add the master path's compensated latency, with its own tests).
- **STFT CPU and artefacts** are unmeasured; M7 is gated on a prototype.
- **Float32 between machines** is fine for mastering; dither means something only where the final quantiser is ours (M5).

Decisions taken in M0 that the user may want differently are listed in §8 with the phase.

## 8. As built

### M0 (2026-11-06, `machines/mastering-m0`)

**Engine.** `ReBuzz/Core/LatencyPlan.cs` (the plan), `ReBuzz/Core/LatencyRing.cs` (the delay), wired through `ReBuzzCore` (build on the UI thread, apply on either thread), `MachineConnectionCore` (its ring), `MachineCore` (the audio thread's view of its latency, the UI event, the bypass's delay and crossfade), `MachineWorkInstance` (the bypass path), `WorkManager` (the apply before each block's work), `ManagedMachineHost` (the report at the tick) and `RenderTools` (a stale plan rebuilt before a render).

- **On by default.** `EngineSettings.MachineDelayCompensation` defaults to true; a value a user stored is kept (settings store only what was changed). Songs don't store it. The null test's 91 cases (serial chains) render bit-identical with every M0 change in, against renders made before it. Old songs change only where a parallel path (a send, a dry/wet split, a sidechain) holds a machine with latency: those now play aligned (Neo Drive's 62 samples on a track with a send, for example). That is the fix, and it is the one change to existing renders.
- **Found on the way.** Inserting a machine into a connection (`InsertMachineAction`) and opening a song never recomputed the compensation, and every recompute reset every compensated connection's ring, changed or not, dropping what was in flight. Now the plan follows every connection added or removed (`MachineCore.AddInput`, `RemoveInput`: a rebuild queued on the UI thread, and done at once before an agent's render), and a ring is touched only when its delay changes.
- **A latency that changes while playing** (Neo Limiter's or Neo Comp's lookahead) is applied to the plan on the audio thread at the machine's tick, before that block's work, allocating nothing; the UI's `Latency` follows through the UI event queue, which also grows a ring that needs room. Before, it waited for the UI thread, so where it landed in a render depended on it.
- **The crossfade.** A changed delay goes from the old tap to the new over 10 ms on a smooth curve (3x² − 2x³, no libm). From delay 0 the ring first collects the new delay's worth of history (the change lands that much later: 96 ms for Neo Multiband); while nothing has passed yet, or everything in flight is silent, the change is instant and exact. A settled delay and the direct path render bit for bit as before.
- **The bypass keeps the latency.** A bypassed stereo effect with latency stays in the plan with it; its dry input runs through its own ring every block (so a bypass has its history at once); it keeps working while bypassed, its output unused, so it comes back current; the switch crossfades over 5 ms. Effects without latency, multi-I/O machines (Neo Comp, for its sidechain) and everything with the compensation off bypass as before.

| Criterion | Target | Measured (Linux, this branch) |
|---|---|---|
| Parallel path | Neo Multiband (linear, unity) beside an inverted Neo Utility path, compensation at its default: cancels; off: combs | cancels exactly (residual 0; the multiband alone −20.4 dBFS); off: +4.6 dB against the multiband alone |
| Latency change | Neo Limiter's lookahead 2 → 5 ms (99 → 231 samples at 44.1 kHz) under a sine, the other path alone: no step larger than the sine's own ×1.2, no gap; the null back after | largest step 1.001×; the quietest 64 samples at 0.79 of the level (the 10 ms comb of two copies 132 samples apart); the null back exactly; the delay moved without the UI thread |
| Ring | 0 → 4607 samples under a 1 kHz sine: direct, then the curve, then the new tap, sample for sample; shorter at once; back to direct; silence switches at once; growing keeps what is in flight; clamped beyond capacity until grown; no allocation | all exact; largest step 1.003× the sine's own |
| Plan | delays against the recursive walk on 300 random graphs; a diamond; a cycle cut; no allocation | equal on all 300; 0 bytes |
| Bypass | the parallel null held through bypass and back; compressing hard (−20.6 dB), bypass and back: no step over ×1.5 the louder signal's own | null exact throughout; largest step 1.000× |
| Inserted machine | Neo Drive inserted into a connection: the parallel path delayed by its 62 samples, nothing else asked | 62 |
| Unchanged | the null test, bit for bit; ReBuzzTests' latency tests | 91 of 91 identical (Linux, against renders made before M0); ReBuzzTests pass |

**Library** (`Machines/NeoBuzz.Dsp/`).

| Block | Measured |
|---|---|
| `ComplexFft`, `PartitionedConvolver`, `SteppedFirDesign`, `LinearPhaseFilters` (moved from Neo Multiband) | Neo Multiband bit for bit: its 59 tests, the null test's five Multiband cases (the crossover moves included); its CPU as before on the Linux runner (linear 56×, three bands 62×, minimum 65×, mid/side unlinked 50×, a crossover moving 28×; main's slower-runner figures: 57×, 66×, 65×, 50×, 28×); one and two FIRs against direct convolution within −137.4 and −130.9 dB |
| `Stft` (N = 512, 2048, 4096; hop N/4) | unit spectrum: the input N − 1 samples back within −137.7 … −139.3 dB; a gain on the left alone (Unpack, Pack): left within −137.5 dB, right −139.2 dB; blocks of 256, 128, 117, 16 and 1 bit-identical; 283× real time at N = 2048 stereo on the Linux runner (the transforms alone); no allocation |
| `QuadratureAllpass` | 90° within ±0.098° (44.1 kHz, 12 coefficients), ±0.108° (48 kHz, 12), ±0.129° (96 kHz, 13) from 20 Hz to 20 kHz; magnitude 1 within 4·10⁻¹⁴; the envelope of sines from 25 Hz to 19 kHz flat within 0.015 dB |
| `TransientSplit` | steady sines at 25, 40, 100, 1000 and 10 000 Hz: weight exactly 0 after 300 ms; 200 Hz hits decaying with 60 ms: 0.55 at 1 ms, 0.78 at 2, 0.82 at 3, 0.80 at 5, 0.64 at 10, 0.41 at 20, 0.16 at 40, 0 from 60 ms on; unity gain exactly 1; blocks of 256, 128, 117 and 16 identical; no allocation |

**Decisions the user may want otherwise.** (1) Compensation on by default, a stored "off" kept. (2) A latent effect keeps working while bypassed (CPU for an aligned, click-free return), where Buzz skipped its work. (3) The 10 ms smooth crossfade for delay changes (a brief comb of the two taps rather than a dip to silence), and coming from no delay, the change lands after the new delay's worth of audio. (4) Multi-I/O effects (Neo Comp) keep the plain bypass: their lookahead isn't kept when bypassed (up to 5 ms); the multi-I/O path can follow when a latent multi-I/O machine needs it (a de-esser with a sidechain, M6). (5) The Session View's heard time still counts only the device's latency (§7).

### M1 (2026-11-06, `machines/mastering-m1`)

**Neo Utility's matrix** (`Matrix`, the last parameter: Off, Encode M/S, Decode M/S). Decode is the first stage and Encode the last, so a Utility's other stages always work on left and right; each in double, switched over 5 ms, skipped at Off (old songs bit for bit). Presets "Mid/Side — Encode (Before a Stereo Machine)" and "Mid/Side — Decode (After It)"; the editor's Channels column has the dropdown; a null-test case (`utility-ms-eq-chain`: an encode, Neo EQ and a decode, the matrix automated).

**Measures** (`Machines/NeoBuzz.Analysis/`: `Microdynamics.cs`, `Tonal.cs`, `Platform.cs`, `Stereo.cs`'s `MsBands`; the four bands in `Bands.Mastering`), in `analyze` as `microdynamics`, `tonal`, `platform` and `stereo.ms_bands`, each with reads, and against a `target`: a reference render or WAV measured on the fly, or a style target. **Style targets** (`ReBuzz.Agent/Audio/StyleTarget.cs`): the mean and the range over the sources of the normalised tonal curve, integrated loudness, loudness range, PLR, side/mid and LDR per band; `target_build` saves one as `targets/<name>.target.json` beside the agent's log (or a file given) and keeps it for the session by name. **eq_fit** (`ReBuzz.Agent/Audio/EqFit.cs`, on `NeoBuzz.Eq.EqDesign`, which moved out of `EqEngine.cs` unchanged so the agent links it): bands placed one at a time where the weighted residual is largest (a bell with the lobe's width, or a shelf where an end leans), each refined alone, then all together by Levenberg–Marquardt on log frequency, gain and log Q with a small gain penalty; a band that takes less than 0.02 dB off the RMS isn't kept. With `machine`, the bands go into B2–B7 of a Neo EQ (Mode Parametric, the rest at 0 dB, the cuts untouched) through `param_set`: one undo step. The tools: `ReBuzz/Agent/MasteringTools.cs`.

**Helpers for M2 on**: `DeltaListen` and `GainMatch` (§2).

**The guide and prompt.** `mastering.md` opens with the flow (inputs → measure → decide → apply → report; live: suggest, don't move knobs unasked), what each service does, a section on working against a reference (tone with `eq_fit`, loudness and PLR, microdynamics, width per band, M/S through the Utility matrix) and a platform check before settling; the `master` prompt takes a `reference`.

| Criterion | Target | Measured (Linux, this branch) |
|---|---|---|
| Matrix | an encode then a decode give back the input (the plan said bit for bit; in float the mid and side each round, so within one rounding); halving the side between them is Width 50 %; switches glide | within 1.00 ulp of the larger channel, null −151.9 dB; equal, difference 0; largest step 2.08·10⁻³ per sample over 5 ms |
| Microdynamics | bursts against steady sound; a level change moves nothing; compression lowers it | 40 ms bursts every 250 ms, 20 dB over a bed: 7.37 dB (predicted 7.7); steady noise 0.31 dB, per band 3.2, 1.2, 0.5 and 0.4 dB (the measure's floor: fewer cycles in 20 ms the lower the band); a 12 dB level change: equal within 10⁻⁹; a fast leveller: 7.35 → 3.55 dB; a kick over hiss: 6.1 dB below 120 Hz, 0.4 above 10 kHz |
| Side/mid per band | mono lows, uncorrelated highs, a mono middle | −154 dB, −0.1 dB (correlation 0.00), −85 dB; a mono file −∞ |
| Tonal | pink flat; itself 0; a level change only an offset; a known tilt recovered | flat within 0.3 dB from 40 Hz to 10 kHz; 0 (10⁻⁹); offset −12.04 dB, RMS < 0.01 dB; f^0.2: −1.96 dB at 100 Hz, +2.06 at 10 kHz (4.02 over two decades, 4 expected) |
| Platform | Spotify's own example and loud masters | −20 LUFS at −5 dBTP plays at −16 LUFS (+4 dB to −1 dBTP); Loud raises it 9 dB into 4.7 dB of limiting; a −8 LUFS master is turned down everywhere (Spotify −6, Apple −8) |
| eq_fit | known bands recovered within 0.1 dB RMS | a −3 dB bell at 300 Hz (Q 1.2), +1.5 dB at 2.5 kHz (Q 2) and a +2 dB shelf at 8 kHz: 1.14 → 0.036 dB RMS, worst 0.10 dB; the cut found at 300 Hz, −3.00 dB, Q 1.20 (the shelf, above the 16 kHz fit range, as a broad bell at 13 kHz); a 10 dB dip clipped to ±3 dB, two bands: 1.69 → 0.07 dB; pink against f^−0.8 from audio: 1.58 → 0.11 dB, three bands |
| Delta, gain match | off bit for bit; on, wet − dry exactly; blocks don't matter; a −6.02 dB change read; no allocation | exact; exact after the 10 ms glide at 300 samples of latency; blocks of 1, 16, 256 and 1024 identical; −6.0206 dB; unknown for 3 s and on silence; 0 bytes |
| Eval (scripted) | the A6 master task still passes; the new briefs pass and their starting songs don't | master 1.00 (start 0.80); master to a reference 1.00 (start 0.77: eq_fit found −2.7 dB at 257 Hz Q 1.03, +1.5 dB at 2.4 kHz Q 1.34 and +3.2 dB at 12 kHz, the master 0.23 dB RMS from the reference, start 1.68); Spotify Loud 1.00 (start 0.67: −11.1 LUFS, −2.30 dBTP, Spotify's gain +0.1 dB, its limiter 0); fix harshness 1.00 (start 0.80: the 2–6 kHz excess 10.4 → 5.5 dB, the tilt −26.1 → −26.4 dB) |
| Unchanged | the null test's existing cases bit for bit | 91 of 91 identical on Linux against main's Neo Utility, Neo EQ, Neo Analyzer and Neo Scope builds |

The lookahead test's "no gap" threshold (M0) is relaxed from half the level to a quarter, with the reason in the tests: the crossfade sums the two taps for 10 ms, which comb (0.79 of the level on Linux, 0.51 on Windows); a gap is silence.

**Decisions the user may want otherwise.** (1) Style targets are files (`targets/` beside the agent's log, or a path), not song data: one target serves every song and a song doesn't carry the user's references' measurements. (2) `tonal` is a metric of its own; `spectrum` is unchanged, so its existing reads and tests stand. (3) Platform figures other than Spotify's are observed values, labelled so, and those services only turn down. (4) eq_fit's defaults: 4 bands, ±4 dB each, the wanted change clipped to ±3 dB (the guide's EQ rule), fitted 25 Hz – 16 kHz, cuts and notches never placed. (5) The matrix round trip is within one float rounding, not bit for bit (§3's "done when" asked for bit for bit; in float that can't hold for every input).

### M2 (2026-11-06, `machines/mastering-m2`)

**Neo Clipper** (`Machines/NeoClipper/`; machines.md, Neo Clipper, has every figure). The card's criteria hold but the CPU (50× at 4× on the 2-core test machine against 100×: the shared 4× oversampler alone runs at 84×). What it took that the card didn't say: a ceiling that holds exactly after oversampling needs a guard, since the downsampled clip rings past the ceiling (up to 2 dB); clamping aliased at −42 dB, so a 5 ms lookahead gain on sample peaks (Neo Limiter's construction) keeps −94 dB. The curves and their ADAA moved to NeoBuzz.Dsp (`ClipCurve`, `ClipAdaa`) for Neo Limiter's soft clip.

**Neo Limiter v2** (machines.md, Neo Limiter, v2). Classic renders v1's bits (four settings' hashes pinned, two block sizes; the null test's existing cases identical). Every stage measured; the styles are presets ("Style — Classic", "Transparent", "Punchy", "Dense (Soft Clip 9 dB, Upward)", "Loud (Soft Clip 3 dB)"), not a parameter, so a style is a starting point the user edits.

**Match.** Neo Clipper's editor: after 3 s of sound the button shows the gain-match reading and a click sets Output from it (`GainMatch.Match`, 0.1 dB steps), one undo step (the studio test: 11.55 dB read, Output −11.5 dB).

| Criterion | Target | Measured |
|---|---|---|
| Clipper ceiling | every sample ≤ ceiling | at most the ceiling in every shape, factor, band count and stereo mode |
| Clipper alias | ≤ −90 dB at 4×, into the knee | −93.7 dB (Knee), −96.8 (Soft), −119.1 (Tanh); Hard −80.7 (8×: −86.8) |
| Clipper CPU | ≥ 100× | 50× (4×), 275× (1×) |
| Limiter Classic | v1 bit for bit | equal |
| Limiter TP | ≤ ceiling + 0.1 dB with the soft clip, each knee and rate | +0.030 dB worst |
| Limiter alias | the clip stage ≤ −90 dB | −103.5 dB worst into the knee |
| Limiter IMD and pumping | each style against Classic, reported | Transparent IMD 0.41 % at 6 dB (Classic 1.40), pumping −21.8 dB (−19.8); the soft clip styles 70–92 % at 6 dB |
| Limiter CPU | ≥ 50× | 30× with every stage (the soft clip at 4×); Classic 159× |
| Listening | ABX notes on a corpus | not done |
| Unchanged | the null test's existing cases | 92 of 92 identical (Linux) against M1's Limiter and Analyzer |

**Decisions the user may want otherwise.** (1) The Clipper's ceiling guard (5 ms lookahead, part of its 7.6 ms latency at 48 kHz) to hold "every sample ≤ ceiling" without aliasing; without it the alias target and the ceiling can't both hold. (2) 1× runs the curve without ADAA (ADAA's mean costs 7.6 dB at 20 kHz there). (3) Delta is the output minus the input (Mix included), and Neo Limiter's is at the input's level. (4) Neo Limiter has no Output for Match: Unity listen trims by the gain-match reading instead (a monitor; off for rendering). (5) The styles are presets, and the two soft-clip styles distort a full-scale test tone far more than limiting; they say so in their names and the guide. (6) CPU floors below the cards' (the shared oversampler's cost). (7) The K-weighting coefficients (`Loudness.KWeighting.For`) now use the portable maths, as the gain match feeds Unity listen's audio; analysis numbers move by an ulp at most.

### M3 (2026-11-06, `machines/mastering-m3`)

**Neo EQ v2** (`Machines/NeoEq/`; machines.md, Neo EQ, v2, has every figure). Per band a channel (Stereo, Left, Right, Mid, Side), a shape (Proportional Q, Baxandall, Vintage, All-pass, in `EqDesign`) and dynamics (Off, Down, Up; threshold, ratio, knee, range, attack, release through `Dynamics.cs`; the detector on the band's own shape, its input or the sidechain on input 2), then Delta; the gain-match reading, per-band dynamic readings, the editor's Match. At the new parameters' defaults v1's bands run v1's code: six renders pinned, the null test's eight EQ cases identical against M2's. What it took that the card didn't say:

- **The detector's envelope.** A peak detector on a band-passed sine ripples at twice its frequency, which slows the attack by the duty cycle; the shared 90° pair (M0) gives the exact envelope of any sound from 20 Hz, at the cost of about 0.5 ms (4 kHz) to 0.9 ms (1 kHz) with the band-pass and the grid, so attacks from 10 ms meet ±10 %.
- **The grid's interpolation.** Straight-line coefficient ramps between the 16-sample designs left kinks whose images at 3 kHz ± the sound read −83.7 dB with the gain swinging 12 dB (designing every sample: −91.6); the coefficients now follow the quadratic B-spline of the last three designs (C¹, 24 samples behind), −98.3 dB.
- **Two inputs.** The host calls a multi-I/O machine with WM_WRITE alone and keeps whatever its output buffer holds when it returns false, where a one-input machine's quiet input passed through: the machine now does that itself, so v1's renders stay bit for bit. ReBuzz's multi-I/O path allocated about 320 bytes a buffer (`Count()`/`ElementAt()` on the connections' channel sets, an enumerator boxed per call; Neo Comp too): it enumerates the sets themselves now (`MachineConnectionCore.SourceChannelSet`, `DestinationChannelSet`), the same order, 0 bytes.
- **A fix (reported).** A live cut band ignored a new slope until it was switched off and on; now a slope change fades the band out and in as a type change does. Only renders that change a live cut's slope move.

**CPU targets of M2's machines.** `FastOversampler` (NeoBuzz.Dsp): `FirOversampler`'s designs, stages, padding and latency, each halfband branch run over the whole block with its outputs in vector lanes (Vector256 where the machine has it, Vector128 otherwise, the same sum in the same order per output, so the same bits everywhere), within −113 dB of `FirOversampler`; a new type used only by Neo Clipper and Neo Limiter v2's soft clip, so no other machine's bits change. Also: `ClipCurve` without branches and `ClipAdaa` a block at a time in vectors (the same bits as one sample at a time); `QuadratureAllpass.Block` (a section at a time over a block; `TransientSplit` uses it: the same bits); `Loudness.KWeighting.SumOfSquares4` (`GainMatch`'s four filters side by side: the same bits); Neo Limiter v2's Upward on a 16-sample grid (its bits move; v1's don't). Neo Clipper's and Neo Limiter v2's renders change by the oversampler's rounding (neither is in a release yet). Neo Limiter re-profiled (`tools/machine-profiler.py --only "Neo Limiter"`).

| Criterion | Target | Measured |
|---|---|---|
| EQ R=D | per channel mode within ±0.01 dB, M/S included | within 0.0091 dB, 8 shapes × 5 channels |
| EQ M/S | an S-only band leaves L+R bit for bit | at rest bit for bit; boosting, within the outputs' rounding (half an ulp each): float outputs can't hold the sum exactly once the side outgrows the quieter channel's binade |
| EQ dynamics | static curve within 0.25 dB; attack and release 63 % within ±10 % | within 0.010 dB; attack 10/30/100 ms → +7.1/+2.0/+0.3 %, release 50/300 ms → +1.2/+0.2 % (4 kHz) |
| EQ zip | ≤ −90 dB with the dynamic gain moving | −98.3 dB (attack 10 ms); attack 2 ms −77.9 dB, as designing every sample (−77.3) |
| EQ unchanged | parametric and graphic renders bit for bit | equal but the slope fix's three; the null test's eight EQ cases identical |
| EQ Blk, Alloc, sidechain | as above | identical at 256/117/16/1/200; 0 bytes (engine and studio); the sidechain within 0.007 dB of the curve |
| EQ CPU | 8 dynamic bands ≥ 30× | 66× (the 2-core Xeon), 57× (Linux runner), 43× (Mac runner), at the reference's speed (throughput guards, machines.md §4) |
| Clipper CPU | ≥ 100× at 4× | 217 (Xeon), 201 (Linux runner), 138 (Mac runner) at the reference's speed (M2: 50× as timed on the Xeon); floors 150 (x64), 100 (arm64) |
| Limiter CPU | ≥ 50× with every stage | 88, 91, 66 (M2: 30× as timed on the Xeon); floors 64 (x64), 50 (arm64) |

**Decisions the user may want otherwise.** (1) "L+R bit for bit" for a Side band holds at rest and within one rounding when it works (float outputs can't do better); the test checks that. (2) The dynamic detector is the exact (Hilbert) envelope, 0.5–0.9 ms late, rather than a peak follower; attacks under 10 ms run a little slow (5 ms reads 5.5–5.9 ms). (3) The coefficients follow a spline between the grid's designs, not straight lines (Neo Filter's), for the zipper target; 8 samples more lag. (4) A dynamic band's gain stays within ±30 dB (setting ± range). (5) A switch of a band's channel, shape or dynamic fades it out and in like a type switch. (6) The slope fix changes renders that automate a live cut's slope. (7) Vintage's dip is fixed (half the gain, Q 1, an octave inside); Proportional Q is Q × 10^(|gain|/40). (8) The CPU tests are throughput guards (main's `Throughput.cs`, against a reference workload in the same run), their floors from the Xeon's, the Linux runner's and the Mac runner's figures; each at or above its card's target. (9) NeoScope's studio test starts from a new song: after ExitTests' songs a loop's first wrap came 82 samples late (on main too); the cause, in the engine or the session, is left to its owners.

### M4 (2026-11-12, `machines/mastering-m4`)

**Neo Transient** (`Machines/NeoTransient/`) and **Neo Low End** (`Machines/NeoLowEnd/`), as the cards §5.4 and §5.5; machines.md has the descriptions. New shared blocks in `NeoBuzz.Dsp`: `LrBands` (zero-latency 2 – 4 band Linkwitz–Riley cascade), `MicroGain` (microdynamics gain with auto gain) and `SmoothRamp` (a linear ramp plus a 4 ms one-pole). Registered in the same lists as Neo Clipper (solution, Studio and Portable projects, presets, guides, null-test cases, profile entries, editor tests, audio hygiene test).

Neo Transient:

| Criterion | Target | Measured |
|---|---|---|
| Attack | the first 5 ms peak moves by the set gain ±0.5 dB (3 ms lookahead) | kick and snare at ±12, 6, 3, −6, −12 dB: exact (±0.02 dB); 0 ms lookahead +12: kick +11.99, snare +11.85 |
| Steady tones | Attack leaves a held sine alone | 100 / 1000 / 8000 Hz at Attack +12: 0.0000 dB; Sustain −9 on a kick tail: −9.00 dB |
| M/S | a side-only hit shaped, the mid untouched | side kick +12.00 dB, mid bed unchanged |
| Impact | one knob per band moves the burst-to-bed ratio, energy kept | ±100 %: 20.00 → 31.13 / 5.68 dB; ±50 %: 26.36 / 12.79 dB; energy within 0.42 dB; four zeros pass the audio |
| Bands | sum flat | 0.0000 dB at 44.1, 48 and 96 kHz |
| Zipper | parameters stepped per block, 220 Hz sine, above 2 kHz ≤ −90 dB | −109.8 dB |
| Switches | Mode and Impact switches without a step | 1.00× the sine's own step |
| Blk, Alloc, Mix0 | blocks of 256/128/117/16/1 identical; 0 bytes; neutral = input delayed bit for bit | hold |
| Latency | the lookahead | 144 samples at 3 ms (44.1 kHz) |
| CPU | ≥ 100× attack/sustain | at the reference's speed on the 2-core Xeon: A/S L/R 559×, M/S 313×, Impact L/R 70×, M/S 66×; floors (x64/arm64) 250/150, 140/90, 30/20, 28/18 |

Neo Low End:

| Criterion | Target | Measured |
|---|---|---|
| Punch | the first 8 ms peak moves by the set gain ±0.5 dB (kick below the crossover) | +12 dB: +12.00; +6: +6.01 (minimum) / +5.99 (linear); −6: −5.93 / −5.97 |
| Sustain, crest | tail follows Sustain, the low crest rises with Punch | Sustain −9, Punch +3: tail −8.23 dB, low crest 13.53 → 16.54 dB |
| Contrast | ±100 % widens or narrows loud against quiet, energy kept | 13.98 → 22.07 / 8.93 dB, energy ±1.10 dB |
| Balance | the low band moves toward the target, the mids stay | +6.03 / +3.00 / −5.66 dB, mid within 0.071 dB |
| Peak control | the low band under the level | −6 dB: −5.975 dBFS (crossover 300 Hz) |
| Mono | the side removed below the frequency | 100 Hz: 40 Hz −32.0, 100 Hz −6.3, 200 Hz −1.8, 1 kHz 0.00 dB |
| Split | flat sum; the linear FIR against the Linkwitz–Riley 24 design | minimum phase 0.0000 dB; linear within 0.0001 dB at 60/120/300 Hz, 0.52 dB at 20 Hz |
| Isolation | above the crossover untouched (card: 1e-6) | not met literally: the 24 dB/octave skirt passes ~6 % of the low band's gain one octave up, 0.0002 % at 8×; the linear option is exact |
| Latency | minimum phase the lookahead; linear reported | 240 samples (5 ms) minimum; 4607 (D+P) linear at 48 kHz |
| CPU | ≥ 50× | at the reference's speed on the 2-core Xeon: minimum phase 139×, linear 116×; floors (x64/arm64) 70/45 and 55/35 |

**Decisions the user may want otherwise.** (1) Neo Transient's default lookahead is 3 ms (latency 144 samples), so the card's first-5-ms figure holds; 0 ms is the live setting. (2) Neo Low End has an added Lookahead parameter (default 5 ms), which is its minimum-phase latency. (3) The card's 1e-6 isolation above the crossover is not met literally by a 24 dB/octave Linkwitz–Riley split; the linear option is exact, the minimum-phase figures are in the table. (4) The linear FIR is 0.52 dB off the design at 20 Hz (8192 taps). (5) Peak control is not an absolute ceiling for content just under the crossover. (6) The Punch target holds when the kick sits below the crossover. (7) Neo Transient's Impact mode is gated by the live blend, so all four knobs at 0 pass the audio unchanged. (8) `SmoothRamp` was added for the zipper target. (9) The CPU floors were set from the 2-core Xeon and need confirming on the Linux and Mac runners; the editors' sizes are the Avalonia sizes (1020 x 311 and 1032 x 277).

### M5a (2026-11-12, `machines/mastering-m5`): Neo Imager, Neo Exciter, Neo Dither

The three new machines of §5.7 – §5.9 and the render option; machines.md has the descriptions and the measured tables. New shared block: `Dither` (TPDF, error-feedback shaping, exact grid). `render(bits=16|24, dither=…)` in the agent (`Requantize`, `Wav.WriteStereoPcm`). Registered like M4's machines (solution, Studio and Portable projects, presets, guides, null-test cases, profile entries, editor and audio-hygiene tests).

**Deviations and decisions.** (1) Neo Imager splits with `LrBands` only (the card's optional `LinearSplit` is not offered). (2) Stereoize keeps l + r to −146.7 dB, not bit for bit: the two outputs are rounded to float separately. (3) "Mono energy" in Recover sides is read as the total energy l² + r² (the side's energy goes into the mid). (4) Neo Exciter's per-band Mix is its Amount: the added harmonics are scaled by it, so a band at 0 % is exactly the input (a separate Mix would repeat it). (5) The `machine_replace` pair from Oomek Exciter is not done in M5a (see M5b). (6) Neo Dither's shaping curves are for 44.1 and 48 kHz only; at higher rates the same curve sits higher in frequency. (7) The HD recorder's dither option is not done; the agent's `render(bits, dither)` is. (8) Neo Dither got a `Blank after` parameter (default 0.7 s) so that its editor has more than one knob. (9) CPU floors were set on the 2-core Xeon (Imager 70/45, Exciter Tape 16/10 and Warm 32/20) and need confirming on the runners.
### M5b (2026-11-12, `machines/mastering-m5`): Neo EQ linear phase and the curve layer, Neo Strip

§5.6 and §5.10; machines.md has the descriptions and the measured tables. Neo EQ got `Phase` (Minimum/Linear) and 32 `Curve` points (appended after `Delta`, so songs and presets keep their values), two readings (`q_capped_bands`, `pre_ringing_db`), three linear-phase presets, and a `LinearPhaseFilters.DesignNow()` in the shared library (a whole design that plays at once, for a start or a phase switch). Neo Strip (38 parameters, 7 presets) links Neo EQ, Neo Comp, Neo Drive, Neo Clipper and Neo Limiter as source and runs their engines on their own parameter values.

**Deviations and decisions.** (1) The curve layer is 32 parameters in the Neo EQ (±24 dB, log-spaced), edited by the host, the agent or a preset; the editor draws the curve in the response but offers no handles for it, and no tool fits it yet (`eq_fit` fits bands). (2) Linear phase works in Parametric mode; Graphic mode stays minimum phase, and dynamic bands are static in linear phase. (3) A band's Q is limited by π · f · D/(5 fs) so its ringing fits the FIR; the limit is reported (`q_capped_bands`) and listed in the editor. (4) Four FIRs (the L/R matrix) run always, so a linear-phase Neo EQ costs 108× real time at the reference's speed on the Xeon (the card's 100×), with floors 80/55. (5) An identity design (every band flat, curve flat) passes the input delayed bit for bit; the card's "Lat engine null −240 dB" is met by that (the machine has no parallel path to null against). (6) Switching `Phase` designs the FIR whole on the audio thread (one stall, once) so a render starts with the right filter; ordinary redesigns are in bounded steps (the longest block 2.5 ms of 5.3). (7) Neo Strip's stages are the machines' engines with fixed secondary settings (the cuts 12 dB/oct, shelves Q 0.7071, the clipper hard at 4× in one band, the limiter's lookahead 2 ms, no comp lookahead or sidechain); its latency (504 samples at 44.1 kHz) is dominated by Neo Drive's 8× oversampler and is always present. (8) All Neo Strip stages start off, so the strip is neutral until used. (9) Not done: the Oomek Exciter successor (written in M5a) is unmeasured on Windows; the HD recorder's dither option; any Windows measurement of a CPU floor.

