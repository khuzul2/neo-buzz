# Cross-platform neo-buzz (macOS + Windows) — Feasibility and options

| | |
|---|---|
| Status | Decided 2026-11-01: D1–D5 as recommended, run as a track parallel to the MCP work. P0 done: the Neo editor kit draws through `INeoCanvas` (CI screenshots pixel-identical), settings go through `ISettingsStore`; the `IMidiBackend` seam moves to P2. P1 done: the engine census, then 10 PRs ([`engine-split.md`](engine-split.md)); `ReBuzz.Engine` builds as plain `net10.0` and `ReBuzz.exe` references it. P2 in progress ([`mac-engine.md`](mac-engine.md)): `rebuzz-headless` runs the engine with the MCP server on Linux and macOS (the A6 suite passes on `macos-15`), native machines are placeholders that save back unchanged, and it plays through PortAudio (CoreAudio) |
| Date | 2026-11-01 |
| Scope | One codebase that runs on macOS (Apple silicon) and Windows; keep C# if possible; what to do with Windows-only machines (keep, bridge, rebuild) |
| Code baseline | fork `22ff4a9` on upstream `wasteddesign/ReBuzz` @ `af162b9` (2026-10-24) |
| Related | docs/specs/mcp-server.md (headless mode §3, phases §9, machine upgrades §12), docs/specs/machines.md (Neo machines, editor kit), docs/specs/session-view.md |

---

## 1. Summary

**Verdict: feasible, and C# stays.** No language switch is needed. The cost is in the UI, not the engine.

| Layer | Today | Mac work |
|---|---|---|
| Engine: song model, audio graph, Session engine, file formats, managed machine host | Mostly portable C#. WPF leaks into files holding 20k of its 45k LOC, mostly through `MessageBox`, dispatcher, colours and `UserControl` | weeks |
| Neo DSP, Session scheduler, clip engine | Already compiled and tested on Linux CI (≈12k LOC) | none |
| UI | WPF everywhere: 110k LOC of C# in files that use WPF/WinForms, plus 185 XAML files (46k lines, 72 of them themes loaded at runtime) | months |
| Legacy native machines | 54 Windows DLLs (34 of them x86-only) in an out-of-process MFC host | can't run natively: rebuild, bridge or drop |
| Audio and MIDI I/O | ASIO/WASAPI (NAudio), WinMM, Windows MIDI Services | weeks; audio already sits behind `IAudioDriver` |

**Recommendation: engine first, then Avalonia.**

1. Split out a UI-free `ReBuzz.Engine` assembly as part of MCP A0. The headless `--agent` mode needs exactly this split.
2. Bring the engine to macOS headless (CoreAudio through PortAudio, CoreMIDI through DryWetMIDI). MCP agents then work on a Mac before any UI is ported.
3. Port the UI to Avalonia 12 (MIT), Mac first. Windows keeps WPF until the Avalonia UI reaches parity, then switches.
4. Legacy native machines don't run on Mac. Recompile the 17 with public sources natively. Use Neo successors (mcp-server §12.5) for the rest. Build exact "compat clones" only for the few that a song census shows matter.

**Effort** is in person-weeks (pw) of focused work with coding agents doing most of the typing. By hand, multiply by 1.7–2.

| Milestone | pw |
|---|---|
| Guardrails, engine split, Mac headless engine with MCP on Mac (P0–P2) | 10–16 |
| + Studio UI MVP on Mac (P3) | +10–15 |
| + Parity, Windows on Avalonia, signed Mac release (P4–P5) | +12–19 |
| **Total** | **32–50** |
| Optional: rebuild the open-source classics natively (R1) | 5–8 |
| Optional: compat clones of closed classics (R2) | 1–3 each |
| Optional: VST3/CLAP plugin host (P6) | 6–10 |

**The fast alternative is Avalonia XPF**, a commercial layer that runs WPF apps on macOS. It costs about 12–19 pw plus €19,500, and upstream merges stay free. Pick it only if you want full parity on Mac in months rather than a year, and accept a closed, paid dependency.

### Decisions (taken 2026-11-01, all as recommended)

| # | Decision | Options | Decided |
|---|---|---|---|
| D1 | UI stack | **A** Avalonia: open source, rewrite the views · **B** Avalonia XPF: €19.5k, keep WPF · **C** headless engine + web UI · **D** no port; run Windows in a VM on the Mac | **A** |
| D2 | Legacy native machines on Mac | **A** not on Mac: recompile the open-source ones, use successors, clone the few the census picks · **B** Wine/CrossOver bridge · **C** not on Mac and no rebuilds (songs show placeholders) | **A** |
| D3 | Timing relative to MCP | **A** before MCP · **B** after MCP: engine split inside A0, Mac headless after A2, UI after A5 · **C** full port in parallel from A0 | **B** |
| D4 | Upstream merges once Windows is on Avalonia | **A** merge everything (only realistic with XPF) · **B** merge engine and machines, re-port the UI features you want · **C** stop tracking upstream | **B** |
| D5 | Plugin hosting on Mac | **A** none in the first Mac release · **B** CLAP + VST3 host machine after P4 · **C** B plus AU | **A**; refined 2026-11-03 ([`plugin-host.md`](plugin-host.md)): a CLAP + VST3 host beside Polac, spike and census now, built after P3 |

## 2. Inventory: what ties us to Windows

### 2.1 Size and shape

Counts exclude `ExternalMachines/`, `3rdParty/`, vendored libraries (PowerCollections, Sanford MIDI, FftSharp) and tests. "WPF files" means files that use `System.Windows.*` UI namespaces, WinForms or `InitializeComponent`.

