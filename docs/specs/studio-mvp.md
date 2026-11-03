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
10. **Edit commands go to the selected view first.** Avalonia's window key bindings see Ctrl/Command shortcuts before the views do. So the shell's Edit menu and its shortcuts (Undo, Redo, Cut, Copy, Paste) go to the selected tab's view when it implements `IStudioEditTarget`. A view returns false to pass the command on to the song's undo history. Views don't bind these keys themselves.

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

**S0, foundations** (#53).
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

**S1, the Neo editors** (#55).
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

**S5, the Session View and its mixer** (`ReBuzz.Studio/Views/Session/`, the "Session" tab).
- **A port of the WPF view** (ReBuzz/AppViews/Session), file for file, built in code as it is: cells are Avalonia borders and text in a grid, the fader, pan, sends, meters and piano roll draw themselves (`Render`). Engine calls and undo are the WPF view's: `SessionLayout` for edits (the session's own undo history), `ISession`/`SessionCore` for launches, `SessionMixer` for the mixer (connections, so the song's undo has sends and cue). The WPF view is unchanged.
  - *Grid:* tracks, scenes, group columns (fold, launch, stop), return columns; clip colours and states (queued blinking, playing green with progress, stopping, ● recording), selection, MIDI learn and controller rings outlined; scene tempo, beats per bar and follow marks; empty slots stop their track, or record a take when armed.
  - *Top bar:* Stop all, Rec (arrangement), Overdub (Session Record), Capture, metronome, Back to arrangement, ↶ Launch, Follow, launch quantization, beats per bar, tempo with nudge (held) and Tap, the session clock, dropouts, ↶ ↷, + Scene, + Track, Clip, Mixer, ⚙ (clip length, record length, count-in, metronome, scenes stop other clips, select on launch, launch timing, returns, cue output, MIDI learn, keyboard shortcuts).
  - *Clip panel:* mode, quantization, legato, velocity, start and loop markers, the clip's follow action, Reset, and the scene row's tempo, beats per bar and follow action; status (playing, position, follow, recording). A **Notes…** button opens the piano roll.
  - *Piano roll* (`PianoRoll`, `PianoRollWindow`): the clip's engine MIDI column, edited as on Windows (draw, move, resize, velocity, box select, keys, quantize, grid, zoom), each edit one session undo step. Until S6 this is where the studio writes notes.
  - *Mixer:* volume, pan, sends to returns, cue, crossfader sides and the crossfader (with its curve), mute and solo, post-fader peak meters (on a 25 ms timer while shown), group strips and the master strip.
  - *Keys* as WPF has them, with Command for Control on macOS. The view's Control shortcuts are key bindings of its own: Avalonia matches key bindings from the focused element up *before* KeyDown, so otherwise the main window's Ctrl+Z (the song's undo) would take the session's. Menus are context menus; dialogs (rename, scene tempo, insert into arrangement) answer tests through `Dialogs.TextResponder` and `ArrangementInsertDialog.Responder`.
- **Not ported:** audio clips (1.0: dropping files, the Audio section, warp markers; the studio has no Session Clip Player yet), the MIDI note grid dialog. (S7 added the Sync and Controllers menus.) Double-click on a clip opens the Pattern tab, as on Windows; until S6 it is a placeholder, and Command/Ctrl+double-click (or Notes…) opens the piano roll.
- **Tests** (`SessionViewTests`, 11, on the shared engine through `SessionRig`: Neo Poly tracks, the view in a window, mouse and keys simulated, the engine's ticks rendered by a `WorkManager` as ReBuzzTests does, the heard clock frozen as its `FreezeClock`), following the WPF view's tests: a scene launch queued then playing, a clip's ▶, track stop and Stop all; a clip made by double-click and notes drawn, dragged, moved and undone in the piano roll, then played; a track armed and a take recorded with the transport running, played back, undone and redone; faders, pan, sends to a return (and their undo in the song), mute, crossfader side, master, meters; groups (launch, fold, keys skipping folded tracks) and cue; undo of grid, clip panel and drag edits with the keys in the main window (not the song's undo); the WPF keyboard test; the clip panel's clip and scene properties; follow actions, quantization, beats per bar, launch undo, nudge; slot and scene menus (colour, assign, rename, tempo, insert into arrangement, duplicate). Screenshots: `session-tab`, `session-grid`, `session-grid-playing`, `session-clip-panel`, `session-piano-roll`, `session-piano-roll-playing`, `session-recording`, `session-mixer`, `session-groups`, `session-undo`, `session-performance`, `session-menus`.

**S3, the machine view** (`ReBuzz.Studio/Views/Machines`, the Machines tab). BuzzGUI.MachineView ported by behaviour.
- **Drawing** (`MachineCanvas`): one code-drawn control, in the default theme's look and colour keys (`MV Background`, `MV Generator`/`Effect`/`Master` and their `Mute` shades, the LED colours, `MV Line`, `MV Arrow`, `MV Machine Text`/`Border`).
  - Machines are 100×50 boxes at their song positions. As in MachineCanvas, the canvas is twice the view, so positions -1..1 fill the view.
  - A box has its mute LED (lit while it sounds), the delete button on hover, the mode text (.NET, MIDI channel, 2x), "missing" or "crashed", the name as (muted), <bypassed> or [not soloed], and the selection's glass.
  - Wires are straight, clipped at the boxes (the Clipper port), with the arrow at the middle pointing downstream.
  - The geometry and hit tests are pure (`MachineGeometry`, `CanvasMap`).
- **Mouse and keys, as in Buzz:**
  - A click selects, and a generator gets MIDI focus. Drag a rubber band on empty space; Ctrl (Command on a Mac) adds to the selection. Dragging moves the selection. An effect with no wires, dropped on a wire's middle, goes into it.
  - Shift-drag (or middle-drag) from source to destination connects. A middle click on an arrow disconnects.
  - The arrow opens the amp control, with the amp knob turning on a left press, pan for stereo inputs, insert (an effect into the wire) and disconnect. Leaving it commits.
  - The LED mutes. A double click opens the machine's GUI (`UiHost.OpenGui`).
  - Right click on empty space: New ▸ Generators / Effects / Control (the installed machines by kind, as the machine list filters them), Unmute all, Snap Machines, Select All, Deselect.
  - Right click on a machine: Mute, Solo or Bypass, Oversample, Parameters..., MIDI Input Channel, Replace, Rename... (`AskName`), Delete. Master has Parameters... only.
  - Keys: Delete (Backspace on a Mac), and Ctrl/Command + A, D, U, M, as the view's own key bindings. Their commands are `MachineCanvas` methods, ready for the shell's Edit routing.
  - Zoom: Ctrl/Command + wheel or a trackpad pinch, around the pointer; or the top bar's slider (-8..8, ×2^(level/8)). A middle double click resets.
  - Scroll: the wheel or a trackpad, the scroll bars, a middle drag, or Shift-drag on empty space.
- **Edits** (`MachineEdits`) go through the engine's undo stack, with the SongCore calls the WPF view makes:
  - create (CreateInstrument: a pattern, a sequence track, and a generator wired to Master while the song has no effects);
  - move, connect, disconnect, insert, replace, rename;
  - delete, which wires a lone machine's sources on to its destinations.
  - Mute, solo, bypass and a wire's amp and pan aren't undoable in ReBuzz. Here they use the engine's own undo steps for them (`AgentActions`, the MCP server's), so they undo in the studio.
- **Tests** (`MachineViewTests`, headless, with mouse and keys only):
  - From an empty song: add Neo Poly and Neo Verb from the gear menu, Shift-drag Poly → Verb → Master, take the direct wire away with the amp control, move Verb, set its wire's amp and pan with the knobs, mute (LED), solo and rename (menus), delete (Delete key). Each step is checked against the engine's graph, then undone with Ctrl+Z, step by step, back to Master alone.
  - Double click opens the GUI; Parameters... and Bypass from the menu.
  - A saved song reopens with each box at its stored position, and a click there selects it. An old Windows song (FSM Infector demo, .bmx) opens with its native machines as placeholders.
  - Zoom keeps the point under the pointer; wheel, middle-drag and Shift-drag scroll; the slider zooms; a middle double click resets.
  - An effect dropped on a wire goes into it, in one undo step. Rubber band, group move, Ctrl+A/D, Ctrl-click, and delete from the menu.
  - Screenshots: `machines-1-empty` to `machines-16-rubber-band`.
  - The tests hold off the menus' hover delay, which runs on the wall clock.
- **Differs from WPF:**
  - Ctrl/Command + wheel zooms and the plain wheel scrolls (WPF's CtrlMouseWheel setting), for trackpads; a pinch zooms too (not testable headless).
  - Mute, solo, bypass and amp/pan undo.
  - A machine added from the menu is selected.
- **Not ported:**
  - the sidebar (machine list, parameters, MDB, templates, MIDI bindings);
  - machine groups (grouped machines are drawn at their own positions);
  - cut, copy, paste, clone and templates; quick-new by typing; rotate (Alt-drag); screen-edge disconnect;
  - curved wires, multi-channel plugs and their channel menus, wireless outputs, VU meters on wires, skins;
  - the CPU monitor, HD recorder, signal analysis, latency, attributes and machine commands.

**S7, Preferences and the rest of the shell** (`ReBuzz.Studio/Shell`, `Shell/Preferences`).
- **Preferences** (Command+, on a Mac, Edit ▸ Preferences… elsewhere): ReBuzz.exe's Preferences and Settings windows in one, a page each. Choices apply and are saved as they are made (no OK or Cancel, as macOS settings windows do).
  - *Audio:* the output device from the PortAudio factory ("PortAudio: …"; a saved device that's gone shows as not connected), the input, sample rate and buffer (the registry's PortAudio section, which `PortAudioDriver` reads). Each choice reopens the device: a new device through `SelectedAudioDriver`, as WPF's toolbar; the same one closed and opened again. Render threads and the work algorithm as WPF's Preferences ▸ Engine has them, with the engine's defaults (4 and Recursive Tasks; WPF's window shows 2 and Threads when nothing is saved). "Restart audio" (and F12, WPF's Reset Audio) reopens it.
  - *MIDI:* the backend's inputs and outputs (CoreMIDI on macOS; none on Linux) and the remembered ones that aren't connected, checked to open them, with their state (open, not connected, didn't open). A change saves the device by name and opens the inputs (or outputs) again as WPF's OK does. The page follows plugging (polled each second while shown; the engine's watcher opens remembered devices). Below them, `MIDISettings`.
  - *Sync:* Ableton Link (with its peers and tempo), start/stop with peers, follow MIDI clock in, and MIDI clock out per open output (and the chosen ones not open). `SessionSync` keeps them.
  - *Agent:* S8.
  - *General:* `EngineSettings` and `GeneralSettings` from their `[BuzzSetting]`s, as WPF's Settings window lists them: a check box for on/off, a choice of the options (the default in bold), text otherwise; names in sentence case, the description under each. The ones only ReBuzz.exe reads aren't shown (WPF rendering and DPI, the sequence editor type, the piano keyboard, update checks, the Windows fill thread).
- **The Session View's Sync and Controllers menus** (⚙), as WPF's, which S5 left out.
- **Menus** (`AppMenu`): one model, shown as the macOS menu bar (`NativeMenu`) on a Mac and as the window's menu elsewhere.
  - The app menu: About ReBuzz, Preferences… ⌘,; Avalonia adds Services, Hide, Show All and Quit ⌘Q (Quit asks about unsaved changes through the window's closing).
  - File (New, Open…, Open Recent ▸, Save, Save As…; Exit elsewhere), Edit (Undo, Redo, Cut, Copy, Paste; Preferences elsewhere), Transport (Play F5, Stop F8, Record F7, Restart audio F12), View (the tabs: Pattern F2, Machines F3, Sequence F4, Session Ctrl+Tab and back), Window (Minimize, Zoom, Agent connection… ⇧⌘A), Help (Keyboard shortcuts…; About elsewhere).
  - Submenus are built as they open (recent files). The View keys are the window's, after the views (F2 renames in the Session View), not menu key equivalents; Minimize has no ⌘M, which the machine view uses for mute.
- **Recent files:** File ▸ Open Recent lists the engine's "Recent File List" (song, then its folder), greyed when the file is gone, with Clear Menu.
- **Autosave:** at start-up a leftover autosave is offered (Yes opens it unsaved under its song's name, No deletes, Cancel keeps it), then unsaved changes autosave once a minute when due, as WPF's `StartAutosave` (`SongAutosave`). Not in `--smoke`.
- **The window's size, place and maximized state** are kept in the settings ("Studio" section) and put back on a screen that is still there.
- **Status line:** the engine's load ("CPU 12%") once a second while a device plays, from `TakePerformanceDataSnapshot` as WPF's CPU monitor reckons its total; the agent's state.
- **About** (`AboutWindow`): the build and the credits rolling on black, as WPF's (without the particles).
- **Tests** (`PreferencesTests`, `StudioShellTests`):
  - Audio: each choice writes its registry value and reopens the device (a stand-in device list and reopen); the page shows them again when reopened; a saved device that's gone. Where PortAudio is (Linux CI's null sink, the Mac job), Restart audio opens the device for real, and the status line shows it and the load.
  - MIDI: a stand-in backend: a device checked opens and is saved by name, unchecked closes; a remembered device plugged in opens and the page follows; a new page shows the saved states. `MIDISettings` round-trip.
  - Sync: MIDI clock in on and off (and kept), a clock out that isn't open, Link on (or the reason why not) and off.
  - General: a check box and a choice change the engine's settings and are saved (a new instance loads them); Windows-only settings aren't shown.
  - Recent files after save as and open, picked from the menu, cleared; an autosave left over offered, restored (song name, machines, tempo, unsaved), kept on Cancel, deleted on No; the Mac menu bar's model and its `NativeMenu` (app menu, ⌘ gestures, no Quit or Preferences in File and Edit), Preferences from it; Command+, , Shift+Command+A, F2–F4, Ctrl+Tab; the window's size, place and maximized state; About; the Session View's Sync and Controllers menus.
  - Screenshots: `prefs-audio`, `prefs-audio-chosen`, `prefs-audio-open` (CI), `prefs-midi`, `prefs-midi-devices`, `prefs-sync`, `prefs-sync-on`, `prefs-general`, `studio-file-menu`, `studio-autosave-recovered`, `studio-about`, `session-sync-menu`.
- **Not done:** theme colours and editable keyboard shortcuts (§4's row; the shortcuts are listed in Help), WPF's Wavetable directories, MIDI controller and DAW bindings pages, processor affinity, the CPU monitor window; menus of their own for Preferences, About and the machine windows (on a Mac, what the menu bar shows while one of them is in front is to be checked by hand); opening songs from the Finder.

**S8, the MCP server in studio mode** (`Shell/AgentServer.cs`, `Preferences/AgentPage.cs`).
- As ReBuzz.exe's `StudioAgent`: `AgentHost` in "studio" mode on the studio's dispatcher, so the A7 live tools (transport, session launch and record, `midi_play`, meters) play through the studio's audio device. It starts with the engine and listens on 127.0.0.1 (Streamable HTTP, bearer token) when allowed.
- **Preferences ▸ Agent** (Shift+Command+A, Window ▸ Agent connection…): allow agents, the port (7481), the state (listening, or the port in use), the `claude mcp add --transport http rebuzz … --header "Authorization: Bearer …"` line with Copy and New token, and the log folder. The settings are WPF's `AgentSettings` (`BuzzGUI\AgentSettings`); the token is in the settings folder's `settings.json`, the logs in its `agent-logs`.
- The status line shows where it listens, or the call running ("Agent: machine_add…").
- The engine's messages during an agent call become the call's error, not a dialog (`StudioMessages`, as WPF's `UserMessagesViaMessageBox`).
- **The views follow the agent's edits** through the engine's events, as they follow the user's: the machine view (song and machine events), the Session View (`SessionLayout.Changed` and its 50 ms poll), the transport, title and status (the window's 100 ms timer), the Neo editors (their parameters, as undo shows).
- **Test** (`AgentServerTests`): the page sets a free port and allows agents; the line has the URL and token; Copy puts it on the clipboard. A client calls `initialize`, `studio_status` (mode "studio") and `machine_add` over HTTP while the test pumps the UI thread; the machine view then hits the new machine at its box, wired to Master (`agent-machine-added`, `prefs-agent`). A wrong token and, after New token, the old one are refused; undo takes the machine back; turned off, nothing listens.
