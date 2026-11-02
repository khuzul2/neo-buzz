# Mac engine (P2) — Plan

| | |
|---|---|
| Status | In progress. M0 and the PortAudio binding are merged (#43); M6's precise waiter is in PR (#44). M1 is built: `rebuzz-headless` runs on Linux with the portable Neo machines, stacked on the flip. M2 is partly done (§6): songs with native machines open and save back headless (#48). The null test passes: Neo renders on Linux and macOS match Windows within −129.7 dBFS (§6) |
| Scope | cross-platform.md §5.2 P2: the engine running headless on macOS (arm64), with real audio and MIDI, and the MCP server on stdio. No UI: the Avalonia studio is P3 |
| Exit | The A6 scripted suite passes on `macos-15`; Neo renders null-test against the Windows package at ≤ −100 dBFS; a 1-hour play test at 128 samples has no dropouts (run by hand on a Mac) |
| Related | cross-platform.md (decisions D1–D5, §3.2 the pieces), engine-split.md (P1), mcp-server.md §10 (the A6 suite) |

---

## 1. Shape

P2 delivers one new product: **`rebuzz-headless`**, a `net10.0` console app that hosts `ReBuzz.Engine` and serves MCP on stdio. It runs the same `AgentHost` as `ReBuzz.exe --agent`.

- It builds on Linux, Windows and macOS. Linux CI runs it with renders only, which catches most Mac problems for a tenth of the cost.
- On macOS it plays through PortAudio (CoreAudio) and talks MIDI through DryWetMIDI, so studio-style live tools work from a terminal session too.
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
| `IMidiBackend` | NAudio WinMM, Windows MIDI Services | DryWetMIDI; none on Linux CI |

## 2. Decisions

1. **Headless first, as its own app.** The P3 studio will host the same engine, so nothing here is thrown away. A separate console app avoids pulling WPF or Avalonia into P2.
2. **Single-threaded rendering on macOS first.** The CoreAudio I/O thread is already real-time and in the device's workgroup. `WorkThreadEngine(1)` renders on it, which needs no thread policy work. Multi-core rendering joins worker threads to the device's `os_workgroup` and gives them a time-constraint policy. That is step M7, kept separate, since dropouts from unjoined threads are the main risk (cross-platform.md §7).
3. **PortAudio through our own P/Invoke** (about 300 lines: init, devices, open and start a stream, a callback, latency). No NuGet runtime package, so the dylib we sign is the one we build (P5).
4. **Managed machines load without WPF.** A machine DLL built for `net10.0-windows` is IL; it fails only when a WPF type is touched.
   - The loader treats `FileNotFoundException`/`TypeLoadException` for WPF assemblies (`PresentationFramework`, `PresentationCore`, `WindowsBase`, `System.Xaml`) as "no GUI", not "broken machine".
   - The Neo machines build `net10.0` as well: the editor and its WPF canvas backend go under `#if WINDOWS`. The DSP, parameters and manifest are already toolkit-free (P0).
5. **Native machines are placeholders.** Their DLLs are Windows PE files. They show as missing machines that keep their parameters and data, so songs open and save back unchanged (the existing missing-machine path), and the agent says which ones didn't load.
6. **The null test compares against Windows.** The Windows package job renders a fixed set of Neo songs and uploads the WAVs. The Mac job renders the same songs and compares, sample by sample, at ≤ −100 dBFS. Floating point can differ in the last bits (FMA, vectorisation), hence a threshold rather than bit identity.

## 3. Steps (PR-sized)

| # | PR | Needs | Exit check |
|---|---|---|---|
| M0 | **Portable seam implementations in the engine:** `JsonRegistry` (with `IRegistryKey`), `ConsoleUserMessages`, `LoopDispatcher` and its timers, `NullAudioDriver`. They are pure, tested on Linux (`ReBuzz.SessionTests` or a new `ReBuzz.EngineTests`) | – (new files only) | Tests green; census unchanged |
| M1 | **`rebuzz-headless`:** start-up as `HeadlessAgent` does, minus the HWND, with M0's seams. `--agent [song]`, `--gear <folder>`, `--settings <folder>`. Linux CI starts it, runs `initialize`, `studio_status`, makes a sound with Neo Poly and renders | the flip | The stdio smoke test from `AgentHeadlessTests` passes on Linux against it |
| M2 | **Machines without WPF:** the loader change (decision 4); the Neo machines multi-target with `net10.0`; native DLLs skipped by the scanner off Windows and loaded as placeholders | M1 | On Linux: every Neo machine loads, plays and reports no GUI; a song with a native machine opens and saves back byte for byte apart from the version stamp |
| M3 | **The A6 suite on Linux** against `rebuzz-headless`, plus the reference renders from the Windows package job and the null test | M1, M2, A6 | Suite green on Linux; null test ≤ −100 dBFS against Windows |
| M4 | **macOS CI:** a `macos-15` job on PRs into main (cost: §5). It builds the engine and the headless app (osx-arm64), with libsndfile from Homebrew and r8brain's C wrapper built with CMake, then runs M3's suite and null test | M3 | The P2 exit's first two checks |
| M5 | **PortAudio driver** behind `IAudioDriverFactory`. It covers:<br>• the default device, with buffer size and sample rate from settings;<br>• input, for audio takes;<br>• latency reported to the session (heard time).<br>Real-time rules: no allocation, and no locks taken from the callback beyond `AudioLock`, as today.<br>The binding itself (`ReBuzz/Platform/PortAudio.cs`, `NativeLibraries.cs`) went in early: Linux CI runs streams on ALSA's null device in real time, with the same LP64 ABI as macOS. The render allocates nothing | M1 | On a Mac: plays a song with `transport`, and `meters_read` reads it. In CI: the engine plays through the null device on Linux |
| M6 | **MIDI and sync:** DryWetMIDI behind `IMidiBackend` (inputs, outputs, virtual ports, hot-plug); MIDI clock out timed with `mach_wait_until`; the Link helper built with CMake for macOS | M5 | On a Mac: a keyboard plays an armed track, a take records it; clock out drives another app; Link joins a session |
| M7 | **Multi-core rendering on macOS:** worker threads join the device's I/O workgroup (`kAudioDevicePropertyIOThreadOSWorkgroup`, `os_workgroup_join`) and take a time-constraint policy | M5 | The 1-hour play test at 128 samples with worker threads on |

M0 can go in now. M1 waits for the flip. M2 and M5 can then run in parallel, then M3 and M6, then M4 and M7.

## 4. What only the user can do

- **The play test** (exit check 3): an hour of a dense song at 128 samples on an Apple silicon Mac, with the audio device the user has. CI runners have no real audio device.
- **Trying it**: install the CI artifact (unsigned in P2, so it needs `xattr -d com.apple.quarantine`). Signing and notarization are P5 and need an Apple Developer ID.

## 5. Cost and risk

- **macOS minutes** cost about 10× Linux. The Mac job runs on PRs into main and by hand, never on every push. M3 on Linux catches portability problems before they reach the Mac job.
- **Real-time from .NET.** The engine allocates nothing per buffer (0.4.1 hardening), and GC pauses are what's left. Use `GCSettings.LatencyMode = SustainedLowLatency` while playing. The play test is the check.
- **Floating-point drift** between x64 and arm64 can exceed −100 dBFS in feedback-heavy patches (FM feedback, resonant filters at self-oscillation). If a Neo song fails the null test, the fix is in the machine (deterministic denormal handling, no `MathF` intrinsics with different rounding), and the threshold doesn't change. Measured in M3: the feedback-heavy cases stay below −139 dBFS (§6).

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

**The null test against Windows (M3, M4; decision 6).** `tools/null-test.py`, in the package workflow.
- **Cases.** 50 fixed cases cover every Neo machine. Each runs in a fresh process: it builds a short song through the MCP tools and renders it (44.1 kHz, 32-bit float).
  - Neo Poly (9): the default patch with drift, Acid, Reese, FM Growl, Sync Scream, Evolving PWM, Noise Sweep, a self-oscillating ladder with drive, ring, sync and glide, and parallel SVFs with FM and pink noise.
  - Neo Wave (7): Init, Vowel Choir, Window Sync Sweep, PM Bloom, Grit Motion, Reso Pluck, Mirror Bell.
  - Neo FM6 (7): Tine Piano, Glass Bell, Random Chimes, Bright Section, Saw Lead, Metal Sweep, and a sine at feedback 7 on the Raw 1× engine.
  - Effects (23), on an FM6 brass source: Neo Comp's four modes (lookahead, auto release); Neo Drive's five models at 2×, 4× and 8×; Neo EQ's bells, shelves, cuts at 12 to 48 dB/oct and notch; Neo Limiter pushed by 18 and 24 dB, with Neo Analyzer after it; Neo Utility's width, bass mono, mono pan, swap, polarity and DC removal; Neo Verb's four algorithms, shimmer, a 20-second decay, and freeze switched on mid-render by automation.
  - Four 16-second cases (Poly, Wave, FM6, Verb), where a difference that grows with time would show.
- **CI.** The package job renders the references with `ReBuzz.exe --agent` (92 s) and uploads them (`null-test-references`). Two jobs then build `rebuzz-headless` and compare: Linux x64, and macOS arm64 on `macos-15`, about 2 minutes each, once per package run. A case fails above −100 dBFS, on a length mismatch, or when it is missing on either side. The per-case figures go to a notice and the step summary.
- **Results** (run 41545747463). Every case is within −100 dBFS on both platforms. Largest absolute difference against Windows:

  | Machine | Cases | Linux x64 | macOS arm64 |
  |---|---:|---|---|
  | Neo Poly | 10 | 2 identical; worst −129.7 dBFS (16-second pad) | worst −129.7 dBFS (16-second pad) |
  | Neo Wave | 8 | 5 identical; worst −141.0 dBFS | 4 identical; worst −141.0 dBFS |
  | Neo FM6 | 8 | all identical | all identical |
  | Neo Comp | 4 | 1 identical; worst −141.0 dBFS (Glue) | 1 identical; worst −138.5 dBFS (Opto) |
  | Neo Drive | 5 | 2 identical; worst −186.6 dBFS | 2 identical; worst −186.6 dBFS |
  | Neo EQ | 3 | all identical | 2 identical; −343 dBFS (Telephone: a denormal) |
  | Neo Limiter | 2 | worst −135.0 dBFS | worst −130.5 dBFS |
  | Neo Utility | 2 | all identical | all identical |
  | Neo Verb | 7 | all identical | all identical |

  Neo Analyzer passes the audio through (the loud limiter case ends with it).
- **Where the differences come from.** Linux and Windows run the same JIT code on the same x64 CPUs. What differs is the C runtime behind `Math.Pow`, `Exp`, `Tan` and the `MathF` functions: glibc, the UCRT, or Apple's libm, which can round the last bit differently. macOS adds Neo Limiter's true-peak filter, summed with `Vector<float>`: 4 lanes on arm64, 8 on x64.
- **The differences don't grow.** The 16-second cases are no worse than the short ones. The feedback-heavy cases stay below −139 dBFS: the self-oscillating ladder, FM feedback 7, shimmer, the 20-second decay and freeze.
- **No machine needed a fix, and nothing is excluded** (`EXCLUDED` in the script is empty).
- **Found on the way:** a relative `--settings` folder made `render` fail (its file URI). `rebuzz-headless` now makes it absolute.

**M5–M7:** as in §3.