| Layer | Projects | C# LOC | of which in WPF files | XAML |
|---|---|---|---|---|
| Engine side | ReBuzz `Core/ Audio/ MachineManagement/ FileOps/ ManagedMachine/ NativeMachine/ Midi/` | 45,204 | 20,154 | 3 |
| App shell | ReBuzz `AppViews/ Common/`, `App.xaml` (includes the Session View UI, 5,708 LOC) | 11,105 | 10,257 | 11 |
| Shared UI library | BuzzGUI.Common | 29,256 | 13,002 | 29 |
| Machine API | BuzzGUI.Interfaces (including `Buzz.MachineInterface`) | 1,584 | 225 | – |
| Views and editors | BuzzGUI.MachineView, three sequence editors, WaveformControl, WavetableView, ParameterWindow, FileBrowser, PianoKeyboard … (15 projects) | 38,764 | 32,797 | 51 |
| Pattern editor | ModernPatternEditor.NET | 11,850 | 7,385 | 5 |
| Built-in machines with GUIs | AudioBlock, ConnectionMixer, EnvelopeBlock, ModernPianoroll | 30,862 | 23,251 | 14 |
| Small built-in machines | MidiOutput, QuickNote, Audio In/Out, Groove, Clip Player, MultiIO | 1,426 | 1,378 | – |
| Neo DSP and engines | NeoBuzz.Dsp, `Neo*/Engine` | 5,987 | 0 | – |
| Neo glue and editors | NeoBuzz.Machine, NeoBuzz.Editor, `Neo*/*.cs` | 3,169 | 1,690 | – |
| **Total** | 831 files | **179,374** | **110,200 (61 %)** | **113 compiled** |

There are also 72 runtime-loaded XAML files (35,563 lines), read with `XamlReader` in 19 places:

- 66 files in `RunTime/Themes`, for two themes (Default and Mire);
- 6 machine parameter editors in `RunTime/ParameterEditors`.

Some code is already portable. `ReBuzz.SessionTests` and `NeoBuzz.MachineTests` compile 5,772 + 5,987 LOC of engine code on Linux in CI (the `session-tests` job in `fork-ci.yml`). That is the pattern to grow.

Native code outside the machines:

| C++ project | Purpose | Notes |
|---|---|---|
| `ReBuzzEngine`, `ReBuzzEngine32` | the native machine host | 6.5k LOC, MFC |
| `NativeMachineFramework` | wraps native machine editors for .NET | C++/CLI (`CLRSupport NetCore`) |
| `ReBuzzPatternXP` | the PatternXP pattern editor | MFC |
| `LinkHelper` | Ableton Link helper process | CMake; portable |

### 2.2 Windows dependencies by kind

| Kind | Where and how much | On Mac | Cost |
|---|---|---|---|
| WPF UI | Every layer above: 110k LOC, 113 compiled XAML files | Avalonia (D1) | the bulk |
| Runtime XAML themes | 72 files, 35.6k lines; `XamlReader` in 19 places | One built-in theme. Keep the colour table (`ThemeColors`, key → colour) as data | 2–4 pw |
| WinForms | 7 files: `Screen`, keyboard hook, wrappers for old `Forms.UserControl` editors; plus NativeMachineFrameworkUI | Avalonia screens; drop the wrappers | small |
| HWND interop | `HwndHost`, `HwndSource` and `WindowInteropHelper` in 41 files. Native pattern editors are embedded with `CreateWindowEx` (`NativeEditorHost`) | None on Mac. On Windows, Avalonia's `NativeControlHost` | small |
| P/Invoke into Windows | 176 declarations: user32 70, winmm 33, kernel32 19, gdi32 17, shell32 12, dwmapi 9, gdiplus 7, others 9. Most sit in BuzzGUI.Common (WindowChrome shell code, Sanford MIDI) | Most disappear with the UI. The kernel32 calls (job objects, waitable timers, thread affinity) get POSIX equivalents | medium |
| P/Invoke into portable native libraries | The other 147 of 323 declarations (42 files): libsndfile, SoundTouch, r8brain, rnnoise; plus box2d | Build osx-arm64 dylibs | small |
| Registry | Settings in HKCU (`Settings.cs`, `RegistryEx`); `Microsoft.Win32` in 33 files. `IRegistryEx` is already injected into the engine | `ISettingsStore`: JSON in `~/Library/Application Support/ReBuzz` | 1 pw |
| Audio I/O | `IAudioDriver` with ASIO and WASAPI drivers (NAudio), about 550 LOC, plus 2 WPF config windows | Add a CoreAudio driver (§3.2) | 1–2 pw |
| MIDI | NAudio.Midi (WinMM), Windows MIDI Services (`Midi2.cs`, virtual endpoint), Sanford (winmm); MIDI clock on kernel32 waitable timers | DryWetMIDI or CoreMIDI (§3.2) | 1–2 pw |
| Threads and priority | `SetThreadAffinityMask`, `ThreadPriority.Highest`, process priority classes, winmm multimedia timer | Real-time thread policy and audio workgroup (§3.2) | 1 pw |
| Native machine host | `ReBuzzEngine32/64.exe` (MFC). Messages go over a named memory-mapped file, synchronised by named `Global\Ping…`/`Global\Pong…` events | §4 | §4 |
| C++/CLI and MFC editors | NativeMachineFramework, ReBuzzPatternXP | None on Mac. PatternXP data stays in songs | – |
| VST | Polac VST/VSTi hosts: closed and native, with helpers in `Gear/vst` and `bin*/vst*.exe` | D5 | – |
| Paths | Backslash literals such as `buzzPath + "\\bin64\\…"`: 86 lines in 35 files | `Path.Combine` | small |
| Build | `build.ps1`, vcxproj files, `cmd` Exec steps (`ForcePerformRestore`), TFM `net10.0-windows10.0.26100.0` everywhere | Multi-target; `dotnet` + CMake | 1 pw |

### 2.3 How machines are hosted today

**Managed machines** (`IBuzzMachine`, found by reflection) load in-process with `Assembly.LoadFile`. The loading itself is portable, with two problems:

