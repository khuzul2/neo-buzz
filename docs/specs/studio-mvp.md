# Studio MVP (P3) — Plan

| | |
|---|---|
| Status | Planned 2026-11-03. P2 done (mac-engine.md) except the user's hands-on Mac test |
| Scope | cross-platform.md §5.2 P3: ReBuzz's studio UI on Avalonia, Mac first. Windows keeps the WPF app until P4 |
| Exit | On a Mac: compose a song from scratch with Neo and Pedal machines; old songs open (native machines as placeholders) and save back without loss; the MCP server works in studio mode |
| Related | cross-platform.md (D1–D5, §3.1 O2), mac-engine.md (the engine on macOS), machines.md §4 (the Neo editor kit), session-view.md (the Session View) |

---

## 1. Constraints

1. **Free components only.** Avalonia itself is MIT, free for commercial use too. The studio uses only it and other MIT or Apache packages. It does not use:
   - Avalonia XPF;
   - the Pro controls (TreeDataGrid, Charts, Rich Text, PDF, Media Player…);
   - the Plus tooling (DevTools, Parcel, Hot Reload, the Visual Studio extension).

   Pricing as of 2026-11: <https://avaloniaui.net/pricing>.
2. **No paid signing in P3.** The Mac app is built unsigned by CI. It runs after `xattr -d com.apple.quarantine`. A Developer ID ($99 a year) is needed only for notarized distribution (P5), and it's optional.
3. **The engine doesn't change for the UI**, except through seams (P1) that the WPF app also uses.
4. **Windows stays on WPF** until P4. Nothing here may break `ReBuzz.exe`, its tests or its package.

## 2. Shape

| Project | What | Builds on |
|---|---|---|
| `ReBuzz.Host` (new, net10.0) | The engine start-up `rebuzz-headless` does today (settings, seams, steps 1–6, audio, MIDI, Link, agent). Shared by the headless app and the studio | Linux, macOS, Windows |
| `ReBuzz.Studio` (new, net10.0, Avalonia 12.1) | The app: shell, views, dialogs, the Avalonia seams (`IUiDispatcher`, `IUserMessages`, `IFileNameChoice`, `IKeyboard`, `IMachineUiHost`) | all three; shipped for macOS |
| `ReBuzz.Studio.Tests` (new) | Avalonia.Headless (NUnit): views render to PNG and take input, on Linux CI | Linux, macOS |
| `NeoBuzz.Editor.Avalonia` (new) | The Neo editor kit on Avalonia: an `INeoCanvas` backend and the kit's controls with the WPF kit's API | all three |
| Neo machines, studio flavour | Each Neo machine built `net10.0` with its editor on the Avalonia kit, into the studio's Gear (the portable flavour stays for `rebuzz-headless`) | all three |
| Pedal machines, portable | The 19 Pedal machines (ExternalMachines/source/thepedal) built `net10.0`, without their WPF GUIs: they get the generic parameter window | all three |

## 3. Decisions

1. **Avalonia 12.1** (MIT), Fluent theme in dark mode, plus our palette.
   - There is one built-in dark theme. ReBuzz's colour keys (`ThemeColors`) stay as data, so views read the same keys as on Windows.
   - The runtime XAML themes aren't ported.
2. **Code-drawn views port nearly line for line.** These are the machine view, the sequence editor, the pattern editor, the Session View and the Neo kit. WPF's `OnRender`/`DrawingContext`/`FormattedText` map onto Avalonia's. XAML views and styles are rewritten (Avalonia uses selectors, not triggers).
3. **Pure logic moves to portable classes** where porting a view shows it is mixed in: geometry, hit testing, selection, edit commands. Tests run on Linux. The WPF views keep their code until P4 switches Windows over.
4. **Machine GUIs on the Mac:**
   - Neo machines show their own editors, through the Avalonia kit.
   - Other managed machines show the generic parameter window. Their WPF GUIs don't load there.
   - Native machines are placeholders, with no GUI.
   - The machine API's `IMachineGUI` is already toolkit-free: the studio's `IMachineUiHost` expects an Avalonia control.
5. **One sequence editor: the horizontal one** (ModernSequenceEditorHorizontal), an arrangement timeline as in Ableton's arrangement view. Windows' default is the vertical one; P4 can add it.
6. **The pattern editor is the Modern Pattern Editor, ported.**
   - Its machine side (the pattern data, PXP, playback) builds `net10.0`.
   - Its view moves to Avalonia.
   - Until then, the Session View's piano roll edits notes in the engine's own columns. Off Windows, patterns have no editor machine (mac-engine.md M2).
