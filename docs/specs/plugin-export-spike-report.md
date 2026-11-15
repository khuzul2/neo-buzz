# experiments/plugin-export: Neo Filter as a CLAP plug-in written in C#, DSP and editor as two NativeAOT libraries

Branch `experiments/plugin-export-px0`. Full measurement run (300 s per configuration, Linux x64 + macOS arm64 + Windows x64):
workflow run **43704017271**, all three jobs green, on code head `679534d` (later commits changed only the AU CI step and this report).
Code: `experiments/plugin-export/` + `.github/workflows/plugin-export.yml` (push-driven on this branch only). Not in the sln or the package.

## Verdict (plain)

**Editor path A works: an allocating managed editor in its own NativeAOT runtime cannot stall the audio thread of the DSP library.**
Same workload, same harness, one runtime versus two:

| 300 s, 16 instances, 64-sample blocks | one runtime, 1 MB/s | **two runtimes, 1 MB/s** | one runtime, 20 MB/s + 256 MB heap | **two runtimes, same load** |
|---|---|---|---|---|
| Linux x64: worst callback (control 638 / 701 us) | 1012 us (1 stall) | **699 us (0)** | 26,743 us (18 misses) | **711 us (0)** |
| Windows x64: worst callback (control 1051 / 1233 us) | 1386 us (1 miss) | **921 us (0)** | 11,572 us (142 misses) | **1,232 us (0)** |
| macOS arm64: stalls > noise baseline + 0.5 ms (runner noise: control worst 27 to 74 ms) | 3 | **0** | 525 | **2** |

The DSP library's own runtime did **0 garbage collections in every control and editor run** (5 runs x 3 OSes x 300 s) with the editor library loaded and allocating up to 6.6 GB over 5 minutes
(editor runtime: up to 380 GCs on Windows, 1,012 on macOS, longest editor pause 15.5 ms Windows / 46 ms Linux / 135 ms macOS, 3 gen2 per run). Those pauses stopped the editor, not the audio thread.
In the one-runtime control the same load stalled the audio thread by up to 26.7 ms (Linux) and 11.6 ms (Windows) with 142 to 525 deadline-relevant callbacks.

Not attributable on macOS: the runner is noisy (control alone: p99.9 4.4 ms, worst 27 to 74 ms, 3,000 to 4,300 callbacks over the period with no GC anywhere); that table is only usable for the order of magnitude (525 vs 2), as in plugin-gc.

## What was built