- `ManagedMachineDll.LoadManagedMachine` calls `Assembly.GetTypes()`. That throws if any type in the DLL has a missing WPF base class. On Mac, catch `ReflectionTypeLoadException` and keep the types that did load.
- GUIs are WPF `UserControl`s (`IMachineGUIFactory`), and pattern editors return a `UserControl`; old ones return WinForms.

**Native machines** always run out of process: one `ReBuzzEngine64.exe` or `ReBuzzEngine32.exe` per machine (or one per bitness in single-process mode). Native GUIs are HWNDs parented across processes.

**Consequence:** the bridge boundary already exists. What a Mac lacks is the transport (named Windows kernel objects) and the host (MFC loading Win32 DLLs).

**The machine API leaks WPF types:**

- `IMachineDLL.Skin`, `IMachineDLL.SkinLED` (`ImageSource`);
- `IMachineDLL.TextColor`, `IBuzz.ThemeColors` (`System.Windows.Media.Color`);
- `IMachine.ParameterWindow` (`Window`).

`ICommand` is fine: it lives in `System.ObjectModel` and is cross-platform.

### 2.4 The package's machines by portability

| Group | Count | Examples | On Mac |
|---|---|---|---|
| Neo | 6 (Wave and FM6 in progress), more planned | Neo Drive … Neo FM6 | DSP is portable today; editors need the editor-kit backend (§8) |
| ReBuzz built-ins (source in repo) | 11 | Audio In/Out, Session Clip Player, AudioBlock, EnvelopeBlock, ConnectionMixer, Modern Pianoroll | Ported with the UI |
| Pedal (source in repo) | 19 | Juno106, SH-101, Drum Matrix, MComp | All 19 build `net10.0` without their WPF GUIs (studio-mvp.md S2) and load and play; they show the generic parameter window. The synths take notes in their Note column only, not MIDI. Chord, LFO and Peer Control are targeted in WPF dialogs, so off Windows only from songs |
| Prebuilt managed | 19 | Jeskola Live, Loop Recorder, Arpeggiator, Info, TrackScript, Yitten Mongoose | All 19 reference PresentationFramework; 7 target .NET Framework 4.0. Their DSP may load; their GUIs won't, except under XPF |
| Native, public source | 17 | FSM Infector, KickXP, PhatMan, PanzerDelay, Chorus2, WahMan/WahManPro/WahPro2; Jeskola Delay, Cross Delay, Distortion, NiNjA dElaY, Trilok, Noise; Oomek Aggressor 303, Exciter, Masterizer | Recompile (R1, §4.2) |
| Native, closed | 29 | Jeskola Bass/2/3, Tracker, Organ, Reverb, Stereo Reverb, Raverb, EQ-3, Filter/2, Chorus, Flanger, Wave Shaper; Joachims ×7; Rout 909; FSM Philthy; BTDSys PeerChord; Bigyo, Intoxicat, KrimZon, kibibu, mixIO | Successor, clone or nothing (§4.2) |
| Native, Polac I/O and VST | 8 | Polac VST/VSTi 1.1, Audio In/Out, MIDI In, MIDI Bus II, Out, Out II | ReBuzz's own I/O machines; VST through D5 |

### 2.5 Engine/UI separation today

**What is already in place:**

- **Injected services.** Between December 2024 and March 2025, upstream extracted `IUiDispatcher`, `IRegistryEx`, `IUserMessages`, `IFileNameChoice` and `IKeyboard` from `ReBuzzCore`, for its test automation. `ReBuzzCoreInitialization` calls itself "work in progress", and the test Driver already runs the engine without a window.
- **Audio drivers.** Upstream put ASIO and WASAPI behind `IAudioDriver` in September 2026.
- **Pure fork code.** The Session scheduler, document, MIDI map, takes, clip engine, sync clocks and Neo DSP compile on Linux.

**Where the engine still leaks into WPF** (engine-side folders only):

| Leak | Count | Fix |
|---|---|---|
| `MessageBox`, `MessageBoxWindow` | 25 lines in 13 files | `IUserMessages` (exists) |
| WPF `UserControl`/`Window` (pattern editor control, parameter window, machine GUI host) | 18 lines in 5 files, plus the parameter-window code in `MachineCore` | `IMachineUiHost`, implemented by the UI |
| WPF `Color` and `ImageSource` on machine DLLs | 8 files | ARGB `uint` and image paths in the engine; the UI converts |
| `Keyboard.Modifiers` read inside the engine | 4 lines in 3 files | `IKeyboard` (exists) |
| `HwndSource`, `MachineViewHWND` at startup | `ReBuzzCoreInitialization`, step 2 | The native-host parent handle comes from the UI service |
| Everything ships in one exe | `ReBuzz.csproj`: `WinExe`, `UseWPF`, Windows TFM | `ReBuzz.Engine` (net10.0) + `ReBuzz` (UI) |

## 3. Options

### 3.1 Compared

Every real port shares a base of 11–17 pw: the engine split (P1, 4–6), the Mac platform layer (P2, 5–8), and packaging and CI (P5, 2–3). The totals below include it.

