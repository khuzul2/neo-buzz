# Engine split (P1) — Census and plan

| | |
|---|---|
| Status | Done (P1). PRs 1–10 done: the seam interfaces stand alone, `Utils` is split, `BuzzGUI.Interfaces` and `BuzzGUI.Common` build twice (Windows and portable), `ReBuzz.Engine.csproj` exists, the engine's message boxes go through `IUserMessages`, its timers, keyboard reads, import dialog, build info and logging go through seams, theme colours and skins are portable data, machine windows belong to an `IMachineUiHost`, the view state the engine read from WPF statics goes through seams, and audio drivers and MIDI devices sit behind `IAudioDriverFactory` and `IMidiBackend`. `ReBuzz.Engine` compiles as plain `net10.0` on Linux and `ReBuzz.exe` references it (the flip): the engine files are compiled once, into `ReBuzz.Engine.dll`. Linux CI builds the engine (the census ratchet is retired); Windows CI checks the portable API surface (§5, Progress) |
| Date | 2026-11-01 |
| Scope | What keeps the engine from compiling as plain `net10.0` (no WPF, no Windows); the seams that fix it; how `BuzzGUI.Interfaces` and `BuzzGUI.Common` stay binary compatible; the PR sequence for P1 |
| Code baseline | fork `a90558a` (main, 2026-11-01). The MCP work is editing `ReBuzz/Agent/*` and `ReBuzzCore.cs` in parallel, so re-run the census after it merges |
| Related | docs/specs/cross-platform.md (§2.5, §5.2 P1, §8), docs/specs/mcp-server.md (A0 headless mode) |
| Tools | `experiments/engine-census/`: `EngineCensus.csproj` (staged file set), `census.py` (measure and classify), `tool/` (Roslyn and metadata reader), `dim_check.py` (§4.3 proof) |

---

## 1. Summary

**The engine is 228 files and 55.8k LOC, and 356 compile errors stand between it and plain `net10.0`.** They sit in 48 of the 228 files, on 295 distinct lines.

- `dotnet build` shows only 139 of the 356. `csc` stops after declaration errors and hides every error inside method bodies, so the census compiles through Roslyn's API as well.
- 65 of the 356 are `using` lines. 12 files have nothing else wrong.
- The other 291 fall under 10 seams (§3). Four of them already exist upstream and only need widening.
- No error comes from NuGet. The engine has no package references: every library is a local DLL in `3rdParty/` or vendored source.

**Five findings the spec didn't have:**

1. **The four existing seam interfaces live in the same files as their WPF implementations.** `IUiDispatcher`, `IUserMessages`, `IRegistryEx` and `IKeyboard` can't be compiled without WPF. Moving them into their own files removes 101 errors in 34 files.
2. **`BuzzGUI.Common` needs the same treatment as `BuzzGUI.Interfaces`.** The engine can't link its files: `Global` is static state that the engine and the machines must share (74 engine references). All 19 prebuilt managed machines reference `BuzzGUI.Common`.
3. **The engine can stay one `net10.0` binary on both OSes.** Give the 7 WPF-typed interface members default bodies in the Windows build of `BuzzGUI.Interfaces`. `dim_check.py` shows this loads, and that without it the engine type fails to load (§4.3).
4. **`IUiDispatcher` itself has a WPF type in its signature**: `BeginInvoke(Action, DispatcherPriority)`.
5. **Engine code lives in UI places.** `ReBuzz/Common/` holds `Utils`, `BurstProtectionEngine`, `MultimediaTimer` and `ChildProcessTracker`. The r8brain resampler interop sits in `BuzzGUI.WaveformControl`, and `ReBuzzCoreInitialization` in `ReBuzz/AppViews`.

**Binary compatibility: yes.** `BuzzGUI.Interfaces` can be made portable without breaking existing machine DLLs on Windows, by multi-targeting one assembly identity (§4). Measured on the 19 prebuilt managed machines:

- None implements `IBuzz`, `IMachine` or `IMachineDLL`. Only the engine does.
- 5 call a WPF-typed member: `IMachine.ParameterWindow` (3) and `IBuzz.ThemeColors` (2). None uses the five `IMachineDLL` skin members.

**Plan:** 10 PRs (§5). The first three are mechanical and carry most of the error count: seam interfaces in their own files, plus a census ratchet in Linux CI; engine helpers out of UI files, plus dead `using` lines; `BuzzGUI.Interfaces` and `BuzzGUI.Common` multi-targeted. This fits the spec's 4–6 pw for P1.

## 2. Census

### 2.1 Method

- **`EngineCensus.csproj`** compiles the engine sources as `net10.0` by linking them where they are. Settings: `EnableDefaultCompileItems=false`, no `Nullable`, no `ImplicitUsings`, `AllowUnsafeBlocks`, no packages. It references `ReBuzz.Agent`, the agent server's portable core. `-p:CensusStage=N` picks one of six nested file sets (§2.2). It is expected to fail: the errors are the census.
- **Engine folders:** `Core`, `Audio`, `MachineManagement`, `FileOps`, `ManagedMachine`, `NativeMachine`, `Midi` and `Agent`. `NativeMachine` is included because cross-platform.md §2.1 counts it as engine, and the engine calls it directly.
- **`census.py`** runs each stage three ways and de-duplicates:
  - `dotnet build`: what `csc` shows;
  - the same file list through Roslyn's `Compilation.GetDiagnostics()`: all errors, including method bodies;
  - the SDK's own CA1416 analyzer, for platforms Linux, macOS and Windows: calls that compile but are Windows-only at run time.

  It also counts `DllImport` declarations by library.
- **Classification.** Each error gets one cause from the symbol it names. Missing namespaces and types are resolved against a namespace-aware index of every type declared in the repo:
  - a WPF type;
  - a type in a UI file or another project;
  - a library;
  - Win32, registry or GDI+;
  - WinForms;
  - cascade.
- **Closure.** Stage 4 keeps only the `BuzzGUI.Common` files the engine needs. It compiles without `BuzzGUI.Common` and adds back each file that declares a missing type, until nothing changes: 7 passes. The result was `CommonClosure.props`; since PR 3 it is `ReBuzzGUI/BuzzGUI.Common/PortableFiles.props`, the file set of the library's portable build.
- **Limit: counts are lower bounds.** Roslyn suppresses errors in expressions that already involve a missing type. Example: the second `Keyboard.Modifiers` in `MachineCore.cs` (line 824) sits inside a lambda on the missing `MachineGUIHostWindow`, and appears only once that window is fixed. Expect a few new errors in each PR.