7. **Testing.**
   - Every view renders headless on Linux CI (Avalonia.Headless + Skia) to a PNG uploaded with the run, and takes simulated input in tests.
   - The Mac job builds the app bundle, and starts it headless, once per PR into main.
8. **The dev loop builds locally.** The workspace can't reach NuGet, so a workflow (`dev-nuget-cache.yml`) restores the studio's and the tests' packages on Linux and publishes them as one asset of a prerelease (`dev-nuget-cache`). A local build reads them as its only package source. This is tooling only: the product build restores from NuGet as usual.
9. **The app on the Mac in P3:**
   - CI publishes a self-contained osx-arm64 `ReBuzz.app` (zip) with PortAudio, libsndfile and the Link helper inside, unsigned.
   - It needs no .NET install.
   - Settings live in `~/Library/Application Support/ReBuzz`, as for `rebuzz-headless`.

## 4. Steps (PR-sized)

| # | PR | Needs | Exit check |
|---|---|---|---|
| S0 | **Foundations.** The dev NuGet cache; `ReBuzz.Host` (rebuzz-headless moves onto it, unchanged in behaviour); `ReBuzz.Studio` with the Avalonia seams, a main window (menu, transport bar, view tabs, status bar: CPU, dropouts), File new/open/save/save as, undo/redo, audio through PortAudio; `ReBuzz.Studio.Tests`; CI on Linux (build, headless tests, screenshots) and the Mac job (bundle, start) | – | The window renders headless with a song open; open → save round-trips a song; the bundle starts on macos-15 |
| S1 | **Neo editors.** `NeoBuzz.Editor.Avalonia`; the Neo machines' studio flavour; the studio's machine windows (`IMachineUiHost`) | S0 | Every Neo editor renders headless; a knob drag changes the parameter and undo restores it; screenshots match the WPF editors' layout |
| S2 | **Generic parameter window** and the portable Pedal machines | S0 | Every Pedal machine loads, plays and shows its parameters; a slider changes a value |
| S3 | **Machine view.** Machines and wires, add (gear menu), delete, rename, move, connect by drag, amp and pan on wires, mute/solo, open editor/parameters | S0 | Build a chain from an empty song with the mouse alone (headless input), and it renders as expected |
| S4 | **Sequence editor** (horizontal). Tracks, pattern blocks, place/move/delete, loop and song end markers, play cursor | S0 | Arrange patterns, play, the cursor follows; save and reopen keep it |
| S5 | **Session View and mixer.** The WPF Session View's code-drawn grid, clip panel, piano roll, mixer, sends, meters | S0 | Launch, record a take, mix; the Session View's existing tests' scenarios as headless UI tests |
| S6 | **Modern Pattern Editor.** Its machine side portable, its view on Avalonia | S0 | Write notes and automation with the keyboard; songs with PXP data open and save unchanged |
| S7 | **Preferences and the rest of the shell.** Audio device and buffer, MIDI inputs/outputs, sync (clock, Link), theme colours, recent files, autosave recovery, keyboard shortcuts, the Mac menu bar | S0 | Every setting round-trips; shortcuts work headless |
| S8 | **MCP studio mode.** The agent's HTTP server with its token, as on Windows (Settings → Agent) | S0, S7 | `claude mcp add` against the studio; the A7 live tests' scenarios pass against it |
| S9 | **The exit test**, on CI and by hand: a song from scratch; old songs (Windows-made, natives as placeholders) open and save back | S1–S8 | §5 |

S1 to S6 are independent once S0 is in: they run in parallel, each in its own branch, and each adds one view to the shell.

## 5. What only the user can do

- **Try it on a Mac:** download the CI's `ReBuzz.app`, clear the quarantine flag, open it. The feel matters here: scrolling, fonts, Retina, keyboard shortcuts. So does the menu bar.
- **The exit test by hand:** a song from scratch, and an old song of yours.
- **Signing** (optional, P5): a Developer ID, if the app should open without the quarantine step on other Macs.

## 6. Risks