| | O0 Windows VM | O1 Avalonia XPF | **O2 Avalonia** | O3 Headless engine + web UI | O4 Uno Platform | O5 .NET MAUI | O6 Rewrite (C++/JUCE, Rust) |
|---|---|---|---|---|---|---|---|
| Effort, pw | 0 | 12–19 (+4–6 for the split MCP needs anyway) | **32–50** | 50–80 | 40–60 | – | 150–300 |
| Cash | VM subscription + Windows 11 licence | €19,500 Business (€7,800 Internal: no distribution outside your organisation) | 0 | 0 | 0 | – | 0 (JUCE is GPL or paid) |
| Kept | everything | all C#, WPF views, XAML themes, probably WPF machine GUIs | engine, machines, MCP, view logic | engine, machines, MCP | engine, machines | – | the song format |
| Rewritten | nothing | platform layer, Win32 interop | views (110k LOC touch WPF), themes | the whole UI in a second language, plus a protocol | views, in the WinUI dialect | – | everything |
| Fidelity on Mac | full, but it's Windows | high, depends on the vendor | high where ported. Legacy WPF machine GUIs fall back to a generic or manifest editor | a new UI | as O2 | – | – |
| Fit with MCP and agents | no Mac engine | fine | good: the engine split is A0's headless mode | best | good | – | start over |
| Custom editors (Neo kit) | as today | as today | `DrawingContext` maps almost 1:1 | rewrite as a web canvas | Skia, further from WPF | – | JUCE is good at this |
| Upstream merges | free | nearly free | engine free, UI by hand (§6) | engine free, UI none | as O2 | – | none |
| Main risk | latency, no Mac feel | closed vendor; Win32 shim gaps; custom-drawing speed unknown; everyone who builds the Mac target needs a licence | size; legacy WPF GUIs on Windows | two stacks and a protocol to keep forever | dialect, smaller desktop community | – | years |

**O0 Windows VM: a stopgap, not a port.** Run Windows 11 on Arm in Parallels. It runs ReBuzz (x64) and the x86/x64 native hosts through Windows' own emulation. Zero code, every machine works, and MCP works inside the VM. Latency is higher and there is no Mac integration. It is the answer to "I need it on my Mac this month", and the escape hatch for old songs.

**O1 Avalonia XPF.** XPF keeps the WPF API and swaps WPF's renderer for Avalonia's. Its docs claim "if it compiles against WPF today, it will most likely compile against XPF".

- **Status and licence.** Current release 1.6.7 (2026-09-15). Licences are perpetual, with 12 months of updates and a 30-day trial.
- **Still needed:** all of P2 (audio, MIDI, settings, threads, native machines), plus removing the user32/gdi32/dwmapi calls and HWND hosting that XPF doesn't cover. Its Win32 shims are documented only as "enough for common third-party controls".
- **Spike before paying:**
  - Rendering speed of the heavy `OnRender` views (pattern editor, machine view).
  - Whether the `XamlReader` themes load.
  - Whether the .NET Framework 4.0 machine GUIs work.
- **When it wins:** full parity on Mac in months rather than a year, with upstream UI merges kept cheap.

**O2 Avalonia (recommended).**

- **Why it fits.** MIT licence, version 12.x, supports macOS 14–26 on arm64 and x64, and has the API model closest to WPF.
- **Code-drawn views port nearly line for line.** Our heaviest views draw in code (`OnRender`, `DrawingContext`, `StreamGeometry`, `FormattedText`): the Neo kit, the Session View, the pattern and sequence editors.
- **XAML needs rewriting.** XAML views, styles and templates have to be redone, because Avalonia styles use selectors instead of triggers.
- **Port order, by value:** Neo editors, Session View, machine view, Modern Pattern Editor, parameter window.
- **Dropped:** two of the three sequence editors and the runtime theme system.

**O3 Headless engine + web UI.** An engine process with a local API, and a UI in a webview (Photino or Tauri, both WKWebView on Mac) or Electron.

- **For:** it matches MCP perfectly. The agent and the UI become two clients of one engine API.
- **Against:** it is a new UI from scratch in a second language, plus a state-sync protocol to design and keep stable. Machine editors become web components, so WPF and native GUIs are lost.
- **Worth it only** if a browser or remote UI is a goal in itself.

**O4 Uno Platform.** Uno 6 (Apache-2.0) targets the WinUI API, not WPF, and renders with Skia on Mac. It means more translation than Avalonia for the same result, and its strengths (WebAssembly, mobile) aren't what we need.

**O5 .NET MAUI.** Its only macOS target is Mac Catalyst, it is built on native controls, and its XAML is a different dialect from WPF's. Not viable for a DAW-class desktop UI.

**O6 Full rewrite in C++/JUCE or Rust.** It throws away:

- the managed machine API and every C# machine;
- 18k LOC of Session View;
- 45k LOC of engine;
- the MCP design.

It would arrive roughly where O2 lands, with a better plugin ecosystem. Rust has no mature DAW-grade GUI toolkit. A rewrite only makes sense as a new product, not as a port.

### 3.2 Pieces every port needs

**Audio and MIDI**, behind `IAudioDriver` and a new `IMidiBackend`:

| Need | Mac choice | Why | Alternatives |
|---|---|---|---|
| Audio out/in, duplex | PortAudio (CoreAudio backend), through a thin P/Invoke or PortAudioSharp2 (osx-arm64 runtime package) | Mature CoreAudio duplex, device enumeration, fixed buffer sizes | miniaudio through SoundFlow (MIT; its maintainer is on hiatus until Feb 2027); our own AUHAL shim (~500 LOC of C); AudioToolbox bindings in `net10.0-macos` |
| MIDI in/out, virtual ports | DryWetMIDI (MIT): device API on Windows and macOS, virtual devices on macOS | One API for both OSes | CoreMIDI through `net10.0-macos`; RtMidi |
| MIDI clock out | A thread timed with `mach_wait_until` | Replaces the kernel32 waitable timer | – |
| Audio worker threads | Join the device's audio workgroup (`os_workgroup`) and set a time-constraint policy | `ThreadPriority.Highest` gives no real-time scheduling on macOS. Unjoined threads on Apple silicon aren't scheduled for the audio deadline, so they cause dropouts | – |
| Ableton Link | Build `LinkHelper` with CMake on macOS | Link is cross-platform; the helper only talks over stdin/stdout | – |
| Windows | Keep ASIO and WASAPI | – | – |

**Settings, files, processes**