### 2.2 Iterations

| Stage | Change | Files | LOC | csc errors | All errors | of which `using` | Files with errors | CA1416 sites | P/Invoke decls |
|---|---|---|---|---|---|---|---|---|---|
| 0 | Raw: every `.cs` in the 8 engine folders, `BuzzGUI.Interfaces`, `BuzzGUI.Common` (non-XAML) | 422 | 109,829 | 807 | 2,288 | 162 | 129 | 58 | 129 |
| 1 | + the repo's portable binaries: `NAudio.Core`, `NAudio.Midi`, SharpZipLib | 422 | 109,829 | 786 | 2,249 | 148 | 126 | 58 | 129 |
| 2 | − 70 UI files: `*.xaml.cs`, WPF implementations of existing seams, `NativeEditorHost`, the studio and headless app hosts, `BuzzGUI.Common` controls, behaviours and window chrome | 352 | 92,588 | 268 | 621 | 85 | 76 | 54 | 28 |
| 3 | − 7 Windows platform files: ASIO and WASAPI drivers, WinMM and Windows MIDI Services devices, the registry implementation | 345 | 91,470 | 267 | 590 | 73 | 77 | 44 | 28 |
| 4 | `BuzzGUI.Common` reduced to its closure: 47 of 174 files | 218 | 54,136 | 241 | 457 | 78 | 65 | 44 | 21 |
| 5 | Mechanical moves simulated: seam interfaces on their own (`sim/SeamShims.cs`); `ReBuzz/Common` helpers, r8brain interop and `ReBuzzCoreInitialization` added | 228 | 55,797 | 139 | 356 | 65 | 48 | 46 | 38 |

- **Stage 1.** The repo's own binaries remove all 19 SharpZipLib errors and 20 of 74 NAudio errors. The other 54 NAudio errors are WinMM, WASAPI and ASIO APIs: Windows-only, whatever the package.
- **Stage 2** removes 72 % of the errors (2,249 → 621).
- **Stage 3** removes only 31 errors. `AudioDriverFactory` and `MidiEngine` create the Windows drivers directly, so they now miss them: seam S8.
- **Stage 4** halves the LOC (91k → 54k). The engine uses 47 of `BuzzGUI.Common`'s 174 files: actions, `Global`, two extension files, presets, settings, LZMA, libsndfile, `TrackerNotes`, `VUMeter` and helpers.
- **Stage 5** removes 153 errors and adds 52.
  - The four seam interfaces on their own remove 101 (§1).
  - The added helpers remove 52 "type outside the engine set" errors and bring 52 of their own: 47 in `Utils.cs`, 4 in `ReBuzzCoreInitialization`, 1 in `ChildProcessTracker`.

### 2.3 Remaining errors by cause (stage 5)