| Risk | Impact | Mitigation |
|---|---|---|
| The Modern Pattern Editor is the largest port (12k LOC, its own rendering) | S6 runs long | It is last and independent; the Session View's piano roll covers notes meanwhile |
| Custom-drawn views are slow on Avalonia (the pattern and sequence editors redraw a lot) | sluggish UI | Draw in layers (static grid cached, cursor separately), as the WPF views do; measure in headless tests |
| WPF habits that don't port (triggers, `DependencyProperty` metadata, adorners) | rework | Port by behaviour, not by XAML; keep the code-drawn parts as they are |
| No local NuGet | slow loop | §3.8 |
| Mac-only behaviour (menu bar, Retina, keyboard) can't be seen in CI | surprises on the user's Mac | A Mac CI start-up check; the user's try-out after S0 and after S3 |

## 7. Progress

**S0, foundations** (this PR).
- **`ReBuzz.Host`** holds the engine's start-up off WPF, moved out of `rebuzz-headless`. The behaviour is unchanged: the smoke, live, MIDI and round-trip tests are the same. `rebuzz-headless` and the studio both use it, and `AppGear.targets` puts the Neo machines and the Link helper beside either app (and into a publish).
- **`ReBuzz.Studio`** (Avalonia 12.1.4, Fluent dark):
  - Seams: `IUiDispatcher` on Avalonia's dispatcher, with DispatcherTimer timers. The engine's messages appear as dialogs; its synchronous questions run in a nested dispatcher frame. File choice goes through the storage provider. Modifier keys come from the windows' input.
  - **The main window:**
    - a menu (File, Edit, Transport) with Command on macOS and Control elsewhere;
    - a transport bar: play, stop, record, loop, BPM, position;
    - one tab per view (`StudioViews`, placeholders until S3–S6);
    - a status line: audio device, rate, buffer, dropouts.
  - **Song files:** new, open, save and save as. The studio asks about unsaved changes itself (async) before the engine acts, so the engine never has to.
  - **Machine windows** (`StudioMachineUiHost`, the engine's `IMachineUiHost`): one parameter window and one GUI window per machine, kept as long as the machine and saved with the song. Their content comes from `ParameterWindowContent` (S2) and `GuiContent` (S1). A machine with no GUI here opens its parameter window. Renaming asks through `Dialogs.AskText`.
  - Arguments: `ReBuzz.Studio [song] [--settings <folder>] [--no-audio] [--smoke]`.
- **`ReBuzz.Studio.Tests`** (Avalonia.Headless.NUnit with Skia):
  - the engine starts with the Neo machines installed and the window shows its views;
  - F5 plays and stop stops;
  - save as, new, then open gives the song back.
  - The window is saved as PNGs, which CI uploads (`studio-screenshots`).
- **Dev NuGet cache** (§3.8): `dev-nuget-cache.yml` keeps the restored packages as one release asset (`dev-nuget-cache`, a prerelease, not a ReBuzz release). Locally: `dotnet restore <project> --configfile <a nuget.config whose only source is that unpacked folder>`. The test projects also run locally that way.
- **The Mac app:** `tools/mac-bundle.sh` builds `ReBuzz.app`. It is self-contained osx-arm64, with PortAudio, libsndfile (and its libraries) and the Link helper inside, and ad-hoc signed. The Mac job runs the headless view tests on arm64, then builds the app and starts it for real (`--smoke`, with the runner's audio device), and uploads it (`ReBuzz-macos-arm64`).

**S1, the Neo editors** (this PR).
- **`NeoBuzz.Editor.Avalonia`** (`Machines/NeoBuzz.Editor.Avalonia/`, linked as source like the WPF kit):
  - `AvaloniaCanvas`: `INeoCanvas` on Avalonia's `DrawingContext`, with immutable brushes and pens cached, and paths' geometry kept between frames. The font is Segoe UI, else one of its widths (Helvetica Neue, Arial, Liberation Sans), so labels fit layouts made with it.
  - The kit's controls with the WPF kit's API: the editor shell with its header and preset bar, knobs, choices, drop-downs, meters, graph views (envelopes, interactive graphs), sections, `Layout` and `Build`. They port line for line on `NeoElement`, a base with WPF's shape (`OnRender`, `OnMouseLeftButtonDown`…).
  - `WpfNames.cs` maps the WPF names the editors use onto Avalonia: global using aliases (`UIElement`, `FrameworkElement`, `Grid`…) and a few shims (`Visibility`, `ToolTip`, `Cursors`, `MessageBox`). So each machine's `Neo*Editor.cs` is one file for both; it only wraps its `System.Windows` usings in `#if !AVALONIA`. Neo FM6's and Neo Wave's file dialogs go through `EditorFiles`, which both kits have (the storage provider on Avalonia).
  - `EditorContext` and `ParamBinding` moved to `NeoBuzz.Editor/Params/`, shared. Edits set parameters as on WPF (`IParameter.SetValue`).
- **Undo.** `ParamEdits` (shared, toolkit-free) collects what a gesture changes and puts it on the song's undo history as one step: a knob drag, a double-click reset, a run of wheel turns, a click on a choice or a menu, a handle drag, a preset loaded from the bar. Each Avalonia editor has one. WPF's editors set none, so Windows behaves as before (its Neo editors' edits aren't undoable).
- **The studio flavour** (`Machines/Studio`, `NeoStudio.targets`): each Neo machine built `net10.0` from the portable build plus its editor, with `AVALONIA` defined. Its GUI factory (`#if WINDOWS || AVALONIA`) returns the Avalonia editor, and Neo machines' About text shows in a window. `AppGear.targets` builds it for apps that set `NeoGearFlavour=Studio` (the studio and its tests); rebuzz-headless keeps the editor-less portable flavour.
- **In the studio:** `StudioMachineUiHost.GuiContent` is `MachineGuis.Create`: a machine's GUI factory, when it makes an Avalonia control. Other GUIs (WPF) fall back to the parameter window. The window is made once (no editor is built for a window already open), and its resize follows the factory's declaration. A closed editor lets go of its parameters; opened again, it is built anew.
- **Tests** (`NeoEditorTests`, headless):
  - every Neo editor opens as its machine's GUI and renders, a screenshot each (`neo-*-editor`), with the WPF editor's size within 6 %. On Linux CI each is within 4 px of it;
  - dragging Neo Comp's threshold knob changes it in one undo step, and undo and redo restore it, with the knob following. A double-click resets it, and a choice click is a step too;
  - presets load from the bar: next, and from the grouped list. Each load is one undo step (`neo-comp-editor-preset`);
  - dragging a band's handle in Neo EQ's graph moves its frequency and gain in one undo step (`neo-eq-editor-dragged`);
  - live parts, with Neo Poly → Neo EQ → Neo Analyzer playing a chord through the engine offline. Neo Poly's and Neo EQ's meters show it, as do Neo EQ's spectrum under its response curve and Neo Analyzer's spectrum and loudness. The analyzer's graphs animate (`neo-*-editor-live`);
  - Neo FM6's algorithm view takes clicks through the WPF-shaped callbacks: right click goes back, and both are undoable. A closed editor opens again.
- **Differs from WPF:** text is greyscale-antialiased in a fallback font. Paths' corners all follow the stroke's join, as Avalonia has no per-corner smooth join. A message box returns at once. Saving a preset asks for its name in an Avalonia dialog; it isn't tested headless.

**S6, the Modern Pattern Editor** (§3.6).
- **One source, three builds.** The WPF project (`ModernPatternEditor.NET`) still builds for Windows; fork CI's Windows job now builds it. Its machine side is shared, unchanged, with two `net10.0` projects:
  - `ModernPatternEditor/Portable`: the editor without a view, for `rebuzz-headless`;
  - `ModernPatternEditor/Avalonia`: the same with the Avalonia view, for the studio.
  - Both build `Modern Pattern Editor.NET.dll` into the app's `Gear/Generators` (`AppGear.targets`). The engine finds the default pattern editor there as on Windows, and songs name it as they do.
  - How: `PatternEditor.Core.cs` holds the machine side of `PatternEditor`, moved verbatim out of `PatternEditor.xaml.cs` (a partial class). WPF-only lines in the shared files are under `#if WINDOWS`. `Portable/PatternControl.cs` is the toolkit-free controller: keys, cursor, selection and the edit commands, ported from the WPF control. `PortableShims.cs` and `EditorDispatcher.cs` stand in for the few WPF types the shared code uses.
  - The studio can't use the editor's types: managed machines load in contexts of their own. So the view is part of the editor's Avalonia build, and the Pattern tab shows the control the editor machine gives the engine (`PatternEditorControl`), as ReBuzz's WPF window does.
- **Engine seams:**
  - `ManagedMachineHost`: an editor's `PatternEditorControl` may return `object`. The portable editor returns its view, or null without one.
  - `ReBuzzCore.PatternsAwaitingEditor`, set by the BMX and BMXML loaders. While an old song's patterns are made, `SetPatternEditorPattern` no longer creates an editor for a machine whose built-in contents are converted next. Otherwise such a machine got two editors, and each save of the song differed. The code is the same on Windows.
- **The view** (`ModernPatternEditor/Avalonia`), a port of `PatternControl` and the code-drawn `BeatVisual`:
  - column set headers (machine and track; column names turned up; a click mutes, selects a column or the set), row numbers, the beats, the cursor row, the selection, the cursor and the play position;
  - only the beats and column sets in view are drawn, with the cursor, selection and play position on a layer of their own;
  - the toolbar (play solo, create, clone and delete pattern, beats, tracks, wave, base octave, keyboard mapping, steps down and right, MIDI edit, zoom, help) and the status bar, as in WPF;
  - keys as on Windows: notes by key position (the PC scan codes `PEKeys.xml` names), values, note off, clear, insert and delete rows, Tab between tracks, Shift to select, Ctrl+1–0 subdivisions. Command acts as Control on macOS, and the main keyboard's + and − as the keypad's;
  - the edit commands of the WPF editor: cut, copy, paste, undo, redo on the editor's own stack, transpose (Shift and Ctrl+Shift with + and −), interpolate (linear, exponential), randomize, rotate, write state, select all and unselect. They are also in the grid's context menu;
  - pattern properties (Ctrl+Backspace): name, beats, rows per beat, and which columns and MIDI lanes the pattern has.
  - Avalonia matches key bindings from the focused element up, before KeyDown. So the grid binds its Ctrl and Command shortcuts itself, or the window's Ctrl+Z would undo the song.
- **The Pattern tab** (`Views/PatternView.cs`):
  - It shows the editor for a chosen machine and pattern, with a machine chooser, a pattern chooser and New pattern. It follows the engine when the pattern editor's pattern (`SetPatternEditorPattern`) or the selected machine changes, and comes to the front when another view sets `ActiveView` to `PatternView` (the Session View's double-clicked clip).
  - The editor's Return (WPF: to the sequence editor) selects the Sequence tab.
  - **For other views:** `PatternView.Edit(IPattern)` and `Edit(IMachine)` show a pattern or a machine's patterns and bring the tab to the front, `PatternView.Show()` brings it to the front alone, and `PatternShown` says what it shows.
  - **For the shell's Edit menu:** the page's `Undo`, `Redo`, `Cut`, `Copy` and `Paste` reach the editor's own undo stack and clipboard (its `IEditContext`). They return false when the editor has nothing to do, so the song's undo can take the command (`IStudioEditTarget`).
- **Tests** (`PatternEditorTests`, headless):
  - notes, a note off, and global and word values typed with the keyboard are the engine's pattern events and the editor's data (format 255);
  - period clears, Delete moves rows up;
  - transpose by a semitone and an octave, copy and paste, linear interpolation with the WPF editor's values;
  - undo and redo through Ctrl+Z/Y and through the page, without touching the song's undo;
  - a click places the cursor, a drag selects;
  - pattern properties: renamed, 8 beats, 8 rows a beat, two MIDI lanes typed into;
  - the pattern plays through the engine (offline render): the notes sound at the Master, the automation reaches the machine, and the play cursor follows;
  - the choosers, the API, and `SetPatternEditorPattern` with `ActiveView` pick what the tab edits; a machine without patterns gets New pattern;
  - a song's editor data saves and opens byte for byte, as BMXML and as BMX;
  - a frame of the beats in view draws in 10–55 ms headless (Neo Poly, 91 global columns).
  - Screenshots: `pattern-editor-empty`, `-notes`, `-typed`, `-pasted`, `-interpolated`, `-playing`, `-selection`, `-midi-lanes`, `-no-patterns`, `-reopened`.
- **Songs** (`tools/song-roundtrip.py`, Linux CI):
  - old songs' built-in contents now arrive in the editor's data, as on Windows;
  - a Windows-style song, with editor data written in the editor's own format (columns, MIDI lanes, 4 and 8 rows a beat), saves byte for byte as BMX and BMXML.
  - The A6 suite against `rebuzz-headless`, with the editor present, shows no regressions (run locally).
- **Not ported, or different from WPF:**
  - the parameter knobs in the column headers, the blinking cursor, the background image (a solid colour);
  - the chords window, XML pattern import and export, the settings window and the About box;
  - Paste is enabled whenever the clipboard holds data;
  - deleting a pattern, undone, gives it back empty;
  - pattern properties are a smaller dialog.
- **Not checked yet:** on a Mac (fonts fall back from Consolas to Menlo; Retina, scrolling, keys), and a real song from Windows with editor data (the tests use synthetic data built in the editor's format).
