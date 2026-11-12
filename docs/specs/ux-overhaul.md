# UX overhaul: one modern, intuitive studio

| | |
|---|---|
| Status | Plan written 2026-11-06 (P0). P1 (quick wins) on `ux/overhaul-1-quick-wins` |
| Scope | The whole UX overhaul the UI/UX review of 2026-11-06 proposes: quick fixes, the big bets, then the polish and the review's "Later" items, in the Avalonia studio (`ReBuzz.Studio`, today the Mac app), which then becomes the Windows app too (P6). WPF (`ReBuzz.exe`) gets bug fixes only and stays as "Classic" until the studio reaches parity |
| Source | The review (a Claude Doc: tabs Review, Mac app screens, Windows app screens, Machine editors) and the gallery it judged (`ui-gallery.md`, captured at `83fb0b7`) |
| Goal | The owner's words: "as modern and as intuitive a UX as possible" |
| Related | studio-mvp.md (the studio), session-view.md (the Session View), machines.md §4 (the Neo editor kit), ui-gallery.md (the screenshots every phase is checked with), cross-platform.md §5.2 (P4 there: Windows on the studio) |

---

## 1. Decisions that bind every phase

1. **One UI.** The studio is overhauled first and then ships on Windows as the main app (§6, P6). WPF gets bug fixes only: no restyling, no new features, until it is retired to "Classic".
2. **Order:** the quick fixes (P1), the foundations they need (P2), the big bets (P3), the polish (P4), the review's "Later" items (P5), Windows (P6). Each phase is a branch from `origin/main`, shippable on its own; a phase splits into smaller PRs wherever a smaller step can merge on its own.
3. **Keep** what the review says works:
   - the Session View's grid and its clip panel;
   - Space plays and stops everywhere (studio-mvp.md §3.11);
   - the honest help text under every setting, and the live "Now" rows;
   - the Neo editors' graphs and their kit (one kit, both apps);
   - the Buzz veterans' keys and power tools: the F-keys as alternates, keyboard note entry, Shift-drag wiring, a parameter list for every machine, hex wave slots, Clone with patterns, Replace, Latency, Signal Analysis, MIDI bindings, panic;
   - the machine guard that mutes a runaway machine;
   - Classic Buzz (the beige canvas) as a theme option.
4. **Songs open and play exactly as before.** No parameter order, range or saved data changes; `tools/null-test.py` stays bit-identical. Settings: new keys only, old keys never reinterpreted. A colour index saved in a song keeps drawing its colour (§3.4).
5. **No third-party product names** in UI text, machine, preset or template names. This spec names benchmarks ("Live's Clip View") only to say where an idea comes from.
6. **Free/MIT dependencies only** (studio-mvp.md §1).
7. **Measured, not described:** WCAG contrast from the pixels of the gallery, sizes in points, frame times on big songs. Every phase reports its figures (§8).

## 2. Target structure (the end state, P3)