- `dsp/` (**neofilter_dsp**, NativeAOT, 1.1 MB): Neo Filter's own engine (`Machines/NeoFilter/Engine` + `NeoBuzz.Dsp`, compiled in as source, unchanged) behind the CLAP ABI written in C#
  (`[UnmanagedCallersOnly]` exports + blittable structs, no C++): descriptor, plug-in factory, audio ports (one stereo in/out, in-place pair), 18 parameters (id = Neo index, value = Neo raw value, names, group as module,
  stepped/enum flags, `value_to_text` with units "2.00 kHz", "-6.0 dB", "90 deg", `text_to_value`), state save/load (80 bytes, "NFC1" + raw values), latency, transport (tempo and song position drive the LFO's Song retrig),
  sample-accurate parameter events, and the gui extension. `Process` allocates nothing (0 bytes over 397 blocks, checked in the self-test).
- `editor/` (**neoeditor**, NativeAOT, 1.1 MB): a separate library with its own runtime, loaded by the DSP library on `gui.create` (`NativeLibrary.Load` of the file next to itself). For the spike it does not draw: it runs what a Neo editor does while open
  (60 Hz loop reading values and meters, formatting every parameter's text, keeping recent state alive), plus the plugin-gc load generator (1 MB/s, 20 MB/s, 256 MB heap) selectable by `NEO_EXPORT_EDITOR_LOAD`.
  **The Neo editor itself (INeoCanvas, offscreen) was not rendered**, and `gui.set_parent` creates no window; the brief allowed the workload variant.
- `shared/Shared.cs`: the only thing the two runtimes share is one blittable struct in native memory (current parameter values, input/output peak meters, a 64-entry single-producer single-consumer edit ring, the editor runtime's GC counters). No managed object crosses; verified on all three OSes: edits posted by the editor come back as parameter changes on the audio thread (3 of 3 in 3.5 s), meters flow the other way.
- `native/` (30 lines of C): the exported `clap_entry` **data symbol**. A C# export cannot be a data symbol (NativeAOT exports functions), so three C functions forward to the C# exports `neo_clap_init/deinit/get_factory`.
  clap-wrapper's "clap first" helper links this static library into every format, so the .clap, the VST3 and the AU are the same code. This is the one place the plug-in is not C#.
- `bench/clapbench.cpp`: CLAP host for the measurement (plugin-gc's timing method: real-time thread, absolute deadlines, 16 instances, parameter events every block, 10 s warm-up) and a `--selftest` through the CLAP ABI.
- `ci/null-test.py`: our Neo Plug FX host in rebuzz-headless renders the native Neo Filter and the exported one (CLAP and VST3) side by side from one Neo Poly and subtracts the probes.
- `selftest/`: the exports run in-process under CoreCLR against the engine used directly (layouts against the CLAP headers' sizes, parameters, state, events, 400 blocks bit-identical, 0 allocation, editor ring).

## Measurements

Per configuration 300 s (225,000 callbacks of all 16 instances), 10 s warm-up, SCHED_FIFO on Linux (sudo), TIME_CRITICAL + MMCSS on Windows, time-constraint policy on macOS.
"one runtime" is the plugin-gc situation (the load thread runs inside the DSP library's runtime), "editor" is path A (the load runs in the editor library). A stall is a callback overlapping a GC that exceeds the non-GC p99.9 by more than 0.5 ms.
All times microseconds.

### Linux x64 (ubuntu-latest, Xeon 6975P-C), period 1333 us

| label | load | mean | p99 | p99.9 | p99.99 | worst | misses | stalls |
|---|---|---|---|---|---|---|---|---|
| ctl-start | none | 321.8 | 340.2 | 406.2 | 449.5 | 637.8 | 0 | 0 |
| editor-L0 | UI loop only | 320.8 | 334.8 | 391.9 | 420.0 | 620.2 | 0 | 0 |
| editor-L1 | 1 MB/s | 322.5 | 339.0 | 390.7 | 425.1 | 699.1 | 0 | 0 |
| editor-L3 | 20 MB/s + 256 MB | 324.2 | 340.7 | 425.0 | 592.7 | 710.8 | 0 | 0 |
| oneruntime-L1 | 1 MB/s | 321.7 | 336.9 | 376.2 | 406.3 | 1011.6 | 0 | 1 |
| oneruntime-L3 | 20 MB/s + 256 MB | 326.4 | 340.3 | 389.5 | 681.9 | 26743.2 | 18 | 18 |
| ctl-end | none | 324.2 | 340.6 | 367.0 | 411.7 | 701.3 | 0 | 0 |

| label | DSP runtime GCs (g1/g2) | DSP pause max ms | editor runtime GCs (g1/g2) | editor pause total / max ms | editor alloc MB |
|---|---|---|---|---|---|
| editor-L1 | 0 | 0 | 3 (2/0) | 2.85 / 1.45 | 293 |
| editor-L3 | **0** | 0 | 79 (41/3) | 1382.8 / 46.0 | 6,620 |
| oneruntime-L1 | 3 (2/0) | 1.72 | 0 | | |
| oneruntime-L3 | 78 (40/2) | 46.6 | 0 | | |

### Windows x64 (windows-latest), period 1333 us

| label | load | mean | p99 | p99.9 | p99.99 | worst | misses | stalls |
|---|---|---|---|---|---|---|---|---|
| ctl-start | none | 453.7 | 622.2 | 687.8 | 891.0 | 1050.9 | 0 | 0 |
| editor-L0 | UI loop only | 461.7 | 626.7 | 656.9 | 765.0 | 943.7 | 0 | 0 |
| editor-L1 | 1 MB/s | 457.1 | 622.0 | 654.5 | 778.6 | 920.5 | 0 | 0 |
| editor-L3 | 20 MB/s + 256 MB | 451.6 | 616.7 | 760.5 | 947.9 | 1231.6 | 0 | 0 |
| oneruntime-L1 | 1 MB/s | 447.9 | 606.2 | 649.5 | 989.4 | 1386.0 | 1 | 0 |
| oneruntime-L3 | 20 MB/s + 256 MB | 457.8 | 621.7 | 683.1 | 9214.6 | 11571.5 | 142 | 140 |
| ctl-end | none | 452.6 | 620.1 | 660.0 | 964.6 | 1233.2 | 0 | 0 |

| label | DSP runtime GCs (g1/g2) | DSP pause max ms | editor runtime GCs (g1/g2) | editor pause total / max ms | editor alloc MB |
|---|---|---|---|---|---|
| editor-L1 | 0 | 0 | 19 (18/0) | 10.8 / 0.89 | 293 |
| editor-L3 | **0** | 0 | 380 (129/3) | 2412 / 15.5 | 6,620 |
| oneruntime-L1 | 18 (17/0) | 0.84 | 0 | | |
| oneruntime-L3 | 381 (129/3) | 14.4 | 0 | | |

### macOS arm64 (macos-15, M1 virtual), period 1333 us. Runner noise dominates (see above).

| label | load | mean | p99 | p99.9 | p99.99 | worst | misses | stalls |
|---|---|---|---|---|---|---|---|---|
| ctl-start | none | 670.2 | 1545 | 4418 | 8565 | 26831 | 3061 | 0 |
| editor-L0 | UI loop only | 776.4 | 1629 | 4515 | 10466 | 47380 | 4016 | 0 |
| editor-L1 | 1 MB/s | 871.2 | 1496 | 3285 | 8935 | 29298 | 4165 | 0 |
| editor-L3 | 20 MB/s + 256 MB | 875.9 | 1429 | 3313 | 12919 | 24905 | 3650 | 2 |
| oneruntime-L1 | 1 MB/s | 869.3 | 1454 | 3764 | 11360 | 112534 | 3786 | 3 |
| oneruntime-L3 | 20 MB/s + 256 MB | 870.3 | 1520 | 5816 | 12089 | 73952 | 3681 | 525 |
| ctl-end | none | 869.9 | 1533 | 3901 | 10488 | 74292 | 4273 | 0 |

| label | DSP runtime GCs (g1/g2) | DSP pause max ms | editor runtime GCs (g1/g2) | editor pause total / max ms | editor alloc MB |
|---|---|---|---|---|---|
| editor-L1 | 0 | 0 | 48 (24/0) | 70.2 / 8.7 | 288 |
| editor-L3 | **0** | 0 | 1012 (517/3) | 4102 / 135.4 | 6,614 |
| oneruntime-L1 | 48 (24/0) | 8.5 | 0 | | |
| oneruntime-L3 | 1011 (517/3) | 34.1 | 0 | | |

Reading it:
- The audio thread's distribution in "editor" rows equals the control rows on Linux and Windows (p99.99 within the control's own ctl-start/ctl-end spread, no callback over the period). Both runtimes are in one process and compete for the same cores; the editor's GC pauses of 15 to 46 ms (heavy heap) do not show.
- The one-runtime rows reproduce plugin-gc's finding with a lighter engine (Filter x16: 22% of the period against Poly/Drive's 58%): 1 MB/s is a pause of 0.8 to 1.7 ms that only sometimes lands in a callback; the 256 MB heap case stalls the audio thread by 10 to 27 ms (Linux and Windows).
- macOS: 525 attributable stalls with one runtime against 2 with two, worst GC callback 34.9 ms vs 6.1 ms; but the control rows already drift (mean 670 to 870 us) so only that order of magnitude is trustworthy.
- The "callbacks overlapping an editor GC" column in the job summary uses a 1 ms sampler on the editor's counters and over-counts; use the timing columns.

## Wrap

- **clap-wrapper** (MIT, pinned to commit 806bba0 = 0.17.0, October 2026) `make_clapfirst_plugins`: one CMake call builds the CLAP, the VST3 and (macOS) the AU from the same stub. **It needs the VST3 SDK 3.8.0 (the tag it pins), not 3.8.1**: with 3.8.1 `wrapasvst3.h` fails to compile (`iid` ambiguous). Our host uses 3.8.1; both are MIT.
- The AOT libraries (`neofilter_dsp`, `neoeditor`, ~1.1 MB each; the .so/.dll/.dylib) are copied next to every module (`ci/stage.py`; macOS: `Contents/MacOS`, then ad-hoc `codesign --deep`); the stub finds them from its own path with `dladdr` / `GetModuleHandleEx`. Total per format about 2.4 MB.
- **Loaded by our Neo Plug host on Linux** (rebuzz-headless, `Neo Plug FX`): both `neo-buzz/Neo Filter (CLAP)` and `neo-buzz/Neo Filter (VST3)` are scanned (out of process, `neoplug-scan`), listed, added, show 18 parameters with units, take parameter changes, save and reload with the song.
- The VST3 renders the **same bits as the CLAP** in all three phases (clap-wrapper adds no processing).
- **AU (macOS arm64)**: `Neo Filter.component` (clap-wrapper `auv2`, subtype `NFlt`, manufacturer `NBuz`, ad-hoc signed after staging) passes **`auval -v aufx NFlt NBuz`: "AU VALIDATION SUCCEEDED"**
  (run 43707270394; the registrar needed a restart to see the component, and the bundle had to be re-signed after the AOT libraries were copied in). Two NativeAOT runtimes live in auval's process.
- **VST3 and CLAP on macOS and Windows**: built, staged, and the CLAP loaded and played by the bench there (the self-test and the measurement); not loaded by a VST3 host on those two OSes (our Neo Plug host test is Linux-only in this spike).

## Null test against the native machine (Linux CI, our own Neo Plug host, rebuzz-headless)

One Neo Poly chord into three filters in parallel; probes of their outputs subtracted (44.1 kHz, 5 s, stereo float, 441,480 samples).

| case | CLAP - native | VST3 - native | VST3 - CLAP |
|---|---|---|---|
| defaults, whole render | max 3.65e-13 (-249 dBFS), 708 of 441,480 samples differ, **bit-identical in every sample outside the ring-out tail** | same | **bit-identical** |
| Type SVF LP, Cutoff 5000, Resonance 500, after the first 0.5 s | max 8.1e-7 (-122 dBFS), 17,324 samples differ, all in the ring-out of the last note (3.5 to 4 s); bit-identical from 0.5 s to 3.5 s | same | **bit-identical** |
| same, first 0.5 s | max 0.72 (start-up glide, see below) | same | bit-identical |
| song saved, reopened, after the first 0.5 s | max 1.29e-4 (-78 dBFS) | same | bit-identical |

Tolerances the test enforces: settled parts 3e-6 (-110 dBFS), reloaded song 3e-4 (-70 dBFS). Parameter texts of all 18 parameters are identical between the native machine and both formats after the reopen.

Why the three differences exist (all in the engine's behaviour, none in the export):
1. **Ring-out tail**: the native machine goes idle (`Engine.Idle`, then `Work` returns false) and writes exact zeros after the input has been silent for a while; the plug-in keeps processing the decaying ring, so a few hundred samples at -122 to -249 dBFS differ. (Probable cause; the idle gate was not instrumented.) A host that wants the same can use `CLAP_PROCESS_CONTINUE_IF_NOT_QUIET`; the spike returns `CONTINUE`.
2. **Start-up glide**: the engine smooths parameter changes (~50 ms to 0.25 s). The native machine and the plug-in see the new values at slightly different moments of the first block after activation, so the glide differs for 0.25 s (0.72 at a resonant peak). By 0.5 s the states are bit-identical.
3. **Glide history after a reload (1.3e-4)**: an engine that glided from the defaults to its parameters and one that started at them differ by ~1e-4 *for good* (the smoothers rest at slightly different values). The self-test reproduces it with the engine alone (1.05e-4 on white noise). After a song reload the native machine glides (constructed with defaults, values loaded after) while the plug-in's state is loaded before activation and starts settled. This is the engine's history dependence, worth knowing for any host (and a candidate fix in `FilterEngine`: snap the smoothers to their targets when a value changes while stopped, which would also make "Song retrig" renders independent of history).

## Findings

- NativeAOT + `[UnmanagedCallersOnly]` is enough for the whole CLAP ABI, including the gui extension, with no C# runtime support code and no allocation in `process`. The one exception is the exported data symbol (above).
- **Two NativeAOT runtimes in one process work on Linux, macOS and Windows** (in the bench, in rebuzz-headless through Neo Plug and its scanner, and in `auval`); no signal-handler or loader conflicts were seen. Neither can be unloaded (`clap_entry.deinit` is a no-op; the libraries stay for the process).
- The editor library is loaded only when the editor is created: with the editor closed the process has one runtime and zero GCs, and an editor opened later costs only its own runtime (~1 MB of code; load time not measured).
- Run-time cost of the second runtime: none visible in the DSP callback; memory not measured.
- CLAP parameters carry the knob's raw Neo scale (a frequency's raw value is its position on the log scale), so host automation curves match the editor's; units come from `value_to_text`. CLAP has no unit field.

## Not done / known limits

- **No real editor**: the Neo editor (INeoCanvas) is not rendered, no window is created (`set_parent` is a no-op). The editor library runs an allocating UI-shaped workload instead. Embedding a real GUI is the next step and the harder one (Avalonia or a native canvas on three window systems).
- Only Neo Filter; the stereo effect case. No note ports, no MIDI, no multi-output, no tail/latency extensions beyond latency 0, no preset-load, no parameter-change output events (the editor's edits go through the ring and are applied on the audio thread but are not announced to the host, so host automation would not record them: needs `clap_output_events`/gesture events).
- The native entry stub is 30 lines of C; there is no pure-C# route for the data symbol.
- Measurement: single 5-minute run per row; the Windows and Linux runners are VMs with the harness thread at real-time priority; the macOS runner is too noisy below ~5 ms. No physical-machine run, no real audio device, no host process (Ableton Live was not available), 16 instances at 64 samples only.
- The VST3 and AU were loaded by our host (VST3, Linux) and `auval` (AU, macOS) only, not by Ableton Live or another commercial host.
- Windows `.vst3` is a single file with the two DLLs beside it (not a bundle folder); installers must put them together. macOS needs signing (ad hoc here; Developer ID and notarisation for distribution).

## Decisions the user might take differently

- Raw Neo values as CLAP parameter values (instead of normalised 0..1 or real units): chosen for identical automation curves and exact state round trips.
- clap-wrapper pinned to 0.17.0 with VST3 SDK 3.8.0, not 3.8.1.
- CLAP "clap first": the CLAP is the primary build and the wrappers link the same stub (no separate VST3 code).
- Bench `mode inproc` as the baseline instead of plugin-gc's Poly/Drive numbers: same engine and harness for both sides.
