# Studio MVP (P3) — Plan

| | |
|---|---|
| Status | S0–S9 built 2026-11-03 (§7). The exit test passes headless on Linux and arm64, and the A7 live scenarios pass against the studio app on Linux (Xvfb) and against NeoBuzz Live.app on macOS CI. Left: the user's hand test on a Mac (§5), and P2's hands-on test (mac-engine.md) |
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
2. **No paid signing, decided (2026-11-04).** The Mac app stays free: no Apple Developer ID ($99 a year) and no notarization, now or at release. CI builds it ad-hoc signed, not notarized, as a disk image. The first time it opens, macOS asks for Privacy & Security → Open Anyway (or `xattr -dr com.apple.quarantine`), once per version. That disk image and that step are how the Mac app is distributed.
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
   - Done in S6: the editor machine builds for every platform (a portable build for `rebuzz-headless`, an Avalonia one for the studio), so songs keep their editor data off Windows too. The Session View's piano roll still edits notes in the engine's own columns.
7. **Testing.**
   - Every view renders headless on Linux CI (Avalonia.Headless + Skia) to a PNG uploaded with the run, and takes simulated input in tests.
   - The Mac job builds the app bundle, and starts it headless, once per PR into main.
8. **The dev loop builds locally.** The workspace can't reach NuGet, so a workflow (`dev-nuget-cache.yml`) restores the studio's and the tests' packages on Linux and publishes them as one asset of a prerelease (`dev-nuget-cache`). A local build reads them as its only package source. This is tooling only: the product build restores from NuGet as usual.
9. **The app on the Mac in P3:**
   - CI publishes a self-contained osx-arm64 `NeoBuzz Live.app` (zip) with PortAudio, libsndfile and the Link helper inside, unsigned.
   - It needs no .NET install.
   - Settings live in `~/Library/Application Support/ReBuzz`, as for `rebuzz-headless`.