- `ISettingsStore` (JSON) replaces the registry.
- `Path.Combine` everywhere.
- `ChildProcessTracker`'s job objects become a process group that is killed when the app exits.
- File dialogs go through Avalonia's `StorageProvider`.
- Setting `Process.PriorityClass` to High fails without root on macOS, so guard it.

**Mac distribution**

- **Signing:** a Developer ID from the Apple Developer Program, notarization with `notarytool`, hardened runtime.
- **Entitlements:** .NET needs `allow-jit`, `allow-unsigned-executable-memory`, `allow-dyld-environment-variables` and `disable-library-validation`. The last one also lets native machine dylibs and plugins load.
- **Downloaded machines:** managed machine DLLs are IL, so library validation doesn't apply to them. Native dylibs that users download are quarantined by Gatekeeper: install them through the app, which clears the quarantine flag.
- **arm64 only.** macOS 27 requires Apple silicon, and Intel Macs stop at macOS 26.
- **Sign every bundled dylib:** PortAudio, libsndfile, SoundTouch, r8brain, rnnoise, LinkHelper, the portable native host.

**Mac CI**

- **Runners.** GitHub's `macos-15` and `macos-26` (`macos-latest`) runners are arm64; `macos-14` retires on 2026-11-02.
- **Cost.** macOS minutes cost $0.062, against $0.006 on Linux and $0.010 on Windows: about 10× Linux. On this private repo, run Mac jobs only on PRs into main: build, engine tests and the headless MCP script (A6). Build the package on demand.
- **Guard.** The existing Linux job should compile `ReBuzz.Engine`. It is the cheapest guard against new Windows leaks.

## 4. Legacy machines on macOS

### 4.1 Running the Windows binaries

**Drop on Mac (the default).** Songs load these machines as missing machines. ReBuzz already keeps their data and patterns, so they play again on Windows.

**Wine/CrossOver bridge: not a foundation.**

- **How it would work:**
  - Run `ReBuzzEngine64.exe` in a Wine prefix with the MFC and VC runtimes.
  - Replace the named events and named shared memory with a transport both sides can see: a shared file mapping plus a socket handshake (still to be proven).
  - Run 32-bit machines through Wine's WoW64.
- **The emulator underneath is changing (November 2026):**
  - x64 code needs Rosetta 2, which stays general-purpose only through macOS 27. macOS 28 (fall 2027) keeps a subset for old games.
  - CrossOver's native ARM64 build, based on FEX, is in preview and aimed at CrossOver 28.
  - CrossOver 27 runs only on Apple silicon and dropped 32-bit bottles, though "almost all 32-bit applications run just fine in a 64-bit bottle".
  - Wine 11 made WoW64 mode feature-complete.
  - I found no maintained macOS equivalent of yabridge.
- **Why not:**
  - Each native machine adds a translated process hop to every audio block.
  - GUIs float as separate Wine windows.
  - Users must install CrossOver or Wine, or we sign and ship Wine ourselves.
- **Cost if wanted anyway:** 4–8 pw plus a spike, and ongoing upkeep to track Wine and CrossOver.

**Windows VM (O0).** Works today and is the escape hatch for old songs.

### 4.2 Rebuilding: four routes

This adds a Mac route to mcp-server.md §12.5 (Neo successors); it doesn't replace that table.

| Route | What | Use for | Cost |
|---|---|---|---|
| **R1 Recompile** | Build the public-source classics as arm64 dylibs. The Buzztrax collection already builds them with gcc on Linux, with its own `windef`, `mdk` and open `dsplib`. Host them in a portable `ReBuzzEngine`: no MFC, POSIX shared memory, and the same message protocol `NativeMessage` speaks now, so only the transport changes | the 17 open natives; same code, same sound | 5–8 pw for host, transport and the first 17 |
| **R2 Compat clone** ("Neo Classic …") | A new C# machine with the original's exact parameter layout (names, order, types, ranges, defaults, no-value codes, tracks, attributes) and saved-state format; sound matched by measurement | closed machines that old songs depend on (census) | 1 pw for a simple effect, up to 3 pw for a Jeskola Tracker–class machine |
| **R3 Successor** | The §12.5 table: a better design with new parameters; songs migrate with `machine_replace` and a render comparison | the default on Mac for everything else | already planned |
| **R4 Nothing** | A placeholder keeps the data | niche closed machines | 0 |

| Machines | Source | Mac route |
|---|---|---|
| FSM Infector, KickXP, PhatMan, PanzerDelay, Chorus2, WahMan, WahManPro, WahPro2 | Buzztrax | R1 |
| Jeskola Delay, Cross Delay, Distortion, NiNjA dElaY, Trilok, Noise Generator | Buzztrax (published by Jeskola) | R1 |
| Oomek Aggressor 303, Exciter, Masterizer | Buzztrax | R1 |
| Jeskola Bass 2/3, Jeskola Tracker, Rout 909 | closed | R2 if the census shows real use (classic songs live on their exact sound); otherwise R3 (Neo Poly, Neo Sampler, Neo Drums) |
| Jeskola Reverb, Stereo Reverb, Raverb, EQ-3, Filter/2, Chorus, Flanger, Wave Shaper, Organ | closed | R3 (Neo Verb, EQ, Filter, Ensemble, Drive, Wave); R2 only for top census hits |
| Joachims ×7, FSM Philthy, BTDSys PeerChord, Bigyo Wood One, Intoxicat ACloud15, KrimZon AY-3-8910, kibibu Green Milk, mixIO | closed | R3 where §12.5 lists a successor, otherwise R4 |
| Polac ×8 | closed | ReBuzz's own Audio In/Out and MIDI machines; VST through D5 |

**Run a song census before any R2.** A small tool counts machine use across a corpus of songs: yours, plus public .bmx archives. Weight each machine by how much it is used, then clone only the top 3–5.

The A2 profiler (mcp-server §12.2) already measures what a clone has to match: parameter text across each range, frequency and impulse responses, pitch convention, latency. Run it on Windows against the original, and make the clone's tests assert the same numbers.

