# UI gallery — screenshots of both apps for UI/UX reviews

| | |
|---|---|
| Status | Built 2026-11-06 (branch `ui/screenshots`) |
| Scope | A complete, regenerable screenshot set of NeoBuzz Live's Mac app (the studio, `ReBuzz.Studio`, Avalonia) and its Windows app (`NeoBuzzLive.exe`, WPF) with one demo song, for reviews of the UI |

## 1. What it is

Two harnesses draw every view, menu, context menu, window and dialog that can be reached without a person, with the same demo song loaded, and write an index beside the pictures: what each shows, how a user gets there, the window's size and scale, notes on the capture.

| | Mac app (the studio) | Windows app (NeoBuzzLive.exe) |
|---|---|---|
| Harness | `ReBuzz.Studio.Tests/GalleryTests.cs`, NUnit category `Gallery`, `[Explicit]` | `NeoBuzzLive.exe --ui-shots <folder>` (`ReBuzz/AppViews/MainWindow.UiShots.cs`), run by `ReBuzzTests/UiGalleryTests.cs` |
| Where it runs | Anywhere the studio's headless tests run (Linux, macOS), on demand | Every package run (Windows), as the other WPF screenshots |
| Drawn | Avalonia.Headless with Skia, 1440×900 points at scale 2 (2880×1800 px, a Retina Mac) | The real app, started as people start it; the window's client area by RenderTargetBitmap, 1440×900, 96 dpi |
| Output | `mac-NNN-*.png`, `index-mac.md` | `win-NNN-*.png`, `index-windows.md`, on the `ci-artifacts` branch |
| Normal CI cost | None: explicit tests run only when a filter names them | About a minute of the package run's tests |

