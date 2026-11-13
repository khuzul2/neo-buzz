# experiments/plugin-gc: does GC stall the audio thread of a NativeAOT C# plugin?

Branch `experiments/plugin-gc`, head `b1ac5e4` (contains origin/main `c118ae77`; nothing newer on main at hand-back).
Full run (300 s per configuration, both platforms): workflow run **43518725514**, all 8 jobs green
(Windows x64 `windows-latest`, macOS arm64 `macos-15`; 4 GC settings per platform). Smoke runs (20 s) before it: 43514001745, 43515260790 (cancelled part-way), 43517045597.
Code: `experiments/plugin-gc/` (+ `.github/workflows/plugin-gc.yml`). Not in the sln or the package. Numstat with and without `--ignore-cr-at-eol` identical (878 / 0).
Logs hold the RESULT lines and a summary table per job; `experiments/plugin-gc/summarize.py` renders them.

## Verdict (plain)

**Criterion: no audio-thread stall over 0.5 ms attributable to GC over the run, 64-sample buffers, 1 MB/s load. It is NOT met.**

- Windows x64, default settings, 1 MB/s: 18 GCs in 5 minutes, longest pause 3.2 ms; one callback grew by 2.6 ms over the non-GC baseline (worst callback 4.2 ms, GC flagged). Nonconcurrent and SustainedLowLatency: same (18 GCs, 3.4 to 3.5 ms pause, excess 2.9 and 3.3 ms). Only the 64 MB gen0 budget showed none (4 GCs, longest pause 1.4 ms), and that is fewer chances, not a shorter stall: every pause was still above 0.5 ms.
- macOS arm64, 1 MB/s: 48 GCs (4 with the 64 MB gen0), longest pause 1.9 to 4.8 ms depending on setting (default 2.2 ms); 0 to 3 callbacks over the non-GC baseline, by 0.3 to 3.6 ms (default: 3 callbacks, +1.4 ms). The runner's own noise on macOS is larger than that (see below), so the macOS figure is a lower bound, not a clean pass or fail.
- 20 MB/s: still 1 to 2 clear stalls on Windows (+1.4 to +9.1 ms) in 5 minutes (359 GCs; the stall only shows when the audio thread is inside managed code or re-enters it during the pause: 238 callbacks overlapped a GC, 40 of them missed the deadline).
- Big gen2 heap (256 MB live, churned) with 20 MB/s: fails by a wide margin everywhere. Windows default: 218 callbacks stalled, worst +10.7 ms (callback 12.3 ms), pauses to 11.5 ms, 2.3 s of total pause in 5 minutes. Windows nonconcurrent: one pause of 234 ms (callback 80 ms). macOS nonconcurrent: one pause of 492 ms.
- **With no allocating managed thread (load 0) there were zero GCs in 5 minutes** (225,000 callbacks, 16 instances, automation every block, chord changes every second), on both platforms and in all four settings. So the audio runtime itself never triggers a GC: the risk comes only from other managed threads in the same runtime.

Noise attribution: on Windows the native control's worst callback is 1.09 ms against a 0.52 ms mean (about +0.57 ms), so stalls of 2 to 20 ms are well outside runner noise and the Windows findings are solid. A stall around 0.5 ms would not be resolvable on this runner. On macOS the control alone shows p99.9 2.7 ms and worst 12 ms (one control run had a 662 ms hiccup), so only the large macOS stalls (tens of ms) are attributable by timing; for small ones the GC pause durations the runtime reports are the better evidence.

## Recommendation

**Option 2 in spirit: keep the C# DSP (NativeAOT, native shell around it), but do not run an allocating managed editor in the same runtime as the audio thread.** C# all the way (option 1, managed editor in the plugin process) fails the criterion at the realistic 1 MB/s load and badly at a heavy UI or a large heap, and none of the knobs rescued it:

