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
- **Not ported:** audio clips (1.0: dropping files, the Audio section, warp markers; the studio has no Session Clip Player yet), the MIDI note grid dialog and the Sync and Controllers menus (S7's preferences). Double-click on a clip opens the Pattern tab, as on Windows; until S6 it is a placeholder, and Command/Ctrl+double-click (or Notes…) opens the piano roll.
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

**S2, the generic parameter window and the portable Pedal machines.**
- **The parameter window** (`ReBuzz.Studio/Parameters`) is BuzzGUI.ParameterWindow ported. `StudioMachineUiHost.ParameterWindowContent` makes it, so every machine without its own GUI here shows it (all but S1's Neo editors), from its GUI or its parameters.
  - **Rows:** every state parameter of every group and track, named as on Windows ("a-"/"p-" for an input's amp and pan, "2-Volume" for track 2), the description as tooltip, the slider and the value as the machine describes it.
  - **The slider** is code-drawn, with the WPF control's geometry: drag the thumb; click either side to page by a sixth of the range (repeating while held); the wheel and the arrow keys step by one; a digit opens the value editor.
  - **Presets:** the box applies one (`<default>` too). Presets… adds, updates, deletes and imports them, in the machine's `.prs.xml` beside it in Gear, as on Windows.
  - **Edit mode:** preset B, the randomize and morph functions with their amount, locks, and which parameters show (`ParameterWindowProperties.xml`, now in the settings folder).
  - **The right-click menu**, as on Windows:
    - MIDI: Bind… (learns the controller moved), the controllers named in the MIDI settings, Unbind All;
    - the attributes;
    - Copy Preset (ASCII, XML) and Paste Preset;
    - Unlock All, Invert Locks;
    - Add Track, Delete Last Track;
    - the keyboard playing the machine (MIDI notes), a check item where Windows has "Settings…".
  - **New here:** a track choice, every track (as Windows lists them) or one.
  - **Undo:** every change is a step on the song's undo stack: a slider gesture (wheel and arrow steps merge), a preset, a paste, an edit function, a track count. On Windows, slider moves don't undo, and presets and edit functions undo on the window's own stack.
  - **Not ported:** the "Editor" tab (WPF XAML templates in `ParameterEditors/`, with RangeSlider and RibbonSlider), and a machine's WPF GUI embedded above the rows.
- **The Pedal machines** (`ExternalMachines/portable`, as `Machines/Portable` for Neo): the 19 build plain `net10.0` from their own sources, without their WPF GUIs. `AppGear.targets` puts them in the studio's and rebuzz-headless's Gear, with their preset files.
  - Six sources gained `#if WINDOWS` around WPF code: Comp's and Gate's GUIs; Chord's dialogs and timer; LFO's dispatcher; Tracker's and Peer Control's menu entries; Peer Control's settings window and NAudio MIDI out. Off Windows, Peer Control sends through the engine's MIDI outputs. `machines.json` lists the patches.
  - The Windows builds compile the same text as before (the SDK defines `WINDOWS` for `net10.0-windows` only).
  - About boxes go to the debug console (`PortableMessageBox.cs`).
- **What the checks found:**
  - Every Pedal machine loads, plays and shows its parameters on Linux.
  - **The synths take notes only in their Note column**, not as MIDI: M1, Juno106, SH101, FM, Tracker. So until the pattern editor (S6), neither the Session View's piano roll nor the agent's `notes_write` plays them. The check writes Note columns into the song file.
  - **Chord, LFO and Peer Control** set their targets in WPF dialogs. Off Windows they work with targets saved in a song (made on Windows), and their menu commands say so.
  - **Pedal Plate's reverb diverges at its default Decay** (75), on any platform: its modulated all-pass reads 1–17 samples behind its write position instead of its length behind. That makes it a comb with a gain of up to 5.7 inside the tank's loop. The check runs it at Decay 0. Not fixed here: it would change the Windows build's sound.
- **Tests** (`ParameterWindowTests`):
  - a Neo machine's window, and each Pedal machine's (screenshots `parameters-*.png`);
  - a slider drag changes the value, the undo button restores it, and redo brings it back; three wheel steps undo as one;
  - a digit opens the value editor, and the arrow keys step;
  - track parameters on Pedal Gain Multi: the track choice, one track's change, and Add Track with its undo;
  - a preset loads from the box, and undo restores the values; Presets… saves and deletes one;
  - the menu binds a MIDI controller; copy and paste of the values;
  - edit mode: hiding a parameter, locks, randomize and its undo;
  - the slider's geometry against the WPF control's arithmetic.
- **`tools/headless-pedal.py`** (Linux CI, and the Mac job) drives rebuzz-headless over MCP:
  - each synth plays a note (Tracker plays a wave cut from a render);
  - each effect passes a Neo Poly chord;
  - Chord plays a chord on its target, and LFO moves its target's parameter;
  - the whole song renders with Peer Control in it.
