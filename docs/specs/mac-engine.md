# Mac engine (P2) — Plan

| | |
|---|---|
| Status | In progress. M0 and the PortAudio binding are merged (#43); M6's precise waiter is in PR (#44). M1 is built: `rebuzz-headless` runs on Linux with the portable Neo machines, stacked on the flip. M2 is partly done (§6) |
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
- **Floating-point drift** between x64 and arm64 can exceed −100 dBFS in feedback-heavy patches (FM feedback, resonant filters at self-oscillation). If a Neo song fails the null test, the fix is in the machine (deterministic denormal handling, no `MathF` intrinsics with different rounding), and the threshold doesn't change.

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

**Native machines as placeholders: they open, but the round trip loses data.**
- **Opening works.** Ten of the first twelve buzztrax demo songs (`ExternalMachines/source/**/*.bmx`) open headless on Linux. Their native machines show as missing machines with their connections.
- **Two "super old" BMX files don't open.** Their parameter data can't be parsed without the machine DLL, the same as on Windows without those machines.
- **Saving back loses data, so this is the open part of M2.**
  - An old demo saved as BMX, reopened and saved again changes size (8,178 → 9,872 → 9,216 bytes).
  - A BMXML round trip drops placeholder machines' parameter values.
  - Fix: a placeholder carries its machine's section as loaded (parameters, data, attributes, pattern columns) and writes it back unchanged.
  - Check whether Windows with missing machines does the same, since it's the same code.

**M3–M7:** as in §3.