| setting | effect (Windows, 64 samples) |
|---|---|
| nonconcurrent | no help: same pauses at 1 MB/s; gen0 budget becomes 128 MB, so half the GCs at 20 MB/s but one pause of 234 ms with the big heap |
| SustainedLowLatency | no measurable effect (same GC counts and pauses as default; it only affects gen2 timing) |
| gen0 budget 64 MB (`DOTNET_GCgen0size=4000000`, hex) | fewer GCs (4 vs 18 at 1 MB/s; 89 vs 359 at 20 MB/s) and less total pause, but each pause is longer at 20 MB/s (9.9 ms vs 3.2 ms): the worst case gets worse, the frequency better |

What does work, from the data: no allocation, no GC. Three ways to get there, in order of how much I would trust the evidence:
1. Editor out of the audio runtime (native UI, or a separate editor process; the H0 spike's sandbox/IPC path already exists). The audio runtime then has the property measured in the load-0 rows: zero GCs.
2. A managed editor in the same runtime but allocation-free in steady state (pooled, `GC.TryStartNoGCRegion` during playback). Not measured here; it is the cheap thing to measure next if keeping a managed in-process editor matters.
3. Accept GC pauses and size buffers at 256+ samples: pauses seen were 1.4 to 11 ms (heavy heap: up to 234 to 492 ms outliers), so this does not close the gap for the big-heap case.

## Method (what was built)

- `experiments/plugin-gc/plugin/`: one NativeAOT shared library (`neoplugin.dll` / `.dylib`) with a plain C ABI (`[UnmanagedCallersOnly]` exports, `host/np_abi.h`): create, param, note on/off, process, GC counters, load generator control. It compiles the real engines (`Machines/NeoBuzz.Dsp`, `Machines/NeoPoly/Engine`, `Machines/NeoDrive/Engine`) as the test project does. No editor.
- Why a bare C ABI and not NPlug or CLAP: the question is about the runtime and the audio thread, not the plugin format. A C ABI with CLAP-shaped calls is the fastest faithful native test: the host thread enters managed code through the same NativeAOT reverse-P/Invoke path any format would use. Format-specific parts (VST3/CLAP descriptors, UI embedding) do not change the GC behaviour. NPlug would add a dependency and a build without changing what is measured.
- Load generator (managed thread, in the plugin): garbage at a set rate (objects 64 B to 2 KB, a 2048-entry ring keeps a little alive, like UI state). Modes: none; 1 MB/s; 20 MB/s; 20 MB/s plus a 1,000,000-node live graph (about 256 MB) with 1 MB/s of it replaced each second (old nodes die in gen2) and a 2 MB LOH buffer each second.
- GC observation: `GC.CollectionCount(0|1|2)` (gen0 count includes every GC), `GC.GetTotalPauseDuration()`, longest pause from a 1 ms sampler thread over `GC.GetGCMemoryInfo` (a lower bound if several GCs fall in one millisecond), all exported allocation-free. A callback is "GC" when the count differs before and after it.
- Host harness (`host/harness.cpp`, C++, no .NET): `dlopen`/`LoadLibrary`, an audio thread at the highest priority available (Windows: `TIME_CRITICAL` plus MMCSS "Pro Audio", 1 ms timer resolution; macOS: Mach time-constraint policy, 80% computation, 100% constraint), one callback per block paced to the wall clock (absolute deadlines; Windows sleeps then spins, macOS `mach_wait_until`), 64 and 128 samples at 48 kHz, 16 instances: 8 Neo Poly (4-note chords, retriggered every second, F1 cutoff and unison detune automated every block) and 8 Neo Drive (Drive and Tone automated every block, 2x oversampling, auto-gain off). 10 s warm-up not recorded. A callback is the whole 16-instance block including the automation and note calls.
- Control: `host/control.cpp`, a native plugin with the same ABI, similar cost (mean 0.52 ms vs 0.77 ms per 64-sample block on Windows), and, under load, a native malloc/free thread with the same allocation rates and a 256 MB live set. No GC.
- GC settings: default (workstation, concurrent); `DOTNET_gcConcurrent=0`; `GCSettings.LatencyMode = SustainedLowLatency`; `DOTNET_GCgen0size=4000000` (hex, 64 MiB). Effective values reported by the plugin (`GC.GetConfigurationVariables`): gen0 max budget Windows 16 MB default, 128 MB nonconcurrent, 64 MB gen0size; macOS 6 MB default, 128 MB, 64 MB.
- Metrics beyond the requested ones, because engine cost bursts make the deadline count ambiguous: "stalls over median" (GC callbacks more than 0.5 ms above the run's median, an upper bound that can include coincident engine bursts) and "GC stalls over non-GC p99.9 + 0.5 ms" (a lower bound: a GC callback must exceed what 99.9% of non-GC callbacks do, plus 0.5 ms). The truth for the 0.5 ms criterion lies between them.
- CI shape: push-triggered on this branch (dispatch needs the workflow on main; `workflow_dispatch` is also defined and will work after a merge). Plain push: Windows, 20 s; `[mac]` in the commit message adds macOS; `[full]` runs both at 300 s. Per job: control 64 (no load), control 64 and 128 (native churn), then 2 buffer sizes x 4 loads for one GC setting; 4 jobs per OS.
- Local: NativeAOT could not be built on the Linux box (no ILCompiler package in the offline feed); the managed library was compile-checked there, the native harness and control were run locally, the AOT build and all measurements were iterated in CI.

## Caveats and doubts

- **Engine cost is high at 64 samples.** Mean 0.77 ms of a 1.33 ms period on one Windows runner core, with bursts to 2.1 ms (0.2% of callbacks, 417 of 225,000, over the period with no GC at all). Those misses are not GC and not runner noise (the control stays under 1.1 ms); the deadline-miss columns therefore mix engine bursts with GC. Use the GC-attribution columns for the verdict. Which block in Poly or Drive causes the burst was not isolated.
- Side finding, not isolated: Neo Drive re-runs a 4096-sample auto-gain probe (`EstimateCompensation`) on every parameter change. With auto-gain on and Drive automated each block, the first CI run (4x oversampling) averaged 2.35 ms per 64-sample block against 0.76 ms with auto-gain off and 2x. The harness turns auto-gain off (`--drive-autogain 1` re-enables it). Probably worth a look if hosts automate Drive per block.
- The 3 ms pauses for gen0 GCs with a tiny heap are long. CI VMs, suspension of a thread that spins at real-time priority, and the load thread's normal priority (it runs the GC and may be descheduled) can all inflate them. A quiet physical machine will probably show shorter pauses; it would still need to be below 0.5 ms for every GC to meet the criterion, and the smallest per-run longest pause seen here was 1.4 ms.
- Only 12 of 18 GCs overlapped a callback at 1 MB/s and only one was visibly stretched: a stall appears only when the audio thread is inside managed code (or re-enters it) during the pause. Longer or heavier callbacks (a real host calls more plugin code per block) raise the chance.
- macOS runner (M1 virtual, 3 cores) is too noisy to resolve stalls under about 5 ms: control p99.9 2.7 to 4.4 ms, worst 11 to 20 ms (one control run: 662 ms), 1,300 to 3,300 misses per 225,000 even without any GC or managed code. Both its time-constraint thread policy and the VM's scheduling were out of my control.
- Single runs of 5 minutes per row; rare events (a gen2 compaction, a runner hiccup) appear in one row and not in the next. L3 pause maxima in particular vary run to run.
- Rows of a given OS ran on separate VMs, one job per GC setting; cross-setting comparisons include VM-to-VM variation.
- **A real-machine run would need:** a quiet desktop (no VM), a real audio device callback instead of the paced timer, a plugin loaded by an actual host (so the host's own threads and priorities are in the picture), several runs per row to see the variance, and ideally a measurement with `GC.TryStartNoGCRegion` and with an allocation-free editor loop. The harness runs unchanged there (`harness --plugin <lib> --block 64 --seconds 300 --load N`).

## Decisions you may want to take differently

- Plugin format: bare C ABI instead of NPlug or CLAP (reason above).
- 16 instances at 64 samples are about 58% of the period on average here; I lowered Drive to 2x oversampling and turned auto-gain off so the baseline is not dominated by CPU. At 4x with auto-gain on the workload misses every deadline and the GC question cannot be asked.
- Control runs: 64-sample without load, 64 and 128 with load, in each job (not at every load rate).
- Workflow triggers: push on the branch, because dispatch needs the file on the default branch.

## Results

Times in microseconds unless noted. "misses" = callbacks longer than the block period (1333 us at 64, 2667 us at 128). "GC callbacks" = callbacks during which the GC count changed. "Over median" uses the run's median; "over non-GC p99.9" uses the non-GC callbacks of the same run (see Method). In the 128-sample tables the "all" part of "over median" is inflated by the engine's two cost modes. Pause maxima are from a 1 ms sampler (lower bound).

#### Windows x64, 64-sample buffer (period 1333 us), 5 min per row

| setting | GC load | callbacks | mean | worst | p99.9 | p99.99 | misses | misses w/ GC | GCs (gen1 / gen2) | GC callbacks | pause total ms | pause max ms | stalls >0.5 ms over median (GC / all) | GC stalls over non-GC p99.9 + 0.5 ms | worst GC callback minus non-GC p99.9 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| default | none | 225000 | 774 | 2132 | 1591 | 1628 | 417 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 513 | 0 | 0.0 |
| default | 1 MB/s | 225000 | 760 | 4199 | 1573 | 1679 | 445 | 4 | 18 (15 / 1) | 12 | 12.8 | 3.163 | 5 / 516 | 1 | 2626 |
| default | 20 MB/s | 225000 | 832 | 3550 | 1636 | 1693 | 455 | 40 | 359 (359 / 3) | 238 | 159 | 3.167 | 54 / 516 | 2 | 1915 |
| default | 20 MB/s + 256 MB gen2 | 225000 | 768 | 12274 | 1813 | 9666 | 578 | 218 | 380 (129 / 3) | 218 | 2286 | 11.451 | 218 / 658 | 218 | 10705 |
| nonconcurrent | none | 225000 | 766 | 2053 | 1575 | 1655 | 530 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 646 | 0 | 0.0 |
| nonconcurrent | 1 MB/s | 225000 | 768 | 4429 | 1569 | 1790 | 479 | 3 | 18 (15 / 1) | 8 | 12.8 | 3.363 | 5 / 651 | 1 | 2860 |
| nonconcurrent | 20 MB/s | 225000 | 768 | 2987 | 1571 | 1726 | 429 | 19 | 180 (180 / 1) | 116 | 74.2 | 5.530 | 25 / 576 | 1 | 1417 |
| nonconcurrent | 20 MB/s + 256 MB gen2 | 225000 | 771 | 79981 | 1635 | 11879 | 712 | 108 | 189 (51 / 3) | 108 | 1815 | 233.980 | 108 / 928 | 108 | 78394 |
| sustained | none | 225000 | 760 | 2251 | 1570 | 1681 | 354 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 402 | 0 | 0.0 |
| sustained | 1 MB/s | 225000 | 761 | 4872 | 1569 | 1664 | 389 | 5 | 18 (15 / 1) | 12 | 13.8 | 3.465 | 7 / 495 | 2 | 3303 |
| sustained | 20 MB/s | 225000 | 762 | 3727 | 1573 | 1613 | 435 | 45 | 359 (359 / 3) | 217 | 178 | 3.162 | 109 / 586 | 2 | 2155 |
| sustained | 20 MB/s + 256 MB gen2 | 225000 | 770 | 11854 | 1856 | 10586 | 712 | 221 | 380 (129 / 3) | 222 | 2618 | 17.367 | 221 / 824 | 221 | 10278 |
| gen0size | none | 225000 | 761 | 2212 | 1574 | 1687 | 444 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 539 | 0 | 0.0 |
| gen0size | 1 MB/s | 225000 | 763 | 2482 | 1577 | 1720 | 519 | 0 | 4 (1 / 0) | 1 | 3.6 | 1.417 | 1 / 653 | 0 | 0.0 |
| gen0size | 20 MB/s | 225000 | 761 | 10674 | 1571 | 1651 | 437 | 9 | 89 (79 / 1) | 47 | 47.9 | 9.908 | 17 / 539 | 1 | 9103 |
| gen0size | 20 MB/s + 256 MB gen2 | 225000 | 762 | 20834 | 1573 | 12597 | 461 | 46 | 96 (49 / 2) | 47 | 1249 | 20.448 | 46 / 541 | 45 | 19266 |

#### Windows x64, 128-sample buffer (period 2667 us), 5 min per row

| setting | GC load | callbacks | mean | worst | p99.9 | p99.99 | misses | misses w/ GC | GCs (gen1 / gen2) | GC callbacks | pause total ms | pause max ms | stalls >0.5 ms over median (GC / all) | GC stalls over non-GC p99.9 + 0.5 ms | worst GC callback minus non-GC p99.9 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| default | none | 112500 | 1517 | 3366 | 3070 | 3324 | 147 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 13804 | 0 | 0.0 |
| default | 1 MB/s | 112500 | 1496 | 5259 | 3080 | 3220 | 203 | 2 | 18 (15 / 1) | 12 | 12.8 | 3.193 | 8 / 12487 | 1 | 2180 |
| default | 20 MB/s | 112500 | 1496 | 5094 | 3064 | 3203 | 168 | 5 | 359 (359 / 3) | 216 | 162 | 3.108 | 76 / 12684 | 3 | 2031 |
| default | 20 MB/s + 256 MB gen2 | 112500 | 1578 | 12657 | 7268 | 10332 | 1058 | 239 | 380 (129 / 3) | 239 | 2205 | 10.768 | 239 / 21127 | 239 | 9517 |
| nonconcurrent | none | 112500 | 1511 | 3388 | 3090 | 3183 | 267 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 13777 | 0 | 0.0 |
| nonconcurrent | 1 MB/s | 112500 | 1499 | 5416 | 3090 | 3161 | 272 | 3 | 18 (15 / 1) | 11 | 12.9 | 3.405 | 8 / 13043 | 1 | 2326 |
| nonconcurrent | 20 MB/s | 112500 | 1500 | 4580 | 3089 | 3956 | 312 | 2 | 180 (180 / 1) | 109 | 77.5 | 5.732 | 34 / 13036 | 2 | 1275 |
| nonconcurrent | 20 MB/s + 256 MB gen2 | 112500 | 1551 | 176666 | 6326 | 15457 | 747 | 106 | 189 (51 / 3) | 106 | 1887 | 175.118 | 106 / 18511 | 105 | 171668 |
| sustained | none | 112500 | 1504 | 3533 | 3088 | 3152 | 208 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 13572 | 0 | 0.0 |
| sustained | 1 MB/s | 112500 | 1498 | 5546 | 3088 | 3123 | 274 | 1 | 18 (15 / 1) | 13 | 13.2 | 3.534 | 5 / 13033 | 1 | 2458 |
| sustained | 20 MB/s | 112500 | 1500 | 5370 | 3088 | 3446 | 244 | 5 | 359 (359 / 3) | 204 | 170 | 3.243 | 98 / 13025 | 4 | 2284 |
| sustained | 20 MB/s + 256 MB gen2 | 112500 | 1530 | 24965 | 7828 | 11667 | 487 | 223 | 380 (129 / 3) | 223 | 2564 | 22.840 | 223 / 13992 | 223 | 21874 |
| gen0size | none | 112500 | 1541 | 6741 | 3721 | 5314 | 977 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 16194 | 0 | 0.0 |
| gen0size | 1 MB/s | 112500 | 1530 | 9137 | 4220 | 6424 | 753 | 1 | 4 (1 / 0) | 2 | 4.5 | 1.609 | 2 / 13909 | 0 | 0.0 |
| gen0size | 20 MB/s | 112500 | 1505 | 6562 | 3097 | 4357 | 312 | 1 | 89 (79 / 1) | 54 | 51.8 | 10.669 | 35 / 13223 | 0 | 0.0 |
| gen0size | 20 MB/s + 256 MB gen2 | 112500 | 1521 | 23818 | 3099 | 20078 | 260 | 56 | 96 (49 / 2) | 57 | 1311 | 22.239 | 57 / 13957 | 56 | 20734 |

#### Windows x64: control (native plugin, no GC), 5 min per row

| run | block | native churn load | callbacks | mean | worst | p99.9 | p99.99 | misses | start lateness p99.9 | start lateness max |
|---|---|---|---|---|---|---|---|---|---|---|
| ctl-L0-b64-default | 64 | none | 225000 | 521 | 1092 | 649 | 859 | 0 | 298 | 740 |
| ctl-L3-b64-default | 64 | 20 MB/s + 256 MB gen2 | 225000 | 519 | 939 | 660 | 835 | 0 | 308 | 635 |
| ctl-L3-b128-default | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1047 | 1803 | 1379 | 1608 | 0 | 371 | 672 |
| ctl-L0-b64-gen0size | 64 | none | 225000 | 522 | 3699 | 854 | 1874 | 59 | 514 | 6215 |
| ctl-L3-b64-gen0size | 64 | 20 MB/s + 256 MB gen2 | 225000 | 520 | 2201 | 672 | 967 | 8 | 315 | 1801 |
| ctl-L3-b128-gen0size | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1048 | 2763 | 1488 | 1887 | 2 | 380 | 5492 |
| ctl-L0-b64-nonconcurrent | 64 | none | 225000 | 521 | 1048 | 686 | 880 | 0 | 303 | 662 |
| ctl-L3-b64-nonconcurrent | 64 | 20 MB/s + 256 MB gen2 | 225000 | 519 | 1494 | 700 | 951 | 2 | 313 | 782 |
| ctl-L3-b128-nonconcurrent | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1047 | 1915 | 1381 | 1602 | 0 | 381 | 700 |
| ctl-L0-b64-sustained | 64 | none | 225000 | 521 | 1123 | 652 | 896 | 0 | 303 | 616 |
| ctl-L3-b64-sustained | 64 | 20 MB/s + 256 MB gen2 | 225000 | 520 | 1147 | 635 | 881 | 0 | 307 | 595 |
| ctl-L3-b128-sustained | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1047 | 1967 | 1196 | 1632 | 0 | 340 | 536 |

#### macOS arm64, 64-sample buffer (period 1333 us), 5 min per row

| setting | GC load | callbacks | mean | worst | p99.9 | p99.99 | misses | misses w/ GC | GCs (gen1 / gen2) | GC callbacks | pause total ms | pause max ms | stalls >0.5 ms over median (GC / all) | GC stalls over non-GC p99.9 + 0.5 ms | worst GC callback minus non-GC p99.9 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| default | none | 225000 | 853 | 15302 | 2814 | 4926 | 27722 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 32087 | 0 | 0.0 |
| default | 1 MB/s | 225000 | 852 | 34104 | 2777 | 4346 | 29778 | 26 | 48 (23 / 1) | 40 | 38.0 | 2.187 | 38 / 33321 | 3 | 1388 |
| default | 20 MB/s | 225000 | 910 | 19404 | 3177 | 5496 | 36973 | 519 | 959 (479 / 3) | 743 | 710 | 4.562 | 706 / 42685 | 21 | 4389 |
| default | 20 MB/s + 256 MB gen2 | 225000 | 884 | 44370 | 4554 | 7373 | 29968 | 752 | 1010 (517 / 3) | 753 | 2694 | 41.474 | 752 / 35369 | 421 | 41572 |
| nonconcurrent | none | 225000 | 827 | 11711 | 2719 | 4114 | 31574 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 34768 | 0 | 0.0 |
| nonconcurrent | 1 MB/s | 225000 | 828 | 8848 | 2718 | 4566 | 31920 | 15 | 48 (23 / 1) | 30 | 30.7 | 1.905 | 26 / 35147 | 0 | 333 |
| nonconcurrent | 20 MB/s | 225000 | 825 | 8762 | 2730 | 4296 | 31237 | 86 | 240 (120 / 2) | 178 | 156 | 3.764 | 165 / 34499 | 6 | 2199 |
| nonconcurrent | 20 MB/s + 256 MB gen2 | 225000 | 834 | 493240 | 3495 | 11114 | 28729 | 176 | 251 (66 / 3) | 176 | 2324 | 492.182 | 176 / 31568 | 176 | 490492 |
| sustained | none | 225000 | 886 | 55836 | 3463 | 5733 | 29237 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 33346 | 0 | 0.0 |
| sustained | 1 MB/s | 225000 | 857 | 69980 | 3256 | 5364 | 24393 | 26 | 48 (23 / 1) | 33 | 43.6 | 3.212 | 30 / 27360 | 1 | 948 |
| sustained | 20 MB/s | 225000 | 823 | 25209 | 3048 | 5107 | 24179 | 425 | 959 (479 / 3) | 719 | 626 | 4.813 | 590 / 27475 | 15 | 2974 |
| sustained | 20 MB/s + 256 MB gen2 | 225000 | 823 | 85032 | 3901 | 6404 | 23886 | 755 | 1010 (517 / 3) | 756 | 2305 | 84.306 | 755 / 26996 | 267 | 82201 |
| gen0size | none | 225000 | 953 | 15176 | 3276 | 6474 | 37765 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 41752 | 0 | 0.0 |
| gen0size | 1 MB/s | 225000 | 848 | 9067 | 2827 | 5080 | 28552 | 4 | 4 (1 / 0) | 4 | 12.5 | 4.841 | 4 / 32306 | 2 | 3647 |
| gen0size | 20 MB/s | 225000 | 848 | 18462 | 2806 | 5079 | 30506 | 57 | 89 (40 / 1) | 71 | 76.5 | 9.366 | 71 / 33338 | 2 | 8101 |
| gen0size | 20 MB/s + 256 MB gen2 | 225000 | 847 | 32586 | 3268 | 13080 | 25285 | 64 | 97 (48 / 3) | 66 | 1138 | 31.238 | 64 / 29376 | 64 | 29578 |

#### macOS arm64, 128-sample buffer (period 2667 us), 5 min per row

| setting | GC load | callbacks | mean | worst | p99.9 | p99.99 | misses | misses w/ GC | GCs (gen1 / gen2) | GC callbacks | pause total ms | pause max ms | stalls >0.5 ms over median (GC / all) | GC stalls over non-GC p99.9 + 0.5 ms | worst GC callback minus non-GC p99.9 |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| default | none | 112500 | 1722 | 9287 | 5175 | 5904 | 11622 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 35594 | 0 | 0.0 |
| default | 1 MB/s | 112500 | 1670 | 11481 | 5239 | 6477 | 15330 | 15 | 48 (23 / 1) | 35 | 39.0 | 2.590 | 34 / 30869 | 0 | 0.0 |
| default | 20 MB/s | 112500 | 1714 | 19225 | 5292 | 8223 | 12591 | 277 | 959 (479 / 3) | 697 | 707 | 3.956 | 611 / 32178 | 9 | 2130 |
| default | 20 MB/s + 256 MB gen2 | 112500 | 1716 | 584870 | 6209 | 8672 | 11844 | 734 | 1011 (506 / 3) | 739 | 3523 | 583.542 | 739 / 32348 | 150 | 579669 |
| nonconcurrent | none | 112500 | 1619 | 19775 | 5192 | 7812 | 14036 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 26340 | 0 | 0.0 |
| nonconcurrent | 1 MB/s | 112500 | 1593 | 15231 | 5176 | 5476 | 13877 | 14 | 48 (23 / 1) | 28 | 35.4 | 2.454 | 26 / 25892 | 0 | 271 |
| nonconcurrent | 20 MB/s | 112500 | 1598 | 109881 | 5185 | 5749 | 14011 | 54 | 240 (120 / 2) | 162 | 154 | 4.191 | 116 / 25736 | 5 | 104697 |
| nonconcurrent | 20 MB/s + 256 MB gen2 | 112500 | 1594 | 152871 | 5309 | 12444 | 13724 | 167 | 251 (67 / 3) | 167 | 1687 | 193.801 | 167 / 26134 | 82 | 147946 |
| sustained | none | 112500 | 1737 | 40122 | 5834 | 9705 | 14322 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 26677 | 0 | 0.0 |
| sustained | 1 MB/s | 112500 | 1724 | 14479 | 5544 | 9206 | 13349 | 19 | 48 (23 / 1) | 31 | 67.7 | 7.203 | 28 / 26039 | 5 | 7245 |
| sustained | 20 MB/s | 112500 | 1704 | 81255 | 5792 | 9066 | 13132 | 261 | 959 (479 / 3) | 709 | 724 | 5.000 | 606 / 25178 | 11 | 13820 |
| sustained | 20 MB/s + 256 MB gen2 | 112500 | 1715 | 29514 | 6815 | 10161 | 12555 | 721 | 1010 (517 / 3) | 723 | 2634 | 18.442 | 723 / 24490 | 118 | 14350 |
| gen0size | none | 112500 | 1734 | 17192 | 5382 | 8425 | 12139 | 0 | 0 (0 / 0) | 0 | 0.0 | 0.000 | 0 / 33164 | 0 | 0.0 |
| gen0size | 1 MB/s | 112500 | 1717 | 47435 | 5294 | 7901 | 11784 | 3 | 4 (1 / 0) | 3 | 15.1 | 9.763 | 3 / 32393 | 1 | 6403 |
| gen0size | 20 MB/s | 112500 | 1691 | 22712 | 5337 | 8352 | 15373 | 27 | 89 (40 / 1) | 57 | 89.3 | 9.404 | 56 / 29933 | 1 | 6450 |
| gen0size | 20 MB/s + 256 MB gen2 | 112500 | 1733 | 101710 | 5724 | 21441 | 10809 | 69 | 98 (49 / 3) | 71 | 1208 | 99.634 | 70 / 32723 | 68 | 96381 |

#### macOS arm64: control (native plugin, no GC), 5 min per row

| run | block | native churn load | callbacks | mean | worst | p99.9 | p99.99 | misses | start lateness p99.9 | start lateness max |
|---|---|---|---|---|---|---|---|---|---|---|
| ctl-L0-b64-default | 64 | none | 225000 | 669 | 11883 | 2703 | 5753 | 1467 | 5383 | 14704 |
| ctl-L3-b64-default | 64 | 20 MB/s + 256 MB gen2 | 225000 | 686 | 10162 | 2800 | 5856 | 1278 | 5262 | 15152 |
| ctl-L3-b128-default | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1806 | 23635 | 5450 | 11477 | 1489 | 8030 | 21518 |
| ctl-L0-b64-gen0size | 64 | none | 225000 | 663 | 15233 | 3245 | 8018 | 2068 | 10052 | 33865 |
| ctl-L3-b64-gen0size | 64 | 20 MB/s + 256 MB gen2 | 225000 | 716 | 662301 | 3557 | 9385 | 3330 | 31214 | 65333 |
| ctl-L3-b128-gen0size | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1697 | 79342 | 6880 | 14534 | 2460 | 15704 | 77105 |
| ctl-L0-b64-nonconcurrent | 64 | none | 225000 | 655 | 13121 | 2972 | 5900 | 1643 | 6604 | 20814 |
| ctl-L3-b64-nonconcurrent | 64 | 20 MB/s + 256 MB gen2 | 225000 | 714 | 27757 | 3836 | 8733 | 3242 | 19676 | 56342 |
| ctl-L3-b128-nonconcurrent | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1790 | 33865 | 5406 | 12962 | 1601 | 48212 | 122861 |
| ctl-L0-b64-sustained | 64 | none | 225000 | 709 | 20331 | 4421 | 9178 | 3200 | 14334 | 53080 |
| ctl-L3-b64-sustained | 64 | 20 MB/s + 256 MB gen2 | 225000 | 710 | 79795 | 4196 | 10315 | 3951 | 27447 | 51514 |
| ctl-L3-b128-sustained | 128 | 20 MB/s + 256 MB gen2 | 112500 | 1648 | 37340 | 5569 | 14166 | 1292 | 10817 | 34749 |