| Cause | Errors | of which `using` | Distinct symbols | Files |
|---|---|---|---|---|
| WPF types | 223 | 38 | 54 | 35 |
| Types declared in UI files or other projects | 83 | 25 | 32 | 27 |
| NAudio Windows APIs (`MidiIn`, `MidiOut`, `AsioOut`, `AsioDevice`) | 20 | 0 | 4 | 4 |
| WinRT (`DeviceWatcher`, `DeviceInformation`) | 14 | 1 | 5 | 1 |
| Serilog (portable, vendored; the census doesn't reference it) | 9 | 1 | 2 | 3 |
| System.Drawing.Common (GDI+, Windows-only since .NET 7) | 5 | 0 | 4 | 1 |
| WinForms | 1 | 0 | 1 | 1 |
| Cascade | 1 | 0 | 1 | 1 |
| **Total** | **356** | **65** | | **48** |

**Not compile blockers, but Windows-only at run time (P2):**

- **CA1416: 46 sites in 4 files.**
  - Registry, 44 sites: `RegistryEx.cs`, `Settings/Settings.cs` and `RegistryMonitor.cs` in `BuzzGUI.Common`.
  - `Process.ProcessorAffinity`, 2 sites in `Utils.cs`.
- **P/Invoke: 38 declarations.**

  | Library | Declarations | Files |
  |---|---|---|
  | libsndfile | 11 | 1 |
  | kernel32 | 9 | `DedicatedThreadPool`, `ChildProcessTracker`, `MidiClockOutput` |
  | r8brain | 8 | 2 |
  | user32 | 3 | `Utils` |
  | advapi32 | 3 | `RegistryMonitor` |
  | winmm | 2 | `MultimediaTimer` |
  | shcore | 1 | `Utils` |
  | gdi32 | 1 | `UIMessage` |

### 2.4 Remaining errors by file (stage 5, top 15)

| File | Errors | Seams |
|---|---|---|
| `ReBuzz/Common/Utils.cs` | 47 | split the file (§5, PR 2) |
| `Midi/MidiEngine.cs` | 38 | S8 |
| `Core/ReBuzzCore.cs` | 37 | S1, S2, S3, S4, S6, S9, S10 |
| `Core/MachineCore.cs` | 34 | S3 (22), S2, S4, S5, S7 |
| `NativeMachine/UIMessage.cs` | 19 | S4 (skin bitmaps through GDI+) |
| `BuzzGUI.Common/InterfaceExtensions/MachineExtensions.cs` | 17 | `#if WINDOWS` (§4) |
| `FileOps/BMXMLFile.cs` | 13 | S1, S3, S5 |
| `MachineManagement/MachineManager.cs` | 13 | S1, S3, S4 |
| `Core/MachineDLL.cs` | 11 | S1, S4, S7 |
| `Audio/Engine/AudioEngine.cs` | 10 | S6, S8 |
| `Core/Session/SessionControllers.cs` | 10 | S7, S8 |
| `FileOps/BMXFile.cs` | 10 | S1, S3, S5 |
| `Agent/NonModal.cs` | 9 | S1 |
| `ManagedMachine/ManagedMachineHost.cs` | 6 | S3 |
| `BuzzGUI.Interfaces/IMachineDLL.cs` | 6 | §4 |

### 2.5 What the machine API leaks

`BuzzGUI.Interfaces` has 9 errors in 3 files. Two are `using` lines and seven are members:

| Member | Type | Prebuilt machines that use it (of 19) |
|---|---|---|
| `IMachineDLL.Skin`, `SkinLED` | `ImageSource` | 0 |
| `IMachineDLL.SkinLEDSize` | `Size` | 0 |
| `IMachineDLL.SkinLEDPosition` | `Point` | 0 |
| `IMachineDLL.TextColor` | `Color` | 0 |
| `IMachine.ParameterWindow` | `Window` | 3: DAWControl, GuitarChordBox, Jeskola Note Matrix |
| `IBuzz.ThemeColors` | `IIndex<string, Color>` | 2: Info, Modern Sequence Editor |
| `Global.MachineViewHwndSource` (in `BuzzGUI.Common`) | `HwndSource` | 1: GuitarChordBox |

`IBuzz` also has UI-flavoured members with portable types: `MachineViewHWND`, `IsPianoKeyboardVisible`, `ActivatePatternEditor()` and others. They compile. On a Mac the engine implements them as no-ops or events.

This confirms cross-platform.md §2.5 for the leaks it listed, with the same counts: `MessageBox` on 25 lines in 13 files, `Keyboard.Modifiers` on 4 lines in 3 files. It adds what §2.5 lacked: dispatcher and timers, audio and MIDI backends, themes, view state, and the seam interfaces themselves.

## 3. Seams

Each seam is an interface in the engine (or a portable type), implemented by the WPF app on Windows and by a headless or Mac implementation later. Errors are at stage 5, `using` lines included.

| Seam | Interface or change | Plugs in at | Errors | Files |
|---|---|---|---|---|
| S1 Messages | `IUserMessages` (exists) | `ReBuzzCore` constructor (already injected) | 41 | 12 |
| S2 Dispatcher and timers | `IUiDispatcher` (exists) + `IUiTimer` | `ReBuzzCore` constructor (already injected) | 27 | 8 |
| S3 Machine windows | `IMachineUiHost` (new) | `ReBuzzCore.UiHost`, set by `MainWindow`; none when headless | 58 | 12 |
| S4 Colours and skins | ARGB `uint`, `RawImage` | `MachineDLL`, `UIMessage`, `MachineManager` | 50 | 9 |
| S5 Keyboard | `IKeyboard` (exists) + `KeyModifiers` | already injected; two classes still read WPF directly | 8 | 3 |
| S6 Themes and XAML | out of the engine; driver config UI behind S8 | UI theme service | 30 | 4 |
| S7 View state read by the engine | settings and song data, not UI statics | `EngineSettings`, song model | 10 | 5 |
| S8 Audio and MIDI backends | `IAudioDriverFactory`, `IMidiBackend` (planned in P0) | `ReBuzzCoreInitialization` | 51 | 5 |
| S9 File dialogs | `IFileNameChoice` (exists) | `ReBuzzCore` (already injected) | 4 | 2 |
| S10 Build info and logging | embedded resource; Serilog reference | engine project | 11 | 3 |
| S0 `using` lines | delete, or they go with their seam | – | 65 | 33 |

**S1 `IUserMessages`** (`ReBuzz/Core/UserMessagesViaMessageBox.cs`). Today it has one member, `Error(message, caption, exception)`.

- Add `Info(message, caption)` and `UserAnswer Ask(message, caption, UserButtons buttons)`. The enums are portable: `UserButtons { Ok, OkCancel, YesNo, YesNoCancel }` and `UserAnswer { Ok, Cancel, Yes, No }`.
- `NonModal.TryCapture` switches to these enums; it has 9 of the errors.
- Callers: `MachineDLLScanner`, `MachineDatabase`, `MachineDLL`, `MachineManager`, `BMXFile`, `BMXMLFile`, `AudioDriverService` and `ReBuzzCore`. They reach the instance through `ReBuzzCore`.
- `BespokeFusion.MessageBoxWindow` (in `ReBuzz/Common`) becomes the WPF implementation's business.

**S2 `IUiDispatcher`** (`ReBuzz/Core/WindowsGuiDispatcher.cs`).

- `BeginInvoke(Action, UiPriority)` replaces `DispatcherPriority`. `UiPriority` uses the same names and values. Callers: 3 in `ParameterCore`, `SessionLayout` and `SessionLayout.Audio`.
- Add `IUiTimer CreateTimer(TimeSpan interval, Action tick)` as a default interface method: `System.Threading.Timer` plus `BeginInvoke`. Test fakes such as `FakeDispatcher` then compile unchanged, as they already do for `CheckAccess()`.
- The WPF implementation wraps `DispatcherTimer`. It replaces 6 timers: `ReBuzzCore` (engine thread, MIDI activity, VU meter), `ParameterCore`, `MachineCore`, and `SessionLayout.Takes` (which also reads `Dispatcher.FromThread`).
- `SimpleCommand` uses WPF's `CommandManager`. It lives in `BuzzGUI.Common`, so it goes under `#if WINDOWS` (§4).

**S3 `IMachineUiHost`** (new, in the engine; the WPF app implements it):

```csharp
public interface IMachineUiHost
{
    void OpenParameterWindow(IMachine machine, WindowPlacement? at = null);
    void OpenGui(IMachine machine, WindowPlacement? at = null);
    void CloseWindows(IMachine machine);
    MachineWindowState GetWindowState(IMachine machine);  // placement and visibility, saved in BMX/BMXML
    string AskName(string title, string current);         // rename machine or group
    object CreatePatternEditorControl(IMachine editor);   // a UserControl on WPF
}
```

- **`MachineCore`, 22 errors.** The parameter window, the windowed GUI with `Viewbox`, `SizeToContent` and `WindowInteropHelper`, and `RenameMachineWindow` all move into the WPF host.
- **`BMXFile` and `BMXMLFile`, 4 each.** They read `Visibility` and build `Rect` to save and restore open windows; they go through `GetWindowState`.
- **`ManagedMachineHost`, 4.** The reflected `PatternEditorControl()` delegate returns `object`. The WinForms wrapper (`NativeMachineFrameworkUI.WinFormsControl`) moves to the UI.
- **`ReBuzzCore`, 3.** The `SetPatternEditorControl` event becomes `Action<object>`.
- **`ReBuzzCoreInitialization`, 2.** It takes the host and stops creating `Global.MachineViewHwndSource`.
- **`MachineGroupCore`, 2.**
- Headless mode, `ReBuzzTests` and the Mac pass no host.

**S4 Colours and skins.**

- Colours become ARGB `uint`; images become `RawImage { int Width, Height; byte[] Bgra32 }`.
- `MachineDLL` keeps portable data: `SkinImage`, `SkinLedImage`, `SkinLedX`, `SkinLedY`, `TextColorArgb`.
- `UIMessage.UIGetResources` returns raw BGRA bytes. That drops `System.Drawing.Bitmap` and `Imaging.CreateBitmapSourceFromHBitmap`, the only GDI+ use in the engine. The UI builds a `BitmapSource` with `BitmapSource.Create`.
- `ReBuzzCore.ThemeColors` moves to the UI's theme service. The engine keeps `GetThemeColour(name)`, which returns the portable `System.Drawing.Color` from an ARGB table.
- The WPF members of `IBuzz` and `IMachineDLL` are covered in §4.

**S5 `IKeyboard`** (`ReBuzz/MachineManagement/WindowsKeyboard.cs`).

- Add `[Flags] enum KeyModifiers { None = 0, Alt = 1, Control = 2, Shift = 4, Windows = 8 }`, with the same values as WPF's `ModifierKeys`, so the WPF implementation just casts.
- `MachineCore` (2 sites) and `BMXMLFile` (1) read the static `Keyboard.Modifiers`. They switch to the injected `IKeyboard`, as `BMXFile` already does.

**S6 Themes and XAML.** The engine stops loading XAML.

- `Utils`' theme loading (14 errors) moves to the UI half of `Utils` (PR 2).
- `AudioEngine` opens the ASIO and WASAPI config windows and loads their `ResourceDictionary` (6 errors). The dialog becomes the driver's job, behind S8.
- `ReBuzzCore` sets `RenderOptions.ProcessRenderMode` from a setting (3 sites). The UI watches the setting instead.
- The font lists in `BuzzGUI.Common/Settings/Settings.cs` go under `#if WINDOWS`.

**S7 View state the engine reads from UI statics.**

- `MachineView.StaticSettings.ShowEngineThreads` and `EnableSkins` (`MachineCore`, `MachineDLL`) move into `EngineSettings`.
- `SequenceEditor.ViewSettings.PatternAssociations` holds pattern colours (`DeleteMachinesAction` ×3, `SessionActions` ×3). It becomes song data the sequence editor reads.
- `SessionPalette.ColorChoices` (`SessionControllers`) becomes an ARGB table in the engine.

**S8 Audio and MIDI backends.**

- **`IAudioDriverFactory`:** device names, `Create(...)` and `ShowConfig(...)`. It replaces the direct `new AsioAudioDriver` and `new WasapiAudioDriver` in `AudioDriverFactory`, the ASIO enumeration in `AudioDriverService`, and the `AsioDevice` casts in `AudioEngine`.
- **`IMidiBackend`** (cross-platform.md §3.2, planned in P0, not yet in the code): device lists, open input and output, hot-plug events.
  - `MidiEngine` has 38 errors: `MidiInDevice` and `MidiOutDevice` on NAudio WinMM, `Midi2` on Windows MIDI Services, `DeviceWatcher` from WinRT.
  - `SessionControllers` has 8: control-surface ports opened with NAudio `MidiIn` and `MidiOut`.
- The Windows implementations stay in `ReBuzz.exe`. No new assembly is needed: the app is Windows-only anyway, and `--agent` is the same exe.

**S9 `IFileNameChoice`.**

- `ReBuzzCore` has one `OpenFileDialog` (line 1545); it uses the injected `fileNameToLoadChoice` instead.
- `BuzzGUI.Common/WaveCommandHelpers.cs` has a `SaveFileDialog`; it goes under `#if WINDOWS`.

**S10 Build info and logging.**

- `Properties.Resources.BuildNumber` and `BuildDate` become `BuildNumber.txt` and `BuildDate.txt`, embedded in the engine and read by a small `BuildInfo` class.
- Serilog: the engine references `3rdParty/serilog`. Its project targets `net9.0`; add `net10.0`, so Linux CI needs no second targeting pack.

## 4. `BuzzGUI.Interfaces` and `BuzzGUI.Common`: one identity, two builds

### 4.1 Measured facts

`census.py machines` reads the metadata of every managed DLL in `RunTime/Gear` without loading them.

- **19 prebuilt managed machines:**
  - 7 target .NET Framework 4.0 (Client), 11 target .NET 8, 1 targets .NET 9;
  - all 19 reference both `BuzzGUI.Interfaces` and `BuzzGUI.Common`: the 7 older ones by version 0.0.0.0, the other 12 by 1.0.0.0;
  - neither assembly is strong-named.
- **What they implement:** `IMachineGUIFactory` (17 types), `IMachineGUI` (17), `IBuzzMachine` (14), `IAction` (3), `IEditContext` (1). All five are WPF-free. None implements `IBuzz`, `IMachine` or `IMachineDLL`.
- **What they derive from:**
  - 31 types from WPF's `UserControl` and 6 from `Window`. They don't load on a Mac, as cross-platform.md §2.3 says.
  - 3 from `BuzzGUI.Common.Settings.Settings`: Info, Jeskola Loop Recorder, Modern Sequence Editor. That class persists to the registry (CA1416, P2).
- **`BuzzGUI.Common` types outside the portable subset:** 11 machines use them. Modern Sequence Editor alone uses 33.
  - Some are UI types: `NumericUpDown`, `Dragger`, `MachineGUIUpdateTimer`, `WPFExtensions`.
  - Some are portable but unused by the engine: `CountSet`, `PatternColumnExtensions`, the sequence actions.
  - Windows keeps shipping all of them. Grow the portable subset when a Mac machine needs one (P2/P3).
- **`Global` is shared:** the engine reads or writes `Global.*` 74 times, and machines read `Global.Buzz`. There must be exactly one `BuzzGUI.Common.Global` type at run time.

### 4.2 Options

| | **A. Multi-target, one identity (recommended)** | B. Type forwarding | C. Link the files into the engine |
|---|---|---|---|
| How | `TargetFrameworks` `net10.0-windows10.0.26100.0;net10.0`. Same name, `AssemblyVersion` 1.0.0.0, unsigned. `#if WINDOWS` (defined by the SDK for `-windows` TFMs) guards the WPF parts. Windows ships the Windows build; Mac and Linux ship the portable one | A new portable assembly holds the WPF-free types; `BuzzGUI.Interfaces` keeps `[TypeForwardedTo]` for them | `ReBuzz.Engine` compiles the `.cs` files of both libraries itself |
| Machine DLLs on Windows | Load against the same assembly as today | Break: a type can't be split. `IBuzz`, `IMachine` and `IMachineDLL` either drop their WPF members (`MissingMethodException` in 5 machines) or stay behind, and then the engine can't use them | Break: two `BuzzGUI.Common.Global` types. The engine sets one `Global.Buzz` and machines read the other, which is null |
| Verdict | **Yes** | No | No for `BuzzGUI.Common`. For `BuzzGUI.Interfaces` too, because machines would get a second `IMachine` |

### 4.3 How a `net10.0` engine satisfies the Windows interfaces

On Windows the app loads the Windows build of `BuzzGUI.Interfaces`, where `IMachine` has `ParameterWindow`. An engine compiled against the portable build doesn't implement it, so `MachineCore` fails to load. There are two ways out:

- **E1. Multi-target the engine too,** with `#if WINDOWS` partial classes that implement the 7 members. That means two engine binaries, a Windows half that Linux CI never compiles, and `#if` blocks in upstream files.
- **E2 (recommended). Default bodies in the Windows build only.** In the Windows build of `BuzzGUI.Interfaces`, the 7 members keep their name and signature and gain a body that asks a hook the WPF app sets:

  ```csharp
  #if WINDOWS
      System.Windows.Window ParameterWindow => WpfMachineApi.ParameterWindow?.Invoke(this);
  #endif
  ```

  The engine is then one `net10.0` binary on both OSes.

**Proof: `dim_check.py`** builds six small projects:

- a machine compiled against today's interface, where the member is abstract;
- an engine compiled against a portable interface without the member;
- two hosts.

| Host ships | Result |
|---|---|
| The proposed Windows build (default body) | `OK: MachineCore / window of MachineCore`. The machine's call reaches the hook |
| Today's Windows build (abstract member) | `TypeLoadException: Method 'get_ParameterWindow' in type 'MachineCore' … does not have an implementation` |

**Rules for both libraries:**

- `#if WINDOWS` may add types, members and default bodies. It never changes a signature, a base type or an interface list, so the portable build is a strict subset of the Windows build.
- A CI step checks this: it compares the public surface of the two builds with the census tool's metadata reader.
- `WpfMachineApi` hooks are set before the first machine loads. An unset hook returns null; `ParameterWindow` is already documented as null "if the window hasn't been opened".

### 4.4 The portable subsets

- **`BuzzGUI.Interfaces`, 46 files.** Put the 7 members and 2 `using` lines under `#if WINDOWS`, with default bodies for the members.
- **`BuzzGUI.Common`, 47 of 174 files** (72 after PR 3: `ReBuzzGUI/BuzzGUI.Common/PortableFiles.props`). 31 errors in 7 files go under `#if WINDOWS`:
  - `MachineExtensions` (17): preset error message boxes, theme colour, the parameter value popup;
  - `Settings` (5): font lists;
  - `Preset` (3): message boxes;
  - `SimpleCommand` (2): `CommandManager`;
  - `WaveCommandHelpers` (2): save dialog;
  - `Global` (1): `MachineViewHwndSource`;
  - `RegistryEx` (1): a stray `using`.
- **The `net10.0` build drops** the 7zip project reference (an old-style .NET Framework project; the subset doesn't need it). It conditions the upstream `RemoveEmptyObj` target (a `cmd` command) on Windows.

## 5. Plan: PR-sized steps

Each PR builds and passes tests on its own:

- **Windows:** `core-build` in `fork-ci.yml`, plus ReBuzzTests in `fork-package.yml` (run by hand on the PR branch).
- **Linux:** the census ratchet from PR 1 on. Errors may only go down, and each PR states its new baseline.

Error counts are stage-5 census numbers. Expect small upward corrections from hidden cascades (§2.1).

| # | PR | Main files | Errors removed | Exit check |
|---|---|---|---|---|
| 1 | **Seam interfaces stand alone; census ratchet.** Move `IUiDispatcher`, `IUserMessages`, `IRegistryEx` with `IRegistryKey`, and `IKeyboard` into their own files (same namespaces). `UiPriority` replaces `DispatcherPriority`; add `KeyModifiers`. Delete `sim/SeamShims.cs`. The Linux CI job runs `census.py --check` against `baseline.json` | 4 interface files out of `WindowsGuiDispatcher`, `UserMessagesViaMessageBox`, `WindowsRegistry`, `WindowsRegistryKey`, `WindowsKeyboard`; 3 priority callers; `FakeDispatcher` | 101 (at stage 4) | Windows build and ReBuzzTests green; census ≤ 356 |
| 2 | **Engine helpers out of UI files; dead `using` lines.** Split `ReBuzz/Common/Utils.cs` into portable `Utils.cs` and WPF `UiUtils.cs`. The engine links `BurstProtectionEngine`, `MultimediaTimer`, `ChildProcessTracker`, `IMachineExtension`, `ReBuzzCoreInitialization` and the r8brain interop where they are | `Utils.cs` and its callers; 11 files with only `using` errors (done: 56 removed, see Progress) | 68 | census ≤ 288 (done: 295) |
| 3 | **Multi-target `BuzzGUI.Interfaces` and `BuzzGUI.Common`** (§4): portable subset, default bodies plus `WpfMachineApi` hooks set by `App`, the surface check. Add `ReBuzz.Engine.csproj` (`net10.0`, linking the engine set); it is not yet referenced, and the ratchet reads its file list | 2 csproj files, 10 files with `#if`, `App.xaml.cs` | 39 | both libraries build `net10.0` on Linux; surface check passes; the 19 prebuilt machines load on Windows (the 5 in §2.5 opened by hand); census ≤ 249 (done: 258, see Progress) |
| 4 | **S1 messages** | `NonModal`, `MachineDLLScanner`, `MachineManager`, `ReBuzzCore`, `BMXFile`, `BMXMLFile`, 4 more | 31 | census ≤ 218; dialogs unchanged on Windows; `FakeUserMessages` captures the new calls |
| 5 | **S2 timers, S5 keyboard, S9 dialogs, S10 build info and Serilog** | `ReBuzzCore`, `ParameterCore`, `MachineCore`, `SessionLayout*`, `BMX*`, `NativeMachineHost`, `ChildProcessTracker` | 45 | census ≤ 173 |
| 6 | **S4 colours and skins** | `UIMessage`, `MachineDLL`, `MachineManager`, `ReBuzzCore`, `MachineCore` | 35 | census ≤ 138; native skins, LEDs and machine text colours unchanged in the machine view |
| 7 | **S3 `IMachineUiHost`** | `MachineCore`, `ManagedMachineHost`, `BMX*`, `MachineManager`, `MachineGroupCore`, `ReBuzzCore`, `ReBuzzCoreInitialization` | 56 | census ≤ 82; open, move and close parameter and GUI windows, then save and reload a song: windows come back |
| 8 | **S6 themes, S7 view state** | `AudioEngine`, `ReBuzzCore`, `MachineCore`, `MachineDLL`, `DeleteMachinesAction`, `SessionActions`, `SessionControllers` | 27 | census ≤ 55; theme switch and pattern colours unchanged |
| 9 | **S8 `IAudioDriverFactory`, `IMidiBackend`** | `MidiEngine`, `SessionControllers`, `AudioDriverFactory`, `AudioDriverService`, `AudioEngine` | 55 | census = 0; ASIO, WASAPI, MIDI in and out, and hot-plug unchanged |
| 10 | **Flip.** `ReBuzz.csproj` stops compiling the engine files (`Compile Remove`) and references `ReBuzz.Engine`, which gets `InternalsVisibleTo` for `ReBuzz` and `ReBuzzTests`. The `ReBuzz/Agent` glue moves onto the engine and `ReBuzz.Agent` references only the engine. Linux CI builds `ReBuzz.Engine`; the ratchet retires | `ReBuzz.csproj`, `ReBuzz.Engine.csproj`, `ReBuzz.sln`, `fork-ci.yml` | – | cross-platform.md P1 exit: the engine builds on Linux CI; ReBuzzTests pass on Windows against it; `ReBuzz.exe --agent` runs headless |

**Progress.** The ratchet is `census.py --check` in the `session-tests` job: Roslyn errors at stage 5 against `experiments/engine-census/baseline.json`. More errors fail the job and list the files that gained them; fewer pass with a warning to lower the baseline (`--check --update`).

| PR | Census baseline (stage 5) | Notes |
|---|---|---|
| 1 (#27) | 351 | Main had moved since the census: the A1 agent tools (`PatternTools`, `MachineTools`, `StudioTools`) use 10 `BuzzGUI.Common` action files outside the closure, so stage 5 with the shims stood at 369, not 356. `census.py closure` now gives 57 files (was 47), which removes those 18 errors. PR 1 itself removed 104 at stage 4 (plan: 101) and 5 at stage 5 (the priority callers). `IKeyboard` keeps `HasModifierKeyPressed(Enum)`; `WindowsKeyboard` also accepts `KeyModifiers`, so PR 5 only switches callers |
| 2 (#28) | 295 | **Census fix:** `InterfacesImplicitUsings.cs` made `System.Collections.Generic` global for every file, so `SequenceCore`'s `OrderedDictionary<,>` (Wintellect's on Windows) bound to .NET 9's and hid 7 errors; its `using Wintellect.PowerCollections` looked dead and isn't. The Roslyn pass now gives the SDK's implicit usings to the `BuzzGUI.Interfaces` files only, and the closure grows to 71 files (PowerCollections). **Row deviation:** the engine calls WPF members of `Utils` at 11 counted sites: `MessageBox` ×7 (one inside `Utils.SetProcessorAffinityMask`), `GetUserControlXAML` ×3, `GetThemeColors` ×1. The split shows them as `UiUtils` errors; they go with S1, S3, S4 and S6. Removed: `Utils` 47 → 1, `using` lines 20 (11 files, plus `SequenceCore`'s, which now resolves); added: 10 call sites in other files |
| 3 (#29) | 258 | **Ratchet:** `census.py --check` now compiles `ReBuzz.Engine.csproj`'s 144 files against the real portable builds, which it builds first; the two libraries no longer count. Removed: the 40 library errors and `MachineCore`'s missing `ParameterWindow`; 3 hidden engine errors appear (`MachineDLL` reads `IBuzz.ThemeColors` ×2, S4; `ReBuzzCoreInitialization` sets `Global.MachineViewHwndSource`, S3). **Builds:** off Windows the libraries target only `net10.0` (no Windows targeting pack); the `cmd` build steps run on Windows only; `BuzzGUI.Common`'s portable build compiles `PortableFiles.props` (72 files: the closure plus `PortableMessageBox`), written by `census.py closure`. **`#if WINDOWS`:** whole members where only UI uses them (`GetThemeColor`, the attribute menu, `SaveToFile`, `MachineViewHwndSource`, the font lists); message boxes in `MachineExtensions` and `Preset` go through an internal `PortableMessageBox` (stderr off Windows); `SimpleCommand.CanExecuteChanged` has empty accessors off Windows. **Hooks:** `App` sets `WpfMachineApi` before anything loads; they forward to the engine classes, which still implement the 7 members, so the default bodies are unused until S3/S4. A test asserts both. **Machines:** the surface check runs in the Windows `core-build` job (Linux can't build the Windows TFM). Instead of opening the 5 machines by hand, `PrebuiltMachineBindingTests` resolves every type and member reference of the 19 prebuilt machines into the two libraries. It found a break that predates P1: upstream changed `ISong.SequenceAdded/Removed/Changed` to `Action<int, ISequence>`, so Jeskola Live and Yitten Mongoose can't subscribe (listed as known breaks) |
| 4 (#31) | 220 | Removed 38 (plan: 31; PR 2 moved 7 `UiUtils.MessageBox` sites here). `IUserMessages` gains `Info`, `Ask` and **`KeepWaiting`** (not in §3): the two "Wait more?" prompts in `BMXFile`/`BMXMLFile` used the themed `MessageBoxWindow`, which the WPF implementation keeps; the default body asks a Yes/No question. `UserButtons`/`UserAnswer` have WPF's values, so `UiUtils.MessageBox` casts. Callers reach the instance through `ReBuzzCore.UserMessages`; `Utils.SetProcessorAffinityMask` and `MachineDLLScanner.Save` take it as a parameter, `AudioDeviceService` in its constructor. **Windows:** every message box shows as `UiUtils.MessageBox` did (main window topmost while open, when on its thread); the 6 sites that called `MessageBox.Show` directly gain that. The "Wait more?" and "Safe load machine?" prompts and the direct `MessageBox.Show` errors are now captured during an agent call and in headless mode, like every other message. Tests: `UserMessagesTests` (save prompt through the engine, capture of all four methods, enum values) |
| 5 (#32) | 179 | Removed 41 (plan: 45). **S2:** `IUiDispatcher.CreateTimer` returns an `IUiTimer` (`Interval`, `IsEnabled`, `Start`, `Stop`); the default body is a thread-pool timer that queues ticks with `BeginInvoke`. `WindowsGuiDispatcher` wraps a `DispatcherTimer` made as the engine made them (calling thread, Background priority). 6 timers switched; `ParameterCore`'s describe timer keeps its handlers adding up (a private event). `SessionLayout.Takes` asks `dispatcher.CheckAccess()` instead of `Dispatcher.FromThread` (the same thread in the app; agent calls run on the UI dispatcher). `FakeDispatcher`'s timers tick only when a test fires them, as the WPF timers never ticked in tests. **S5:** `IKeyboard.Modifiers` (default body from `HasModifierKeyPressed`; WPF casts `Keyboard.Modifiers`), reached through `ReBuzzCore.Keyboard`. `MachineCore`'s second read (line 824, the windowed GUI's mouse wheel) stays for S3, which moves that code. **S9:** `ImportSong` asks the injected load-file choice (the same dialog and filter). **S10:** `BuildInfo` reads `BuildNumber.txt`/`BuildDate.txt`, embedded in `ReBuzz` and `ReBuzz.Engine` (`ReBuzz.exe` keeps its resx for the About box). The engine references `3rdParty/serilog`: off Windows it targets only `net10.0` (Windows builds `net9.0` as before) and its `cmd` steps run on Windows only; the Linux CI job checks out that submodule. Tests: `EngineSeamTests` |
| 6 (#36) | 142 | Removed 37 (plan: 35). **Theme colours:** the engine loads the theme as an ARGB table (`ThemeColours`, moved from `UiUtils.GetThemeColors`; its hex parser reads colours as WPF's `ColorConverter` does) and answers `IBuzz.GetThemeColour` from it. The WPF app builds `IBuzz.ThemeColors` (the same `UiUtils.ColIndex`) from the table, once per table. **Skins:** `UIMessage.UIGetResources` returns raw pixels (`RawImage`, BGR32 as GDI+'s `Format32bppRgb` read them) and the LED position; the GDI+ bitmap and the engine's last gdi32 P/Invoke are gone. `MachineDLL` keeps `SkinImage`, `SkinLedImage`, `SkinLedX`/`Y` and `TextColorArgb`; the hooks answer `IMachineDLL`'s WPF members with a frozen 96-dpi `Bgr32` `BitmapSource` made on first use. **Deviation, theme XAML:** the shipped themes bind `Buzz.ThemeColors[...]`, `Machine.DLL.SkinLED`, `SkinLEDSize.Width` and `SkinLEDPosition.X` on the engine objects by name, which a default interface body can't serve. So `ReBuzzCore.ThemeColors` (`object`, the app's index), `MachineDLL.Skin`/`SkinLED` (`object`, the app's image) and `SkinLEDSize`/`SkinLEDPosition` (portable structs with WPF's property names) stay. The test `Driver` installs the hooks, as `App` does. Tests: `WpfMachineApiTests` (default bodies answer, pixels, XAML property names, theme index, `.col` parsing against `ColorConverter`) |
| 7 (#38) | 83 | Removed 59 (plan: 56). `IMachineUiHost` (`ReBuzzCore.UiHost`): `OpenParameterWindow`, `OpenGui`, `CloseWindows`, `GetWindowState` (`MachineWindowState`/`WindowPlacement`, saved in BMX and BMXML), `ReleaseMachine` (a deleted machine's parameter window lets go), `AskName` (machine and group rename) and `AttachMachineView` (sets `Global.MachineViewHwndSource`). The parameter window, windowed GUI and rename code moved unchanged into `WpfMachineUiHost`; its windows live in a weak table keyed by machine, as long as the MachineCore fields did. `IMachine.ParameterWindow` comes from the host through the default body. **Deviations:** (1) the host is every core's default (`ReBuzzCore.DefaultUiHost`, set with the hooks by `App` and the test `Driver`), so studio, headless and tests behave as before; with no host a machine remembers which windows a song wants open, so a song saved without UI keeps them. (2) No `CreatePatternEditorControl`: the control comes from the managed machine; the engine passes it on as `object`. A WinForms control (machines built for .NET Framework) is wrapped by a UI hook, chosen by the method's return type instead of a failed delegate bind. Tests: `MachineUiHostTests` (BMX and BMXML round trip without a host, the app host is the default) |
| 8 (#41) | 62 | Removed 21 (plan: 27): the ASIO/WASAPI settings windows in `AudioEngine` (6) go with S8 in PR 9, behind `IAudioDriverFactory.ShowConfig`. **S6:** `ReBuzzCore` no longer sets WPF's `RenderOptions.ProcessRenderMode`; it calls `ReBuzzCore.SoftwareRendering` at the same moments (start-up when on, setting changed), and the app sets it. **S7, deviations:** rather than moving the settings, the engine reads them through seams the app answers, so nothing persisted changes. `MachineViewState.ShowEngineThreads`/`EnableSkins` read `MachineView.StaticSettings`; `PatternColours` wraps the sequence editor's `ViewSettings.PatternAssociations` (null while no editor is up, as before; set, add and remove keep their dictionary semantics); controller pads read `SessionColourChoices`, the Session View palette as ARGB, which `SessionPalette.ColorChoices` keeps current. Tests: `ViewStateTests` |
| 9 (#PR9) | 0 | Removed 62 (plan: 55; 6 more came from PR 8). **`IAudioDriverFactory`** (`ReBuzzCore.AudioDriverFactory`): ASIO support and driver names, `Create`, `ShowConfig` (the ASIO and WASAPI settings windows; true when the user accepted, and the engine reopens the device as before) and the ASIO preferred buffer size. `WindowsAudioDriverFactory` holds the code moved from `AudioDriverFactory` (deleted), `AudioDeviceService` and `AudioEngine`. **`IMidiBackend`** (`MidiEngine.Backend`): device counts and names by WinMM index, `OpenInput`/`OpenOutput` (`IMidiInput`/`IMidiOutput`, which `MidiInDevice`/`MidiOutDevice` implement), `OpenControllerPort` (the control-surface ports, `NAudioPort` moved from `SessionControllers`), hot-plug (`StartWatching` reports `MidiDeviceChange` with the device counts taken before the WinRT lookup, as before) and the MIDI 2 endpoint. `WindowsMidiBackend` holds the WinMM, Windows MIDI Services and `DeviceWatcher` code; `ReBuzz.Engine.csproj` leaves out both Windows files. **Deviation:** as for S3, the Windows backends are every core's defaults (`ReBuzzCore.DefaultAudioDriverFactory`, `DefaultMidiBackend`), set with the hooks by `App` and the test `Driver`, so studio, headless and tests are unchanged; with none there are no ASIO drivers (WASAPI is still listed) and no MIDI devices (`NoMidiBackend`). Tests: `BackendTests` (fake MIDI devices through the engine incl. hot-plug, controller port names, ASIO-then-WASAPI device list, device errors to the user). **Linux build:** `dotnet build ReBuzz.Engine/ReBuzz.Engine.csproj` needs no NuGet: restore succeeds with an empty package source and an empty package folder, once the vendored Serilog is not trimmable off Windows (`IsTrimmable` pulled in `Microsoft.NET.ILLink.Tasks`, which only some SDKs carry offline). It needs the `3rdParty/serilog/serilog` submodule checked out |
| 10 (#PR10) | – | **The flip.** `ReBuzz.Engine/EngineFiles.props` lists the engine's files once (`EngineSource`, including `ReBuzz/Platform` for P2): `ReBuzz.Engine.csproj` compiles them, `ReBuzz.csproj` imports the list and leaves them out, and references the engine. The engine has `InternalsVisibleTo` for `ReBuzz` and `ReBuzzTests`, is in `ReBuzz.sln`, does not copy the portable `BuzzGUI.Interfaces`/`BuzzGUI.Common` (the app ships the Windows builds, §4.3), and now stamps `BuildDate.txt` (it builds first; the step was `ReBuzz.csproj`'s). `ReBuzz/Agent` stays where it is: the engine compiles all of it except `StudioAgent.cs` and `HeadlessAgent.cs` (WPF), which stay in the app; the portable MCP core `ReBuzz.Agent` is referenced by the engine (not the reverse, as §5 row 10 had it). Linux CI builds `ReBuzz.Engine` instead of running the census ratchet (`census.py --check` still works by hand; baseline 0). `build.ps1` gives external machines `ReBuzz.Engine.dll` next to `ReBuzz.dll` |

**Order.**

- PRs 1–3 are mechanical and take the count from 457 to 249, with no behaviour change.
- PRs 4–9 don't depend on each other once PR 3 is in, so they can come in any order. Several touch `MachineCore` and `ReBuzzCore`, so land them one at a time. The "census ≤" targets assume the order above; in another order, check that each PR lowers the ratchet by its "errors removed".
- **Upstream.** Offer PRs 1, 2, 4 and 5 upstream: they continue upstream's own seam extraction (December 2024 – March 2025).

## 6. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| Hidden errors appear as blockers are fixed (Roslyn suppresses cascades, §2.1) | certain | a few extra lines per PR | Treat each count as a lower bound; the ratchet re-measures after every PR |
| Upstream merges conflict: 98 of upstream's 415 commits in the last 12 months touched at least one of the 48 files with errors. `ReBuzzCore.cs` alone had 48, `MachineCore.cs` 15, `MidiEngine.cs` 14 | high | painful merges | Small PRs; link files in place; offer the seam PRs upstream; land `ReBuzzCore` changes right after an upstream merge |
| The MCP work edits `ReBuzz/Agent/*` and `ReBuzzCore.cs` now; `NonModal.cs` carries 9 of the errors | high | the census baseline moves | Re-run the census after each A-track merge; sequence PR 4 (`NonModal`) after A1 |
| The portable build stops being a subset of the Windows build (a changed signature under `#if`) | medium | `MissingMethodException` or `TypeLoadException` in machines | The PR 3 surface check in CI; the "add, never change" rule (§4.3) |
| A `WpfMachineApi` hook is unset when a machine calls `ParameterWindow` or `ThemeColors` | low | null where a machine expects a value | `App` sets hooks before `ReBuzzCoreInitialization`; a test asserts it |
| Third-party managed machines outside `RunTime/Gear` implement `IBuzz`, `IMachine` or `IMachineDLL` | low | none on Windows (an explicit implementation beats a default body); extra members on Mac are harmless | – |
| `BuzzGUI.Common.Settings.Settings` persists to the registry, and 3 prebuilt machines derive from it | certain on Mac | settings not saved on Mac; P1 is unaffected | P2 routes `Settings` persistence through `ISettingsStore` without changing its public surface |
| The vendored Serilog targets `net9.0` | certain | Linux CI needs a second targeting pack | Add `net10.0` to its `TargetFrameworks` (PR 5) |
| Name confusion: `ReBuzz.Engine/` (new, managed) next to `ReBuzzEngine/` (the native MFC host) | medium | wrong folder in reviews and scripts | Keep the spec's name; say "native host" for `ReBuzzEngine` in docs and comments |

## 7. Reproduce

```sh
python3 experiments/engine-census/census.py              # stages 0-5 → experiments/engine-census/out/report.md
python3 experiments/engine-census/census.py closure      # regenerate ReBuzzGUI/BuzzGUI.Common/PortableFiles.props
python3 experiments/engine-census/census.py machines     # prebuilt machine scan → out/machines.md
python3 experiments/engine-census/dim_check.py           # §4.3 proof
python3 experiments/engine-census/census.py --check      # the ratchet against baseline.json (~15 s; CI used it until PR 10)
python3 experiments/engine-census/census.py --check --update   # lower the baseline after removing errors
python3 experiments/engine-census/census.py surface "-p:SolutionDir=$PWD\" -p:ReBuzzCleanBuild=true   # Windows: portable surface ⊆ Windows surface
dotnet build experiments/engine-census -p:CensusStage=5  # csc's view; fails by design
```

- **Requirements:** .NET 10 SDK only. Roslyn and the CA1416 analyzer come from the SDK, not NuGet.
- **Run time:** about 2 minutes for all six stages.
- **The report** (`out/report.md`, not committed) lists every symbol per cause with its files, and every file with its errors per cause and per seam.