### 4.3 Song compatibility

- **Substitution table** (`Gear/substitutes.json`). Each entry maps an original DLL name to a replacement machine, with a parameter map and a converter for saved machine data. Clones (R2) use an identity map; successors (R3) map through the manifests' roles and units.
- **Clones load under the original name.** The song keeps the original machine name, and the loader creates the clone when the original is missing, which on Mac is always.
  - Saving writes the original name back, so the song still opens on Windows with the original machine.
  - Patterns, automation and values carry over untouched, because the parameter layout is identical.
- **Successors migrate explicitly**, through `machine_replace` (A4) with its before/after render report.
- **Matching targets.** Clones match within measured tolerances (level ±0.5 dB, third-octave spectrum ±1 dB, envelope times ±5 %), not bit for bit: compiler, floating-point model (x87, SSE, arm64) and noise seeds all differ. R1 recompiles carry the same caveat to a lesser degree.
- **Comparing renders across OSes.** The same managed code can differ in its last bits between x64 Windows and arm64 macOS, because the math libraries differ.
  - Compare renders with a null test: the difference must be at or below −100 dBFS.
  - MCP A6's "bit-identical across runs" check stays per platform.

### 4.4 Legal notes (EU law as implemented in Italy; not legal advice)

The Software Directive (2009/24/EC) sets the same rules in every EU country; Italy implements it in the copyright law, L. 633/1941.

- **Black-box study is allowed and can't be taken away by contract.** Art. 64-ter(3) lets anyone entitled to use a copy observe, study and test how the program works, to find the ideas and principles behind it, while loading, running or storing it as they are entitled to. Contract terms against it are void (same article). This covers profiling a machine's parameters and sound. Directive basis: Art. 5(3) and Art. 8.
- **Decompiling is much narrower.** Art. 64-quater (Directive Art. 6) allows it only:
  - for interoperability information that isn't otherwise readily available;
  - limited to the parts of the program needed;
  - never to develop a program substantially similar in its expression, nor to pass the information on beyond what interoperability needs.

  Don't decompile machines to clone them. The interface they expose (parameter layout, state format) is observable anyway.
- **Public sources come with licences.** Buzztrax machines carry per-author licences; check each one (see ExternalMachines/README.md). Buzz's `MachineInterface.h` allows freeware only: nothing built on it may be sold. This matters if any of it ever becomes a product.
- **Names.** Ship clones under our own names, e.g. "Neo Classic Bass, Jeskola Bass 3 compatible". The original name appears only in the substitution table.

### 4.5 Plugins (D5)

Today VST runs through Polac's closed Windows machines, so a Mac build has no plugin hosting until we build a host machine.

