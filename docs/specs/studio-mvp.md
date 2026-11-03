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
  - Keys: Delete (Backspace on a Mac), and Ctrl/Command + A, D, U, M.
  - Zoom: Ctrl/Command + wheel around the pointer, or the top bar's slider (-8..8, ×2^(level/8)). A middle double click resets.
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
  - Ctrl/Command + wheel zooms and the plain wheel scrolls (WPF's CtrlMouseWheel setting), for trackpads.
  - Mute, solo, bypass and amp/pan undo.
  - A machine added from the menu is selected.
- **Not ported:**
  - the sidebar (machine list, parameters, MDB, templates, MIDI bindings);
  - machine groups (grouped machines are drawn at their own positions);
  - cut, copy, paste, clone and templates; quick-new by typing; rotate (Alt-drag); screen-edge disconnect;
  - curved wires, multi-channel plugs and their channel menus, wireless outputs, VU meters on wires, skins;
  - the CPU monitor, HD recorder, signal analysis, latency, attributes and machine commands.
