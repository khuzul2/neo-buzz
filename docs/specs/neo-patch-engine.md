# Neo Patch engine: NP1 (as built) and NP0 (the runtime gate)

Contract: `docs/specs/neo-patch.md` (§2, §4, §5). Code: `Machines/NeoPatch/` (the engine in `Engine/`, portable net10.0; the machine `NeoPatch.cs`; three csproj flavours; `ReBuzz.sln`; the package). Tests: `NeoBuzz.MachineTests/NeoPatchTests.cs` (`dotnet test NeoBuzz.MachineTests --filter NeoPatchTests`), the presets in `PresetTests`, the guide and Explain checks in `ReBuzz.AgentTests`. Part 1 is NP1; part 2 is the NP0 record it grew from.

# Part 1: NP1, the engine and the machine (as built)

## 1.1 What it is

A modular synth in one machine, with no inner canvas yet (NP2 draws it): the device view shows the generic parameters (Volume, Voices, Glide, Audio-rate mod, Macro 1 to 16, each with Explain How and Sound), and a patch is built from code (`PatchGraph`, the factory patches in `PatchPresets.cs`), from a text file (`machine_data_import` kind `patch`) or from a preset. The patch is the machine's state (`NeoPatchState.Patch`: one line of text, `PatchText`), with the user's own sample (kind `sample`, 24-bit PCM as in Dye Essence). 16 tracks of Note and Velocity, MIDI and MPE (per-channel bend by 48 semitones, channel pressure and CC 74 reach the Keys node's Pressure and Slide outputs).

| Part | File | What it does |
|---|---|---|
| Graph and text | `PatchGraph.cs`, `PatchText.cs` | 22 node kinds, 128 nodes at most (`Add` returns null at the cap), wires with an amount, `Connect` refuses a cycle that does not pass through Feedback, macro mappings (macro, node, control, low, high). The text is `neopatch 1;name …;n id Kind global x y controls…;w from port to port amount;m macro node control low high`: no newline, no quote; unreadable parts are skipped, never thrown. |
| Compiler | `PatchCompiler.cs` | Orders the graph (sources first, Feedback read before write), drops what does not reach an Out, resolves each node's scope, builds fused inputs (`Inp`: wires with amounts summed by the consumer, no mix jobs), allocates the buffers, and takes the state of nodes of the patch before (matched by node id and half) so an edit does not retrigger anything (`CompiledPatch.Adopted`). |
| Jobs and kernels | `PatchJobs.cs`, `PatchKernels.cs`, `PatchControlJobs.cs` | 64-sample blocks, sample-major `[sample][16 voices]` buffers, kernels generic over Dye's `ILanes` (AVX2, two Vector128, plain floats: the same bits). Oscillators, Envelope, Filter, VCA, Mixer, Math, Crossfade, Shaper, Sum and Out are lane kernels; Keys, LFO, Random, Slew, S&H, Macro, Noise, Sample and Delay are scalar jobs over the sounding voices. |
| Runtime | `PatchRuntime.cs` | 16 voices through `VoiceAllocator`, per-block voice state (pitch with bend, glide, pressure, slide, gate, velocity, gain ramps for steals), jobs in order (a global job runs in a one-voice view), macro smoothing (20 ms), DC blocker, soft safety limiter and NaN guard on the output, hand-over of a new compiled patch at a block start (the first block removes the step against the last block's trend). |
| Engine glue | `PatchEngine.cs` | The machine's engine: parameters to the runtime, `SetPatch`/`SetGraph`, readings, `Busy` (tables and samples still building). |
| Presets | `PatchPresets.cs`, `Presets.txt` | Eight patches built in code; each is stored as text (`state Patch = …`) with its macros and a Volume that puts the chord at -22 dB. |
| Test patches | `PatchTemplates.cs`, `PatchHost.cs` | The two NP0 patches and the headless host (`Render(events, frames)`). |

## 1.2 Decisions taken (the lead's, and mine where marked)

1. **Signals.** Every wire is mono. Audio inputs (Osc FM, PM and Sync; Filter In; VCA In; Shaper In; Delay In; Mixer and Out inputs) run at the audio rate. Control inputs (pitch, cutoff, resonance, level, rate, drive, position, pulse width) are read once per control interval (16 samples) and interpolated across it; the parameter **Audio-rate mod** reads them for every sample (filter FM). A pitch input is read at the interval's end.
2. **Stereo** only at Out: four inputs, each with its own pan (equal power) and a Spread that gives every voice a fixed place (a golden-ratio sequence, so the first voices lie far apart). Everything before Out is mono.
3. **Oscillator.** Saw, square (PWM: two band-limited saws, one phase-shifted by the pulse width), triangle: Dye's mip tables (`TableArena`, eleven levels, level chosen per voice and control interval from the largest increment including FM) with cubic reading; sine: an odd polynomial (error 6e-8). Wavetable Osc: Dye's ten built-in tables (`TableBank`), frame mix by Position. Aliasing and cost: §1.4. A Sync input restarts the phase on a rising zero crossing (not band-limited: it is a sync, it sounds hard).
4. **Filter.** Neo's SVF (low-pass 12, band-pass, high-pass, notch) and a 24 or 12 dB ladder (tanh stages in the lanes). Coefficients are made per voice at the control interval and ramped across it; the exact `tan` is a sine-over-cosine from `PatchMath` (no libm).
5. **Global variants.** Any node can be switched to global (it runs once, in a one-voice view, even when no note sounds); a node whose inputs are all global is global by itself; **Voice sum** turns the per-voice signal into the global one (the fused sum is done by whoever reads it). Global Keys follows the held keys (last note priority with a held-key stack). Global LFO, Random and Macro run the same code on one lane.
6. **State across swaps.** A node of the new patch with the same id and kind takes the old node's state (phases, envelope stage, filter memory, delay lines, smoothers, LFO phase). Edits that change nothing audible leave the sound sample for sample (to 2e-3 in the test, from the ramp's trend removal).
7. **Feedback is one block (64 samples) of delay.** *One-sample feedback was not built.* The 20-node patch's loop (Filter, Feedback, VCA) is 12.8 % of its time in block mode (`PatchRuntime.Profile`); a sample-by-sample variant needs a second set of kernels with per-sample dispatch and per-sample coefficient work, which I estimate at 2 to 3 times those jobs, so about 1.3 to 1.5 times the patch: inside the lead's 2× rule, **so the spec keeps it** and it is NP2 or later work, not dropped. This is an estimate, not a measurement; it needs the lead's call. The Delay node's own feedback is sample accurate, which covers echoes and Karplus-style loops.
8. **Mix jobs are fused** into their consumers (Filter In, VCA In, Out, Mixer, Osc FM and the like): no job and no buffer for a sum.
9. **Idle detection** (mine): a released voice ends after two quiet blocks or 20 s of release; an Envelope's end does not mark the voice by itself (a patch with no envelope stops by level).
10. **Node cap 128** is a hard limit (`PatchGraph.Add` returns null; the text loader drops the rest).
11. **Presets** (mine): eight patches only (no parameter states beyond Volume and the two macros each uses), Volume chosen so the chord C3 E3 G3 B3 held for two thirds of a second has its loudest 50 ms at -22 dB RMS (±0.1); levels are therefore -13 to -5 dB on Volume, i.e. the patches are hot at 0 dB: Out levels stay at 1 so a patch built by hand is as loud as the node graph says.
12. **Machine without an editor** (mine): the Studio flavour is the portable build plus the studio kit with nothing to draw; the Windows gallery test lists Neo Patch after the Learn machines (its numbers do not move the others').

## 1.3 CPU

x64 Linux (the box's AVX2 core), load 1.1 to 2.2, `WhereTheTimeGoes` (ten of one node in a chain on 8 voices against the same patch without them; ms of CPU per node per second of audio, so 0.01 = 1 % of one core at 8 voices) and `ThePatchesAgainstTheirFloors` (Dye's reference-corrected figure, the 8-voice chord held):

| Node | ms per node per second | Node | ms per node per second |
|---|---|---|---|
| Osc (saw) | 1.08 | LFO | 0.56 |
| Wavetable Osc | 1.26 | Random | 0.38 |
| Sample | 7.29 | Slew | 0.47 |
| Noise | 1.46 | Sample and hold | 0.43 |
| Envelope | 0.27 | Shaper | 0.33 |
| Filter | 0.47 | Delay | 2.77 |
| VCA | 0.07 | Mixer | 0.09 |
| Math | 0.09 | Crossfade | 0.30 |

| Patch | x real time (8 voices) | Floors in the test (x64, arm64) |
|---|---|---|
| 8 nodes (Core8) | 142 | 65, 45 |
| 20 nodes (Big20, with the feedback loop) | 75 | 35, 25 |

NP0's 20-node figure was about 130 times (two-point band limiting and exact coefficients per interval were not in it): the band-limited oscillators, the ramped coefficients and the scopes cost about 40 % more than the spike did. The arm64 floors are set from the NP0 ratio of the two runners, not measured here; CI's figures for them are in the report. The Sample node is the expensive one (a scalar cubic read per voice and sample, with loop handling and a layer choice per control interval): worth lane kernels later.

## 1.4 Alias floor

`OscillatorsAreBandLimited`: a note held long enough for 65536 samples after the attack; the loudest bin more than 40 bins (29 Hz) from any harmonic of the fundamental, relative to the fundamental (dB):

| Shape and pitch | floor |
|---|---|
| saw 1250 Hz | -87.6 |
| square 1250 Hz | -89.0 |
| triangle 1250 Hz | -106.9 |
| saw 4400 Hz | -105.2 |
| square 4400 Hz | -105.2 |
| saw 9000 Hz | -137.2 |
| saw 14000 Hz | -106.8 |
| square 11000 Hz | -106.9 |

The target was -90 dB; two cases at 1250 Hz miss it by 1 to 2.4 dB, the test's limit is -85. The worst bin is one tone at 12000 Hz (a multiple of the block rate 750 Hz and of the default control interval's 3000 Hz; it does not move when the interval is 8, 32 or 64 samples); I did not find its cause, and it is the same level for the saw and the square at that pitch. Everything else is under -100 dB. To be looked at in NP2 with a spectrogram.

## 1.5 What it checks

`NeoPatchTests`: the compiler (cycles, order, dead nodes, cap, text round trip), each node kind's behaviour (oscillator levels and pitch, FM, PM, sync, pulse, wavetable tables, noise colours, every sample source, LFO frequency and depth, Random range, Slew, Macro, S&H, Shaper modes, Delay time, Mixer, Math, Crossfade, Out pan and spread), the audio-rate switch, global and per-voice LFO, the Voice sum and Global Keys, state across an edit, a swap's step, macros and MPE, determinism (two renders, every lane kind the same bits, the host's block size), 0 bytes allocated, NaN and runaway guards, the alias floors, and the throughput guards. The 20-node render's checksum is pinned (`PinnedBig20`) so Linux, Windows and Mac must agree.

## 1.6 Doubts

- The alias floor at 1250 Hz (§1.4), and the 12 kHz tone.
- One-sample feedback (decision 7) is an estimate.
- The arm64 floors are derived, not measured; the Throughput guard on the Mac runner will say.
- The Delay node's memory is 6 MB per per-voice node at 48 kHz (8 voices of 2 s); a patch with several per-voice Delay nodes is memory-heavy: make them global when they can be.
- Presets are patch-only and sit at -22 dB; the Out nodes' pan law (equal power) means a mono patch loses 3 dB per side: that is why the Volumes are around -13 to -5 dB.

---

# Part 2: NP0, the runtime gate (as built)

Status: spike, measured. The contract is `docs/specs/neo-patch.md` (§2 decisions 1–6, §4.3, §4.4, §5). This file records what NP0 built, what it measured, and what NP1 changes. Code: `Machines/NeoPatch/` (Engine, portable net10.0; not in `ReBuzz.sln`, not in the package), tests: `NeoBuzz.MachineTests/NeoPatchTests.cs`.

## 1. Verdict

**Go.** A 20-node, 8-voice patch costs 0.16–0.33× the closest equivalent sound in Dye Essence (the criterion is ≤ 1.5×; the worst figure is arm64 against Dye at 1×), on both runners, with 0 bytes allocated on the audio thread and bit-identical renders. The margin is large enough that NP1 can spend some of it on quality (§5) without reopening the gate.

## 2. What was built

| Part | File | What it does |
|---|---|---|
| Graph model | `PatchGraph.cs` | Nodes (7 kinds) with typed ports and controls in real units, wires with an amount, `Connect` refuses a cycle that doesn't pass through Feedback. Port types are advisory: any output feeds any input. |
| Compiler | `PatchCompiler.cs` | Orders the graph into block jobs (dependencies first, ties by node id), drops nodes that don't lead to an Out, builds one mix job per input that needs one (several wires, a wire amount, or a per-voice source into a global node: the voice sum), allocates every buffer and every voice's state. Feedback is two jobs, Read (outputs the last block's input) and Write (after its source), so a loop costs exactly one block. |
| Block jobs and kernels | `PatchJobs.cs` | 64-sample blocks, per-voice buffers sample-major `[sample][16 voices]`, so 8 voices are one vector. Kernels are generic over Dye's `ILanes` (`Lanes.cs`, linked as source: AVX2 on x64, two Vector128 on arm64, plain floats as the reference). |
| Runtime | `PatchRuntime.cs` | 16 voices through Neo's `VoiceAllocator` (poly, mono, legato, glide, sustain, steal with a 3 ms fade), per-block voice state into a context, the jobs, a one-block crossfade when a new compiled patch is taken (`Publish` is an interlocked exchange; `Compile` allocates and runs off the audio thread), DC blocker, soft limiter, NaN guard. |
| Headless host | `PatchHost.cs` | `Render(noteEvents, frames)`: notes in, two channels out; events land on the first block start at or after their sample. |
| Test patches | `PatchTemplates.cs` | `Core8()` (8 nodes) and `Big20()` (20 nodes, with FM through a VCA, an LFO, three filters and a feedback loop). |

Nodes: Keys (pitch, gate, velocity), Osc (saw, square, sine; FM input; 2-point or 4-point polynomial band limiting), Envelope (Dye's `NoteEnvelope`, linked as source: closed form per control tick, ramped across it), Filter (Simper's state-variable filter, Neo's `Svf.Prewarp` for the coefficients; low, band, high), VCA, Out (pan, level), Feedback.

Rules held: no allocation in `Run`; every kernel has `AggressiveOptimization`; only `PortableMath` for transcendentals (`PortableMathTests` scans `Machines/NeoPatch/Engine`); no `Vector<T>` (width-dependent); no FMA; sums across voices in Dye's fixed lane order.

## 3. Measurements

"× real time" is one core, wall clock, the same unit as Dye's G0 table (`dye-essence.md` §5.1); the figure beside it is process CPU time. Each pair is the best of 5 alternating runs of 4 s of audio in 256-sample host blocks, Dye's `NoteBank` and the patch in the same process.

### 3.1 The gate: cost against Dye Essence, 8 notes

| Patch | Dye Essence equivalent | x64 Dye | x64 Neo Patch | x64 cost | arm64 Dye | arm64 Neo Patch | arm64 cost |
|---|---|---|---|---|---|---|---|
| 8 nodes (saw FM'd by a square, SVF low-pass, 2 envelopes, VCA) | 2 Analog osc (FM warp), 1 SVF low-pass, 1× | 55.3× | 298.0× | **0.19×** | 25.6× | 129.8× | **0.20×** |
| 8 nodes | the same at 2× (Dye's Standard) | 35.0× | 297.0× | 0.12× | 13.9× | 130.1× | 0.11× |
| 20 nodes | 3 osc (one a silent FM sine, one a 2-voice unison for the detuned saw) + sub, 2 SVF low-passes, LFO and mod envelope on routes, 1× | 29.0× | 131.3× | **0.22×** | 19.0× | 57.0× | **0.33×** |
| 20 nodes | the same at 2× | 21.6× | 133.1× | 0.16× | 11.5× | 57.1× | 0.20× |

Linux x64 (AVX2): Fork CI run 43491606233. macOS arm64 (two Vector128): Fork Mac run 43491610175. Locally (load under 2.5) the 20-node ratios were 0.28 and 0.21, the 8-node 0.24 and 0.15.

The Dye equivalents are close in sound, not identical: Dye has no feedback loop and no third filter, and its filters and mixing are stereo (both channels through every filter, a pan per oscillator); the patch is mono until Out. Neither runs the other's feature set; the ratio is honest about cost per sound, not per feature. Dye's own figures are low because its 704-parameter machinery, its modulation matrix and its 3 × 16 lane groups are paid whatever the patch does.

### 3.2 Where the time goes (20 nodes, 8 voices, 4000 blocks, x64 local)

A block is 1333 µs; the whole patch takes about 10 µs of it on x64 CI (0.76 % of a core), 18 µs on arm64 (1.84 %). Share of the jobs' time, x64 CI (arm64 in the notes):

| Job | Share | Notes |
|---|---|---|
| Filter (3) | 31 % | the coefficient work per voice and tick (`Exp2` for the cutoff CV, `Tan`, one division) is more than the recurrence (arm64: 13 %) |
| Osc (6) | 33 % | 4-point band limiting; the 2-point version is 7 % cheaper in total on x64 (141× against 132×). **arm64: 70 %** of the time, and 2-point is 26 % cheaper (68× against 51×): the polynomial pieces are all selects, and the two-half vectors pay for each |
| Mix (6) | 13 % | input sums and the voice sum; a wire with an amount ≠ 1 forces one |
| Env (4) | 13 % | one `Advance` per voice per tick, scalar |
| VCA (4) | 5 % | |
| Feedback (2), Keys, Out | 5 % | |

Scaling, × real time on CI x64 (arm64): 1 voice 168× (59), 4 voices 151× (57), 8 voices 132× (54), 16 voices 73× (28). The sample-major layout makes 1–8 voices one vector group and 9–16 two; groups with no sounding voice are skipped.

### 3.3 The control interval (the cost of audio-rate CV)

Spec decision 3 says signals are audio rate everywhere. The kernels read pitch, cutoff and envelope levels every *tick* and ramp between ticks; the tick is a setting.

| Control interval (samples) | 64 | 32 | 16 | 8 | 4 | 2 | 1 |
|---|---|---|---|---|---|---|---|
| 20 nodes, 8 voices, × real time, CI x64 | 166 | 153 | 133 | 104 | 72 | 46 | 26 |
| the same, CI arm64 | 61 | 60 | 56 | 51 | 43 | 32 | 22 |

Audio-rate cutoff (interval 1) costs 5.1× the interval-16 patch on x64 and 2.6× on arm64: the per-voice coefficient work (an `Exp2` and a `Tan` per voice per filter per sample) dominates. Even so it is 3.9 % (x64) and 4.6 % (arm64) of a core for 8 voices, inside any budget. The default is 16 (Dye's tick); NP1 can offer 8 or 4 at High quality, and a cheaper coefficient (see §5) would make audio rate affordable.

### 3.4 Other gate criteria

| Criterion | Result |
|---|---|
| Allocation on the audio thread | **0 bytes** over 1500 blocks of notes (10 notes on 8 voices: steals), glide, control edits every 7th block, and two patch swaps (`AllocationCheck`, `GuardPolicy`). |
| Determinism | Two renders identical; Scalar, Pair and Avx2 lanes give the same bits; the 20-node render's checksum `9D77BCC6A1411EF0` is pinned and must be the same on x64 and arm64; the output is the same for host block sizes 64, 256, 117, 16 and 1. |
| Voices independent | A two-note chord equals the sum of its two notes to 4e-7 (float rounding of the sum). |
| Feedback | Exactly one block (64 samples) of delay; a cycle without Feedback is refused at connect time and leaves the graph as it was. |
| NaN and denormals | A NaN gain, an infinite cutoff and a resonance of 1e9 injected behind the controls' clamps: output stayed finite and within ±1 for 400 blocks and the patch played again afterwards. Guards: the voice sum zeroes non-finite and runaway values per voice, filter and feedback state are flushed and cleaned each block, the master stage guards again. |
| Swap | Replacing the 8-node patch with the 20-node one under 4 held notes: largest sample step across the swap 0.074, before it 0.092 (one-block crossfade; the new patch's voices start fresh). |
| Steals | 12 notes on 4 voices: no click (largest step 0.23 against rms 0.27). |

### 3.5 Quality: band limiting (not part of the gate, but it decides NP1)

Loudest bin more than 10 bins away from any harmonic (Hann window), relative to the fundamental, x64 (arm64 measured the same to the decimal):

| Osc | 1250 Hz | 4400 Hz |
|---|---|---|
| saw, 2-point polynomial band limiting | −39.1 dB | −29.8 dB |
| saw, 4-point (the default) | −50.9 dB | −43.5 dB |
| square, 4-point | −50.9 dB | |
| sine (series to the 11th power) | −71.3 dB (Hann leakage; the series error is −145 dB) | |
| Dye Essence Analog saw (mip tables), for reference (`dye-essence.md` §5.1) | −93 … −101 dB | |

The worst alias is a mirrored high harmonic near 19–20 kHz. A BLEP-class oscillator will not reach Dye's floor; Dye's tables do.

## 4. Decisions taken that you might want different

1. **Voice allocation is Neo's `VoiceAllocator`, not `NoteBank`.** `NoteBank` is Dye's renderer (note slots, oscillator packing, filters); the allocator (poly, mono, legato, glide, steal fades) is what `DyeEngine` uses on top of it and stands alone. Neo Patch uses the same allocator, so notes behave as in Dye.
2. **Envelope is Dye's `NoteEnvelope`; the filter is Neo's SVF.** The envelope is linked as source and used unchanged. Dye's own filter kernels (`NoteFilters`) are written over Dye's note layout and stereo; the SVF's recurrence is re-implemented over lanes with the same operation order, and its coefficients come from `Svf.Prewarp`.
3. **A pitch input is read at control ticks**, FM at audio rate, as a linear frequency modulation of the increment (through zero is not supported for saw and square: the increment is floored at a tiny positive value).
4. **Global scope is Out and Feedback only.** The spec's global LFO/Random/Macro are not in the six nodes; NP1 runs the same kernels on one lane.
5. **Controls are read live from the node's array** (a plain float write, heard at the next block) with no smoothing; the patch ignores NaN and clamps in `Node.Set`.

## 5. What NP1 should change

1. **Oscillator band limiting** is the one quality gap, and the one place arm64 is expensive (70 % of the patch). Before adding quality, make the 4-point polynomial cheaper: it is non-zero only within two samples of the wrap, so a group whose lanes are all away from it skips the four pieces (eight voices at different pitches are near a wrap on about a third of the samples); and use Dye's integer phase so the window test is an integer compare. Options, cheapest first: keep 4-point BLEP as "Standard" (−51 dB at 1.25 kHz); add 2× oversampling for the sources that need it (the 2× path exists in Dye: Dye's halfband and the tick grid carry over); or read Dye's mip tables (`TableArena`, Analog's saw and square) for saw and square, which is also the planned Wavetable Osc, at −93 dB for Dye's cost per lane. The gate has room: the Osc jobs are 31 % of a patch that costs a quarter of Dye.
2. **Cheaper filter coefficients.** `Exp2` and `Tan` per voice and tick are most of the Filter job. Per-block exact coefficients plus a per-tick polynomial for the cutoff CV (a rational `tan` and a polynomial `2^x` on the CV only) would make an audio-rate cutoff cost about what interval 4 costs now.
3. **Fuse the simple nodes.** A wire with an amount ≠ 1 or two wires into one input currently build a Mix job and a buffer (6 of 27 jobs, 11 % of the time). Folding the amounts into the consuming kernel (Filter In, VCA In) removes most of them; Math, Mixer and Crossfade should be compiler-level fusions too.
4. **Global variants** of Osc, Envelope, LFO, Random, Macro on one lane (decision 4 above), and per-voice × global broadcast inputs, which the mix job already handles.
5. **State across swaps.** A swap restarts the sounding voices' state in the new patch (crossfaded). For edits that keep a node's kind and id, carry its state over so adding a node under a held chord doesn't retrigger the envelopes.
6. **One-sample Feedback** (High quality) needs a sample-by-sample loop over the strongly connected component around the Feedback node; it can't use the 64-sample jobs and lane groups the way the rest does. Not built; the gate doesn't need it, and it should be sized (a loop of N nodes at audio rate for 8 voices) before the spec promises it.
7. **Smoothing and macro mapping.** Control edits should glide (Neo kit parameter glides) before 16 macros drive them; allow for a small rise in cost.
8. **Stereo.** The patch is mono until Out. A per-voice pan or stereo ports would double the lane work after the split; decide before the node set is frozen.
9. **Idle detection by output level** (two quiet blocks, or 20 s of release) works for envelope-gated patches; a patch whose Out never falls silent after its gate (a drone) keeps its voice until the 20 s timeout. NP1 should let an Envelope's end mark the voice as finished.
10. **CPU readout for the editor**: `PatchRuntime.Profile` already times each job; a cheaper per-block timer gives the CPU number in §4.5.

## 6. Picking it up

`PatchHost` is the harness: build a `PatchGraph` (or `PatchTemplates.Big20()`), `new PatchHost(graph, voices)`, `Render(events, frames)`. `dotnet test NeoBuzz.MachineTests --filter NeoPatchTests`. The CPU figures are reported by `TheTwentyNodePatchAgainstDyeEssence` and `WhereTheTimeGoes`.
