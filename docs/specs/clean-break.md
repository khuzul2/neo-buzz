# The clean break (CB1): what goes, what stays, in what order

| | |
|---|---|
| Status | Stage 1 (inventory), written before any deletion. Updated at each stage with what the removal found |
| Decision (binding) | The user, 2026-11-23, option C: NeoBuzz Live opens and saves only its own songs (`.nbl`) and runs only Neo machines and our internal ones. The user has no old songs to keep |
| Base | origin/main `cf910e79` (wave 57b, holds CU3) |
| Related | clips.md 10.0b (CU3 as built, its "left in, harmless, for CB1" list), legacy-machines.md (the families), machines.md, FORK.md |

## 1. What is removed, what is kept

**Removed.** Song formats `.bmx`, `.bmw`, `.bmxml` (open and save), Export for Buzz and the derived blob; the old-song corpus and its tests; native Buzz DLLs and the native machine host, the 32-bit host (`ReBuzzEngine32`, VST2 32-bit), Polac, the Pedal rebuilds, the prebuilt managed machines (Jeskola and others) with manifests, profile rows, catalog entries, presets, guides and gallery shots; the WPF Classic app and its Windows-only paths (the Modern Pattern Editor's own player under `#if WINDOWS`); the agent's tools for old machines and songs (`machine_replace` from a classic, placeholder handling, the migrating-songs guide) and their eval tasks.

**Kept.** Every Neo machine, Neo Plug and Neo Plug FX with the plugin host they use, plugin export, Audio In, Audio Out, MIDI Output, Groove, the internal blocks the engine needs, the tracker (MPE's view and editing on the clip store), the piano roll, `.nbl` v1 to v3, the Studio on Windows, macOS and Linux.

## 2. Findings that shape the order

1. **`.nbl` is built on the section codec of `BMXFile`.** `NblFile` wraps a `BMXFile` (`inner`) and stores its core sections (`bmx-sections-v1`, content-addressed) in the container; the Session, Arrangement, Notes and snapshot stores and `SongSnapshots` use `BMXFile.Assemble`, `ParseSubSections`, `BuildSubSections`, `Utf8Strings` and the `SectionType` enum. Songs saved by wave 57 must open and play the same, so the **codec stays**; what goes is the `.bmx`/`.bmw` file entry points, the Buzz-only sections' writers and the derived blob. The class is renamed `NblCoreSections` (stage 3), and the string `bmx-sections-v1` stays: it is a value inside saved `.nbl` files. Kept on purpose, listed in the report.
2. **The Studio does not reference the Classic app.** `ReBuzz.Studio` and `ReBuzz.Headless` reference `ReBuzz.Host`, which references `ReBuzz.Engine` (net10.0, compiles the engine files of `ReBuzz/` where they are, `ReBuzz.Engine/EngineFiles.props`). `ReBuzz/ReBuzz.csproj` (WinExe, WPF) is Classic; it imports the same props to leave the engine files out. So the engine sources stay in `ReBuzz/` (renaming the folder is out of scope), and what leaves is everything `EngineFiles.props` does not compile: `App.xaml*`, `AppViews/` (except `ReBuzzCoreInitialization.cs`), `Common/` WPF windows, `Properties/`, the Windows `*Dialog` and `Windows*` files.
3. **The Windows Studio uses the portable (net10.0) builds of `BuzzGUI.Interfaces` and `BuzzGUI.Common`.** The Windows TFM of both stays: the four kept Windows machines (Audio In/Out, MIDI Output, Groove) are `net10.0-windows` projects that compile against it. WPF libraries only Classic used go.
4. **Neo machines reach the Studio through `Machines/Portable` and `Machines/Studio`** (one tiny project each, compiling the shared `Machines/Neo*/` sources). The per-machine WPF project `Machines/Neo*/Neo*.csproj` is the Classic build; it leaves the solution, its sources stay (the portable and studio projects compile them).
5. **`#if WINDOWS` is the implicit symbol of the `net10.0-windows` TFM**: in the machines and `ModernPatternEditor.NET` it guards the WPF flavour, which only Classic builds. In `BuzzGUI.Interfaces/Common` it is the machine API's WPF-typed members, which the kept Windows machines bind to: **kept**.
6. **The package** (`build.ps1`, `fork-package.yml`) builds `ReBuzz.sln`, adds Classic's Gear (`RunTime\Gear`, 73 prebuilt machines), `ExternalMachines` (managed and native), the native hosts, and lays the Studio at the root with Classic in `Classic\`. After CB1 it is the Studio, its Neo Gear, the four Windows machines, plugin host, libsndfile, PortAudio and licences.

## 3. Inventory: what goes, what depends on it

Counts are tracked files in the base.

| # | Part | Size | Depends on it | Order |
|---|---|---|---|---|
| A | Repo-owned song files in old formats: none in `Other/Templates` (both `.nbl`); `.bmx`/`.bmxml` only inside `ExternalMachines/source/**` (20 demo songs of other people's machines) and `ReBuzz.Studio.Tests` fixtures built from them (`ClipCorpus.cs`, `ClipConverterTests`, `ClipRoundTripTests`) | 20 + tests | tests, `tools/song-roundtrip.py`, `clip-change-report.py` | 2 (check templates and presets), 3 |
| B | Old song formats: `BMXFile` (the load of `.bmx` and Buzz-only sections), `BMXMLFile`, `BuzzExport`, `BuiltInPattern` (PatternXP blob writer), `LoadedStateMigration` check, `ImportSongAction` (old-format import), Studio `BuzzExportFlow`, open dialog filters, file associations (`.bmx`), `GeneralPage`, `StudioKeymap`, agent `song_open/export` paths, `Headless` extension switch | ~6k lines | `ReBuzzCore.OpenSongFileCore`, `SaveSongCore`, Studio `SongFiles`, `MainWindow` | 3 |
| C | CU3 leftovers: `ClipDerived` (`Provider`, `EditRefusal`, `Refused`, `Native`/`Direct` switches), `ClipStore.Export`, `ClipBlob`, `ClipExport`, the `NBST` stamp, `ClipEditPolicy` and the `clipEdit` manifest flag (57 manifests, `tools/gen-machine-params.py`, `tools/source_manifests.py`, `PlugManifest`), `ClipWords`, `LostInputCells` (`PatternCore`, `ClipStore`), the `--audio` steps of the clip goldens in the three workflows, `tools/clip-change-report.py`, `tools/test_clip_change_report.py`, the ClipsOfOldSongs gallery test | ~1.5k lines | MPE (`MPEPatternColumn`), `ClipLanes.cs`, `NblFile`, `ClipOpen` | 3 |
| D | Classic: `ReBuzz/ReBuzz.csproj`, `App.xaml*`, `AppViews/*` (except `ReBuzzCoreInitialization.cs`), `Common/` WPF windows, `Properties`, `ReBuzzTests` (the Classic exe's tests, 140 files; see 4), `SysExTests`, WPF libraries only Classic referenced (`BuzzGUI.MachineView`, `ParameterWindow`, `PianoKeyboard`, `SequenceEditorExtended Rebuzz`, `ToolBar`, `WavetableView`, `EnvelopeControl`, `FileBrowser`, `MetadataControl`, `BuzzUpdate`, `ModernSequenceEditor*`, `NativeMachineFrameworkUI`, `WaveformControl` except the `r8brain` interop the engine compiles), `ModernPatternEditor.NET.csproj` and `#if WINDOWS` in its sources (the own player: `PlayRecordManager`, `PatternEditor.Work`), the WPF `Machines/Neo*/Neo*.csproj` projects, `Machines/ModernPianoroll` (the WPF machine; the piano roll of the Studio is `ReBuzz.Studio/Views/Session/PianoRoll.cs`), `RunTime/Help`, `RunTime/Themes`, `RunTime/ParameterEditors`, `RunTime/PE*.xml`, `RuntimeFiles.vcxproj`, `docs/ReBuzz Guide` (Classic's help, 198 files) | ~900 files | the sln, `build.ps1`, `fork-package.yml`, `fork-ci.yml` (core-build) | 4 |
| E | Old machines and hosts: native host (`ReBuzz/NativeMachine/*`, `NativeMachineHost` uses in `MachineManager`, `MachineWorkInstance`, `MachineDLLScanner`, `MachineDatabase`, `Gear.cs`, Studio `NativeGui.cs`), `ReBuzzEngine/`, `ReBuzzEngine32/`, `NativeMachineFramework/`, `ReBuzzPatternXP/`, `3rdParty/Gear`, `RunTime/Gear`, `RunTime/bin32`, `RunTime/bin64`, `ExternalMachines/` (Pedal sources and portable, Buzztrax, Jeskola examples, ReTracker, binaries, `machines.json`), `Machines/{AudioBlock,EnvelopeBlock,ConnectionMixer,ManagedMultiIOEffect,QuickNote,FakeNative*}`, `NeoBuzz.PedalTests`, `Machines/Manifests` rows of non-Neo machines (profile, catalog, 79 effect + 58 generator manifests, the 30 `Successors` pairs), `tools/` for natives (`build-natives*`, `native-*`, `k2*`, `pedal_knowledge`, `jeskola_knowledge`, `headless-pedal`, `headless-replace`, `fetch-machine-binaries.ps1`), CI jobs `natives-linux`, `natives-macos`, "Classic machines and native host" steps, package-check names, `MachineAliases` (keeps `Session Clip Player` only) | ~2.4k files | `AppGear.targets`, `EngineHost`, tests that use FakeNative or Pedal, `docs/machine-health.md` | 5 |
| F | Agent: `machine_replace` classic path (`MachineReplace.cs`, `ReplaceTools.cs`, `SuccessorConverter`, `MachineSuccessors.cs`), placeholder handling, `migrating-songs.md`, 100+ guides of removed machines in `ReBuzz.Agent/Guides/machines`, `MachineCatalog` rows, eval tasks for them, `eval/baseline.json` rows | ~150 files | `ReBuzz.AgentEval` tasks, `agent-coverage.py`, `ToolReference` | 6 |
| G | Docs: README, FORK.md, user guide, UI gallery list, specs that describe removed things (a note, not a rewrite: `legacy-machines.md`, `native-format.md` section on Export for Buzz) | | | 7 |

### 4. Tests

`ReBuzzTests` drives `ReBuzz.exe` (Classic) through its automation driver and stays with it. Tests in it that cover engine behaviour with Neo machines have counterparts in `ReBuzz.Studio.Tests` (net10.0, the Studio's host) and `NeoBuzz.MachineTests`; the list of what was removed and what covers each family is in the report. `ReBuzz.Studio.Tests` loses what loads old songs or machines (`ClipCorpus`, `ClipConverterTests`, `BuzzExportTests`, parts of `ClipRoundTripTests`, Pedal and native tests); the clip goldens and null tests stay and must not change.

### 5. Order and checks per stage

1. Inventory (this file).
2. Our own files: confirm templates, default songs and gallery songs name only Neo machines; convert or delete the rest.
3. Song formats, Export for Buzz and the CU3 leftovers. Build `ReBuzz.Engine`, `ReBuzz.Host`, `ReBuzz.Studio`, `ReBuzz.Studio.Tests`; run `ClipOpenTests`, the Nbl and Clip round-trip tests.
4. Classic.
5. Old machines and hosts. `tools/machine_catalog.py` 0 problems; regenerate `docs/agent-coverage.md`.
6. Agent tools, guides, eval tasks. `dotnet run --project ReBuzz.AgentEval -- selftest`; every guide keeps a task.
7. Docs.

### 6. Rule for a kept part that depends on a removed one

Keep the smallest piece and say why (section 2, item 1 is the first case). Each such case is added below as it is found.

| Piece kept | Why |
|---|---|
| `NblCoreSections` (was `BMXFile`) and the string `bmx-sections-v1` | `.nbl` v1 to v3 core sections are in this codec |
