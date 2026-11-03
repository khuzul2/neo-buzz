# Mac engine (P2) — Plan

| | |
|---|---|
| Status | In progress (2026-11-03). Merged: M0 and the PortAudio binding (#43), M6's precise waiter (#44), M1 `rebuzz-headless` (#47), M2 placeholders (#48), M5 PortAudio driver and live mode with M4's Mac job, where the A6 suite passes on `macos-15` (#49), M7 render workers in the audio workgroup (#50), M3's null test: Neo renders on Linux and macOS match Windows within −129.7 dBFS (#51), M6 MIDI through CoreMIDI and the Link helper on macOS (#52). This PR: Neo renders bit-identical on all three platforms (decision 6, §6). Then the user's play test (§4) |
| Scope | cross-platform.md §5.2 P2: the engine running headless on macOS (arm64), with real audio and MIDI, and the MCP server on stdio. No UI: the Avalonia studio is P3 |
| Exit | The A6 scripted suite passes on `macos-15`; Neo renders are bit-identical to the Windows package's (the null test); a 1-hour play test at 128 samples has no dropouts (run by hand on a Mac) |
| Related | cross-platform.md (decisions D1–D5, §3.2 the pieces), engine-split.md (P1), mcp-server.md §10 (the A6 suite) |

---

## 1. Shape

P2 delivers one new product: **`rebuzz-headless`**, a `net10.0` console app that hosts `ReBuzz.Engine` and serves MCP on stdio. It runs the same `AgentHost` as `ReBuzz.exe --agent`.

- It builds on Linux, Windows and macOS. Linux CI runs it with renders only, which catches most Mac problems for a tenth of the cost.
- On macOS it plays through PortAudio (CoreAudio), talks MIDI through CoreMIDI and joins Ableton Link through the Link helper, so studio-style live tools work from a terminal session too.
- `ReBuzz.exe` stays the Windows app. `--agent` keeps working there.

The engine itself changes only behind seams P1 created. Each new implementation lives in the engine, under `ReBuzz/Platform/` (portable, or guarded by `OperatingSystem.IsMacOS()`), or in the headless app.

| Seam (P1) | Windows (ReBuzz.exe) | Headless / Mac |
|---|---|---|
| `IUiDispatcher` | WPF dispatcher | `LoopDispatcher`: one thread running queued work and timers |
| `IUserMessages` | message boxes | stderr lines; questions answer their default (agent calls capture them anyway) |
| `IRegistryEx` | HKCU registry | `JsonRegistry` over the settings folder (`~/Library/Application Support/ReBuzz`, `$XDG_CONFIG_HOME/rebuzz`, `%APPDATA%\ReBuzz`) |
| `IFileNameChoice`, `IKeyboard` | dialogs, WPF keyboard | none (fail fast), no modifiers |
| `IMachineUiHost` | WPF windows | none (P1 already allows null) |
| `IAudioDriverFactory` | ASIO, WASAPI | PortAudio, and a null driver (renders only) |
| `IMidiBackend` | NAudio WinMM, Windows MIDI Services | CoreMIDI on macOS (decision 7); none on Linux |

## 2. Decisions

1. **Headless first, as its own app.** The P3 studio will host the same engine, so nothing here is thrown away. A separate console app avoids pulling WPF or Avalonia into P2.
2. **Single-threaded rendering on macOS first.** The CoreAudio I/O thread is already real-time and in the device's workgroup. `WorkThreadEngine(1)` renders on it, which needs no thread policy work. Multi-core rendering joins worker threads to the device's `os_workgroup` and gives them a time-constraint policy. That is step M7, kept separate, since dropouts from unjoined threads are the main risk (cross-platform.md §7). (As built, M5 rendered with the engine's usual worker threads, Multithreading being on by default; M7 put them in the workgroup.)
3. **PortAudio through our own P/Invoke** (about 300 lines: init, devices, open and start a stream, a callback, latency). No NuGet runtime package, so the dylib we sign is the one we build (P5).
4. **Managed machines load without WPF.** A machine DLL built for `net10.0-windows` is IL; it fails only when a WPF type is touched.
   - The loader treats `FileNotFoundException`/`TypeLoadException` for WPF assemblies (`PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml`) as "no GUI", not "broken machine".
   - The Neo machines build `net10.0` as well: the editor and its WPF canvas backend go under `#if WINDOWS`. The DSP, parameters and manifest are already toolkit-free (P0).
5. **Native machines are placeholders.** Their DLLs are Windows PE files. They show as missing machines that keep their parameters and data, so songs open and save back unchanged (the existing missing-machine path), and the agent says which ones didn't load.
6. **The null test compares against Windows, bit for bit.** The Windows package job renders a fixed set of Neo songs and uploads the WAVs. The Linux and Mac jobs render the same songs and compare: every sample must be identical. The Neo machines compute only with IEEE basic operations, which round the same everywhere: portable maths instead of the C runtime's, and sums in one fixed order (machines.md §4). Until the follow-up to M3 this was a threshold (−100 dBFS), for differences in the last bits; the user chose bit identity.
7. **CoreMIDI through our own P/Invoke, not DryWetMIDI** (`ReBuzz/Platform/CoreMidi.cs`, about 600 lines with its parser). This plan first said DryWetMIDI. But CoreMIDI is a system framework: there is nothing to bundle, sign or notarize. DryWetMIDI is a NuGet package whose device support on macOS is a native library of its own, which P5 would have to sign. We need a dozen calls: list endpoints, input and output ports, virtual endpoints, notifications. Same reasoning as decision 3.
   - The cost: it uses the MIDI 1.0 packet-list calls (`MIDIInputPortCreate`, `MIDISend`, `MIDIReceived`). macOS 11 deprecated them but still supports them. The UMP event-list calls that replace them take Objective-C blocks, which P/Invoke can't make easily.
8. **The Link helper reports Stopwatch's time on each OS.** ReBuzz compares the helper's host times with its own (`HostClock`, .NET's `Stopwatch`) as they are, so they must be one clock.
   - Windows: QueryPerformanceCounter, Link's own clock there.
   - macOS: `mach_absolute_time`, Link's own clock. `Stopwatch` reads the same ticks as `CLOCK_UPTIME_RAW`.
   - Linux: `CLOCK_MONOTONIC`. Link's own is `CLOCK_MONOTONIC_RAW`, which NTP doesn't slew, so it drifts from `Stopwatch`'s (0.38 s apart after 1.8 hours of uptime on one machine). The helper gives Link `CLOCK_MONOTONIC`.

## 3. Steps (PR-sized)

| # | PR | Needs | Exit check |
|---|---|---|---|
| M0 | **Portable seam implementations in the engine:** `JsonRegistry` (with `IRegistryKey`), `ConsoleUserMessages`, `LoopDispatcher` and its timers, `NullAudioDriver`. They are pure, tested on Linux (`ReBuzz.SessionTests` or a new `ReBuzz.EngineTests`) | – (new files only) | Tests green; census unchanged |
| M1 | **`rebuzz-headless`:** start-up as `HeadlessAgent` does, minus the HWND, with M0's seams. `--agent [song]`, `--gear <folder>`, `--settings <folder>`. Linux CI starts it, runs `initialize`, `studio_status`, makes a sound with Neo Poly and renders | the flip | The stdio smoke test from `AgentHeadlessTests` passes on Linux against it |
| M2 | **Machines without WPF:** the loader change (decision 4); the Neo machines multi-target with `net10.0`; native DLLs skipped by the scanner off Windows and loaded as placeholders | M1 | On Linux: every Neo machine loads, plays and reports no GUI; a song with a native machine opens and saves back byte for byte apart from the version stamp |
| M3 | **The A6 suite on Linux** against `rebuzz-headless`, plus the reference renders from the Windows package job and the null test | M1, M2, A6 | Suite green on Linux; null test against Windows (≤ −100 dBFS at first, bit for bit since, §6) |
| M4 | **macOS CI:** a `macos-15` job on PRs into main (cost: §5). It builds the engine and the headless app (osx-arm64), with libsndfile from Homebrew and r8brain's C wrapper built with CMake, then runs M3's suite and null test | M3 | The P2 exit's first two checks |
| M5 | **PortAudio driver** behind `IAudioDriverFactory`. It covers:<br>• the default device, with buffer size and sample rate from settings;<br>• input, for audio takes;<br>• latency reported to the session (heard time).<br>Real-time rules: no allocation, and no locks taken from the callback beyond `AudioLock`, as today.<br>The binding itself (`ReBuzz/Platform/PortAudio.cs`, `NativeLibraries.cs`) went in early: Linux CI runs streams on ALSA's null device in real time, with the same LP64 ABI as macOS. The render allocates nothing | M1 | On a Mac: plays a song with `transport`, and `meters_read` reads it. In CI: the engine plays through the null device on Linux |
| M6 | **MIDI and sync:** CoreMIDI behind `IMidiBackend`, through our own P/Invoke (decision 7): inputs, outputs, virtual ports, hot-plug; MIDI clock out timed with `mach_wait_until`; the Link helper built with CMake for macOS | M5 | On a Mac: a keyboard plays an armed track, a take records it; clock out drives another app; Link joins a session |
| M7 | **Multi-core rendering on macOS:** worker threads join the device's I/O workgroup (`kAudioDevicePropertyIOThreadOSWorkgroup`, `os_workgroup_join`) and take a time-constraint policy | M5 | The 1-hour play test at 128 samples with worker threads on |

M0 can go in now. M1 waits for the flip. M2 and M5 can then run in parallel, then M3 and M6, then M4 and M7.

## 4. What only the user can do

- **The play test** (exit check 3): an hour of a dense song at 128 samples on an Apple silicon Mac, with the audio device the user has. CI runners have no real audio device.
- **MIDI and Link** (M6's exit check): a keyboard, a control surface, clock out into another app, and Link with a peer. §6 lists the steps.
- **Trying it**: install the CI artifact (unsigned in P2, so it needs `xattr -d com.apple.quarantine`). Signing and notarization are P5 and need an Apple Developer ID.

## 5. Cost and risk

- **macOS minutes** cost about 10× Linux. The Mac job runs on PRs into main and by hand, never on every push. M3 on Linux catches portability problems before they reach the Mac job.
- **Real-time from .NET.** The engine allocates nothing per buffer (0.4.1 hardening), and GC pauses are what's left. Use `GCSettings.LatencyMode = SustainedLowLatency` while playing. The play test is the check.
- **Floating-point drift** between platforms. Measured in M3: up to −129.7 dBFS, from the C runtimes' last bit and a vector sum whose width followed the CPU (§6). Now none: the Neo machines use portable maths and fixed-order sums (machines.md §4), and the null test fails on any differing sample. What could bring it back: a machine (or the engine, in a render) calling `Math`'s or `MathF`'s transcendental functions, a width-dependent sum, or a flush-to-zero mode. `PortableMathTests` checks the first two in the sources and the third at run time; the null test catches anything else.

## 6. Progress

**M1, `rebuzz-headless`** (`ReBuzz.Headless/`). It is stacked on engine-split PR 10.
- **Start-up** is `HeadlessAgent`'s with M0's seams, and `--settings <folder>` sets where its settings live.
- **Settings.** Off Windows, BuzzGUI's `RegistryEx` and its settings classes go through a store: `RegistryEx.Store` (`IRegistryStore`), which `JsonRegistry` implements. Without a store, reads give the defaults.
- **Linux.** The smoke test (`tools/headless-smoke.py`) passes there, and Linux CI runs it. It drives the tools as a client does:
  - a chord through Neo Poly and Neo Verb renders at −21.7 LUFS and reads in tune;
  - the song saves and reopens, and renders the same;
  - `param_set "800 Hz"` reads back, and undo restores it;
  - the server exits when stdin closes.

**M2, done so far.**
- **Neo machines build for plain `net10.0`** (`Machines/Portable/`: one tiny project per machine, built by `Portable.proj` into the host's `Gear`).
  - Their editors and About boxes are under `#if WINDOWS`: `NeoAbout` writes a line instead.
  - Same assembly names as on Windows, so songs and the scanner take them as they are.
- **Native machines** are skipped off Windows (`NativeMachineHost.Supported`). The scanner and the machine index start no host processes.
- **No pattern editor machine.** The Modern Pattern Editor is WPF, so off Windows machines get no editor. Patterns keep the engine-played note and automation columns, which play without one.
- **Fixes on the way, which help Windows too:**
  - the DLL cache stayed open when a rescan rewrote it;
  - a failed scan left the cache file open;
  - gear and theme paths were built with backslashes;
  - missing gear folders threw.

**Waves and native libraries.**
- Wave import works headless on Linux. `NativeLibraries` now resolves libsndfile for BuzzGUI.Common's binding too.
- Wave buffers use anonymous memory maps off Windows. Named maps exist only so that native hosts can share waves, and they aren't supported there.

**The A6 scripted suite against `rebuzz-headless` on Linux.** It ran locally with the evaluation from PR #40.
- **Tasks:** all five reference tasks score 1.00 (Beat, Sound match, Fix the mix, Master at −14.1 LUFS/−2.0 dBTP, Song).
- **Checks:** notes, units and analysis pass.
- **Three checks wait for fixes that are on #40's branch, not here yet:**
  - the undo/redo output schemas;
  - render tick-phase reproducibility;
  - a connection's pan lost on undo.

  With #40 merged, the whole scripted suite passes against `rebuzz-headless` on Linux, with the same baseline as Windows. Linux CI runs it ("Agent evaluation on Linux"), which covers M3's first check. The null test, its second, is below.

**Native machines as placeholders: they open and save back what the song says about them** (PR #48).
- **Opening.** 35 of the 36 demo songs under `ExternalMachines/source` open headless on Linux, 11 of the first 12. Their native machines are missing machines, with their connections.
  - One "super old" BMX doesn't open: `FSM_Kick_Demo.bmx` has no PARA section, so its parameters can't be read without the machine's DLL. Windows without the machine is the same.
- **What saving lost, and why.** The code is the engine's, so Windows lost the same with missing machines (except the pattern contents, below).
  - BMX: a placeholder's track values and its attributes. Its track group came from PARA with no track count, so it saved as no tracks (8,178 → 9,872 → 9,216 bytes). Attributes were copied only onto a machine's own, and a missing machine has none.
  - BMXML: all of a placeholder's parameter values and track counts (its groups were rebuilt from the XML without them), its attributes, and its pattern columns (skipped for missing machines).
  - Old songs' pattern contents: the built-in editor's, in PATT. They go to the default pattern editor, converted to its PXP format. There is none off Windows, so they were dropped, the Master's tempo changes included. Windows has the editor and kept them.
  - A song with a pattern editor machine that isn't installed, and no default to replace it, didn't open: a song from Windows ReBuzz, off Windows.
  - For every machine:
    - BMX wrote names as ASCII, so "µs" became "?s";
    - BMXML encoded a missing machine's parameter descriptions once more on each save;
    - BMXML reloaded a machine's inputs in their sources' order, and wrote an empty channel set (BMX connections have none) that loads as `[0]`;
    - a wave with the envelope flag didn't save as BMXML at all, and BMXML waves lost their last sample on each open.
- **The fix.**
  - A placeholder keeps its tracks, values and attributes in both formats, and BMXML keeps its pattern columns.
  - Built-in pattern contents that no editor takes stay on the pattern (`FileOps/BuiltInPattern.cs`, `PatternCore.BuiltInData`):
    - BMX writes them back into PATT, and XTAP keeps the patterns the built-in editor's, so Windows converts them as it does for an old song;
    - BMXML writes them as the pattern's `BuiltInPattern` element; older readers skip it (checked: the previous build opens these files);
    - the PXP conversion gives the same bytes as before (checked against a port of the old code, for the 29 machines with patterns in the first 12 songs).
  - A missing pattern editor with no default stays as a hidden placeholder with its data (`MachineCore.PlaceholderEditor`). The engine doesn't drive it, and the song saves back with it as the machine's editor.
  - For every machine: BMX strings in Latin-1, as they're read; BMXML connections in input order, an empty channel set written as it loads, descriptions decoded once; `WaveFlags.Envelope`; the last sample.
- **Checked by `tools/song-roundtrip.py`**, which Linux CI runs. The first save rewrites a song in ReBuzz's own format (its sections, uncompressed waves, its build string), and keeps every machine's values, data, attributes, tracks, pattern contents, connections and sequences. Later saves are identical:

  | Song (first 12) | Original | BMX A = B | BMXML A = B (but stamps) |
  |---|---:|---:|---:|
  | CyanPhase_DTMF-1_Demo | 3,904 | 4,989 | 45,792 |
  | Elak_Dist2 | 2,172 | 2,601 | 20,127 |
  | Elak_SVF | 1,606 | 2,055 | 17,197 |
  | elenzil_ampmod | 5,845 | 7,819 | 92,920 |
  | FSM_Infector_Demo | 378,333 | 380,570 | 699,550 |
  | FSM_Kick_Demo | 1,277 | doesn't open | – |
  | FrequencyUnknown_O-Delay_delaytest | 109,764 | 324,755 | 890,269 |
  | FrequencyUnknown_O-Delay_sparkle | 1,844 | 2,428 | 25,010 |
  | Jeskola_NiNjA_dElaY | 73,043 | 73,440 | 123,867 |
  | MadBrain_Dynamite6_Demo | 59,235 | 62,173 | 251,063 |
  | Oomek_Aggressor_ctrack | 1,356 | 1,597 | 13,900 |
  | Oomek_Aggressor_demo | 8,178 | 10,463 | 115,388 |

  - The other 24 demo songs pass too.
  - A Windows-style song, with a Modern Pattern Editor machine per machine and an automation column, round-trips in both formats with its editors kept as placeholders (synthetic editor data).
- **Installed machines don't change.** A Neo song saved by the previous build and by this one gives the same BMXML. BMX differs only in a name that isn't ASCII: Neo Poly's "Ring 1×2" was written "Ring 1?2", so it no longer matched on reload.
- **Not run on Windows yet:**
  - a song saved here, opened in ReBuzz.exe: its built-in contents should convert into the Modern Pattern Editor as the original song's do;
  - the same for a BMXML `BuiltInPattern`;
  - a real Windows song with Modern Pattern Editor data, opened here;
  - ReBuzzTests' song save and load tests.
- **Left:** deleting a machine here leaves its placeholder editor behind. It is hidden and unused, and saves with the song.

**M4, the Mac job** (`fork-mac.yml`, on PRs into main and by hand). It builds `rebuzz-headless` for osx-arm64 and runs:
- the smoke test;
- the A6 scripted suite: 6/6 checks and 5/5 tasks, against the same baseline as Windows and Linux (exit check 1);
- the live test (M5).
The null test (exit check 2) is M3's.

**M5, audio through PortAudio.**
- **The driver.** `PortAudioDriver` and `PortAudioDriverFactory` (`ReBuzz/Audio/Driver/`). One stereo float stream; the device's thread renders, as ASIO's does on Windows.
  - Input is optional, for audio takes, and off by default, so macOS doesn't ask for the microphone.
  - The device's reported latency goes to the session's launch timing.
  - Settings in the registry's `PortAudio` section: `BufferSize` (default 256), `SampleRate` (0: the device's), `Input`.
- **Device lists.** `IAudioDriverFactory.OutputDevices()` lets a factory list its own devices. Windows still lists the ASIO drivers, then WASAPI. PortAudio's are "PortAudio: default" and "PortAudio: <device> (<host API>)".
- **`rebuzz-headless --audio`** `[--device <name>] [--input <name|default>] [--buffer <frames>] [--rate <hz>]`, and `--list-devices`.
  - With the device open the agent runs in **live mode**: the A7 live tools work as in studio mode, with no window.
  - The GC runs in `SustainedLowLatency` mode.
- **Checked by `tools/headless-live.py`.** It plays a phrase and the arrangement, meters them, renders with the device open, and exits.
  - Linux CI: PortAudio 19.7 on a PulseAudio null sink, 44.1 kHz. It plays in real time: 2.00 beats/s at 120 BPM.
  - macOS CI: CoreAudio on the runner's Apple Virtual Sound Device, 48 kHz. 2.03 beats/s.
  - ALSA's bare null device takes audio as fast as it comes, so the real-time check needs a clocked device.
- **Found on the way.** Off Windows the per-user application data folder can be missing (`~/.config` on a fresh Mac). `GetFolderPath` then returns "", and the DLL cache landed in the current folder. Fixed. `rebuzz-headless` now keeps its cache and autosaves in its settings folder.
- **Not covered yet:**
  - following a change of the system's default output while playing (PortAudio opens a device, not "the default");
  - a device that goes away;
  - the 1-hour play test (§4).

**M7, render workers in the audio workgroup** (`ReBuzz/Platform/AudioWorkgroup.cs`).
- **Attach.** When the PortAudio stream starts, the driver asks CoreAudio for the device's I/O workgroup (`kAudioDevicePropertyIOThreadOSWorkgroup`, on the device `PaMacCore_GetStreamOutputDevice` names). It detaches it when the stream stops.
- **Join.** Both kinds of render worker call `AudioWorkgroup.Enter()` before their work: the audio thread pool (`DedicatedThreadPool`, used by the default algorithm) and `WorkThreadEngine`.
  - Enter reads one number. Only when the workgroup changed does it leave the old one, set a time-constraint policy for the buffer period, and `os_workgroup_join` the new one.
  - Each joined thread holds its own reference (`os_retain`), so detaching never frees a workgroup that a thread is still in.
  - Workers leave when they end.
  - Off macOS it does nothing.
- **Reported.** `studio_status` has an `audio` object in studio and live mode: device, buffer, latency, render threads, threads in the workgroup, dropouts (deadline misses).
- **Checked.**
  - `ReBuzz.SessionTests` on macOS: a worker joins a running stream's workgroup and leaves it, and a worker joined when the device stops leaves at its next Enter.
  - The live test on macOS: four synths play at once, and `studio_status` must show render threads in the workgroup.
- **Left for the play test (§4):** the hour at 128 samples, with the default four render threads.

**The null test against Windows (M3, M4; decision 6).** `tools/null-test.py`, in the package workflow.
- **Cases.** 50 fixed cases cover every Neo machine. Each runs in a fresh process: it builds a short song through the MCP tools and renders it (44.1 kHz, 32-bit float).
  - Neo Poly (9): the default patch with drift, Acid, Reese, FM Growl, Sync Scream, Evolving PWM, Noise Sweep, a self-oscillating ladder with drive, ring, sync and glide, and parallel SVFs with FM and pink noise.
  - Neo Wave (7): Init, Vowel Choir, Window Sync Sweep, PM Bloom, Grit Motion, Reso Pluck, Mirror Bell.
  - Neo FM6 (7): Tine Piano, Glass Bell, Random Chimes, Bright Section, Saw Lead, Metal Sweep, and a sine at feedback 7 on the Raw 1× engine.
  - Effects (23), on an FM6 brass source: Neo Comp's four modes (lookahead, auto release); Neo Drive's five models at 2×, 4× and 8×; Neo EQ's bells, shelves, cuts at 12 to 48 dB/oct and notch; Neo Limiter pushed by 18 and 24 dB, with Neo Analyzer after it; Neo Utility's width, bass mono, mono pan, swap, polarity and DC removal; Neo Verb's four algorithms, shimmer, a 20-second decay, and freeze switched on mid-render by automation.
  - Four 16-second cases (Poly, Wave, FM6, Verb), where a difference that grows with time would show.
- **CI.** The package job renders the references with `ReBuzz.exe --agent` (about 100 s) and uploads them (`null-test-references`). Two jobs then build `rebuzz-headless` and compare: Linux x64, and macOS arm64 on `macos-15`, about 2 minutes each, once per package run. A case fails on any differing sample (the bits: NaN and −0 count), on a length mismatch, or when it is missing on either side; one that differs reports its first differing sample (frame, time, channel, both values), how many differ, and its largest difference in dBFS. The per-case results go to a notice and the step summary.
- **Bit-identical** (the follow-up to M3, package run RUN_PKG): all 50 cases are identical to Windows on Linux x64 and on macOS arm64. Nothing is excluded (`EXCLUDED` in the script is empty).
- **At first** (M3, runs 41545747463 and 41547910703, with a −100 dBFS threshold), the largest difference against Windows was:

  | Machine | Cases | Linux x64 | macOS arm64 |
  |---|---:|---|---|
  | Neo Poly | 10 | 2 identical; worst −129.7 dBFS (16-second pad) | worst −129.7 dBFS (16-second pad) |
  | Neo Wave | 8 | 5 identical; worst −141.0 dBFS | 4 identical; worst −141.0 dBFS |
  | Neo FM6 | 8 | all identical | all identical |
  | Neo Comp | 4 | 1 identical; worst −141.0 dBFS (Glue) | 1 identical; worst −138.5 dBFS (Opto) |
  | Neo Drive | 5 | 2 identical; worst −186.6 dBFS | 2 identical; worst −186.6 dBFS |
  | Neo EQ | 3 | all identical | 2 identical; −343 dBFS (Telephone) |
  | Neo Limiter | 2 | worst −135.0 dBFS | worst −130.5 dBFS |
  | Neo Utility | 2 | all identical | all identical |
  | Neo Verb | 8 | all identical | all identical |

  Neo Analyzer passes the audio through (the loud limiter case ends with it). The differences didn't grow with time, and the feedback-heavy cases (the self-oscillating ladder, FM feedback 7, shimmer, the 20-second decay, freeze) stayed below −139 dBFS.
- **Where the differences came from, measured.** The artifacts can't be downloaded here, so two detectors on Linux stood in for the other platforms:
  - **The C runtime.** An `LD_PRELOAD` shim that moves every inexact libm result by one ulp (`sin`, `pow`, `exp`, `tanh`, `asinh` … and the float versions; CoreCLR and the JIT both call them) changed every one of the 50 renders, by up to −34 dBFS. FM6, Verb and Utility had matched only because the three libms happened to round their inputs alike. With the portable maths, all 50 are bit-identical under the shim: nothing in a render calls libm, the engine included.
  - **Vector width.** With `DOTNET_EnableAVX2=0` (`Vector<float>` 4 lanes, as on arm64), only Neo Limiter changed, by −130.5 dBFS: macOS's figure. Now identical with AVX off and with all hardware intrinsics off (software rounding and vector fallbacks).
  - **Neo EQ's −343 dBFS on macOS was not a denormal.** One sample of about 7·10⁻¹⁸ (a normal float), in a quiet stretch where the float output shows the last bits of the double-precision sections. Their coefficients come from `tan` and `pow`, so most likely Apple's libm: the portable maths is the only change to the EQ, and the case is identical now. Subnormals are IEEE on all three platforms (no flush-to-zero mode; `PortableMathTests` checks it where the tests run), and feedback state is flushed (`DspMath.Flush`) the same way everywhere.
- **What changed** (machines.md §4, the portable maths rule):
  - `PortableMath` and `PortableMathF` in NeoBuzz.Dsp: exp, expm1, log, log1p, log2, log10, pow, 2^x, 10^x, sin, cos, tan, tanh, asinh, atan, atan2, from IEEE basic operations only. Exp and 2^x are table-driven (Tang's method), the rest fdlibm's. Within 1 ulp of the exact result (Atan2 1.2, Asinh 1.3, Tanh 1.8), measured against 200-bit references.
  - Every transcendental call in NeoBuzz.Dsp and the nine machine engines goes through them: 164 call sites, coefficient maths from parameters included. Neo Comp's and Neo Limiter's per-sample dB use ln × 20/ln 10, cheaper than log10.
  - Neo Limiter's true-peak sum: eight lanes in one fixed order, one `Vector256` on x64 or two `Vector128` halves elsewhere (the same additions).
  - `PortableMathTests` (NeoBuzz.MachineTests): within a few ulps of the platform's libm, C99's special cases, a pinned checksum per function (the same on every platform), and a scan of the audio sources for libm calls and width-dependent sums. The scan found Neo Drive's diode shaper on `Math.Asinh`.
  - **Found on the way: a render that depended on time.** One Linux run differed on Mirror Bell (−36.7 dBFS) while Windows, macOS and every local render agreed. The render's wait for Neo Wave's background table build processed audio for as long as the build took; a slower build meant a tick more pre-roll, and every synth's free-running state (16-sample control grid, free LFOs) met the first note at another phase (5 extra pre-roll blocks reproduce that run's samples exactly). Renders now wait for busy machines first, without running them (up to 30 s; mcp-server.md §3.5), and Neo Wave is busy until all its built-in tables are built. (A first try waited only for the tables chosen; but a preset's choice reaches the machine at the engine's next tick, after the wait, and Mirror Bell rendered silent: run 41594980034.) With the table build delayed by up to 1.5 s, Wave renders stay identical.
- **The cost.** The sound: against the old Linux renders, the largest change is −128.9 dBFS, and most cases are unchanged. NeoBuzz.MachineTests pass with their tolerances. CPU on the Linux runner, against main: Neo Poly's 16 voices 11.6–11.8× → 11.2× real time (its sine oscillator: the float functions cost about twice glibc's); Wave (16.4×), FM6 (8.0×), EQ (215×) and Limiter (161×; 120–176× on main) unchanged. Per call, exp and 2^x are as fast as glibc's or faster; sin, cos, tan and tanh within 10 %.
- **Found on the way:** a relative `--settings` folder made `render` fail (its file URI). `rebuzz-headless` now makes it absolute.

**M6, MIDI and Link on macOS.**
- **The CoreMIDI binding** (`ReBuzz/Platform/CoreMidi.cs`, decision 7).
  - One client per process, made on a thread of its own that runs a CFRunLoop. CoreMIDI ties a process's notifications to the run loop of the thread that first calls it, even for a call that only counts devices. So the client is the first call, on that thread.
  - Endpoints carry CoreMIDI's display names. A client sees other processes' endpoints come and go as its run loop runs.
  - Input ports parse packets in place into short messages, in WinMM's order: running status, real-time bytes anywhere, SysEx skipped (ReBuzz's Windows inputs skip it too). Packets are 4-byte aligned on arm64.
  - Sends build their packet list in a buffer allocated up front; only a longer SysEx gets a buffer of its own. Receiving and sending allocate no managed memory.
  - Virtual sources and destinations keep their unique IDs from run to run, so other apps find them again.
- **The backend** (`ReBuzz/Midi/CoreMidiBackend.cs`).
  - Devices are indexed in a snapshot of the sources (inputs) and destinations (outputs), rescanned on each change, so counts and names agree between changes. A second device of the same name is "Name (2)".
  - Hot-plug: each rescan reports the names that came and went, and `MidiEngine` opens those its settings list, as on Windows.
  - In place of the MIDI 2 endpoint (`CreateMidi2Endpoint`): a virtual destination "ReBuzz", whose notes play as a keyboard's do (what the Windows virtual device was for), and a virtual source "ReBuzz", offered as an output. So clock out and MIDI out machines reach other apps without the IAC driver. Neither is offered back as a device.
  - Control surfaces get their own input and output ports.
  - Closing an output sends notes off and sustain off on every channel, as WinMM's reset does.
- **`rebuzz-headless`** installs it on macOS. Linux keeps none.
  - `--midi-in <name|all|none>` and `--midi-out <name|all|none>` (both repeat), `--clock-out <name|none>`, `--link` and `--no-link`. They are kept in the settings, as ReBuzz.exe keeps its MIDI and sync settings.
  - `--list-devices` lists the MIDI inputs and outputs too.
  - The engine's warnings and errors (`DCWriteLine`) go to stderr; before, they went nowhere. So do MIDI devices coming, going, opening or failing to open.
- **The Link helper** builds with CMake on macOS and Linux as `ReBuzzLink`, which `LinkClient` looks for off Windows. Building `rebuzz-headless` copies it from `build/LinkHelper` beside the app. Its host times are `Stopwatch`'s on each OS (decision 8).
- **Found on the way.** `JsonRegistry.ReadDictionary` gave a number written in the same run as text. `MidiEngine` casts the MIDI lists' flags to int on hot-plug, and drops text ones when it opens devices, so a device turned on in that run never opened. Fixed.
- **Checked in CI.** Fork Mac run 41548401048 (macOS 15, arm64), and Fork CI on Linux:
  - Seam tests, macOS: a loopback through virtual ports (a note on, a 2,000-byte SysEx that is skipped, a clock, two note offs in running status); a virtual source into an input port; nothing allocated receiving or sending; a virtual source coming and going is notified and listed. The parser runs on Linux too.
  - The Link helper, macOS and Linux: it starts and says hello; its state lines are in `Stopwatch`'s time (a fresh line is read 48 µs after its time on macOS, 16 µs on Linux); it takes `enable`, `tempo` and `play`; beats advance at the tempo; `quit` ends it.
  - `tools/headless-midi.py`, macOS, against `rebuzz-headless --audio --midi-in "ReBuzz Test Keys" --clock-out ReBuzz --link` on the runner's virtual sound device:
    - a keyboard (a virtual source the test makes) plugged in while it runs opens in 0.46 s, and its chord plays Neo Poly: −17.6 dBFS RMS by `meters_read`. Note offs let go;
    - another app's chord into the "ReBuzz" destination plays too;
    - clock out reaches another app through the "ReBuzz" source: Start, 47.9 pulses a second at 120 BPM (48 expected), Stop. With the real-time policy (below; Fork Mac run 41553919563), the gaps are 19.8–22.3 ms (5–95 %; 20.8 expected). Before it, 5% were 0 and 5% 45 ms or more;
    - `--link` starts the helper beside the app, and it ends with the app.
- **For the hands-on test on a Mac** (runners have no MIDI devices or network peers):
  - A USB keyboard: `rebuzz-headless --list-devices`, then `rebuzz-headless --agent --audio --midi-in all`. Notes play the machine added last. With a track armed, `session_record` records a take.
  - A control surface (Launchpad, APC, Push 2). The built-in scripts match the port names Windows gives. CoreMIDI's display names differ (for example "Launchpad Mini MK3 LPMiniMK3 MIDI Out"), so a script may not find its ports. Not tried.
  - Clock out driving another app: `--clock-out ReBuzz`, and the app takes MIDI clock from the "ReBuzz" source (or `--clock-out "IAC Driver Bus 1"`). Whether pulses are steadier than on the runner.
  - Link with a real peer: Live, or an app on a phone on the same network. macOS may ask to let ReBuzzLink accept incoming connections and use the local network.
  - Unsigned binaries need `xattr -d com.apple.quarantine` (§4), the helper included.
- **Left:**
  - SysEx input isn't forwarded (as on Windows), and MIDI 2.0 (UMP) isn't used.
  - **Found:** the precise waiter's seam test failed on the macOS runner, where a 0.5 ms `mach_wait_until` took 2.5–3.7 ms (median). macOS coalesces an ordinary thread's timers. It had failed since M5, unseen, because the Mac workflow's test steps piped through `tee`, which hid `dotnet test`'s exit code. The uneven clock pulses came from it.
  - **The fix:** `RealtimeThread` (`ReBuzz/Platform/`) gives the waiting thread a time-constraint policy on macOS: aperiodic, 0.2 ms of CPU within 1 ms. `AudioWorkgroup` uses the same helper. Fork Mac run 41553919563: a 0.5 ms wait takes 0.52 ms (median), and clock out's pulses are 19.8–22.3 ms apart (5–95 %), 47.9 a second.
  - The Mac test steps now run with `pipefail`, so a failing test fails the job.