10. **Edit commands go to the selected view first.** Avalonia's window key bindings see Ctrl/Command shortcuts before the views do. So the shell's Edit menu and its shortcuts (Undo, Redo, Cut, Copy, Paste) go to a focused text box first, as a Mac app's go to the first responder, then to the selected tab's view when it implements `IStudioEditTarget`. A view returns false to pass the command on to the song's undo history. Views don't bind these keys themselves.
11. **Space plays and stops the song, in every view, in both apps.** As in most DAWs, and as the Session View had it; F5, F8 and F7 stay as Buzz has them (fn+F5 on a Mac keyboard). The main window takes Space before the focused control (`Shell/TransportKeys.cs`, a tunnel handler; ReBuzz.exe: its PreviewKeyDown, `AppViews/TransportKeys.cs`), so a focused button or check box doesn't click too. It leaves Space to a text box that can be typed in (the Song Notes editor, the BPM box, the pattern editor's number boxes, the Session View's clip boxes, an editable drop-down; on Windows also Song Info, the machine list's search and password boxes), an open drop-down and a menu. A start waiting for its tick counts as playing (`PlayRequested`), so a second press stops it; a held Space toggles once. Shift+Space on the Pattern tab plays the pattern editor's pattern on its own from the editor's cursor, and stops it, as ReBuzz.exe's Shift+Space does; upstream's plain Space there (the pattern solo) is the transport's now, and stops a pattern playing on its own too. Space is not a macOS menu bar key equivalent, which would reach the menu before a text box: Transport ▸ Play or Stop shows it in its text. One command for both apps and both Session Views (`ReBuzz/Core/TransportCommands.cs`). Tests: `TransportKeysTests` (the studio), `TransportKeysTests` in ReBuzzTests (ReBuzz.exe's decision and the command; its main window isn't built in the tests).
12. **A piano keyboard under every machine that plays notes, in both apps.** One source on the Neo editor kit (`Machines/NeoBuzz.ManifestEditor/Keyboard/MachineKeyboard.cs`, WPF and Avalonia; its toolkit-free layout and drawing in `Core/KeyboardFace.cs`), under the machine's Neo editor, generic parameter window, own GUI (a managed GUI, a native one's `.GUI.dll`) and manifest editor. The hosts wrap the window's content (`MachineKeyboardFrame`) and the window grows by the keyboard's height: the GUI keeps its size, and songs save the windows' sizes without the keyboard, so a song opens its windows at their GUIs' sizes with or without it.
   - **Which machines:** generators that take notes of some kind (`Core/Keyboards/KeyboardRoute.cs`). None for effects, the Master, control machines (flag, or a manifest's type `control`), and generators with no note input (audio inputs, the Session Clip Player, Rout 909's triggers, noise generators).
   - **How it plays them,** as the agent's note tools decide (`ReBuzz/Agent/MachineNotes.cs`), with one thing first: a managed machine that has `MidiNote` takes MIDI whatever an older manifest says (so the Pedal synths switch to MIDI by themselves once they take it). MIDI: note-on and note-off with velocity; when a track of the machine is armed, through the session as that track's keyboard input (`SessionCore.OnMidiInputFor`), so takes, overdubs and Capture MIDI record it, only that machine's tracks; otherwise straight to the machine, as the piano roll's and `midi_play`'s notes go. Tracks: the Note track parameter, a note per track (the lowest free, the oldest taken over when all are busy and given back when a track frees, so a mono synth plays the last key held), its velocity beside it scaled to the velocity parameter's range, an off (255) on release, as `notes_write` writes them; machines of interface 42 or later hear it at once (`SendControlChanges`, as the parameter window sends its moves), older native machines at their next tick, as their parameter windows play them. The manifest's pitch offset makes the written key the pitch heard (FSM Infector: +12). It plays the machine's own tracks and never adds any: a machine with one track plays one note at a time (the strip's tooltip says so).
   - **Feel:** as many octaves as fit the width at about 24 px a white key, two at least (a drum machine's keys about 40 px, from the octave that shows most of its drums); ◀ ▶ move it an octave (the lowest C shown between them; Left and Right with the keys focused); each C named in scientific pitch (C4 = MIDI 60, as the Neo machines and the agent server name pitches); the strike's height is its velocity (12 at the top to 127 at the bottom) and a lit key fills as deep as it was struck; dragging glides from key to key (the new note before the old one's off, so a mono synth slides); keys light up for every note the machine plays, from any source (`Core/Keyboards/NoteMonitor.cs`: the MIDI MachineManager hands it and the Note values its tick sends, 30 times a second while shown, nothing allocated on the audio and MIDI threads); a drum machine's keys carry their drums' names (the machine's `KeyNames`: Neo Drums, Neo Sampler's Kit mode by its zones' samples; else a manifest's `notes.map`), shortened to fit, and the keys that play nothing are dimmed; a screen reader hears each key by name ("C#4", "D2, Snare"). The strip says how the machine gets the notes (MIDI, Note tracks).
   - **Every note ends:** the pointer is captured while pressed, so a release anywhere ends it; leaving the keys ends it; so do the window losing focus, closing or hiding, and the keyboard being hidden.
   - **Shown by default, per machine type:** Preferences ▸ General ▸ Machine keyboard (on) for every machine; a window's ▾ Keyboard toggle, or Ctrl+K (⌘K) in it, shows or hides it for that machine type, kept in the settings (not in songs) and followed by that type's other open windows; a type set back to the default follows the default again. A drum machine with pads hides it once and stays so; synths keep it.
   - **Space** plays and stops the song in machine windows too (the same `TransportKeys` in both apps), so a focused keyboard never takes it.
   - **Small screens:** a GUI of a fixed layout (a Neo editor, a managed GUI, the manifest editor) scrolls above the keys where the screen can't hold it and them (Neo Wave's editor and its keyboard are taller than a 1080 px screen); it is never squashed. Parameter windows scroll their own list; a native GUI scales in its frame as before.
   - **Pictures:** the UI gallery (docs/specs/ui-gallery.md) draws it under every Neo synth's editor and the parameter windows (mac-4xx, mac-450, mac-451; win-600, win-602, win-603), Neo Poly's and Neo Drums' editors on Windows (win-610, win-612), the keyboard playing (mac-470, win-611) and hidden (mac-471).
   - Measured (studio, headless: a click rendered offline and its pitch heard by YIN): Neo Poly's A4 at 440.0 Hz (MIDI 69.00) through MIDI; a tracks-only test synth's (Keys Test) A3 at 220.0 Hz through its Note track, and Pedal SH101's A3 at 220.0 Hz by whichever route it takes. Neo Poly's velocities from a strike at 6 %, 50 % and 95 % of a key: 19, 70, 121 (−29.0, −24.4, −21.3 dBFS). The Windows app (`NeoBuzzLive.exe --keyboard-check`, ReBuzzTests on the package run): Neo Poly's A4 (MIDI) and FSM Infector's written A4 (its Note track, A5 sent for its +12) heard at 440 Hz, within 15 and 25 cents as the tests hold them; the keyboard's notes ending when the window hides, the keyboard hides or the pointer's capture is lost; Space with the keys focused playing and stopping the song.

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

- **Try it on a Mac:** download the disk image from the release *NeoBuzz Live for Mac (latest main)*, drag NeoBuzz Live to Applications, open it (Open Anyway the first time). The feel matters here: scrolling, fonts, Retina, keyboard shortcuts. So does the menu bar.
- **The exit test by hand:** a song from scratch, and an old song of yours. The checklist below.
- **Signing:** decided against (decision 2): no Developer ID, no notarization. The ad-hoc signed disk image plus the one-time Open Anyway step is the distribution.

**The hand test** (about ten minutes, on an Apple-silicon Mac):
1. **Get it.** GitHub → the repo's Releases → *NeoBuzz Live for Mac (latest main)* → `NeoBuzz-Live-macos-arm64.dmg`. Open it and drag NeoBuzz Live onto Applications. (A branch's build: Actions → *Fork Mac* → its run → Artifacts → `ReBuzz-macos-arm64-dmg`, a zip holding the disk image.)
2. **Open it.** Double-click NeoBuzz Live in Applications. macOS says it can't verify it: close the message (not Move to Trash), then System Settings → Privacy & Security → Open Anyway, and confirm. Once per version. (Or in Terminal: `xattr -dr com.apple.quarantine /Applications/ReBuzz.app`.)
3. **Build a chain.** Machines tab: right-click → New ▸ Generators ▸ Neo Poly, New ▸ Effects ▸ Neo Verb. Shift-drag Poly → Verb, Verb → Master. Right-click the Poly → Master arrow → disconnect.
4. **Turn a knob.** Double-click Poly: its editor. Drag F1 cutoff. ⌘Z undoes it.
5. **Type a pattern.** F2. Click the grid, Tab to the Note column, type `z c b q`.
6. **Arrange.** F4. Bar 1 of Poly's track has the pattern already; click bar 2, type `0`. Click bar 1, ⌘B; bar 2, ⌘E: the loop is those two bars.
7. **Play.** Loop on (the transport bar), Space: you hear it, looping. Space again stops. (F5 and F8 do too, fn+F5 and fn+F8 on a Mac keyboard.) Click in the Song Notes editor and press Space: a space is typed and the song plays on.
8. **Save, reopen.** ⇧⌘S (save as `.bmxml`), ⌘N, ⌘O it again, Space: the same.
9. **An old song of yours.** ⌘O a `.bmx` made on Windows: native machines show "missing", Neo and Pedal machines play. ⇧⌘S a copy, reopen it: the same machines, patterns and arrangement.
10. **An agent.** ⇧⌘A: tick Allow agents, Copy the line. Paste it in a terminal (`claude mcp add --transport http neobuzz-live …`). In Claude Code: "What's in my NeoBuzz Live song?" The status line shows `Agent: studio_status…`, and the answer names your machines.
11. **Look and feel.** Fonts and Retina sharpness, trackpad scroll and pinch in the machine view, the menu bar (what it shows with a machine window in front), ⌘ shortcuts.
12. **Song Notes.** F10 (fn+F10 on a Mac keyboard): type a few lines, select a word and ⌘B, the • button on two lines, ⌘K for a link. ⌘Z undoes them one by one. ⌘S, reopen: the notes are there.

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
    - a transport bar: play, stop, record, loop, BPM, position; the master meter, with the clip indicator and the limiting lamp (machine-safety.md §3.3);
    - one tab per view (`StudioViews`, placeholders until S3–S6);
    - a status line: audio device, rate, buffer, dropouts; machines whose guard tripped (machine-safety.md §3.2).
  - **Song files:** new, open, save and save as. The studio asks about unsaved changes itself (async) before the engine acts, so the engine never has to.
  - **Machine windows** (`StudioMachineUiHost`, the engine's `IMachineUiHost`): one parameter window and one GUI window per machine, kept as long as the machine and saved with the song. Their content comes from `ParameterWindowContent` (S2) and `GuiContent` (S1). A machine with no GUI here opens its parameter window. Renaming asks through `Dialogs.AskText`.
  - Arguments: `ReBuzz.Studio [song] [--settings <folder>] [--no-audio] [--smoke]`.
- **`ReBuzz.Studio.Tests`** (Avalonia.Headless.NUnit with Skia):
  - the engine starts with the Neo machines installed and the window shows its views;
  - F5 plays and stop stops;
  - save as, new, then open gives the song back.
  - The window is saved as PNGs, which CI uploads (`studio-screenshots`).
  - The UI gallery for reviews (`GalleryTests`, category `Gallery`, explicit: `dotnet test ReBuzz.Studio.Tests --filter TestCategory=Gallery`): every view, menu, context menu, window and dialog with a demo song, at 1440×900 and scale 2, with an index; ReBuzz.exe's half is `--ui-shots` on the package run. How to regenerate both: [ui-gallery.md](ui-gallery.md).
- **Dev NuGet cache** (§3.8): `dev-nuget-cache.yml` keeps the restored packages as one release asset (`dev-nuget-cache`, a prerelease, not a ReBuzz release). Locally: `dotnet restore <project> --configfile <a nuget.config whose only source is that unpacked folder>`. The test projects also run locally that way.
- **The Mac app:** `tools/mac-bundle.sh` builds `NeoBuzz Live.app` (its executable "NeoBuzz Live", the studio's apphost renamed; the bundle identifier `org.neobuzz.rebuzz`). It is self-contained osx-arm64, with PortAudio, libsndfile (and its libraries) and the Link helper inside, and ad-hoc signed, with ReBuzz's icon. `tools/mac-dmg.sh` packs it into `NeoBuzz-Live-macos-arm64.dmg` (the volume "NeoBuzz Live") with dmgbuild (MIT): a window with the app, an arrow to Applications and how to open an app that isn't notarized (`tools/mac-dmg/`: the settings, and the background with the script that draws it), plus `Read me first.txt`. The Mac job runs the headless view tests on arm64, builds the app and the disk image, checks the image (it verifies, mounts as "NeoBuzz Live" with those items, the app's Info.plist names NeoBuzz Live, the read-me says how to install and open it, and the app copied out keeps its signature), then starts the installed copy for real (`--smoke`, with the runner's audio device) and runs the live MCP scenarios against it, and uploads the image (`ReBuzz-macos-arm64-dmg`). `mac-release.yml` publishes it as the prerelease *ReBuzz for Mac (latest main)* (tag `mac-latest`) when the change reaches main: it reuses the image Fork Mac built for the same tree, so it spends no macOS minutes.

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
  - **Pedal Plate's reverb diverged at its default Decay** (75), on any platform: its modulated all-pass read 1–17 samples behind its write position instead of its length behind. That made it a comb with a gain of up to 5.7 inside the tank's loop. **Fixed** (the user's call; it changes the Windows build's sound too): the all-pass reads its full length behind, modulated by up to 16 samples, and feeds back the same tap. The check runs it at the default Decay, and at full Decay and modulation with a 3 s tail.
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

**S4, the sequence editor** (`ReBuzz.Studio/Views/Sequence`, the Sequence tab). The horizontal editor (ReBuzzGUI/ModernSequenceEditorHorizontal, §3.5) ported by behaviour, in the Default theme's look.
- **Drawing** (`SequenceCanvas`): one code-drawn control.
  - The time ruler: tick numbers per bar, bold where the bar length changes.
  - The track headers: the machine's name, and the M, S and D switches (mute, solo, the sequence's disable).
  - A row per sequence, with its blocks as PatternVisual draws them: 3D box, name strip, the colour, and break, mute, thru and stop as upright labels.
  - A block shows its pattern's contents. With an editor machine, as WPF does: its MIDI events in note lanes, else its columns in detail. Without one (§3.6), the engine's MIDI notes are a small piano roll and its parameter columns are drawn in detail.
  - `SequenceOverlay` draws the layer above: the cursor, the selection, a drag's ghost, the loop start and end and song end markers, and the play position. Playback redraws only this layer.
  - The pure parts are in `SequenceGeometry`: the bar list (`TimeSignatureList`, BuzzGUI.SequenceEditor's, same file format), the selection, tick ↔ pixel, block and edge hit tests, and the resize snapping.
- **Mouse:**
  - A click puts the cursor on a cell. On a block it also selects the block.
  - A drag on a block moves it; Alt or Ctrl/Command copies it. A drag on a block's edge resizes it: the right edge sets the pattern's length, the left edge moves the start and keeps the end (PatternResizeHelper, snap 16).
  - A drag on empty space selects (a rubber band of whole bars and tracks); Shift extends the selection.
  - A double click on a block chooses its pattern for editing. On an empty cell it makes a new pattern there.
  - A click on the ruler moves the play position. Header buttons switch; a double click on a header opens the machine.
  - Right click opens the context menu: Add Track ▸, Delete/Move Track, Set Start, Set End, Set Time Signature..., Insert/Delete (All Tracks), Color ▸, Rename Machine..., Quick Create Patterns ▸, Cut, Copy, Paste.
  - The wheel scrolls; Ctrl/Command + wheel or a pinch zooms around the pointer; the slider zooms (0.25–7.75 px per tick; a right click resets it).
- **Keys** (SequenceEditor.xaml.cs's; Command for Control on a Mac):
  - Arrows, Page Up/Down, Home and End move the cursor by bars and tracks; Shift selects.
  - A pattern's key from the list (0–9, a–z, A–Z) places it. `,` places a break, `-` a mute, `_` a thru, Shift+`,` a stop; `.` clears.
  - Insert and Delete insert or delete a bar (what follows moves); Delete or Backspace clear a selection; Escape drops it. Return chooses the pattern for editing.
  - Ctrl+B and Ctrl+E set the loop start and end (pressed again, the song end). Ctrl+T sets the bar length; Ctrl+I and Ctrl+D insert and delete in all tracks. Ctrl+M and Ctrl+L mute and solo. Ctrl+Up and Ctrl+Down move the track, Ctrl+Delete deletes it, Ctrl+Return opens the menu.
  - The corner holds the add-track button, the first bar length and Follow (playback scrolls the view a page as it leaves it).
- **Edits** (`SequenceEditing`) are undo steps on the song's history, so the shell's Undo and Redo have them.
  - The view is an `IStudioEditTarget`: Undo and Redo return false; Cut, Copy, Paste and Delete act on the selection or on the block under the cursor.
  - The shell binds Ctrl/Command+X, C and V too (in the integration with S7), so they reach the view wherever the focus is in the window. The view's own handling of those keys stays for when it has the focus.
  - The portable BuzzGUI.Common has SetEvent, Clear, SetMarker, CreatePattern and AddSequence. The other actions WPF uses are in its Windows build only (they take `System.Windows.Rect`), so they are ported in `SequenceEdits.cs`: InsertOrDelete, SwapSequences, DeleteSequence, SetLength, the clipboard with cut and paste, and the bar list's actions.
- **Saved with the song** (`SequenceSongState`, one per engine): the bar list, the pattern colours and the zoom. They go in the Windows editors' own subsections (`SequenceEditor`: zoom, bar list, colours; `ModernSequenceEditorHorizontal`: zoom), so songs go both ways.
  - It answers the engine's `PatternColours.Source`, as `SequenceEditorPatternColours` does on Windows, so the Session View shares the colours.
  - A pattern with no colour of its own gets one by its place among its machine's patterns (PatternBoxColors = Pattern).
- **For the pattern editor (S6):** choosing a pattern calls `SetPatternEditorPattern`, which the Pattern tab already follows, and raises `SequenceTab.PatternChosen`. The tab switch is S6's to make. As WPF's AutoSelectPattern does, the cursor's pattern also goes to the editor as the cursor moves.
- **Tests** (`SequenceEditorTests`, in the main window's tab, each step checked against the engine's sequences):
  - Two tracks added from the + menu, patterns placed by key, from the pattern list and by double click, moved and copied by drag, resized at both edges, deleted, copied and pasted over both tracks, cut and pasted, then undone step by step with Ctrl/Command+Z and redone.
  - The typed events (break, mute, thru, stop, clear), Insert and Delete, Shift-selection, moving a track and its undo, and M, S and D.
  - A double click and Return choose the pattern for editing.
  - The shell's Edit ▸ Copy, Paste, Cut, Undo and Redo reach the view.
  - Loop markers by keys and the menu, with undo; a ruler click; playback through the engine's ticks (a `WorkManager`, as ReBuzzTests does): the play marker follows, loops back, and Follow scrolls the view.
  - Save as, new and open keep the events, loop, bar list, a colour and the zoom; an old Buzz song (FSM Infector demo) shows its arrangement, which a save to .bmxml and reopen keep.
  - Pure tests of the bar list (snap, split, insert, delete, file format), the resize snapping, selection rectangles and edge hit tests.
  - Screenshots: `sequence-1-tracks` to `sequence-15-old-song`.
- **Differs from WPF:**
  - A drag on a block moves it right away; WPF moves it only after the button is held for half a second, and a plain drag selects.
  - A vertical drag doesn't swap tracks (WPF's does); Ctrl+Up/Down moves tracks.
  - The edges resize without holding Ctrl.
  - A click on a block selects it, and Delete clears a selection.
  - Copy isn't an undo step. Paste goes to the cursor's track (WPF: only onto the tracks it was copied from).
  - The ruler shows bars past the song end; faint bar lines run through the tracks. Preview lanes fit the area below the name (WPF clips the lowest).
  - The cursor doesn't blink.
- **Not ported:** VU meters in the headers and their target menu, MIDI export, the settings window (the WPF defaults are used), click-to-play patterns (Shift/Ctrl+click, off by default in WPF), the pattern list's hover preview window, the session-override shading.

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
  - Undo and Redo come from the shell (§3.10): the window's Ctrl/Command+Z and Y reach the page first. The editor's other shortcuts, Ctrl+X, C, V, A, U and the rest, reach the grid as keys.
- **The Pattern tab** (`Views/PatternView.cs`):
  - It shows the editor for a chosen machine and pattern, with a machine chooser, a pattern chooser and New pattern. It follows the engine when the pattern editor's pattern (`SetPatternEditorPattern`) or the selected machine changes, and comes to the front when another view sets `ActiveView` to `PatternView` (the Session View's double-clicked clip).
  - The editor's Return (WPF: to the sequence editor) selects the Sequence tab.
  - **For other views:** `PatternView.Edit(IPattern)` and `Edit(IMachine)` show a pattern or a machine's patterns and bring the tab to the front, `PatternView.Show()` brings it to the front alone, and `PatternShown` says what it shows.
  - **For the shell's Edit menu:** the page is an `IStudioEditTarget`. Its `Undo`, `Redo`, `Cut`, `Copy` and `Paste` reach the editor's own undo stack and clipboard (its `IEditContext`). Undo and Redo return false when the editor has nothing to undo or redo, so the song's undo history takes them.
- **Tests** (`PatternEditorTests`, headless):
  - notes, a note off, and global and word values typed with the keyboard are the engine's pattern events and the editor's data (format 255);
  - period clears, Delete moves rows up;
  - transpose by a semitone and an octave, copy and paste, linear interpolation with the WPF editor's values;
  - undo and redo with Ctrl+Z and Y (the shell's, through the page) on the editor's own stack, without touching the song's; with nothing to undo in the editor, the page passes Undo on; the Edit menu's Copy reaches the editor;
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

**S7, Preferences and the rest of the shell** (`ReBuzz.Studio/Shell`, `Shell/Preferences`).
- **Preferences** (Command+, on a Mac, Edit ▸ Preferences… elsewhere): ReBuzz.exe's Preferences and Settings windows in one, a page each. Choices apply and are saved as they are made (no OK or Cancel, as macOS settings windows do).
  - *Audio:* the output device from the PortAudio factory ("PortAudio: …"; a saved device that's gone shows as not connected), the input, sample rate and buffer (the registry's PortAudio section, which `PortAudioDriver` reads). Each choice reopens the device: a new device through `SelectedAudioDriver`, as WPF's toolbar; the same one closed and opened again. Render threads and the work algorithm as WPF's Preferences ▸ Engine has them, with the engine's defaults (4 and Recursive Tasks; WPF's window shows 2 and Threads when nothing is saved). "Restart audio" (and F12, WPF's Reset Audio) reopens it. *Safety* ([machine-safety.md](machine-safety.md) §3.6): the safety limiter on the output (off asks first), its ceiling, the clip indicator's threshold, and the guard on every machine (off for this song only, after asking).
  - *MIDI:* the backend's inputs and outputs (CoreMIDI on macOS; none on Linux) and the remembered ones that aren't connected, checked to open them, with their state (open, not connected, didn't open). A change saves the device by name and opens the inputs (or outputs) again as WPF's OK does. The page follows plugging (polled each second while shown; the engine's watcher opens remembered devices). Below them, `MIDISettings`.
  - *Sync:* Ableton Link (with its peers and tempo), start/stop with peers, follow MIDI clock in, and MIDI clock out per open output (and the chosen ones not open). `SessionSync` keeps them.
  - *Agent:* S8.
  - *General:* `EngineSettings` and `GeneralSettings` from their `[BuzzSetting]`s, as WPF's Settings window lists them: a check box for on/off, a choice of the options (the default in bold), text otherwise; names in sentence case, the description under each. The ones only ReBuzz.exe reads aren't shown (WPF rendering and DPI, the sequence editor type, the piano keyboard, the Windows fill thread). Upstream's update check is no setting at all any more (FORK.md: no update check).
- **The Session View's Sync and Controllers menus** (⚙), as WPF's, which S5 left out.
- **Menus** (`AppMenu`): one model, shown as the macOS menu bar (`NativeMenu`) on a Mac and as the window's menu elsewhere.
  - The app menu: About NeoBuzz Live, Preferences… ⌘,; Avalonia adds Services, Hide NeoBuzz Live, Show All and Quit NeoBuzz Live ⌘Q (`Application.Name`) (Quit asks about unsaved changes through the window's closing).
  - File (New, Open…, Open Recent ▸, Save, Save As…; Exit elsewhere), Edit (Undo, Redo, Cut, Copy, Paste; Preferences elsewhere), Transport (Play or Stop Space, Play F5, Stop F8, Record F7, Restart audio F12; Space is the window's key, not a menu bar key equivalent, so the menu bar shows it in the item's text: §3.11), View (the tabs: Pattern F2, Machines F3, Sequence F4, Session Ctrl+Tab and back), Window (Minimize, Zoom, Agent connection… ⇧⌘A), Help (Keyboard shortcuts…; About elsewhere).
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
- **Preferences ▸ Agent** (Shift+Command+A, Window ▸ Agent connection…): allow agents, the port (7481), the state (listening, or the port in use), the `claude mcp add --transport http neobuzz-live … --header "Authorization: Bearer …"` line with Copy and New token, and the log folder. The settings are WPF's `AgentSettings` (`BuzzGUI\AgentSettings`); the token is in the settings folder's `settings.json`, the logs in its `agent-logs`.
- The status line shows where it listens, or the call running ("Agent: machine_add…").
- The engine's messages during an agent call become the call's error, not a dialog (`StudioMessages`, as WPF's `UserMessagesViaMessageBox`).
- **The views follow the agent's edits** through the engine's events, as they follow the user's: the machine view (song and machine events), the Session View (`SessionLayout.Changed` and its 50 ms poll), the transport, title and status (the window's 100 ms timer), the Neo editors (their parameters, as undo shows).
- **Test** (`AgentServerTests`): the page sets a free port and allows agents; the line has the URL and token; Copy puts it on the clipboard. A client calls `initialize`, `studio_status` (mode "studio") and `machine_add` over HTTP while the test pumps the UI thread; the machine view then hits the new machine at its box, wired to Master (`agent-machine-added`, `prefs-agent`). A wrong token and, after New token, the old one are refused; undo takes the machine back; turned off, nothing listens.

**S9, the exit test** (`ReBuzz.Studio.Tests/ExitTests.cs`; `tools/headless-live.py`, `tools/song-roundtrip.py`; the start-up option in `Program.cs` and `Shell/AgentServer.cs`).
- **A song from scratch, by the UI alone** (headless, on Linux and arm64), with simulated mouse and keys, each step checked against the engine:
  - *Machine view:* from a new song, the gear menu adds Neo Poly, Pedal SH101, Pedal Chorus and Neo Verb. Master is dragged right. Shift-drags wire the synths → Chorus → Verb → Master, and the amp control takes the synths' direct wires to Master away. It sets SH101's wire to −12.2 dB and pans Verb's.
  - *Machine windows:* a double click opens Neo Poly's editor, where a drag turns F1 cutoff (that parameter alone, one undo step), and SH101's parameter window, where a slider drag lowers its Cutoff.
  - *Pattern tab* (F2): the drop-downs, clicked, pick each synth and pattern; New pattern makes Poly's 01; notes are typed after a click on the grid and Tab (SH101's in its Note column, the only way it plays).
  - *Sequence tab* (F4): pattern keys place them; ⌘/Ctrl+B and E set the loop (16–32) and, pressed again, the song end (32). The BPM box takes 132; Loop goes on.
  - *Play:* F5, the engine's ticks rendered offline (a WorkManager, as S6's test), 48 ticks: the position goes back at the loop's end. The Master is heard (peak −14 dBFS), every sample finite, under +12 dBFS. F8 stops.
  - *Session View* (Ctrl+Tab): a slot's menu assigns SH101's 00, its ▶ launches it, it plays; Stop all, Back to arrangement.
  - *Saved and reopened:* ⇧⌘/Ctrl+Shift+S as BMXML and BMX, ⌘/Ctrl+N, ⌘/Ctrl+O each. The same machines (gear, place, tracks, data, every state parameter), wires (amp, pan), patterns (the editor's events and the engine's columns), pattern editors' data, sequences, markers, tempo and session slots. The same play renders bit-identical samples.
  - Screenshots: `exit-1-empty` to `exit-11-reopened-bmx`.
- **Old songs** (the same test class): the 37 Buzz songs in the repository (the machines' demos and bmxplay's), made on Windows, open through File ▸ Open.
  - 35 open. Every native machine is a placeholder ("missing"); the machine view, the sequence editor, the Pattern tab (F2: the pattern it names in the Modern Pattern Editor, the old patterns converted) and the Session View show them.
  - Each is saved as BMX and BMXML, reopened and saved again. `song-roundtrip.py --compare`, the tool's own checks on saves made elsewhere, finds the two saves the same and nothing lost.
  - Two (FSM Kick's, Makk M4's) have no PARA section and don't open, as on Windows without those machines.
  - None of them has pattern editor data (they predate it). `--windows-style` writes Modern Pattern Editor data, in the editor's format, into the richest one; it opens in the studio and saves byte for byte both ways (`--check-windows-style`).
  - Screenshots: `exit-old-machines`, `-sequence`, `-pattern`, `-session`, `-windows-style`.
- **The MCP server on the real app.**
  - `ReBuzz.Studio --agent-port <port> [--agent-token <token>]` allows agents for one run, on that port with that token (else the saved one), whatever Preferences ▸ Agent says, and saves nothing. The page shows it; a change there ends it. Tested headless too.
  - `tools/headless-live.py` gained Streamable HTTP. `--studio <app>` starts the app with audio, a settings folder of its own and a fresh token, waits for its server, runs the scenarios and asks it to quit (SIGTERM: it has unsaved changes). `--url <url> --token <token>` drives one already running.
  - New scenarios, for rebuzz-headless too: a Session View clip launched, heard and stopped; a two-bar take recording what midi_play plays into it on the bar. With the old ones (midi_play, meters_read, the transport in real time, four synths, a render with the device open), that is A7's live set.
  - CI: Linux runs it on Xvfb with the null sink; the Mac job runs it against `NeoBuzz Live.app` after the smoke start, with `--expect-workgroup`.
- **Found:**
  - **BMXML songs replay their stateless values when they open** (the engine, Windows too). `BMXMLFile.CopyParameters` gives each machine the last saved value of every parameter, notes included; BMX's loader (`BMXFile.CopyParameters`) gives state parameters only, as Buzz does. So a song saved after playing opens with its last note sounding until a note-off comes. The fix is one condition there (state, not a note, not the input group), but it changes Windows' engine, so it is left to the user. The test sets those values to "no value" before saving.
  - A machine's pattern editor without patterns (Master's, made when Master is selected) isn't in a BMX file, which links editors through patterns; BMXML keeps it. Nothing is lost.
  - **Fixed in the studio:** after a song opened, F2 showed the Pattern tab naming a pattern above an empty page, until another pattern was picked. The loader sets several patterns and machines in a row, and the last editor it hands over can be another machine's, or none. Now the page follows the engine's pattern when its update runs, and asks the engine for that pattern's editor when another one, or none, is showing (`PatternPage.ShowPatternsEditor`; only the newest page asks, so two windows' pages can't take it from each other forever).
- **Not done:** the hand test (§5). No real Windows song with pattern editor data was available (the Windows-style one is built from the editor's format code).

**Song Notes** (machine-safety.md §3.5, as built in §3.7; `ReBuzz.Studio/Views/Notes`, the "Song Notes" tab, after Pattern). Instead of porting the Info machine (WDE's WPF rich-text notes machine), the song's notes have a tab of their own.
- **The tab:** a toolbar (B, I, U, S; H, bulleted and numbered lists; Link), the editor on the left and the notes as they read on the right, with a splitter between. F10 shows it, as Buzz's Info view; it is in View too.
  - The notes are Markdown (`ReBuzz/Core/Notes/NotesMarkdown.cs`): the toolbar and ⌘/Ctrl+B, I, U and K put the marks around the selection (or the word at the caret) and take them off again; H cycles a line through three heading sizes and back; the list buttons turn the selected lines into items and back; Link makes `[selection](https://)` with the address selected to type over.
  - The page draws paragraphs, headings, nested lists, quotes, rules, colours (lightened to read on the dark page) and links, which open on a click (in the browser). Its text can be selected and copied (Edit ▸ Copy).
  - Opening a song that brought notes in (an Info machine's text, the song's info text) shows a band naming them, with Dismiss.
- **Undo:** the editor is a `SongHistoryTextBox`: its edits are steps on the song's history, not its own. The shell's Undo and Redo (keys and Edit menu) go to the song for it, as for the views (§3.10); Cut, Copy and Paste stay the box's. A run of typing is one step (until a pause of 1.5 s, a new line, or another step); a format is one. An agent's edit, or an undo, updates the editor and the page.
- **Saved with the song** (the `SongNotes` subsection, both formats) by the engine's `SongNotesStore`, so `rebuzz-headless` and WPF ReBuzz keep the notes too.
- **Tests** (`SongNotesTests`): typing and formats (`song-notes`), undo and redo, save and open in both formats (`song-notes-reopened-bmx`, `-bmxml`), the import of a Windows-made Info song (`song-notes-imported`) saved back unchanged in its core sections, the song's info text, links, and the agent's tools (`song-notes-agent`).

**Test reliability** (`ReBuzz.Studio.Tests`: `HeadlessInput.cs`, `TestWindows.cs`, `WindowLifetimeTests.cs`). The studio's headless tests took 7–17 minutes on Linux CI, and their double clicks came apart there (Fork CI 41789849518: three tests; 41820959763: one). Measured per test, in order: its time, memory after a full collection, windows and controls still alive, the dispatcher's timers, and what held a closed window.
- **Closed windows stayed alive and kept working** (app bugs, fixed):
  - The Pattern tab's page subscribed to the engine as it was made and let go only if its tab had been shown before its window closed; Song Notes' view, only once shown and hidden again. So nearly every main window a test opened stayed, held by `PatternView.Page` and `SongNotesStore.Changed`, with its views following the engine. The page now follows the engine while it is in its window's logical tree (which a closed window leaves); Song Notes listens while it is shown.
  - The portable Modern Pattern Editor never let go of the engine's song (`MachineAdded`), and a released editor took the song again on the next machine added: every editor made in a run stayed, with its view (about 900 handlers on `ReBuzzCore.PropertyChanged` and 38,000 controls after the old-songs test; 500–640 MB by mid-run). It lets go in `Release`. (WPF's `PatternEditor.xaml.cs` has the same upstream code; it is unchanged.)
  - `WindowLifetimeTests`: a closed main window and its views are released, tabs shown or not, and the studio's subscriptions on the engine are as before; a new song releases the last one's editor and its view.
- **Fixtures that leaked:** a test that closed a main window on a song with changes left it, and its "Save changes?", open for the rest of the run, drawn with every input after (18 by the safety tests, 14 of them from the pattern editor's tests). Those tests drop the changes first now, and `TestWindows.cs` closes whatever a test leaves open, after it, and names it in the log. The menus' hover delay, put off for an hour, left a timer holding each hovered menu item and its window.
- **No wall clock in the input** (`HeadlessInput.cs`). Avalonia counts a double click's second press by the clock (within 500 ms of the first), and a press and release of a studio window took longer on a loaded runner. The test app replaces the headless platform's settings so that every mouse press is a first click except `MachineViewTests.DoubleClick`'s second, however long the first took; the presses still go through the mouse device (hit testing, capture, gestures). Avalonia has no public way to do it (the platform binds its settings after `AppBuilder.With`'s), so the test project uses `AvaloniaLocator` through `AvaloniaAccessUnstablePrivateApis`. The menus' hover delay is zero (a hover does at once what it does once the user waits) and tooltips are off in test windows. The Neo knob's double-click reset is now always checked (it was skipped when two clicks came too far apart), a drag no longer sleeps 600 ms first, the audio device's CPU load is measured until it shows rather than after two fixed sleeps, and a saved song's title is read once the main window refreshes (it was read whenever the window's 100 ms timer happened to have ticked: quicker, the tests saw "Untitled" on a Mac).
- **Before and after** (the studio test step's own total, from the CI logs): Linux 16.9 and 6.8 minutes (ab58d65, 7142dbc; four and one tests failed) → 1.6 (all 131 passed); macOS arm64 2.4 → 1.3. On Linux CI the slowest test went from 161 s (typing Song Notes) to 1 s, the sequence editor's keys from 73 s to 1 s, and the old-songs exit test (37 songs opened, saved and reopened, real work) from 63–68 s to 28 s. Locally (two cores), the first 90 tests took 659 s and take 108 s, and the run stays under 150 MB where it grew past 600 MB.
- **Main's red runs** (Fork CI on main 41926371493, 41938676130, 41953730788; branches 41937966540, 41866682998's first attempt): two tests failed, always together, in one process.
  - *The first play compiled on the audio thread* (`AudioHygieneTests`): Neo Multiband's `ComplexFft.Transform`, `Partition`, `Dynamics` and `LinearSplit.Process`, at tier 5, on-stack replacements (in the play twice, the four of them; in the stop once, the transform alone). The warm-up compiles them at tier 0. A tier-0 method whose loops run about a thousand times in one call is replaced while it runs, by code compiled on the thread running it: after ten such calls (here in the test's stopped buffers, where the machine plays its latency out on silence) and again once the runtime has given it a profiling tier, which it does on its own clock (call counting starts 100 ms after the process last called a method for the first time; 30 calls later the background compiler instruments it). Whether that fell inside the play's 250 buffers was chance. With the delay at 0 (`DOTNET_TC_CallCountingDelayMs=0`) it does every time: 3 of 3 runs before the fix failed, on CI's very method and tier, and the exit test after it. The audio path's long loops are `AggressiveOptimization` now: compiled fully optimised by the warm-up, never replaced. The null-test renders read with `DOTNET_JitDisasmSummary` named them: Neo Multiband's, Verb's, EQ's (the graphic bank and its solver too), Comp's, Drums', Drive's oversampler, Analyzer's spectrum, Scope's sweep start, Verb's guard, the sync clock's line fit and a wave layer's copy (Neo Sampler reads the song's waves in `Work`). Since, none of the 85 renders replaces an audio-path method, and all are bit-identical to before. The test builds the engine in Debug, which never tiers, so it couldn't see the engine's two.
  - *The exit test's Loop* (`ExitTests`): Looping was on before its click (its diagnostics), left there by the failed first play. Avalonia.Headless.NUnit 12.1.4 skips a failed `[AvaloniaTest]`'s TearDown (a probe test showed it; a plain `[Test]`'s runs), and the hygiene tests put Looping back there. They put it back in their finally now (`NewSong`), and the exit test starts with looping off, as the app does. No key reached a shortcut: Looping set on before the test gives the same diagnostics. (Run 41644026501's was the session rig's, before it put Looping back.)
  - Main's push runs failed more often than the branches' (since Neo Scope joined the first play: 3 of 4, against 1 of 10), but nothing differs between a push and a dispatch in the workflow, and the counts are small (p ≈ 0.04): the same race on both.
  - Locally (two cores, two more busy loops): both tests 20 times each, all passed (and 10 more with the delay at 0); before the fix, 6 of 20 runs failed both, as CI did: the four on the audio thread in the second first play (or its stop), then Loop on.
  - *Neo Scope's editor said nothing where it had locked* (`NeoScopeStandsALoopStillLocksOnASineAndItsControlsWork`, Fork Mac 41966698814: Status null, not "Trig'd"): the editor copied the engine's snapshot at most every 10 ms of wall clock, and on Apple silicon the test's 0.7 s of offline audio between two frames took less, so the second frame drew the synced pass's snapshot (with a 300 ms throttle it fails so here every time). It copies when the engine has published a new snapshot now, once a frame for its graphs as before. Neo Analyzer's editor had the same 10 ms throttle and copies so too now; `TheAnalysisEditorsDrawTheNewestSnapshotAfterEveryRender` checks both editors: a frame of one graph, 50 ms of offline audio (4 to 6 ms of wall clock here), another frame, eight times, each frame the engine's newest snapshot (with the old throttle back, 4 of the analyzer's 8 frames and 5 of the scope's drew the one before). The frame-cost helpers' 11 ms sleeps, there for the throttle, are gone.
  - No `[TearDown]` puts global state back any more: `ExitTests`, `PreferencesTests`, `SafetyTests`, `SongNotesTests` and `StudioShellTests` had one. Each of their tests starts with `using var restore = Studio.AfterTest(PutBack);` (the fixture's old TearDown method), which runs it when the test ends, failed or not, after the test's own finally blocks, as the TearDown ran. A probe fixture (a failing test, a passing one, then a count) showed the TearDown run once, for the passing test only, and `AfterTest` twice.
- Left as they are: deadlines on waits for other threads (the agent's HTTP calls, the clipboard, a machine preparing in the background: 5–60 s, failing only on a hang), the JIT listener's settling sleeps in `AudioHygieneTests` (a slow runner could only hide a compilation, not fail the test), and the audio signal's own timer test.