The demo song is one builder for both, `ReBuzz/AppViews/GallerySong.cs` (compiled into the Windows app, linked into the studio's tests), engine APIs only: *Night Drive*, 124 BPM. Neo Drums, Neo Poly (Bass), Neo FM6 (Lead) and Neo Wave (Pad) through Neo Drive, Neo Comp (Drum Bus), Neo Delay, Neo Verb (a send and a Session return), Neo EQ and Neo Limiter to the Master; FSM Infector through Jeskola Delay where Gear has those natives (Windows). Nine patterns with notes, most moved onto the Pattern View's MIDI note lanes (Lead's Hook stays in its clip, for the piano roll); a 16-bar arrangement; five Session scenes (Intro, Verse, Drop at 128 BPM, Break, Fill) with clip, track and scene colours; song notes in Markdown. The galleries save it as `Night Drive.bmxml`, so the title shows a named song.

## 2. Regenerating

**Mac app** (any Linux or macOS machine with the .NET 10 SDK; nothing else):

```
dotnet test ReBuzz.Studio.Tests -c Release --filter TestCategory=Gallery
```

The pictures and `index-mac.md` go to `$GALLERY_DIR` if set, else `ReBuzz.Studio.Tests/bin/Release/net10.0/screenshots/gallery/`. It takes about 12 minutes on two cores. Each section is its own test (Views, Menus, ContextMenus, WindowsAndDialogs, MachineWindows, SongsAndStates); a shot that fails is listed under *Not captured* in the index and the test fails at its end, after taking the rest. On a Mac the same command draws with macOS's fonts and ⌘ in the shortcuts.

**Windows app**: a package run draws it (`UiGalleryTests` among ReBuzzTests); a quick run is enough, except for the Pedal machines' parameter window (a full run: quick builds leave the Pedal set out). The step *Publish screenshots* force-pushes every `screenshots/*.png` and `*.md` to the `ci-artifacts` branch:

```
echo '{"ref":"<branch>","inputs":{"quick":"true","test_filter":"FullyQualifiedName~UiGalleryTests","profile_only":"Neo Utility"}}' | gh api -X POST repos/khuzul2/neo-buzz/actions/workflows/fork-package.yml/dispatches --input -
git fetch origin ci-artifacts && git show origin/ci-artifacts:README.md     # names the run that wrote it
git archive origin/ci-artifacts | tar -x -C <folder>
```

Every package run of every branch overwrites `ci-artifacts`: fetch it as soon as the run ends and check that its README names the run. By hand on Windows: `NeoBuzzLive.exe --ui-shots C:\shots` (the app starts, draws, and quits by itself in a minute or two; don't touch the mouse meanwhile).

## 3. What each harness does, and its limits

**Mac (headless).**
- The main window is drawn as macOS has it: no in-window menu. The menu shots (1xx) draw the Mac menu model (`MainWindow.MenuModel(mac: true)` and the app menu, `AppMenuModel`) as an in-window Avalonia menu, because headless can't draw the macOS menu bar (the app menu is the studio's whole: About, Preferences…, Services, Hide, Hide Others, Show All, Quit). The real menu bar is in Fork Mac's `gallery` job (`tools/ui-gallery-mac.sh`): each menu, Preferences as it opens and a second later, and the menus while Preferences is the key window (`mac-real-*`; the app's log beside them says when Preferences was made, drawn and filled). macOS shows Preferences… as "Settings…" on macOS 13 and later.
- Dialogs are windows of their own: each is drawn over the main window, centred, with a shadow (the harness's composite), from inside its modal frame where it has one (`DialogOnOpen`).
- No window frame or title bar; text is in the headless platform's fonts, greyscale-antialiased; shortcuts that the code spells by platform (`OperatingSystem.IsMacOS()`) show Ctrl off a Mac.
- No audio device: the engine is run offline (a WorkManager filling buffers) for the playing and recording states; the Audio page's device lists are injected. Native (Windows) machines don't load: an old Windows song shows their placeholders.
- Tooltips are off in the headless tests (HeadlessInput), except where a shot turns them on.

**Windows (NeoBuzzLive.exe).**
- The app starts normally (`--ui-shots` changes only: no autosave recovery question, no update check, message boxes go to stderr instead of waiting for a click), builds the demo song, draws, writes `index-windows.md` and `done.txt`, and kills itself.
- The window's client area is drawn (no Windows title bar). Popups (menus, context menus) are drawn over it at the place Windows gave them (`PopupsOver`); a window on another UI thread (the debug console) is drawn on its thread, without popups.
- Windows limits a window to its screen's size; the runner's screen may be smaller than 1440×900, so the gallery lets the window be larger (WM_GETMINMAXINFO). A popup near a small screen's edge is moved by Windows, as it would be for a user with that screen.
- The runner has no audio device: the playing state is run offline, as on the Mac.
- A native machine's own GUI is a Win32 window in the native host process; RenderTargetBitmap can't draw it, so it isn't in the gallery.

## 4. What it found, fixed

The first gallery's capture (2026-11-06) found these, each fixed on `fix/ui-gallery-bugs` with a regression test: WPF View ▸ Settings… threw on the Agent page's free-text Port (a text box now; win-43x); the real Mac's app menu was Avalonia's "About Avalonia", the app's About and Preferences never reached the menu bar, a key Preferences window left the app menu alone in it, and Preferences was blank 2 s after ⌘, (the app menu set before Avalonia's exporter starts, every window given the menus, Preferences drawn before it lists the devices; mac-real-100, -200…-204); WPF's Hard Disk Recorder threw with no audio device (it says there's nothing to record; win-701); switching the WPF theme with a song open threw in the Machine View (wires of machines it doesn't show are left out; win-30x, switched with the demo song open); notes moved onto a pattern's note lanes before it was shown appeared only after the song was opened again (both apps; the galleries no longer open it again); the Mac Beats box cut "32" to "3:" and Pattern Properties' column list was empty (mac-053, mac-056). The UX review that followed added: the View menu's key equivalents, Hide Others on ⌥⌘H, "Quit NeoBuzz Live", Minimize on ⌘M (mute on M in the Machine View on a Mac); the main window kept within the screen in both apps; dangerous questions naming their actions with the safe answer on Return and Escape (mac-321, -332, -337); Preferences pages that scroll.

## 5. Changing it

Add a shot where its section is, with the next free number in its hundred (the numbers order the gallery: 0xx views and states, 1xx menus, 2xx context menus, 3xx windows and dialogs (Mac) or themes (Windows), 4xx machine windows (Mac) or Preferences and Settings (Windows), 5xx songs and states (Mac) or About and dialogs (Windows), 6xx parameter windows, 7xx tool windows). Each shot gives what it shows and how a user reaches it; the index is written from them. Content the shots need belongs in `GallerySong.cs`, so both apps keep showing the same song.