- **Formats:** CLAP (MIT) and VST3 (MIT since VST 3.8, October 2025) are both licence-compatible with the fork. AU through AudioToolbox if needed.
- **Hosting:** built on R1's portable native host, so plugins run out of process like native machines. Plugin editors open as separate windows, because macOS can't embed an NSView from another process.
- **Cost:** 6–10 pw. Not needed for the first Mac release.
- **Analysis (2026-11-03):** [`plugin-host.md`](plugin-host.md): a new host versus Polac, what each format covers (including VST2's licence route), isolation and effort. It recommends a CLAP + VST3 host beside Polac, 10–14 pw with a sandbox.
- **H0 spike (2026-11-03):** [`plugin-host.md`](plugin-host.md) §8. VST3 and CLAP plugins (Surge XT among them) play and keep their state in songs inside the engine on all three OSes. A sandbox costs 8–20 µs per block. Plugin views embed in Avalonia's `NativeControlHost` on macOS and Windows. `tools/plugin-census.py` counts installed plugins and Polac use in songs.

## 5. Plan

### 5.1 Before or after MCP: after, with the engine split inside A0

| | Port before MCP | **Port after MCP, split inside A0** |
|---|---|---|
| Engine split | Done twice: once for the port, then reworked by A0 for headless mode | Done once: A0's `ReBuzz.exe --agent` *is* the split |
| Regression net for the port | ReBuzzTests only (Windows) | Also the A1–A6 scripted MCP suite against headless ReBuzz. The same scripts run on Mac and Windows and null-test each other's renders |
| MCP delivery | Delayed by the whole port (32–50 pw) | Unchanged |
| A4 machines (Neo EQ, Limiter, Utility, Analyzer) and the manifest editor | Built on WPF, then ported | Built once on the portable editor kit (P0) |
| What you can measure | A port with no measurable target | A Mac engine validated by agent tooling before any UI work |

### 5.2 Phases

Windows keeps the WPF UI through P3. New UI built during that time goes on the portable editor kit, so it lands on both platforms: the MCP studio pieces, the A4 editors, the manifest editor.

| Phase | When | Delivers | Exit criteria | pw |
|---|---|---|---|---|
| **P0 Guardrails** | now, in parallel with A0/A1 | The rules in §8; editor-kit drawing behind `INeoCanvas`; `ISettingsStore`; an `IMidiBackend` seam; Linux CI compiles every new pure file | Neo editors draw through the canvas (WPF backend), with no visual change | 1–2 |
| **P1 Engine assembly** | alongside A1 (A0 shipped first: its protocol layer is already pure, so P1 moves only the `ReBuzz/Agent` glue onto the engine) | `ReBuzz.Engine` (net10.0, no WPF) compiles the engine files where they are, by linking. WPF calls go behind `IUserMessages` and `IMachineUiHost`; colours become ARGB. `BuzzGUI.Interfaces` multi-targets one assembly identity: a portable subset plus the current Windows superset. `ReBuzz.Agent` references the engine only. Census and PR plan: docs/specs/engine-split.md | The engine builds on Linux CI; ReBuzzTests pass on Windows against it; `ReBuzz.exe --agent` runs headless | 4–6 |
| **P2 Mac engine** | after A1/A2 | PortAudio driver, DryWetMIDI backend, JSON settings, real-time threads with the audio workgroup, MIDI clock, Link helper, osx-arm64 native libraries. Managed machines load even when WPF types are missing; native machines appear as placeholders. Headless `--agent` on macOS | The A1 scripted suite passes on `macos-15`; Neo renders null-test against Windows at ≤ −100 dBFS; a 1-hour play test at 128 samples has no dropouts | 5–8 |
| **P3 Studio MVP** (Avalonia, Mac first) | after A5 | Shell, machine view, one sequence editor, Modern Pattern Editor, generic parameter window, Session View with mixer, Neo editors (Avalonia canvas backend), preferences, one dark theme; MCP studio mode | Compose a song from scratch on a Mac with Neo and Pedal machines; old songs open (natives as placeholders) and save back without loss | 10–15 |
| **P4 Parity and Windows switch** | after P3 | AudioBlock, EnvelopeBlock, ConnectionMixer, Pianoroll; wave editing, file browser, HD recorder, signal analysis; the manifest editor for machines without a GUI; the Windows build moves to Avalonia, with native editors through `NativeControlHost`; the WPF UI is retired | Parity checklist signed off; ReBuzzTests and the MCP suite green on both OSes | 10–16 (themes included) |
| **P5 Release** | with P3/P4 | Mac app bundle, Developer ID signing, notarization, DMG; a cross-platform build script; the Mac CI job | A notarized build installs and runs on a clean Mac | 2–3 |
| **R1 Classics, native** | any time after P2 | The portable native host plus the 17 open-source classics | Each one loads and renders, and old songs that use it play on a Mac; tested like the Neo machines | 5–8 |
| **R2 Clones** | after the A2 profiler and the census | The top 3–5 closed machines | Each matches its original within the §4.3 tolerances; old songs open with the clone in its place | 1–3 each |
| **P6 Plugins** (optional) | after P4 | CLAP + VST3 host machine | A CLAP and a VST3 plugin each load, play and save their state | 6–10 |

## 6. Upstream merge cost

Upstream is busy and speeding up: 415 commits on `main` in the last 12 months, 96 of them in October 2026. Changed lines over that year, excluding docs and build stamps:

| Area | Changed lines per year | Share |
|---|---|---|
| Engine side: Audio, Midi, Core, FileOps, MachineManagement, NativeMachine, ManagedMachine, Interfaces, ReBuzzEngine | ~12,800 | 55 % |
| UI: ReBuzz Common/AppViews, MachineView, BuzzGUI.Common, MetadataControl, pattern and sequence editors, ParameterWindow, BuzzUpdate, FileBrowser … | ~6,700 | 29 % |
| Machines, RunTime (including themes), tests, CI, other | ~3,800 | 16 % |

| Option | Engine changes | UI changes | Ongoing |
|---|---|---|---|
| O1 XPF | merge as today | merge as today | about what it costs now |
| O2, P1–P3 (Windows still on WPF) | merge cleanly: files stay where they are, linked into the engine | merge into WPF; the Mac UI lags | re-port only what you want on Mac |
| O2 after P4 | merge | re-port by hand: 29 % of upstream's changes | about 1–3 pw a year if you take most UI features; near 0 if you take none |
| O3 web UI | merge | nothing applies | as O2 after P4, with no reuse |
| O6 rewrite | – | – | the fork becomes a separate product |

**P1's own conflict surface is small.** Replacing WPF calls in engine files touches about 150–300 lines: the `MessageBox` lines, `UserControl` returns, colours, images, keyboard reads. These are exactly the lines upstream might also edit. Offer them upstream as PRs: upstream began this separation itself, and once it merges them, the conflicts are gone.

## 7. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| The UI port drags: 110k LOC; agents translate fast, but look, feel and input parity take human time | high | months | Cut the P3 scope to the views you use; drop 2 of 3 sequence editors and the runtime themes; ship Mac at the MVP |
| Legacy WPF machine GUIs on the Avalonia Windows build (19 prebuilt managed machines, the Pedal GUIs) | high | those GUIs are lost on Windows after P4 | Spike: open them in a separate WPF window from the Avalonia app on Windows. Fallbacks: generic or manifest editor, or keep WPF on Windows longer |
| Real-time audio from .NET on macOS (GC, scheduling) | medium | dropouts | The engine already allocates nothing per buffer (0.4.1 hardening); audio workgroup and time-constraint threads; the P2 exit test at 128 samples |
| Old songs with native machines sound different or silent on Mac | certain | some songs | R1 for 17 machines, R2 driven by the census, the VM escape hatch. Songs keep their data and still play on Windows |
| Changing `BuzzGUI.Interfaces` breaks machine binaries | medium | managed machines stop loading | Multi-target with one assembly identity; the Windows superset stays unchanged; test with all 19 prebuilt managed machines |
| P1 edits conflict with upstream | medium | painful merges | Link files in place, keep edits small, send them upstream as PRs |
| XPF, if chosen: shim gaps, custom-drawing speed, the vendor's future | medium | stuck on one version | Spike the real app during the 30-day trial before paying; the licence is perpetual |
| Wine, if chosen: Rosetta ends (macOS 28), FEX matures slowly, 32-bit support shrinks | high | an OS update breaks the bridge | Don't build on it (D2) |
| Mac CI cost | low | about 10× Linux per minute | PRs into main only; a self-hosted Mac mini if usage grows |
| Licences of recompiled or cloned machines | low | can't redistribute | Check each author's licence; `MachineInterface.h` is freeware-only; keep the repo private until cleared |

## 8. What to do now (cheap, whatever you pick)

1. **Keep engine code UI-free.** New code in `ReBuzz/Core`, `Audio`, `Midi`, `FileOps` and `MachineManagement` doesn't touch WPF:
   - no `System.Windows.*`, `MessageBox` or `Keyboard`;
   - no dispatcher except `IUiDispatcher`.

   Enforce it by compiling new pure files on the Linux CI job, the way `ReBuzz.SessionTests` already does.
2. **MCP talks to the engine, never to the UI.**
   - `ReBuzz.Agent` (A0) depends only on engine types and on services: `IUiDispatcher`, `IUserMessages` and the engine queue.
   - The studio-mode pieces (settings page, Agent badge, confirmations) live in the UI assembly and subscribe to events.
   - Draw MCP plots with a toolkit-free renderer such as SkiaSharp. The same code then works headless, in the WPF studio and in Avalonia later.
3. **Put the Neo editor kit behind a thin canvas.**
   - Today `NeoBuzz.Editor` (1,057 LOC) and the per-machine editors (633 LOC) draw with WPF directly: `OnRender`, `Pen`, `SolidColorBrush`, `StreamGeometry`, `FormattedText`, WPF mouse events.
   - Move drawing to an `INeoCanvas` (lines, rectangles, arcs, paths, text, clipping) and input to plain events. Layout, hit-testing and value mapping stay toolkit-free.
   - WPF backend now; Avalonia and Skia backends later. The Skia backend renders PNGs for the editor screenshots in tests and in MCP.
   - About 1 pw. Do it before A4 adds four editors and §12.3 adds the manifest editor.
4. **Keep Neo machine projects ready to multi-target.** DSP stays in `Engine/` (already pure), WPF only in the editor files, and no new `BuzzGUI.Common` dependencies in machine glue.
5. **New settings go through `ISettingsStore`**, not `Registry`/`RegistryEx`.
6. **No new Windows-only plumbing.** Use `Path.Combine`, not `"\\"` literals. Add no new kernel32, user32 or winmm P/Invokes.
7. **Keep song files portable.** New song data (Session View, clips) uses song-relative paths and no Windows types.
8. **Build the census tool** (§4.2) and run it once on your songs. It is cheap and settles D2 and R2.

## 9. Sources

Repository evidence: file paths and counts in §2 and §6, measured on the working tree and with `git log upstream/main` on 2026-11-01.

- Avalonia supported platforms (12.x: macOS 14–26 on arm64/x64, .NET 8+ on desktop): https://docs.avaloniaui.net/docs/supported-platforms
- Avalonia 12 releases: https://newreleases.io/project/github/AvaloniaUI/Avalonia/release/12.0.4
- Avalonia XPF overview and licensing model: https://docs.avaloniaui.net/xpf/welcome
- Avalonia XPF pricing (Internal €7,800, Business €19,500, Enterprise €55,000): https://avaloniaui.net/xpf/pricing
- Avalonia XPF release notes (1.6.7, 2026-09-15): https://docs.avaloniaui.net/xpf/release-notes
- Avalonia XPF Win32 API shims: https://docs.avaloniaui.net/xpf/third-party/win32-api-shims
- MAUI vs Avalonia vs Uno in 2026: https://startdebugging.net/2026/05/maui-vs-avalonia-vs-uno-in-2026/
- .NET on macOS (.NET 10 on macOS 14, 15, 26; Arm64 and x64): https://learn.microsoft.com/en-us/dotnet/core/install/macos
- .NET notarization and entitlements: https://learn.microsoft.com/en-us/dotnet/core/install/macos-notarization-issues
- macOS 27 is the last release with full Rosetta 2: https://macrumors.com/2026/06/10/macos-golden-gate-last-to-support-intel-apps/
- Rosetta timeline (macOS 27, macOS 28): https://appleinsider.com/articles/26/06/12/how-and-when-macos-will-finally-stop-support-for-intel-apps
- macOS 27 requires Apple silicon; Tahoe is the last for Intel: https://www.techrepublic.com/article/news-apple-macos-27-drops-intel-mac-support-rosetta/
- CrossOver 27 (Apple silicon only, no 32-bit bottles, ARM64 plans): https://www.codeweavers.com/blog/mjohnson/2026/6/11/whats-in-and-whats-out-for-crossover-27
- CrossOver ARM64 preview based on FEX: https://www.ithinkdiff.com/?p=343215
- CrossOver ARM64 preview, coverage: https://borncity.com/news/crossover-fuer-mac-arm64-version-ohne-rosetta-2-verfuegbar/
- Wine 11.0 (WoW64 mode feature-complete): https://linuxiac.com/wine-11-0-brings-fully-supported-wow64-mode/
- DryWetMIDI supported OS (device API on Windows and macOS; virtual devices on macOS): https://melanchall.github.io/drywetmidi/articles/dev/Supported-OS.html
- SoundFlow (miniaudio backend, MIT, maintainer hiatus): https://www.nuget.org/packages/SoundFlow
- PortAudioSharp2: https://nuget.org/packages/PortAudioSharp2
- PortAudio osx-arm64 runtime package: https://packages.nuget.org/packages/org.k2fsa.portaudio.runtime.osx-arm64
- VST 3.8 under the MIT licence; ASIO SDK under GPLv3: https://sonicstate.com/news/2025/10/30/vst-3-now-available-under-mit-license
- GitHub macOS 14 runner retirement and replacement labels: https://github.blog/changelog/2026-10-01-github-actions-macos-14-runner-image-retirement
- GitHub Actions per-minute runner prices: https://docs.github.com/en/enterprise-server@3.17/billing/reference/actions-runner-pricing
- L. 633/1941 art. 64-ter (observe, study and test; contrary clauses void): https://www.brocardi.it/legge-diritto-autore/titolo-i/capo-iv/sezione-vi/art64ter.html
- L. 633/1941 art. 64-quater (decompilation for interoperability): https://www.brocardi.it/legge-diritto-autore/titolo-i/capo-iv/sezione-vi/art64quater.html