```
┌ Top bar ─────────────────────────────────────────────────────────────────────────────────────┐
│ ⏮ ▶ ■ ● ⟲ ♩ | 124 BPM Tap | 9.1.1 · 0:15.4 | 4/4 | L▮▮▮▮▯ R▮▮▮▯▯ −60…0 dB | ● 48 kHz 5 ms | ⚡ Agent │
├ Browser ─┬ Main view: [Arrangement | Session | Machines] ─────────────────────┬ Inspector ─────┤
│ Search   │                                                                 │ follows the    │
│ Instr.   │   the timeline, the clip grid or the machine graph               │ selection:     │
│ Effects  │                                                                 │ machine, track,│
│ Patterns │                                                                 │ clip, pattern, │
│ of the   │                                                                 │ wire, scene.   │
│ selection│                                                                 │ Song tab when  │
│ Presets  │                                                                 │ nothing is:    │
│ Samples  │                                                                 │ notes, tempo…  │
├──────────┴ Detail: [Tracker | Piano roll | Device | Mixer]  ⤢ maximise ─────┴────────────────┤
│ the selected clip or pattern, the selected machine's editor docked (Float), or the mixer     │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

Rules (the review's, taken whole):

1. **One selection everywhere.** Clicking a machine, track, clip or pattern selects that machine and its track in every view; the inspector, the detail panel and the browser follow it. A small `StudioSelection` model (P2) holds it; views publish to it and read from it.
2. **Three main views, not five.** Arrangement (today's Sequence), Session and Machines. Pattern becomes the detail panel's Tracker (maximisable to the whole window, as tracker users expect); Song Notes move to the inspector's Song tab.
3. **Tracker and piano roll are two views of one clip**, docked in the detail panel, not separate windows.
4. **Editors dock** under Detail ▸ Device, with a Float button for a second screen.
5. **One colour per machine**, carried to its track and its clips as their default.
6. **One time unit:** bars.beats (1-based) everywhere; ticks only where precision needs them (the transport's third field, tooltips, Buzz's tick-based dialogs as a secondary line).

Names: one name per thing. "Arrangement" (not Sequence), "Clip" in the Session View and "Pattern" in the tracker (a clip is a pattern placed in a slot), "Track", "Song Notes". P1 keeps the tab labels as they are (the tests and the gallery key on them); P3 renames them with the new view switcher.

## 3. Visual system

### 3.1 Tokens

One token set, dark by default, seeded by the Session View (the one screen already in a shared modern language). P1 added it to the studio and used the state, text and contrast parts; since P2a the table is the editor kit's toolkit-free `Machines/NeoBuzz.Editor/Core/Tokens.cs` (`NeoColor`s and constants, no toolkit), which every build of the kit compiles: the studio's `StudioTokens` gives it to Avalonia, the kit's `Palette` is made from it on Avalonia (the studio's editors), and machine editors read it through the kit. The classic WPF app keeps its own colours (§1: bug fixes only): `Palette` answers with the classic ones there, and so do `Identity(category, classic)`, `Partner(accent, classic)` and `Bands(classic)`.

| Token | Value | Rule | Measured (WCAG) |
|---|---|---|---|
| Surface base / view / panel / control / border | #141416 / #1B1B1E / #242428 / #2E2E34 / #37373F | One ramp for every view and dialog; no white or beige panels inside dark views (Classic is a theme) | – |
| Text primary | #E8E8EC | Body text | 14.1:1 on view, 11.0:1 on control |
| Text secondary | #A0A0AA | Labels, hints | 6.6:1 on view, 5.2:1 on control |
| Text placeholder | #8C8C96 | Placeholders and disabled text, on view and panel surfaces only | 5.2:1 on view, 4.6:1 on panel |
| Accent | #4C8DF6 | Focus rings, selection outlines, the one blue. Never a state | ≥3.6:1 on every surface |
| Selection fill | #1F5FA8 | Lists and text selection, white text on it | 6.4:1 with white |
| Block selection | accent at 35 % over the grid, 1.5 pt accent outline | Pattern editor, sequence, piano roll | ≥3:1 outline |
| State: play | #34C759 | Playing ▶, progress, the playing slot's outline; Play lit (dark glyph) | 7.7:1 on view; 9.5:1 black on it |
| State: queued | play, blinking at 2 Hz | Launched, waiting for its launch point | – |
| State: record | #FF453A | Recording dot, the recording slot, Record lit (white glyph) | 5.0:1 on view; 3.4:1 white on it (a UI part) |
| State: arrangement override | #E8823A | "Back to arrangement", tracks the session has taken | 6.3:1 on view |
| Text on a colour | #FFFFFF or #000000, whichever has more contrast | Clip, track and scene labels on their colour | ≥4.58:1 on any colour |
| State: warning | #FFB340 | Dropouts, a guard off; black text on it | 10.3:1 on base |
| Meter | #7BE495 → #FFC43D (within 6 dB of full scale) → record (over 0 dB) | Level only | – |
| Grid line, control track | #2A2A30, #6E6E7A | Graph and editor grids; a knob's unfilled track | track 3.4:1 on view |
| Identity | one colour per machine category (§3.1.1) | An editor's accent; from P3h its box's, track's and clips' default colour | each ≥5:1 on view |
| Graph series | sky, amber, teal, pink, lime, violet, salmon, orchid (the identities' hues) | Bands and lines of one graph, in this order; a stereo pair's second trace is `Partner(accent)` (sky, or amber beside a blue accent) | ≥ ΔE 30 apart |

**State colours are never offered as clip, track or scene colours.** A palette entry within ΔE (CIE76) 20 of a state colour is left out of the colour menus (in the default palette: Red, Orange, Ochre and Kelly Green); a song that already uses one keeps it (§1.4). A clip keeps its own fill in every state: state is drawn by the ▶/● chip on a dark ground, a 2 pt outline and a 3 pt progress bar, in the state's colour.

#### 3.1.1 Identity colours

One per category of the machine browser (P3d); `Tokens.Identity(category)`. Each is 5:1 or more on a view (it can be text), 37 or more (ΔE) from every other, and 24 or more from the accent and the three state colours (so no green, red or orange accent). A manifest's `type` gives a manifest editor's category (`Tokens.CategoryOf`: "monosynth" → Synth, "reverb" → Delay and reverb, "EQ" and "filter" → EQ and filter, …; other generators Synth, other effects Utility).

| Category | Colour | Machines |
|---|---|---|
| Synth | #F34C82 pink | Neo Poly, Neo FM6, Neo Wave, Dye Essence, Pedal SH101, Juno106, FM, and manifest generators |
| Drums | #EDB235 amber | Neo Drums, drum generators ("drums", "triggered") |
| Sampler | #CFAAF8 lavender | Neo Sampler, Pedal M1 |
| Dynamics | #0BD1B9 teal | Neo Comp, Neo Multiband, Neo Limiter, Neo Clipper, Pedal Comp |
| EQ and filter | #16BCFF sky | Neo Eq, Neo Filter, Pedal EQ, Pedal Filter |
| Delay and reverb | #8777FF violet | Neo Delay, Neo Verb, Pedal Plate |
| Modulation | #E361D3 orchid | Neo Ensemble, Pedal Chorus |
| Distortion | #F8A98D salmon | Neo Drive (Pedal Shaper once its manifest says "distortion") |
| Utility | #A8B4C4 slate | Neo Utility, Neo Scope, Neo Analyzer, Pedal Gain, Pedal Gain Multi, Pedal Drum Matrix ("multi-effect") |
| Control | #D1E181 lime | Pedal Chord, Pedal LFO (Pedal Peer Control once its manifest says "control") |

### 3.2 Type, size, spacing

| | Rule |
|---|---|
| Font | Inter (shipped, `Avalonia.Fonts.Inter`) in the shell and the kit; a monospace (Menlo, Consolas, DejaVu Sans Mono) only for the tracker grid and code |
| Sizes | 13 pt body, 12 pt dense lists, **11 pt minimum** anywhere (12 px on Windows at 100 %); semibold headings; tabular figures for counters and clocks |
| Targets | Controls at least 20×20 pt, hit targets 24×24 pt; menu rows the platform's (22 pt on a Mac) |
| Grid | 8 pt (4 pt half-steps inside controls) for margins, paddings and gaps (P2b sweeps the shell); `Tokens.Grid`, `Tokens.Space(n)`; radii 4, 6, 8 |
| Scale | `Tokens.FontCaption` 11, `FontBody` 13, `FontTitle` 15, `FontHeading` 20 |
| Labels | Never rotated; two lines instead of "…" when a label is wider than its control |
| Contrast floor | Text 4.5:1 (3:1 at 18 pt or 14 pt bold), icons and UI parts 3:1, against the surface they sit on, measured from pixels |

### 3.3 Themes

Dark (default); Light (P4); Classic Buzz, the beige machine canvas and brown arrangement, as an option. Chosen in Preferences ▸ Appearance (P2b), not a top-level menu. A theme is a token table; views never hard-code colours (P2 removes the 133 literal colours the studio has today, outside the code-drawn views' theme hooks).

### 3.4 Colours saved in songs

Clip, track and scene colours are indices into the theme's PatternColors (`SessionColourChoices`), saved in songs. They keep their index and their colour. The overhaul changes only which entries the menus offer (§3.1) and, in P3, the default when a clip has none (its machine's colour, worked out at draw time, never written into the song unless the user picks it).

### 3.5 Note names

MIDI note 60 was "C-5" in the pattern editor, "C3" in the piano roll and "C4" on the machine keyboards. One setting now names notes wherever they are shown: Preferences ▸ General ▸ Note names (`GeneralSettings.NoteNames`, a new key; `BuzzGUI.Common.NoteNames` holds it for the code that can't read settings and for the test projects that link that code). Three namings, by what middle C (MIDI 60) is called:

| Naming (Preferences' label) | MIDI 60 | 61 | 0 | 127 | The pattern editor (three characters) |
|---|---|---|---|---|---|
| Buzz: C-5 = MIDI 60 | C-5 | C#5 | C-0 | G-10 | C-5 |
| **C3 = MIDI 60** (the default) | C3 | C#3 | C-2 | G8 | C-3 (MIDI 0: C--2) |
| Scientific: C4 = MIDI 60 | C4 | C#4 | C-1 | G9 | C-4 |

Read by: the pattern editor (both apps' editor, shared code: its cells, the status bar's value, the base octaves, the octave digit typed: 3 is Buzz's 5 when C3 is 60), the piano roll (both apps), the keyboards under machine windows (the octave, the C labels, the readout, the keys' screen-reader names), MIDI mappings, the piano roll's messages on tracker tracks, the UI's Replace report, Preferences' default base octave, and the classic sequence editor's tooltips. A change redraws every open window. Display only: patterns hold Buzz's values; `BuzzNote.Parse`/`ToString` keep Buzz's names (constants, chord files, and the classic app's wavetable and MIDI controller lists, which parse their text back); the agent server keeps MIDI numbers and scientific names (`mcp-server.md`; its guides now tell it the apps may name notes another way). Machines' own value texts (Neo Sampler's keys, Neo FM6's, Neo Drums', the Pedal machines') stay scientific, since the agent reads them too; the device view (P3e) and the editors (P5) format notes from values with this setting.

## 4. Keymap

One keymap for the whole app, generated into the menus, the shortcuts window and the tooltips from one command model (`Shell/Commands`, P1). ⌘ is Ctrl off a Mac. F-keys stay as Buzz's alternates (on a Mac they need fn, and F3, F4, F7–F12 are media keys there, so they are never the only key).

| Command | Mac | Windows / Linux | Buzz alternate | Where |
|---|---|---|---|---|
| New, Open, Save, Save As | ⌘N, ⌘O, ⌘S, ⇧⌘S | Ctrl+… | | everywhere |
| Close Window (the key window; on the main window: close the song) | ⌘W | Ctrl+W | | everywhere |
| Revert to Saved | – | – | | File |
| Undo, Redo | ⌘Z, ⇧⌘Z | Ctrl+Z, Ctrl+Y | | the selected view first, then the song |
| Cut, Copy, Paste | ⌘X, ⌘C, ⌘V | Ctrl+… | | the selected view |
| **Duplicate** | **⌘D** | **Ctrl+D** | | every view (machines, arrangement blocks, clips, notes) |
| **Delete** | **⌫** (and ⌦) | **Delete** (and Backspace) | | every view; in the Session View it removes the clip from its slot, ⇧⌫ deletes the pattern |
| Select All, Deselect All | ⌘A, ⇧⌘A | Ctrl+A, Ctrl+Shift+A | Esc deselects | every view |
| Rename | ⌘R | Ctrl+R | F2 in the Session View | the selection |
| Settings | ⌘, | Ctrl+, | | |
| Keyboard Shortcuts (searchable) | ⌘/ | Ctrl+/ | | |
| Play or stop | Space | Space | F5 play, F8 stop (twice: silence) | everywhere but text boxes |
| Play the pattern from the cursor | ⇧Space | Shift+Space | | Pattern |
| Record | R | R | F7 | where the view doesn't type letters (below) |
| Loop | C | C | | as Record |
| Metronome | K | K | | as Record |
| Go to start | Home (fn ←) | Home | | as Record |
| Restart audio | – | – | F12 | Transport menu |
| Views (tab order: Arrangement, Session, Machines, Pattern, Song Notes) | ⌘1 … ⌘5 | Ctrl+1 … Ctrl+5 | F4, Ctrl+Tab, F3, F2, F10 | everywhere |
| Show Mixer | ⌥⌘M | Ctrl+Alt+M | | Session |
| Mute, Solo, Bypass the selected machines | M, S, B | M, S, B | Ctrl+M (Windows) | Machines |
| Minimize | ⌘M | – | | Window |
| Show or hide the keyboard under a machine's window | ⌘K | Ctrl+K | | machine windows (for that machine type) |

Single-key transport keys (R, C, K, Home) act where the view doesn't take letters: Machines, Session, Song Notes outside its editor. The Arrangement types pattern letters and the Pattern editor types notes, so there the F-keys and the buttons do it; the shortcuts window says so. They are not macOS key equivalents (those would take the key from text boxes and the tracker), so the menu shows them in the item's text, as it shows Space.

The tracker keeps its own command keys (⌘R randomize, ⌘W rotate, ⌘I interpolate, ⌘T write state, ⌘1–⌘9 subdivision): while it has the focus, Rename, Close and the view keys leave them to it (the window's binding can't run, so the key goes on; on a Mac the menu item chosen by that key gives it to the editor), and the views are on their F-keys there.

Changed from today (P1): ⌘D in Machines was Deselect (now Duplicate; Deselect is ⇧⌘A and Esc); ⌘D in the Arrangement was "Delete (All Tracks)" (now Duplicate; Delete time on all tracks is ⇧⌘⌫); ⌫ in the Session View stopped the track (now removes the clip from its slot, as Delete did; a track stops with its empty slot's ■, the track menu, or Enter on an empty slot); ⇧⌘A was Window ▸ Agent connection… (now Deselect All; the agent page has no key, it is Preferences ▸ Agent and the Window menu).

Every view's own keys (tracker note entry, Session scene digits, Arrangement pattern letters, piano roll) stay as they are and are listed in the shortcuts window under their view.

## 5. Phases

Each phase: branch from `origin/main`, the gallery before and after for every screen it changes (`ui-gallery.md`; Mac headless locally, the real Mac with Fork Mac's opt-in `gallery: true`, Windows with the package run's `--ui-shots` once P6 starts), a test for every behaviour change, and the three CI runs (Fork CI, Fork Mac, quick package).

### P0: this plan

On the P1 branch. Exit: the plan is reviewed with P1.

### P1: quick wins (`ux/overhaul-1-quick-wins`)

The review's quick-win list, less what the bug branches (#105 `fix/ui-gallery-bugs`, #106 `fix/session-shell-bugs`, `fix/editor-kit-bugs` in `integrate/keyboard-kit-core`) did: dialogs with verb buttons and the safe default, the real Mac menu bar, Preferences that scroll, windows inside the screen, the idle slots drawn as playing, the time signature asked as "4/4", switches as toggles and readable knob tracks, graphs inside their plots.

| Item | What changes | Gallery screens |
|---|---|---|
| State colour tokens | `StudioTokens`; Play lit green, Record lit red, Loop lit neutral; clips keep their fill, state as a chip, outline and progress in the state colour; queued blinks; the transport's record lights whenever anything records; colour menus leave out state colours | mac-040…046, mac-070…072, mac-211, mac-221 |
| Contrast and size floor | Text on a colour by contrast; Session header buttons 20×20 pt; Arrangement M/S/D 20×20 pt; pattern list selection #1F5FA8; playhead 2 pt; Song Notes placeholder and hint ≥4.6:1; 11 pt minimum text; the default button of every dialog accented, a destructive one red | mac-014, mac-030/031, mac-040, mac-060, mac-071, mac-321, mac-332, mac-337 |
| Complete menus, one keymap | The command model; File: Close Window, Revert to Saved; Edit: Duplicate, Delete, Select All, Deselect All, Rename; Transport: Go to Start, Loop, Metronome, Silence; View: ⌘1–5; Window: Bring All to Front and the open windows; Help: Keyboard Shortcuts, Show Log Folder. The views' keys unified (§4) | mac-100…107, mac-200, mac-203, mac-210, mac-220 |
| Shortcuts window | Searchable, one section per view, generated from the command model and the views' key lists; replaces the 12-line message box | mac-331 |
| Tooltips with keys | Every transport and toolbar button: "Record (R or F7)", from the command model | (tooltips are off in the headless shots; tested) |
| Plain-language problems with a fix | A problem bar under the transport: a runaway machine ("Runaway blew up (+66 dBFS) and was muted to protect your ears" [Reset and Unmute] [Parameters…]), a machine that stopped working, Windows-only machines in a song on a Mac ([Show Them]), the machine guard off ([Turn On]); an audio-health badge in the status line (no output: red, [Audio Settings]; dropouts: amber with their count); the canvas says "muted: blew up", "Windows only" | mac-010, mac-500, mac-520, mac-521, mac-real-010 |
| Pattern editor block selection visible | The accent at 35 % with an accent outline (was 1.06:1) | mac-052 |
| One time unit | The Arrangement ruler counts bars from 1 (ticks in its tooltip); the clip tooltip and clip panel say bars.beats (ticks as a second line); the clip panel's progress "bar 2.1 of 2" | mac-030, mac-031, mac-041…043 |
| Tab order | Arrangement (Sequence), Session, Machines, Pattern, Song Notes, so ⌘1–⌘3 already are the three main views; the app still opens on Machines | every main-window shot's tab bar |
| Note names (§3.5; asked for during P1) | One setting, Preferences ▸ General ▸ Note names, names notes in the pattern editor, the piano roll, the machine keyboards and MIDI mappings; C3 = 60 by default | the pattern editor's shots, the keyboard shots, Preferences ▸ General |

Not in P1: Windows' labelled view tabs (WPF is bug-fix only, §1.1); the knob and switch fixes (already in `integrate/keyboard-kit-core`).

Exit: the gallery shows each change; the measured figures in §8 meet the floor on the changed screens; every changed behaviour has a test; the three CI runs and a Mac gallery run are green.

### P2: foundations

Needs `integrate/keyboard-kit-core` on main (the kit and the machine windows change there).

P2 lands in three mergeable steps from `ux/overhaul-2-foundations`, tokens first so the machine threads can adopt them:

- **P2a Tokens (first PR).** `Tokens` in `NeoBuzz.Editor/Core` (toolkit-free, ARGB; §3.1), read by the studio's `StudioTokens` and by the kit's `Palette` on Avalonia (the classic WPF app keeps its look until P6); Inter as the app's typeface (the shell's default family and the kit's); the Neo editors this overhaul owns take their category's identity as their accent (Neo Poly, FM6, Sampler, Drums, Comp, Multiband, Filter, Delay, Verb, Ensemble, Drive, Scope, Analyzer; Neo Wave after `fix/followups-1`), manifest editors theirs from the manifest's type; the adoption note for the other machines (§6).
- **P2b Views on the tokens; Appearance.** The Sequence view and every other view, dialog and Preferences page on the tokens: no beige by default, no literal colour outside the theme hooks of the code-drawn canvases; the type scale and the 8 pt grid swept through the shell, the Session View and the Preferences; Preferences ▸ Appearance with Dark (the default) and Classic Buzz (the beige machine canvas and arrangement), a new settings key.
- **P2c One selection.** `StudioSelection` (machine, track, clip, pattern, wire, scene; plus "nothing": the song): the Machine View, Arrangement, Session View and Pattern View publish to it and follow it (select a machine: its track is highlighted in the Arrangement and the Session View, the Pattern View shows its pattern). A test per view pair.

Exit: the gallery unchanged except where tokens moved a colour (listed), every Neo editor's screenshot compared before and after.

### P3: big bets

In this order, each its own PR:

1. **P3a Panels.** The window gets the browser (left), inspector (right) and detail (bottom) panels, each collapsible and remembered (new settings keys); View ▸ Browser ⌥⌘B, Inspector ⌥⌘I, Detail ⌥⌘D. The Session clip panel moves into the inspector (Clip and Scene sections; machine, track, wire, pattern sections after it); Song Notes become its Song tab. The view switcher becomes a segmented control (Arrangement | Session | Machines) and the tabs are renamed. Pattern becomes the detail's Tracker, maximisable (⇧⌘F or F2).
2. **P3b Tracker and piano roll docked**, one clip in two views (Tracker | Piano roll), the piano roll with a bar ruler, playhead, loop brace and a velocity lane.
3. **P3c Pattern editor, notes first:** note and velocity columns first, empty parameter columns behind "+ Automation", horizontal two-line headers at 11 pt, beat and bar rules at 3:1, drum names from the machine's note names, decimal values by default (hex as an option), centred scrolling.
4. **P3d Machine browser and port dots:** a searchable browser, opened by double-clicking the canvas or by typing on it, with categories (Synths, Drums, Samplers, Dynamics, EQ & Filter, Delay & Reverb, Modulation, Distortion, Utility, Control), brand as a tag, recents and favourites, one-click starters (Neo Poly, Neo Drums, Neo Wave) in empty states; port dots on machine boxes for drag-to-connect (Shift-drag stays).
5. **P3e Device view** replacing the generic parameter window: grouped from the manifest (else name prefixes, Buzz's "- " indents, Global/Track/Inputs), searchable (⌘F), 24 pt rows, sliders filled from zero with units, values typed by double-click, ⌥-click resets, a macro strip, up to four choices as a segmented control, switches as toggles, track parameters as a table, the piano keyboard under it. Docked in Detail ▸ Device with Float; double-click opens the richest view; an Editor | Parameters switch.
6. **P3f Preferences, one window:** General, Audio, MIDI, Appearance (themes), Agent, Safety, Advanced (today's "General" engine flags), with Restore Defaults; Safety first-class; device tables for MIDI.
7. **P3g Feedback:** meters on machine boxes and wires that brighten with level, a stereo master meter with a −60…0 dB scale and a latching clip light, the audio-health badge clickable to Audio settings; a top bar with metronome, time signature, min:sec, Tap and a draggable BPM; one global transport (the Session View's second one goes).
8. **P3h Colour per machine** carried to its track and clips as their default.
9. **P3i Empty states** with a next step in every view (and the starters).
10. **P3j Export Audio** (⇧⌘R): render the song (or the loop) to WAV with a progress sheet, on the agent's render path.

Exit per PR: the gallery's new shots for it, a usability script run headless (first sound from an empty song: steps counted, target 3 from the 7 the review counted), frame times on the big test song unchanged within 10 %.

### P4: polish

Consistency (one button, one menu, one dialog style), animation and timing (≤150 ms transitions, none on the audio-critical paths), focus order and keyboard reach to every control, accessibility names on every icon (`AutomationProperties.Name`), Light theme, every gallery screen re-checked against the review's findings with a table of before/after figures.

### P5: the review's "Later" items

The docked piano roll if P3b didn't finish it; Neo Drums around its pads (a pad grid selects one voice panel, per-pad mix strip, pads flash on hits); FM6 and Poly in pages (operator overview with one open; Synth | Modulation); drag-to-modulate (drag a source onto a knob, rings on targets; the matrix as a table with + Add); a shared Samples view for both apps (Windows' wavetable, hex slots kept). Machine code belongs to its owners: these go through the kit and the machines' editors with their owners' agreement.

### P6: the studio becomes the Windows app

The package and installer ship the studio as the main exe (`NeoBuzzLive.exe`) and WPF as "NeoBuzz Live Classic". Before the switch, the parity checklist (§9) is complete and checked on the Windows runner (the gallery's `--ui-shots` equivalent for the studio on Windows, the WPF tests' scenarios against the studio).

## 6. Notes for the parallel threads

- **Dye Essence, the mastering machines (Neo Utility, EQ, Limiter, Clipper), NeoBuzz.Dsp/Analysis, the Pedal synths:** this overhaul doesn't edit their code. Since P2a they have `Tokens` (§3.1) and the kit's components.

**Adopting the tokens** (any machine editor on the kit; each step keeps the classic WPF app as it is, because the old colour is passed along):

1. The accent: `static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.<its category>, NeoColor.FromRgb(<the old accent>));` (§3.1.1).
2. A second trace (a stereo pair's right side): `Tokens.Partner(AccentColor, <the old colour>)`. A graph's band colours: `Tokens.Bands(new[] { <the old colours> })`.
3. No other literal colour: surfaces, text, lines, knob tracks and meters come from the kit (`Palette`, `Theme`, its knobs, meters and graphs), which are the tokens in the studio. A colour the kit lacks is a new token in `Tokens.cs`, not a literal.
4. Text 11 pt or more (`Tokens.FontCaption`), `TextSecondary` or brighter on a panel; no state colour (play green, record red, override orange) as an accent or a graph line; units on every value.
5. Gaps and sizes on the 8 pt grid (`Tokens.Space(n)`); targets 24 pt (`Tokens.HitTarget`).

What each thread changes (as of P2a): **mastering**: Neo Eq's accent → `Identity(EqFilter, …)` and its eight band colours → `Bands(…)` (the first, #FF6B6B, reads as record red: ΔE 24); Neo Limiter's accent → `Identity(Dynamics, …)`, its input colour (#4A5260) → `Palette.Track`; Neo Clipper's accent (#F2A63B, ΔE 19.8 from override orange) → `Identity(Dynamics, …)`; Neo Utility's accent (#8FD14F, ΔE 24 from play green) → `Identity(Utility, …)`. **Dye Essence**: its accent (#E06CD8) → `Identity(Synth, …)`. **Pedal**: the Pedal machines' editors are manifest editors, which take their category from the manifest's `type` already; Pedal Shaper's type should say "distortion" and Pedal Peer Control's "control" for them to get the Distortion and Control identities (both fall back to Utility and Synth today).

**P2b: what the machine editors do now (nothing waits on P2b; each item is one edit in the editor's own file).**

- **Themes don't reach editors.** Classic Buzz (Preferences ▸ Appearance) changes the machine canvas and the sequence only; an editor window is the kit's dark in both themes, so an editor never reads the theme and never branches on it.
- **`Machines/DyeEssence/DyeEssenceEditor.cs` line 19:** `AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xE0, 0x6C, 0xD8))`.
- **`Machines/NeoEq/NeoEqEditor.cs` lines 24 and 28-30:** `AccentColor = Tokens.Identity(MachineCategory.EqFilter, NeoColor.FromRgb(0x4F, 0xB3, 0xFF))`; the eight band colours `= Tokens.Bands(new[] { <the eight now there> })`.
- **`Machines/NeoLimiter/NeoLimiterEditor.cs` lines 19-20:** `AccentColor = Tokens.Identity(MachineCategory.Dynamics, <old>)`; `InputColor = Palette.Track`.
- **`Machines/NeoClipper/NeoClipperEditor.cs` line 19:** `AccentColor = Tokens.Identity(MachineCategory.Dynamics, <old>)`.
- **`Machines/NeoUtility/NeoUtilityEditor.cs` line 17:** `AccentColor = Tokens.Identity(MachineCategory.Utility, <old>)`.
- **Pedal editors:** no code; set `"type"` in the manifest (Pedal Shaper `"distortion"`, Pedal Peer Control `"control"`), and the editor takes the category's colour.
- **Check, in each editor's test:** the accent is ΔE 24 or more from the three state colours (`Tokens.IsReservedForState(accent)` is false) and the editor's text is `Palette.Text` or `Palette.DimText` (6.6:1 on the view). `NeoBuzz.MachineTests/TokensTests` has the pattern.

## 7. Risks

| Risk | Mitigation |
|---|---|
| The keymap change surprises users of the fork's own keys (⌘D, ⌫ in the Session View, ⇧⌘A) | Listed in §4 and in the shortcuts window; Buzz's keys untouched |
| Single-key transport keys swallowed by views that type | Acted only in a bubble handler after the views, and only in views that don't type; tested per view |
| The panels (P3a) conflict with the Session View's own layout code and the WPF Session View sharing it | P3a moves the clip panel in one PR, with the Session View's tests unchanged |
| Frame time on big songs grows with meters on every box and wire (P3g) | Meters drawn from the engine's existing peak values at 30 Hz, measured on the 36-machine song (mac-510) |
| Parallel threads change the kit while P2a moves its colours | P2a waits for `integrate/keyboard-kit-core`; machines adopt tokens on their own branches |
| Windows parity takes longer than the overhaul | P6 is gated on the checklist, WPF stays shippable as Classic meanwhile |

## 8. Measures

`tools/ux-measure.py` (P1) measures a gallery screenshot: the WCAG contrast of a region's text against its background (the most common colour against the one farthest from it in luminance), two points' colours, colour distances (ΔE CIE76); regions in points (the Mac shots are drawn at scale 2). Frame times: the studio's render timing over the 36-machine song (mac-510) and the demo song while playing, before and after.

P1 figures: §11.

## 9. Windows parity checklist (for P6; filled in as P6 starts)

What WPF has that the studio needs before it replaces it on Windows: native (Win32) machine GUIs in their host windows; the vertical sequence editor as an option; the wavetable (as the Samples view, P5); Clone with patterns; Latency and Signal Analysis windows; the CPU monitor; the Hard Disk Recorder; the debug console; MIDI bindings and the MIDI-focus toolbar's functions; machine groups; song properties (TPB, speed); the installer, file associations, `--ui-shots`; the WPF tests' scenarios (ReBuzzTests) run against the studio.

## 10. Decisions taken where the review left a choice (P1)

1. **Deselect All is ⇧⌘A** (as Logic and the Adobe apps), Esc too; Window ▸ Agent connection… loses ⇧⌘A (it is Preferences ▸ Agent).
2. **Single-key transport keys** act only where the view doesn't type letters; F-keys everywhere.
3. **Go to start is Home** (fn ← on a laptop), not Return (Return launches in the Session View and opens a pattern in the Arrangement).
4. **⌫ in the Session View removes the clip from its slot** (not destructive: the pattern stays); ⇧⌫ deletes the pattern after asking. A track stops with its empty slot's ■, Enter on an empty slot, or the track menu.
5. **Reserved state colours:** play #34C759, record #FF453A, queued = play blinking, override #E8823A; palette entries within ΔE 20 of them (Red, Orange, Ochre, Kelly Green) are no longer offered; songs that use them keep them.
6. **Clip text** is pure white or pure black, whichever has more contrast (≥4.58:1 on any colour), not the nearly-white/nearly-black pair (which can't reach 4.5:1 on mid-tones).
7. **Tab order now, names later:** the tabs are reordered to the target order in P1, so ⌘1–3 never change meaning; the "Sequence" tab is renamed "Arrangement" in P3a with the new switcher.
8. **Windows-only machines:** the problem bar offers [Show Them]; replacing them all at once (each with its Neo successor) is P3d's browser work, as the per-machine Replace with… exists.
9. **Note names' labels** (§3.5): "Buzz: C-5 = MIDI 60", "C3 = MIDI 60", "Scientific: C4 = MIDI 60". The C3 naming isn't labelled after the app that made it common (no third-party names in the UI, and "Live" is in this app's own name); the setting's value is `C3`.
10. **Octaves below 0 in the tracker** (MIDI 0–23 in C3 naming, 0–11 in scientific) add their minus sign: "C--2", "C#-2", four characters, drawn over the column's gap. They are rare (below 33 Hz), and three characters can't tell them from octave 2.
11. **An octave typed is the octave shown**: in C3 naming, 5 on a note's octave digit is C-5 as shown (Buzz's C-7); an octave Buzz doesn't have (8 and 9 in C3 naming) changes nothing. The base octave list shows the named octaves (−2…7 in C3 naming); the machine's value stays Buzz's.
12. **The agent server stays scientific**: a display setting must not change what a tool means. The UI's Replace report and the piano roll's tracker messages, which the user reads, follow the setting.

### P2a

13. **Identity colours** (§3.1.1) were searched for, not picked: ten colours each ≥5:1 on a view, ≥37 (ΔE) apart, ≥24 from the accent and the states, each within ΔE 9 of a colour a Neo editor already used where one fitted. Neo Scope and Neo Analyzer are Utility (slate), as the browser's categories have no Analysis.
14. **Meters stay green, amber, red** (#7BE495, #FFC43D, record red over 0 dB): the convention every meter has; the mint green is ΔE 23 from play green (outside the reserved 20) and never a control's accent.
15. **The classic WPF app keeps its look**: the kit's palette and the editors' accents are the tokens only where AVALONIA is defined (the studio and the machines' studio builds); the old colours are written beside the new (`Identity(category, classic)`), so the classic look is one place to change at P6.
16. **Inter in the editors too** (they used Segoe UI's widths: Helvetica Neue on a Mac, Inter on Linux): one typeface across the studio. The local gallery drew them in Inter already, so their layouts fit it.
17. **Neo Wave keeps its accent until `fix/followups-1` lands** (it edits that editor); Neo Sampler's sixteen zone colours stay for P2b's sweep.

### P2b

18. **Themes are Dark and Classic Buzz, and Classic is only the two code-drawn canvases.** The Machine view (beige, `MV Background` #DAD6C9 and the Buzz theme file's box colours, so a user's own .col theme still applies in Classic) and the Sequence (brown, with its light pattern list and corner buttons). Every other view, dialog and Preferences page is the tokens in both themes; a beige Session View or Preferences would be a second design to keep. The setting is `AppearanceSettings/Theme` (`Dark`, `Classic`; shown "Dark", "Classic Buzz"), a studio-only settings class, so the classic app's Settings window doesn't list a setting it can't use.
19. **Dark boxes in the Machine view are the identity-neutral ramp for now**: generators blue-tinted, effects violet-tinted, the Master and control machines grey, muted ones darker (each 4.5:1 or more with the name); P3h makes each box its category's identity colour (§3.1.1). The selection is the accent at 25 % and a 2 pt accent outline (Classic keeps its glass).
20. **The Sequence's dark blocks:** a pattern keeps its saved palette colour (§3.4); a block with none, a break, a mute and a stop get four fixed dark colours. The name sits on a dark plate (the base surface at 82 %) in primary text, so it reads on any palette colour. The playhead is the play colour, the song end primary text, loop markers the warning colour; the cursor's row in the headers is the accent at 20 %.
21. **The type scale is enforced by a test** (`UxP2bTests.EveryFontSizeIsOnTheScale`): 11, 12, 13, 15, 20; the 14, 16, 17, 24, 19 and 30 the views had are on it now (Song Notes: headings 20 / 15 / 13 bold, body 13). The 8 pt grid is swept through Preferences (pages, rows, hints); the Session View and the shell keep their paddings until P4, which is the polish pass (a grid sweep of 30 files with no visible change isn't worth a risk to P3's layout work).
22. **"No literal colour" is enforced by a test** (`UxP2bTests.NoViewHardCodesAColour`) over the studio's sources; its allow-list is the token table, the theme file, the Session View's palette (the clips' colours, saved in songs) and two code-drawn controls with their own dark tables, the piano roll and the wire's amp popup (P3 redesigns it).

## 11. Progress

### P1: quick wins (`ux/overhaul-1-quick-wins`)

Built: everything in §5 P1's table, and (asked for during P1, with `integrate/keyboard-kit-core` on main) §3.5's note names. Where it lives:

| Part | Code | Tests |
|---|---|---|
| Tokens, contrast, the reserved state colours | `ReBuzz.Studio/Shell/Design/StudioTokens.cs`; `SessionPalette` reads them | `UxQuickWinTests` (every palette colour's text ≥4.5:1; the tokens' ratios; the reserved colours never offered) |
| Clip states | `SessionCells.SlotCell.Refresh`: the fill stays, a dark chip under ▶/●, a 2 pt outline, the progress bar | `SessionViewTests` (playing, only the slot it plays from) |
| One keymap | `Shell/Commands/StudioKeymap.cs` (commands and the views' keys), `KeyText.cs` (one spelling per platform), `ShortcutsWindow.cs`; `MainWindow` builds its menus and key bindings from it | `UxQuickWinTests` (no two commands share a key; every menu key is the keymap's; the shortcuts window lists and searches; the tracker keeps ⌘R, ⌘W, ⌘1–⌘9), `StudioShellTests`, `TransportKeysTests` |
| Duplicate, Delete, Deselect, Rename in every view | `IStudioEditTarget` grew them; the Machine View (clone with an offset, one undo step), the arrangement (the block again after itself), the Session View | `UxQuickWinTests`, `MachineViewTests`, `SequenceEditorTests` |
| Problems in plain words | `Shell/ProblemBar.cs`, `Shell/AudioHealthBadge.cs`; the canvas says "muted: blew up", "Windows only" | `SafetyTests` (the bar's Reset and Unmute), `UxQuickWinTests` |
| One time unit | `Shell/TimeText.cs`; the arrangement ruler, the clip panel and the slots' tooltips | `SessionViewTests` (the clip panel), `UxQuickWinTests` |
| The tracker's block selection | `ModernPatternEditor/Avalonia/PatternTheme.cs` | `UxQuickWinTests` |
| Note names | `ReBuzzGUI/BuzzGUI.Common/NoteNames.cs`, `GeneralSettings.NoteNames`; read in the pattern editor (`ParameterExtensions`, `PortableShims`, `MPEParameter`, `ParameterColumn.EditDigit`, the Avalonia view's base octaves), `ClipNotes.Name`, `MidiControl.NoteName`, `KeyboardFace.NoteName`, `MachineReplace` (the UI's report) | `UxQuickWinTests` (the three namings), `PreferencesTests` (the setting, saved, read by each display), `PatternEditorTests` (cells, base octaves, the typed octave), `MachineKeyboardTests` (keys and octave follow a change), `DawModesSeamTests`, `ClipNotesTests`, `SessionMidiMapTests`, `KeyboardFaceTests` |

Measured (Mac gallery, headless, scale 2; `tools/ux-measure.py`):

| What | Before | After |
|---|---|---|
| Session clip text on its colour (mac-040, "Hook" on green) | 2.88:1 | 5.97:1 |
| Session clip text ("Fill" on orange) | 2.96:1 | 5.81:1 |
| Session header chip label (M/S/●), size | 3.54:1, 18×16 pt | 4.55:1, 20×20 pt |
| Song Notes placeholder (mac-014) | 2.54:1 | 4.95:1 |
| Song Notes "Saved with the song" | 4.10:1 | 5.46:1 |
| Pattern editor block selection against the grid (mac-052) | ΔE 4.4 (1.08:1) | ΔE 28.0 (1.58:1), accent outline 4.11:1 |
| Transport Play vs Record when lit (mac-071/072) | both #0078D7, ΔE 0 | #34C759 vs #FF453A, ΔE 130; glyphs black 9.46:1 and 6.16:1 |
| Arrangement playhead (mac-071) | 1 pt yellow, 2.10:1 on the lightest lane | 2 pt with a dark edge, 5.96:1 there |
| Pattern list selection text | 5.36:1 | 6.44:1 |

Frame times (headless, Linux, the gallery's demo song, 1440×900; medians of 3 runs before and 4 after, `UxFrameTimeTests`): Machines drawn 5.29 → 5.19 ms, Sequence 54.2 → 56.7 ms (+4.6 %), Session with a scene launched 4.70 → 4.64 ms, the Session View's state refresh (20 a second) 0.060 → 0.067 ms, the window's status refresh (10 a second, now with the problem bar and the badge) 0.004 → 0.008 ms.

### P2a: the design tokens (`ux/overhaul-2-foundations`, first PR)

Built: `Machines/NeoBuzz.Editor/Core/Tokens.cs` (the table, §3.1), `StudioTokens` and the kit's `Palette` (on Avalonia) made from it, Inter as the app's default family (`App.FontOptions`, in the app and the tests) and the kit's, the identity accents of thirteen Neo editors and of the manifest editors (from the manifest's type), the adoption note (§6). Tests: `TokensTests` (NeoBuzz.MachineTests: contrast on every surface, the identities' and series' distances, the manifest types' categories, the grid, the classic app's colours kept), `UxFoundationTests` (the studio and the kit read one table, Inter in the shell and the editors, seven editors' accents), `NeoEditorTests` (the meters and graphs found by their new colours).

Measured (Mac gallery, headless, before: main's run 42274820119, after: run 42287794670; `tools/ux-measure.py`):

| What | Before | After |
|---|---|---|
| A knob's value text in Neo Poly (mac-411) | 5.15:1 (#8A92A0 on #1E2127) | 6.63:1 (#A0A0AA on #1B1B1E) |
| A knob's label | 13.0:1 | 14.1:1 |
| A knob's unfilled track | 3.02:1 | 3.42:1 |
| Editor accents within ΔE 24 of a state colour | 4 of 13 (Neo Drive 8.3 from override, Neo Drums 13.7, Neo Scope 14.9 from play, Neo Delay 17.9 from record) | 0 (Neo Wave's stays until `fix/followups-1`) |
| Closest pair of editor accents | ΔE 5.8 (Neo Multiband and Neo Sampler) | ΔE 37 between categories |
| The headless Mac shell's typeface | the platform's | Inter (the real app's already) |

### P2b: every view on the tokens, Appearance (`ux/overhaul-2b`)

Built: `StudioTheme` (Dark, the default; Classic Buzz) with the Sequence view's and the Machine view's colour tables (`Shell/Design/StudioTheme.cs`), Preferences ▸ Appearance (`AppearanceSettings/Theme`, a new studio-only key), the Sequence's canvas, pattern list and corner buttons, the Machine canvas, the shell's meter, badge, bars and dialogs, Song Notes, the Pattern page, the parameter window and the Preferences pages on the tokens; the font sizes on the scale; Preferences on the 8 pt grid. Tests: `UxP2bTests` (the Sequence and Machine views dark by default, measured from the window's pixels; Classic's beige and brown and back; Appearance's choices; contrast of the dark tables; no literal colour in the sources; every font size on the scale).

Measured (Mac gallery, headless, before: `main` at P2a, after: this branch; `tools/ux-measure.py`, `UxFrameTimeTests`):

| What | Before | After |
|---|---|---|
| Light area (luminance above 0.3) of the Sequence screen (mac-030) | 31.6 % | 4.3 % (the clip colours) |
| Light area of the Machines screen (mac-020) | 81.1 % | 0.2 % |
| Screens of the gallery that changed | – | 89 of 108 (45 by more than 0.5 point of light area; the rest by the shell's borders and the Preferences' spacing) |
| Bar numbers on the ruler | 9.32:1 (#000 on #BBAA99) | 5.97:1 (#A0A0AA on #242428) |
| A track's name in the header | 6.10:1 | 9.49:1 (on the selected row) |
| A pattern in the list | 19.55:1 | 12.65:1 |
| A machine's name in its box (Drums) | 9.57:1 | 7.79:1 |
| Draw time, Machines / Sequence / Session (UxFrameTimeTests, Linux, 2 cores) | not measured before | 8.3 / 9.3 / 13.9 ms |

Contrast fell where a beige canvas had black text at 9:1; every text on the dark canvases is above the 4.5:1 floor (`TheDarkSequenceMeetsTheContrastFloor`).

Left for later: the Session View's and the shell's paddings stay as they are (P4); the amp popup and the piano roll keep their own dark tables; the Machine view's boxes get their identity colours in P3h; P2c (one selection) is the next step.
