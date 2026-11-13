# Neo Patch NP0: the runtime gate (as built)

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
