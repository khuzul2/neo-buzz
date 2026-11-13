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
| **C3 = MIDI 60** (the default; C3 confirmed by the user 2026-11-12) | C3 | C#3 | C-2 | G8 | C-3 (MIDI 0: C--2) |
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
| Note names (§3.5; asked for during P1) | One setting, Preferences ▸ General ▸ Note names, names notes in the pattern editor, the piano roll, the machine keyboards and MIDI mappings; C3 = 60 by default (C3 confirmed by the user 2026-11-12) | the pattern editor's shots, the keyboard shots, Preferences ▸ General |

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
3. **P3c Pattern editor, notes first (done, decisions 50-55):** note and velocity columns first, empty parameter columns behind "+ Automation", horizontal two-line headers at 11 pt, beat and bar rules at 3:1, drum names from the machine's note names, decimal values by default (hex as an option), centred scrolling.
4. **P3d Machine browser and port dots (done, decisions 56-61):** a searchable browser, opened by double-clicking the canvas or by Add Machine… (⇧⌘N; typing on the canvas does not open it, decision 58), with categories (Synths, Drums, Samplers, Dynamics, EQ & Filter, Delay & Reverb, Modulation, Distortion, Utility, Control), brand as a tag, recents and favourites, one-click starters (Neo Poly, Neo Drums, Neo Wave) in empty states; port dots on machine boxes for drag-to-connect (Shift-drag stays).
5. **P3e Device view (done, decisions 62-70)** replacing the generic parameter window: grouped from the manifest (else name prefixes, Buzz's "- " indents, Global/Track/Inputs), searchable (⌘F), 24 pt rows, sliders filled from zero with units, values typed by double-click, ⌥-click resets, a macro strip, up to four choices as a segmented control, switches as toggles, track parameters as a table, the piano keyboard under it. Docked in Detail ▸ Device with Float; double-click opens the richest view; an Editor | Parameters switch.
6. **P3f Preferences, one window (done, decisions 71-78):** General, Audio, MIDI, Appearance (themes), Agent, Safety, Advanced (today's "General" engine flags), with Restore Defaults; Safety first-class; device tables for MIDI.
7. **P3g Feedback (done, decisions 79-83):** meters on machine boxes and wires that brighten with level, a stereo master meter with a −60…0 dB scale and a latching clip light, the audio-health badge clickable to Audio settings; a top bar with metronome, time signature, min:sec, Tap and a draggable BPM; one global transport (the Session View's second one goes).
8. **P3h Colour per machine (done, decisions 85-87):** each box wears its category's identity colour, its track and clips take it as their default (worked out when drawn, never saved), recolouring works as before.
9. **P3i Empty states (done, decisions 89-90):** a next step in every view (and the starters); first sound from an empty song in 3 steps.
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
19. **Dark boxes in the Machine view are the identity-neutral ramp for now**: generators blue-tinted, effects violet-tinted, the Master and control machines grey, muted ones darker (each 4.5:1 or more with the name); P3h made each box its category's identity colour (decision 85); this ramp now serves the Master and Classic Buzz. The selection is the accent at 25 % and a 2 pt accent outline (Classic keeps its glass).
20. **The Sequence's dark blocks:** a pattern keeps its saved palette colour (§3.4); a block with none, a break, a mute and a stop get four fixed dark colours. The name sits on a dark plate (the base surface at 82 %) in primary text, so it reads on any palette colour. The playhead is the play colour, the song end primary text, loop markers the warning colour; the cursor's row in the headers is the accent at 20 %.
21. **The type scale is enforced by a test** (`UxP2bTests.EveryFontSizeIsOnTheScale`): 11, 12, 13, 15, 20; the 14, 16, 17, 24, 19 and 30 the views had are on it now (Song Notes: headings 20 / 15 / 13 bold, body 13). The 8 pt grid is swept through Preferences (pages, rows, hints); the Session View and the shell keep their paddings until P4, which is the polish pass (a grid sweep of 30 files with no visible change isn't worth a risk to P3's layout work).
22. **"No literal colour" is enforced by a test** (`UxP2bTests.NoViewHardCodesAColour`) over the studio's sources; its allow-list is the token table, the theme file, the Session View's palette (the clips' colours, saved in songs) and two code-drawn controls with their own dark tables, the piano roll and the wire's amp popup (P3 redesigns it).

### P2c

23. **The selection is a machine, with at most one of its patterns and the Session's scene** (`StudioSelection`, `Views/StudioSelection.cs`). A track is its machine's row in the Sequence and column in the Session View, so "select a track" and "select its machine" are one thing; a clip is a pattern in a scene. "Nothing" (no machine) is the song. A wire isn't selectable in the Machine View yet, so it isn't in the model; it joins when P3 makes wires selectable.
24. **Publish what the user selects, follow what the others select, never echo.** A view that only follows doesn't publish what it followed; a selection that changes nothing raises nothing. A view whose tab isn't shown (the Machines, Sequence and Session views leave the visual tree) catches up when it is shown; the Pattern page, which lives as long as its window, follows at once.
25. **What following does in each view.** Machines: that machine is the only one selected. Sequence: the cursor moves to the machine's first track and keeps its time (it selects the pattern under the cursor on the way, as it did). Session: the machine's column is selected, in the selected scene, or in the scene of the selected pattern's slot if the scene hasn't a say; nothing moves when the column already is selected. Pattern: shows the selected pattern, else the machine's pattern; it doesn't switch to its tab. The window starts with nothing selected (the Pattern page's first pattern isn't a selection).
26. **The windows and the tab strip are the base surface** (#141416; an application-wide style on `Window`), not Fluent's #000. The measured shell figures are below.

27. **P3a is split: the inspector first.** This step builds only the right-hand panel; the browser (left), the detail (bottom), the segmented view switcher, the tab renames and the maximisable Tracker are later P3a steps. `View ▸ Inspector` is ⌥⌘I (Ctrl+Alt+I elsewhere).
28. **The inspector is one panel for every view** (`Views/Inspector/InspectorPanel.cs`), driven by `StudioSelection` and not by the Session View: a machine (type, patterns, wires, mute and bypass), its pattern (name, length, "Open in Pattern view"), or the song (tempo, ticks per beat, machines). Its tabs are Selection and Song. It is open at first, as the clip panel was; it collapses to a 28 pt rail with a button that opens it; open or closed and the width (240–560 pt, 288 by default, dragged by its left edge) are remembered in the settings keys `InspectorOpen` and `InspectorWidth` (section Studio). The tab is not remembered: it opens on the selection.
29. **The Session View's clip panel moves in as it is** (the Clip and Scene sections, the same class and the same tests): in a main window the Session View hands it to the inspector, which follows the selection into the machine's track and the selection's scene (a machine with no track in the Session View shows no clip sections). The Session View keeps its own panel when it has no window around it (its tests). The song's own "show clip panel" setting is left alone (the WPF Session View reads it); in the studio the Clip button and "Clip settings" open and close the inspector.
30. **Song Notes are the Song tab, not a tab of the window.** The window's tabs are Sequence, Session, Machines and Pattern (⌘1–⌘4). ⌘5, F10, View ▸ Song Notes and `ShowView("Song Notes")` open the inspector on the Song tab with the editor focused, also when it was collapsed. The editor's undo, cut, copy and paste are the song's as before.

31. **P3a, second step: the browser and the detail panel; the switcher and the renames remain.** `Views/Browser/BrowserPanel.cs` (left) and `Views/Detail/DetailPanel.cs` (under the view). Both are closed at first (a 28 pt rail, a 28 pt bar) so that a window opens with the room it had; their state is remembered (section Studio: `BrowserOpen`, `BrowserWidth` 180-480 pt, 224 by default, dragged by its right edge; `DetailOpen`, `DetailHeight` 120-1200 pt, 340 by default, dragged by its top edge). `View ▸ Browser` is ⌥⌘B and `View ▸ Detail` ⌥⌘D (Ctrl+Alt+B and Ctrl+Alt+D elsewhere).
32. **The browser lists what the user adds and what they select.** A search box over the installed machines (Instruments, Effects, Control, as the Machine View's New menu lists them) and the patterns of the selected machine, which come first (a list of machines is long and the patterns would be out of sight). A double-click or Enter on a machine adds it as the Machine View's New does (one undo step, the studio's selection moves to it); a click on a pattern selects it in every view (`StudioSelection`), a double-click opens it in the Tracker. Presets and Samples come with the machine browser (P3d).
33. **The Pattern view is the detail panel's Tracker, not a tab.** The window's tabs are Sequence, Session and Machines (⌘1–⌘3); ⌘4 and `ShowView("Pattern")` open the Tracker docked, as every other view's "open this pattern" does (a clip's double-click, the inspector's button, `ActiveView = PatternView`). The Tracker's edit commands, its ⌘-keys and Shift+Space are the window's only while the keyboard is in it. Maximised (decision 34) it has the whole window.
34. **Maximise: F2 and ⇧⌘F (Ctrl+Shift+F elsewhere), the ⤢ button, View ▸ Maximise Tracker.** Over the whole body (the side panels stay); the same key, or a view's key (F3, F4, Ctrl+Tab, ⌘1–⌘3 when the Tracker doesn't have them), goes back to what it was before: the bar if it was closed, the panel if docked. It is never remembered as maximised. F2 stays the old Pattern tab's key. ⌘1–⌘9 stay the focused Tracker's own (the beat's subdivision), so inside a maximised Tracker the F-keys are the views'.
35. **A narrow window gives the view its room, the settings are kept.** The main view keeps at least 360 pt across and 160 pt down. The inspector gives way first (not under its 240 pt, then it is a rail), the browser gets what is left (not under 180 pt, then it is a rail while its setting stays open); the detail is lower in a low window. At 640 pt wide the inspector is open and the browser is its rail.
37. **The segmented switcher is the view strip; Sequence is Arrangement.** `Shell/ViewSwitcher.cs`: Arrangement | Session | Machines, one row above the main view, the selected segment lit as the inspector's tabs are (accent border, selection fill), the key in each tooltip as the platform writes it. The TabControl stays as the host of the views with its own strip hidden (so it can no longer wrap), and the switcher follows its selection, so the keys, the menu, `ShowView` and code that selects a tab all keep working. The tab is now titled "Arrangement"; `ShowView` (and so the agent tools, keymaps and tests) still takes "Sequence" as an alias (`ViewNames.Canonical`). The command id stays `view.sequence` (saved keymaps), its label is "Arrangement"; songs and settings are untouched (the names are not stored).
38. **The Song tab stacks in a narrow panel.** Under 520 pt the editor sits over the notes as they read (a horizontal splitter between) instead of two ~90 pt columns side by side; at the inspector's default 288 pt each has the full width.
39. **At 640 x 400 nothing overlaps or clips.** The Session toolbar's right group (undo, scene, track, clip, mixer, settings) drops to a row under the left group when the bar is narrower than the left group's 300 pt plus the right group; the Tracker's chooser bar is a wrap panel, so its machine and pattern choosers and the button flow onto a second row.
36. **Left for the next P3a step (done in step 3, decisions 37-39).** The segmented view switcher and the tab renames.
40. **P3b is split: 3b1 is the shared pattern, the grid, editing and undo; 3b2 is the ruler, playhead, loop brace, velocity lane and the clipboard.** Both are done (decisions 45-49 are 3b2's); **P3b is done**.
41. **The detail bar has two pages, Tracker | Piano roll, of one pattern.** `DetailPanel` shows two buttons (the lit one is the page; clicking it hides the panel, clicking the other switches); `PatternPage` holds both under the one machine and pattern chooser bar, so a choice there, `StudioSelection` (the Browser, a clip, a block) and `PatternView.Edit` change both. The page is remembered (`Studio\DetailPianoRoll`, a new key). View ▸ Piano Roll is ⌥⌘4 (Ctrl+Alt+4); ⌘4 and `ShowView("Pattern")` always show the Tracker. The maximise button, F2 and ⇧⌘F act on the page shown.
42. **The piano roll edits what the Tracker holds, not a copy.** `Core/Notes/EditorTrackerNotes` (the Tracker's Note columns: the editor's own store, read through `IPattern.PatternEditorMachineEvents`, written by the editor's new `EditTrackerEvents` as one `MPEEventChangesAction`) and `EditorLaneNotes` (its MIDI note lanes, read by `ExportClipNotes`, made again by the editor's new `EditClipNotes` as one `MPELaneNotesAction` that keeps the lanes before and after). Both push one step on the **editor's own undo stack**, the Tracker's, so ⌘Z in either view undoes the other's last edit and the song's history is untouched. Notes follow the tracker rules of `TrackerColumnNotes` (a note a track at a time, the moved note keeps its track, velocity to the machine's velocity column, TrackNotes' note-on and note-off events): a start or an end lands on the column's rows; the off the engine's own columns need on the last row isn't written, so a note that runs to the pattern's end has none (the Tracker's way: it goes on into the next pattern, which the roll draws as ending at the pattern's end). An edit writes only the events that differ, so the other notes' events stay exactly as they were; a velocity the Tracker leaves to the machine (no event) is read as 100 and not written back until the note is edited. Looking at the roll, switching pages and selecting change nothing in the song (the saved bytes are equal; a test).
43. **Which notes the roll shows (`TrackerNoteSources`).** The Tracker can hold both a Note column and MIDI lanes (the Session View, the agent and recordings write the lanes). The roll shows the store that has notes when only one has; else the machine's own input (a manifest's `notes.input`, `tracks` meaning the Note column; a machine without a manifest takes its Note column, a generator with a manifest but no note input takes MIDI); a machine that has both gets a "Notes: Note column | MIDI lanes" chooser. A machine with neither (an effect) gets a message instead of an empty grid. The Session View's own clip notes (the engine MIDI column, daw-modes.md S4) are another store the Tracker doesn't show; they appear here after "move into the Tracker" as before.
44. **Mouse and keys of the docked roll.** The class is the Session View's `PianoRoll`, made to draw only the viewer's part (`Viewport`: rows, time lines, notes and keys in view; the keys stay at the left edge as it scrolls) and to take `AltBypassesGrid`: here **Alt (⌥ on a Mac) or Shift while dragging bypasses the grid** (the Session View's roll keeps Alt+drag for velocity); the Tracker's rows still take the note on a row. Double-click draws or deletes, drag moves, a note's end resizes, empty space selects. ←/→ move by a grid step, **⇧←/⇧→ lengthen or shorten**, ↑/↓ a semitone, ⇧↑/⇧↓ an octave, ⌘↑/⌘↓ (Ctrl elsewhere) velocity, ⌘A, ⌘D (duplicate after itself), Delete, Q, Esc. Edit ▸ Undo, Redo, Delete, Select All, Deselect All and Duplicate go to the roll while it is shown; Cut, Copy and Paste of notes came in 3b2 (49). The grid is Row (the Tracker's, from the column), Beat, Bar or Off. Mouse velocity editing is the 3b2 lane (48).
45. **The ruler is the song's bars and ticks a beat, read where the pattern lies.** `Views/Detail/RollRuler`, 26 pt, above the grid and scrolling and zooming with it: a bar line every bar of the song's bar list (`SongMeter.GetBarLengthAt` at the pattern's place in the song: its event that plays now, else its first; the first bar's when the pattern is not in the song), beats every `TPB` ticks from each bar's start (so a bar of 10 ticks at 4 a beat still starts on its line), bar numbers from 1 thinned to 44 px a label, "bar.beat" names when beats are 40 px apart, beats dropped under 6 px. The same bar length (`PianoRoll.BarTicks`) gives the roll's bar lines and Grid: bar, which had the Session View's beats a bar; a change of bar length inside a pattern is not drawn (the bar at the pattern's start rules). Text is primary and secondary on the panel, at least 4.5:1 (a test).
46. **The playhead is the pattern's own play position.** `IPattern.PlayPosition` (the Tracker's row marker): a 2 pt line in the play colour with a dark edge over the viewer (`PlayheadLine`, not drawn by the roll, so a moving playhead repaints a line, not the grid), a marker on the ruler, polled every 33 ms and only changed when the position does. **Follow** (a toggle in the bar, on) scrolls the viewer a page when the playhead leaves it; the spec said nothing, so this is the choice: off lets the user scroll while it plays.
47. **The loop brace is the song's loop.** Patterns have no loop of their own in the Tracker, so a drag on the ruler sets `Song.LoopStart` and `LoopEnd` (what the transport and the arrangement's markers use), in song ticks: the pattern's tick plus where it lies (its event's time less its offset). Snaps to beats, Shift or Alt for ticks; a drag near an edge of the brace moves that edge; a click moves the paste point. Both ends are one `SetMarkerAction` group on the **song's** history, not the editor's: while the roll is shown Edit ▸ Undo and Redo take a loop drag first when it was the last thing done there, and the notes' edits again after any other edit; ⌘Z from the arrangement undoes it as it does any song edit. A pattern that is not in the song cannot set a loop (the ruler says nothing). The brace is accent when the transport loops (C) and grey when it doesn't; a drag does not switch the loop on (it is not an undoable state).
48. **The velocity lane is a bar per note, 72 pt under the grid.** `VelocityLane`: the bar's height is the velocity (1-127), notes selected as in the roll. Dragging a bar of an unselected note selects it alone; of a selected one, drags the selection. One note: its velocity is the pointer's height. Several: every one moves by the amount the pointer moved from the press (clamped to 1-127; relative). The drag is previewed in the lane and written when the mouse is released: one edit through `Commit`, so one undo step of the editor's history, the notes staying selected. Shift or ⌘ (Ctrl) toggles a note in the selection. The velocity goes where 3b1's keys put it (the machine's velocity column).
49. **Cut, copy and paste of notes.** ⌘X, ⌘C, ⌘V (Ctrl off a Mac) and Edit ▸ Cut, Copy, Paste in the roll (the Tracker keeps its own while it is shown). One clipboard in the app, in memory, of notes with their pitch and distances in time (the first at 0); it is not the system clipboard. Paste lands at the **pointer's time when it is over the grid, else at the playhead while the pattern plays, else at the last click** on the roll or ruler (the start by default), rounded to the grid; notes starting past the end are dropped, one that runs over is cut to fit; the new notes are selected; Cut is Copy and one delete step; Paste one step; a chord the Tracker has no tracks for is refused with the roll's reason as any edit. Copy changes nothing in the song.
50. **Notes first, empty automation behind "+ Automation" (the studio's editor only; the WPF build shows every column as before).** `Portable/ColumnPlan`: the sets that have a Note column come first (the globals after the tracks); inside a set the Note column, the Wave column it fills (when it was right after the Note, so the editor's wave auto-fill still finds it), the velocity columns (a track parameter with "vel" in its name, or "Volume"/"Vol"), then the rest in the machine's order. Notes, their wave and velocity always show. Any other parameter shows when it holds an event on any track, or after the user asked for it; the rest are hidden for all tracks alike. Hiding is display only: the pattern's columns, data and saved bytes are untouched (the Parameters dialog's removal is a different thing and stays). "+ Automation" (toolbar) opens a filterable list of the hidden parameters (a click shows one, "Show all n" all); a column shown stays (kept in `PatternVM.RevealedParameters` while the editor lives); an event written into a hidden column (agent, undo) shows it. A pattern with nothing to show says so and points at the button. The cursor starts on track 0's Note column and stays on its column when columns come or go.
51. **Decimal entry.** In decimal a byte, word or switch column shows its value zero-padded to the digits of the parameter's largest value (0-254: "064"; 0-1000: "0700"); a parameter with a negative minimum, ASCII columns and notes stay as they were. Typing a digit 0-9 replaces the digit at the cursor and keeps the others (a column with no value counts as zeros), the cursor moves on as in hex; a value past the parameter's largest becomes the largest. Hex letters are ignored in decimal. In hex mode entry is exactly as before (every older editor test runs in hex).
52. **The value base is a setting.** Preferences > General > Pattern values (`GeneralSettings.PatternValues`, a new key; `BuzzGUI.Common.ValueDisplay` holds it), default Decimal, and a Hex toggle in the editor's toolbar that sets the same key. The status bar gives the value in the other base. Row numbers keep their own existing HexRowNumbers option.
53. **Drum names come from the machine's `KeyNames`** (MIDI key to name), the property the keyboard under a machine's window already reads (Neo Drums, Neo Sampler's Kit mode). `KeyShortNames` (new, optional; Neo Drums gives "BD", "SD"...) gives names that fit three characters; without it a name is cut to the initials of its words ("Closed hat" is "CH") or its first three letters. Notes with no name show their pitch; the status bar gives the full name. Display only: the pattern holds the pitches.
54. **Centred scrolling** (`PatternEditorSettings.CenteredScrolling`, new key, default on; toolbar "Centre"; absent from the WPF build). The cursor row is kept in the middle of the view as it moves; the beats have half a view of room above and below so the first and last rows can reach the middle. While the pattern plays the view follows the play row (centred) and a cursor move only keeps the cursor in view; with Centre off the old CursorScrollMode applies. This reads "the cursor row stays centred while playing and moving" as: the row being worked on (cursor when idle, play row when playing).
55. **Headers and rules.** Column names are horizontal, 11 pt, on at most two lines (split at the space that evens them), and a column is as wide as its longer line (at most 72 pt, then an ellipsis); a half-scrolled name stays whole inside its column. A 1 px rule at each beat (#888888, 3.4:1 against the lighter beat shade) and a 2 px one at each bar (#CCCCCC, 7.4:1); the bar is the song's bar at the pattern's place (the studio hands the editor `BarTicksSource`, as the roll's ruler reads it).
56. **One browser, one model.** `MachineBrowserModel` (`Views/Browser`, one per engine) is the only list of machines: the left panel (`BrowserPanel`) and the canvas popup (`MachineBrowserPopup`) both read it and share `FilterBar` and `BrowserRows`, so they cannot drift. The panel's "Instruments / Effects / Control" headers became the ten categories (Recent, Favourites and, in an empty song, "Start here" come first). Chosen over a popup-only browser so there is no second list to keep in step.
57. **The vocabulary is K0's.** Category, tags and `use_for` come from `MachineCatalog.Entry` (`machine-catalog.json`, the same file `machine_find` reads); a machine without an entry is under "Other". The brand (Neo, Jeskola, FSM, Pedal... else the machine's author) is the first tag and is searchable. Ranking: a word of the name 5 (plural 5, prefix 4.5), a tag 4, the category 3, a word of `use_for` 1, +0.3 for each further field that says it; every word must hit; favourite +1.5, recent +0.5; ties by name.
58. **Opening it.** A double click on empty canvas opens the popup there; so does **Add Machine…** (View menu, ⇧⌘N, Ctrl+Shift+N elsewhere; in the Shortcuts window): from any view it shows the Machine View and opens the popup in the middle of the canvas. **Typing on the canvas does not open it.** The first build had type-to-search (typing with nothing selected seeded the search, so R, C, K, M, S, B typed instead of acting); the user chose against it on 2026-11-13 (option C of the three put to him), so letters always keep their shortcuts, with a machine selected or not. Enter adds the best match (or the arrow-chosen one), Esc or a click elsewhere closes. The machine is created at the clicked song position (`MachineEdits.Create`, one undo step) and selected.
59. **Recents (8) and favourites** are the settings `MachineRecents` and `MachineFavourites` (section Studio, `|`-joined names). Starters (Neo Poly, Neo Drums, Neo Wave) show in the Machine View's empty state (a song with only its Master) and in the panel; one click is one undo step.
60. **Port dots.** An output dot on the right edge of generators and effects, an input dot on the left of effects and the Master, shown while a machine is hovered or selected and (inputs) during a wire drag; hovering one enlarges it and the tooltip names it. A plain drag from the output dot connects (one undo step); Shift-drag from the box is unchanged.
61. **Refusals are visible.** Over an invalid target the cursor is "no", the target is outlined in the error colour and the reason is written at the pointer; a refused drop leaves the song untouched and shows the reason (`MachineCanvas.Refusal`) for two seconds. Reasons: itself, the Master has no output, control machines, an instrument has no input, already connected, a loop (`MachineEdits.WhyNotConnect`).
62. **Groups.** `Views/Device/DeviceLayout` groups a machine's global parameters from, in order: the manifest's `group` (source "manifest", headers in the manifest's order, a parameter with none under "General"); Buzz's "- " indents (a name starting "- " belongs to the parameter above it that is not indented, which names the group and leads it; fewer than two indented names is no grouping); the first word of the names where two or more groups of two or more share one ("F1 cutoff", "F2 cutoff"; the loose ones are "General", first); else one "Global" section. Inputs (an amp, a pan on a stereo input, for each connected input) come first as "Inputs" and the track parameters last as one table. A first word every name of a group shares is cut from the names (the header says it). Only state parameters show (notes and triggers are played, not set); the old window's per-parameter "shown" boxes stay in the classic window.
63. **Controls.** A row is 24 pt: name, control, value. A slider is a 6 pt rail filled from zero, not from the left, where the range holds zero (raw range through zero, or the manifest's mapping passing it: bipolar, linear through zero, a count that starts below it); the value is the machine's own text, with the manifest's unit added where that text is a bare number. Two to four values the machine names (or the manifest names) are a segmented control; a switch, or two values that are numbers, a toggle; the rest sliders. Groups close from their header (not while a search runs).
64. **Gestures.** A drag sets the value live and is one undo step when it ends; a press that does not move changes nothing (so a double click cannot jump the value first; the wheel and arrows are the fine steps); a double click on the slider or the value opens a box over the value for typing, which takes what the agent takes (`ValueResolver`: "1.2 kHz", "-6 dB", "35%", "1/8", a choice's name, "raw:512"), Enter sets it and Esc cancels, and a wrong entry stays open and says what the parameter takes; ⌥-click (Alt) resets to the default; wheel and Left/Right step (Shift: ten times), Up/Down move between rows, Enter types, Delete resets.
65. **Virtualised.** The list is one control that draws and hit-tests only the rows in view (the old window made a control tree of every row: 0.3-0.4 s for 433 parameters, 2 s on a loaded runner). The explain strip has a fixed height so focusing a row never moves the rows under the pointer.
66. **Undo.** Every edit is one `ParameterEdits.ParameterChange` on the song's history, as the classic window's slider moves are: a drag one step, a typed value, a reset, a click on a choice or a switch one step each, wheel and arrow steps on one parameter that follow within a second one step.
67. **Docked and floating.** Detail has a third page, Device (⌥⌘5, Ctrl+Alt+5), that follows the selected machine and shows the piano keyboard under it for a machine that plays notes (`MachineKeyboardFrame`). Float moves the page to a window of its own (one per machine; the dock says so, and Dock closes the window). ⌘F (Ctrl+F elsewhere) opens the page and goes to its search box. Editor | Parameters switches the page between the machine's own editor (a second instance of it, made when asked, or a pointer to its window when that is open) and the device view; the choice is the setting `DeviceEditor`. "Classic…" opens the classic parameter window, which stays for what the device view does not have yet (presets, edit functions, MIDI binding, attributes, locks).
68. **The richest view.** A double click on a machine opens its own editor if it has one; else the manifest's editor where that setting says so (curated manifests, as before); else the device view in Detail (the studio's main window), the classic window only where there is no main window. The machine menu's "Parameters" still opens the classic window.
69. **Macro strip.** Up to eight sliders above the list: the manifest's roles in order (filter.cutoff, filter.resonance, level, mix.wet, pitch.coarse, env.attack, env.release…), else one parameter for each of the words cutoff, reso, threshold, ratio, drive, volume, level, gain, output, makeup, mix, wet, attack, decay, release, size, time, feedback, rate, depth, topped up to four from the first sliders. Cells edit as rows do. Not user-pinnable yet.
70. **Track table and Explain.** The track parameters are one table, a header row and a row per track at 24 pt, a column per parameter (at least 64 pt wide, a horizontal scroll beyond), cells edited as rows are (a choice cell steps to the next choice); it follows the track count. The strip under the list shows the focused parameter's name and value and, when the manifest has them (neo-learn.md §5.3), "How:" and "Sound:" lines, else the manifest's description; with nothing focused it says to click one.
71. **One settings model.** The Preferences window shows the agent's list: `PrefsModel` wraps `IAppServices.Settings()` (K3's `SettingEntry`, the keys settings_get lists: `general.*`, `audio.*`, `midi.*`, `appearance.*`, `agent.*`, `safety.*`, `advanced.*`) and each page's rows are made from the entries of its sections (`PrefsPage.AddSection`): type, choices (an enum's descriptions as `OptionLabels`), range, default (bold), restart flag and description all come from the entry, so there is no second list. Values are checked as `settings_set` checks them (`SettingsService.Normalize`): a number outside its range is refused on the page with what the setting takes, and the box shows what it holds.
72. **One way to change and log.** `SettingsService` (ReBuzz.Agent) is the only writer: the tools `settings_set` and `settings_reset` and the window both go through it. Every change is a line of `settings-history.jsonl` with a new `source`: **Agent** for a tool call, **Manual** for the window (older lines have none). `SettingsService.Changed` announces each change; an open window shows an agent's change at once (every page reads its settings again) and keeps a line under the pages naming the settings that wait for a restart ("Restart NeoBuzz Live for these to take effect: Audio threads"), which clears if the setting goes back to the value it had when the window opened.
73. **Pages and order.** General, Audio, MIDI, Sync, Appearance, Agent, Safety, Advanced, down the left side (a sidebar, as macOS settings windows have), the window 880 by 640. Sync (Link and MIDI clock) is a page after MIDI like the others (it was outside the list until P3g, decision 79). General is the `general` section (the Windows-only keys stay hidden); Advanced is the `advanced` section (today's engine flags) with a warning that songs can sound different; Audio keeps the devices and the render threads and work algorithm (restart-required, and it says so on the row).
74. **Safety is a page.** Safety limiter, its ceiling, the clip indicator's threshold and the machine guard moved from Audio to their own page, first-class in the list; turning a guard off still asks (Turn Off or Keep On, Keep On on Return and Escape), whether from the window; the agent's tool is annotated destructive, so its client asks. A change by the agent shows on the page at once.
75. **Restore Defaults** is one button under the pages for the page shown: its sections go through the same reset as `settings_reset` (logged as Manual). The Advanced and Safety pages ask first (Restore or Cancel, Cancel on Return and Escape). It leaves `agent.AllowAgents` and `agent.Port` (changing them ends the agent's connection) and the audio device, buffer, rate and input (they are the system's to choose; `audio_set` has them).
76. **MIDI device tables.** Inputs and Outputs are tables with a header (Device, check to use; State: open, closed, didn't open, not connected), a rule between rows and the check box in the device column; the options below are the `midi` section.
77. **The switcher is reachable by keyboard.** The Arrangement | Session | Machines segments are Tab stops with a 2 pt ring where the keyboard is; Enter picks one; Space stays the window's play key (a focused segment never takes it).
78. **P3e polish.** The device list gets the room: the Explain strip is one line until a parameter is chosen and opens to its 72 pt only after 450 ms (a click that starts a double click must not move the rows below it), then reveals the row if it would be under it; the piano keyboard starts collapsed in the device view and keeps its state in its own setting (`Studio/DeviceKeyboard`), apart from the machine windows' per-machine choice. Macros: the manifest's roles first, then (only up to four) names that are the word or hold it and are not numbered stages ("rate 1", "level 1", operators); labels are the manifest's plain name else the name, in Title Case. A slider cell's text sits on a backing (and shows the machine's detail alone when narrow: "0 st" for "50 · 0 st") so the marker never runs through it. The inspector shows the Clip card only for a clip selection or in the Session View.
79. **Sync is in the settings list.** Link, start/stop with Link peers, MIDI clock in and the outputs MIDI clock goes to are entries of a new section `sync` in `AppBackend` (`sync.Link`, `sync.LinkStartStop`, `sync.MidiClockIn`, `sync.MidiClockOut`, the last as output names separated by "|"), so `settings_get`, `settings_set`, `settings_reset` and `settings_history` cover them and the Sync page builds its rows from the entries like every other page (`AddSection("sync")`), its changes logged as Manual. Only the list of outputs is drawn by the page, and it changes the setting `sync.MidiClockOut` through the same service. The separate-page exception of decision 73 is gone; no constraint stood in the way. A Link that does not start makes `settings_set` fail with the helper's reason (and the window's row shows it and the box goes back).
80. **One global transport.** The top bar is the only transport: ⏮ ▶ ■ ● Loop, the metronome, the time signature (a box, beats over four: it sets the session's beats per bar), the tempo with "BPM", Tap, the position as bar.beat.tick, minutes and seconds, and the master meter; the groups wrap whole when the window is narrow (the meter is the last group): at 640 pt it takes two rows, 36 pt more than before, so the layout test of decision 35 runs at 640 by 444 (the Tracker's chooser bar needs it). The tempo is a `TempoField`: drag up or down (3 pt a BPM, Shift ten times as fast), scroll, or double-click to type (Enter keeps, Esc puts the old one back, ↑ ↓ step); a drag is one undo step (the tempo's changes in a 500 ms run merge). Tap is the session's tap tempo (from the third tap it follows the last four). The position is the Session View's clock while the session plays (bars count on from where a scene changed the signature), else the song position; minutes and seconds are the song position at the current tempo (a guide: tempo changes by scenes are not followed). The Session View loses its second one: Rec, ♩, Beats/bar, the tempo text, Tap, its clock and its dropout button are gone; it keeps Stop all, Overdub, Capture, Back to arrangement, ↶ Launch, Follow, Launch quantization, the tempo nudge ◀ ▶ and the sync text, and its ⚙ menu keeps Metronome and the MIDI learn of Tap. The audio-health badge stays the window's, at the start of the status line, and a click opens Preferences ▸ Audio.
81. **The master meter has a scale.** Two peak bars on −60…0 dB, the numbers −60, −40, −20, −10 and 0 under them, falling at 60 dB a second and refreshed by the meter itself at 30 Hz while it is shown (it was 10 Hz with the window). The clip light is as before: red with the highest over ("+3.2 dB") once the signal went above the clip threshold, latched until clicked; the LIM lamp lights while the safety limiter turns the output down.
82. **Boxes and wires brighten with the level.** From the engine's own post-fader peak of each connection (`SessionMixer.PeakOf`), taken on the UI thread thirty times a second while the Machine View is on screen; the audio thread does nothing new (the one pass over each block that the Session mixer's meters already use, which the view turns on and off). A wire's line goes from its colour towards green, yellow from −6 dB, and from 1 to 2.5 pt thick; a box shows the loudest wire out of it (the Master: of those into it) as a veil of white up to 27 % and a 4 pt strip along its foot as wide as the level, on the −60…0 dB scale. Levels fall 60 dB a second and the canvas draws again only when one moved visibly; a silent song looks as it always did. A box shows the level after the wire's own amp (the connection's peak is post-fader): a machine sending at zero shows nothing.
83. **Measured on the 36-machine song (mac-510)**: see the progress note below (not separable from the load on the shared box); the meters add a take of the peaks on the UI thread and a draw of the same frame with a few extra rectangles and lines.
84. **The master meter is calibrated, and the song doesn't clip.** Mac gallery shot 342 read "+5.1 dB" with the clip light on: that was the shot's own test signal (a buffer of 1.8, 20·log10 1.8 = +5.1 dB, pushed through the safe master to show the latch), not the demo song. `UxP3hTests.TheMasterMeterReadsTheEnginesOwnPeakOnTheDemoSongAndItDoesNotClip` renders the Drop scene of the demo song for 160 ticks as the audio thread does, takes the largest sample the engine produced and runs the same blocks through `SafeMaster`: the engine's master peak is -1.97 dBFS (0.797), the meter's bars read -1.97 and -2.80 dB (within 0.1 dB of the engine's peak), the clip light stays off. The gallery's caption now says the shot's signal is a test signal. (Neo Analyzer's true peak was not compared: the sample peak of the master is what the bars show.)
85. **A machine's box wears its category's identity colour (§3.1.1).** In the dark theme the body is the identity colour at 25 % over the panel surface (10 % when muted) and the colour whole in a 3 pt band along the top edge (45 % when muted); the name stays 4.5:1 or more on the body in all ten categories (5.6:1 the worst, Control; the band against the body is 3.09:1 or more, a UI part), the colour is 5:1 or more on a view. The category is the machine browser's (`MachineIdentity.CategoryOf`: the catalogue's category by its place in the ten, else a control machine is Control, a generator Synth, an effect Utility, as `Tokens.CategoryOf` does for manifests); the Master has none and stays the neutral grey, a machine the guard muted stays red, and Classic Buzz keeps its beige boxes. The old identity-neutral ramp (decision 19) now serves only the Master and Classic.
86. **Its track and clips take it as their default (§3.4).** A clip with no colour of its own fills with its machine's identity colour in the Session View (the label is white or black by contrast, ≥4.58:1), the Session track header's top strip and the Arrangement's track headers (a 4 pt strip at the left edge) show it, and an Arrangement block with no colour of its own (a pattern the song never coloured) wears it, where it wore one of the palette by its place among the machine's patterns; Classic Buzz keeps that. **Nothing is written into the song:** the colour is worked out when drawn (`UxP3hTests` checks `PatternColors` and the track's index stay -1), a colour the user picks wins (the menus' index, saved as before), and "Machine's colour" (was "None") on a clip's and a track's colour menu takes it back; a scene's menu keeps "None". A palette entry that is a state colour is still not offered (§3.1).
87. **Brightness is on a dB curve.** A meter's level is -60 to 0 dB as 0 to 1 (decision 82) and what is drawn is its square (`MachineCanvas.Glow`): a path at -30 dB glows a quarter as much as one at 0 dB. Wire colour mixes from the track grey by 12 % + 88 % of the glow and its width is 1 + 2·glow pt (was 35 % + 65 % of the level, 1 + 1.5·level), the box veil is up to 35 % white (was 27 %), the box's strip stays linear in dB. Measured on a wire's pixels (R+G+B, headless): silent {SILENT}, -40 dB {Q40}, -20 dB {Q20}, -3 dB {Q3}.
88. **P3g polish.** (a) The inspector's clip card shows for the Session View, and for a clip selection in the other views **except the Machine View**, where a machine is picked and a pattern the selection still holds (from a clip chosen in the Session View) is not the point (gallery mac-341 showed Hook, Launch, Region, Follow action and Scene on Lead). The selection is unchanged (P2c: the machine and its pattern stay selected across views). (b) The gallery empties the device view's search and the browser's search after every shot (mac-341 showed "cutoff", "No parameter matches": shot 028 threw, as Neo FM6 has no cutoff, and never reset it; 028 now searches "feedback"). (c) `settings_set` and `settings_reset` for `sync.*` run `SessionSync` on the UI thread (`AppBackend.OnUiThread`: at once when already there, else `IUiDispatcher.Invoke`, what it throws comes back as the tool's error); `UxP3hTests.SyncSettingsSetByTheAgentRunOnTheUiThread`. (d) The Session View's per-session dropout reset button (removed in decision 80) is on the audio badge's menu (right click): Audio settings… and Reset dropout counter. (e) The Mac gallery job has a step "Frame times, meters on and off": `UxFrameTimeTests` (category UxMeasure) on the 49-machine song, the lines `[ux-measure]` of the log.
89. **Empty states (P3i).** One component, `EmptyState` (a 15 pt title, 12 pt text in the secondary colour, 13 pt buttons; text 4.5:1 or more on a view and a panel), in every view; it says what is missing and gives the step that makes it stop being empty. In a song with only its Master the steps are the three starters (Neo Poly, Neo Drums, Neo Wave: one click, one undo step, the machine selected) and **Add machine…**; a song with machines but no generator gets Add machine… alone. Arrangement: no tracks → that; tracks and nothing arranged → "New pattern here" (creates one at the cursor). Session: no tracks → that; tracks and no clip → "Create a clip" (the first slot). Machines: the starters, as P3d made them. Tracker and piano roll: no machine (or the Master) → that; a machine without patterns → "New pattern"; patterns but none chosen → "New pattern" and a line to choose one (the piano roll's own message no longer shows under it). Device: no machine selected → that (the dock had shown nothing until a machine had been selected and deselected). History: no steps → "Your edits show up here" and the same. Browser: its first rows are the starters (P3d); "No patterns yet: make one in the Tracker"; a search with no result says "Nothing matches “x”: clear the search" (the popup: "try one word, or clear the category"). The state follows the song (a 4 Hz timer in the Arrangement, the views' own events elsewhere). Windows' WPF app is unchanged.
90. **First sound from an empty song takes 3 steps (from 7).** `UxP3hTests.FirstSoundFromAnEmptySongTakesThreeSteps` performs them headless: the Neo Poly starter (1), the keyboard opened under its device view (2), a key pressed (3); the master then carries {SOUND} dB RMS over the first 0.6 s.

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

Left for later: the Session View's and the shell's paddings stay as they are (P4); the amp popup and the piano roll keep their own dark tables; P2c (one selection) is the next step.

### P2c: one selection, the base surface (`ux/overhaul-2c`)

Built: `StudioSelection` (§10, 23-25) published and followed by the Machine, Sequence, Session and Pattern views; the window ground on the base surface. Tests: `UxP2cTests` (the model; select a machine in the Machine View and every other view is on it; the Sequence cursor, a Session clip and a Pattern choice each selecting the machine, pattern and scene in the others; nothing selected is the song; no echo, counted; the Session keeps its scene; the tab strip and the status bar are #141416, measured from the window's pixels).

mac-410 (Neo Limiter), mac-413 (Neo Sampler) and mac-414 (Neo Scope) differed between the P2a and P2b galleries by 85, 333 and 132 pixels, each at anti-aliasing level (at most 34 summed channel levels): the limiter's meter column, the sampler's waveform and the scope's trace, the editors that draw live signal. Every static part is identical, the other editors (mac-401, mac-402) are identical, and two runs of one build are identical to the pixel (mac-410, 413, 414: 0 differing pixels), so it is the signal the demo song feeds them at the moment of capture, which changes with the build's timing, not a colour or layout change.

### P3a (first step): the inspector (`ux/overhaul-3a`)

Built: decisions 27-30. Tests: `UxP3aTests` (the song when nothing is selected; the selection followed in the Machines, Sequence, Session and Pattern views; a pattern's facts and its button; a machine the song lost; the clip panel in the inspector and out of the Session View; clip settings edited from the Machines and Sequence views; a Session click shown in the panel with its scene; the song's clip-panel setting untouched; Song Notes as the Song tab; F10, ⌘5 and the menu opening it when collapsed; collapse to the rail and back; ⌥⌘I in every view including the tracker; the View menu's entry; width and state remembered and clamped; dragging the edge; the Clip button; the text's contrast on the tokens). The tests of the old Song Notes tab (the tab lists, ⌘5, the lifetime check, F10, Space) now go through the inspector.

Changed outside the inspector: the window's `EditorKeepsKey` no longer claims Alt-modified keys (⌥⌘I is not the tracker's ⌘I); the Session View's `IInspectorLink` and `ClipPanel.UseInInspector`; `StudioViews.All` has four views.

Measured (Linux, 2 cores, `UxFrameTimeTests`, the inspector open): draw Machines 7.6 ms, Sequence 10.2 ms, Session 12.5 ms (P1: 8.3 / 9.3 / 13.9). Text on the panel: primary and secondary text at or above 4.5:1 on the panel surface, the shown tab's label on its fill likewise (`ItsTextMeetsTheContrastFloorOnTheTokens`).

### P3a (second step): the browser and the detail panel (`ux/overhaul-3a2`)

Built: decisions 31-36. Tests: `UxP3a2Tests` (the keys on each platform; both panels closed at first and toggled by their keys in every view and by the View menu; the browser's list of machines and of the selection's patterns, the search, a click and a double-click on a pattern, a double-click adding a machine in one undo step; width, height and state remembered, clamped and dragged; the Pattern view as the Tracker in the detail panel and no longer a tab; F2 and ⇧⌘F maximising and going back; the Edit commands going to the Tracker while it has the keyboard; a narrow and a low window; the text's contrast on the tokens). Tests that clicked the Pattern tab now open the Tracker (maximised where they click its grid); `Studio.ResetPanels` closes both panels after each test (their state is a setting kept across windows).

Measured (Linux, 2 cores, `UxFrameTimeTests`, a busy machine, three runs, panels closed): draw Machines 4.5-8.0 ms, Sequence 9.4-11.2 ms, Session 9.7-11.6 ms (first step: 7.6 / 10.2 / 12.5).

### P3a (third step): the switcher, the renames, narrow windows (`ux/overhaul-3a3`)

Built: decisions 37-39; **P3a is done** (inspector, browser, detail, switcher, renames, maximisable Tracker). Tests: `UxP3a3Tests` (the switcher's segments, the lit one following every way of changing the view, the click; `ShowView("Sequence")` as the alias; the tab strip hidden; a 640 x 400 window with no overlap and no clipping in the switcher, the Session toolbar and the docked Tracker's chooser bar; the Song tab stacked at the default width; the contrast of the segments on the tokens). Existing tests that keyed on the "Sequence" tab header now use "Arrangement".

### P3b (first step): the shared pattern, the grid, editing and undo (`ux/overhaul-3b`)

Built: decisions 40-44. Tests: `UxP3bTests` (the two pages of one pattern and the key, the menu and the setting; a machine with no note column; drawing in the roll read back in the Tracker with one undo and redo; the mouse drawing, moving, resizing and deleting with one step each; the grid and Alt's bypass; the keys, including the platform's command key; note-off rules (the next note-on ends a note, the last runs to the end); a chord typed in the Tracker shown in the roll and one note moved with the others' events exactly as typed; a chord refused with its reason and no undo step; the MIDI lanes edited with a step a gesture and undone to the lanes as they were; the store that has notes shown and the other offered; the selection and the choosers followed; undo and redo from the Tracker's side showing in the roll; **the song saved byte-equal after looking at the roll, and after an edit saved, opened and read back as the Tracker showed it**; only the visible part of a 1024-tick pattern with 400 notes drawn; note names following Preferences; the text's contrast on the tokens). Existing tests changed: only `Studio.ResetPanels`, which also resets the page. The gallery has a new shot, `058-piano-roll`.

Changed outside the Studio: the Modern Pattern Editor gets `EditTrackerEvents`, `GetTrackerBeatRows` and `EditClipNotes` (new methods beside `ExportClipNotes`, found by name like it; `MPEEventChangesAction` and `MPELaneNotesAction`); `ManagedMachineHost` and `MachineManager` pass them on; `TrackerColumnNotes` is no longer sealed (its store, row fit and commit are overridable; its behaviour is unchanged). Editors without the methods (the Windows fake editor, PatternXP) get the message instead of a roll. Songs: nothing a song saves changes meaning.

Left for 3b2 (done below): the bar/beat ruler, the playhead, the loop brace, the velocity lane and the clipboard.

### P3b (second step): ruler, playhead, loop brace, velocity lane, clipboard (`ux/overhaul-3b2`)

Built: decisions 45-49; **P3b is done**. Tests: `UxP3b2Tests` (the ruler's lines for 4/4, 3/4, a bar that is not whole beats and a zoomed-out one; the ruler, the roll's bar and Grid: bar following the song's bar list and ticks a beat; the playhead following the pattern's play position, Follow on and off, gone when stopped; a drag on the ruler setting the song's loop in the pattern's place as one undo step, an edge dragged, the roll's Undo and Redo taking it only when it was the last gesture; a pattern outside the song setting no loop; a velocity drag on one note and on a selection of two (relative), one undo step each; copy, paste, cut and the keys, the paste points (pointer, playhead, last click), the edge of the pattern, an empty clipboard; the song saved byte-equal after the ruler, playhead and copy; the text's contrast on the tokens). Existing tests changed: `UxP3bTests`' rig and helpers are internal now, nothing else.

Changed outside the roll: `PianoRoll` (also the Session View's) got `BarTicks`, the clipboard keys, `InsertTime`, `PlayheadTime` and selection/edit calls for the lane; its Session View behaviour is as before except ⌘X/⌘C/⌘V now cut, copy and paste notes there too. Nothing a song saves changes meaning. Frame times: not measured at low load (the box was busy); the playhead repaints a line over the viewer, the ruler and lane draw only what is in view.

Left for later: the Session View's engine-column notes in this roll; a loop of the pattern's own (the Session clip's loop markers) on the ruler; a time-signature change inside a pattern on the ruler.

### P3c: the pattern editor, notes first (`ux/overhaul-3c`)

Built: decisions 50-55; **P3c is done**. Tests: `UxP3cTests` (column order and the cursor's start; empty columns hidden, offered, shown, and a column with data shown after save and open; the headers' size, two lines and contrast; the rules' 3:1 measured from the frame's pixels; drum names and pitches; decimal and hex display and entry, the toggle; the cursor row centred and Centre off; the editor's data byte-equal after only looking). Existing tests changed: the editor tests that reached columns by Tab and arrows now go by column name (`EditorNav.GoTo`), and run in hex (`PatternEditorTests`); the Dye stress test shows every column first. C3 note names confirmed by the user 2026-11-12 (§3.5).

Left for later: a play-follow test (the headless engine render is needed); time-signature changes inside a pattern on the bar rules; Neo Sampler's `KeyShortNames`.

### P3d: the machine browser and port dots (`ux/overhaul-3d`)

Built: decisions 56-61; **P3d is done**. Tests: `UxP3dTests` (search ranking for drum, reverb, compressor, bass, "warm bass"; tags, use_for and brand searchable and shown; the ten categories and the filter; recents and favourites kept in the settings and read by a new model; panel and popup on one model; double click then Enter adds where clicked and one undo removes it; typing seeds the search and Esc adds nothing; starters add and undo; output dot to input connects, hover tip, undo, Shift-drag unchanged; a refused drop leaves the graph and says why; contrast of tags, use_for, star, refusal, dot at 4.5, 4.5, 4.5, 4.5, 3). Old tests changed: the browser's headers and its search expectation (UxP3a2Tests), the single transport keys select a machine first (UxQuickWinTests). Gallery: mac-017, 025, 026.

Left for later: Presets and Samples in the browser; a recents/favourites page in Preferences; the Neo family brand favoured in ties was not done.

### P3e: the device view (`ux/overhaul-3e`)

Built: decisions 62-70; **P3e is done**. Tests: `UxP3eTests` (grouping from indents, prefixes and none; the manifest's groups, the track table and the Inputs section; search and ⌘F; sections closing; 24 pt rows and the fill from zero measured in pixels, for a plain and a bipolar slider; units; a typed value with its unit, wrong entries and Esc; ⌥-click reset; a drag as one step and arrow steps merging; segmented controls and toggles; the macro strip; the track table; a 433-parameter machine (Dye Essence) opened and drawn with 18 items against the old window; Explain with and without How and Sound, and Neo Learn Subtractive's real How and Sound under the focused control; contrast of the view's colours at 4.5 and 3; docked in Detail and following the selection, ⌘F in the window, Float and Dock, the richest view on a double click, the Editor | Parameters switch). Gallery: device-neo-poly, device-neo-poly-choices, device-pedal-chord-table, device-dye-essence, device-docked.

Left for later: presets, edit functions, MIDI binding, attributes and locks in the device view (the classic window has them); pinning macros; keyboard focus rings on the segmented control's segments; Windows (P6) needs the same Float window under WPF.

### P3f: Preferences, one window (`ux/overhaul-3f`), with the P3e polish

Built: decisions 71-78; **P3f is done**. Tests: `UxP3fTests` (the pages and their order; every setting of General, Advanced, MIDI and Appearance is a control under the agent's key and Safety's four are the agent's; a change in the window is in settings_history as Manual, read by the tool; an agent's `settings_set` shows in the open window and is logged as Agent, for a general and a safety setting; a restart-required setting's note, on the row and under the pages; Restore Defaults asks on Advanced and Safety and puts them back; a value the tool would refuse is refused; the MIDI table; the switcher's segments focusable and Space left to the window; the device list's room, collapsed keyboard remembered, macros, the inspector's cards) and `AppControlTests` in AgentTests (Agent and Manual sources, `Changed`, the shared checks and reset). Gallery: device shot 027 as before (now with the room), prefs-safety, prefs-advanced and the other pages (300-319), 322-prefs-restart-note, 323-prefs-restore-question; the device-room and switcher-focus shots are test screenshots.

Left for later: the restart line counts changes since the window first opened; the Windows app keeps its own Preferences (WPF) until P6.

### P3g: Feedback (`ux/overhaul-3g`), with Sync in the settings list and no type-to-search

Built: decisions 79-83 and decision 58 changed (the user's choice, 2026-11-13); **P3g is done**. Tests: `UxP3gTests` (letters keep their shortcuts on an empty canvas and the Add Machine key opens the browser from another view, in the View menu on both platforms; Sync's four settings in `settings_get`, set by the tool as Agent and by the window as Manual, the output list, Restore Defaults; the top bar's metronome, signature, tempo, Tap, bar.beat.tick and minutes and seconds; the tempo dragged, Shift-dragged, scrolled, typed and refused out of range, a drag one undo step; the Session View without a transport of its own; the master meter's scale, falls and latching clip light; the window's badge opening Preferences ▸ Audio; levels on boxes and wires from the engine's real peaks while a scene plays, the scale, the fall, and the pixels of a lit wire against a silent one), `UxFrameTimeTests.ManyMachinesFrameTimes` (explicit). Old tests changed: the typing test of decision 58 (UxP3dTests), Sync (PreferencesTests), the Session View's clock, beats per bar and tempo text (SessionViewTests), the BPM box in the transport (ExitTests, TransportKeysTests, SequenceEditorTests). Gallery: mac-340 (the Session View without its transport), mac-341 (the Machine View while a scene plays, real engine peaks), mac-342 (the top bar and the master meter with a latched clip), mac-343 (the 36-machine song with a level on every wire).
Frame times (`UxFrameTimeTests.ManyMachinesFrameTimes`, Linux headless, 49 machines and 46 wires, the window drawn whole in the Machine View, median of 40; Linux, 2 shared cores): the box was never quiet enough to separate the meters from the noise (the 1-minute load was 2.0 to 2.4 when a run began and 3.1 to 3.6 by its end, the test itself adding to it): the same configuration measured 10.5 to 20.6 ms from one round to the next, meters off 13.1 to 17.4 ms, meters on and silent 10.4 to 19.3 ms, meters on with a level on all 46 wires 10.5 to 20.6 ms, so there is **no figure that separates them** (decision 83's budget, within 10 %, is not shown either way here: the gallery run's and a quiet machine's figures are the ones to read). What is measured cleanly: taking the peaks costs 0.014 to 0.044 ms a take, thirty takes a second (under 0.15 % of a core), the draw adds a veil and a strip per lit box and a cached pen per lit wire, and the audio thread has nothing new.

### P3h Colour per machine and P3i Empty states (`ux/overhaul-3h`), with the P3g polish

Built: decisions 84-90; **P3h and P3i are done**. Tests: `UxP3hTests` ({TESTS}). Gallery: mac-018, 019, 029, 034, 044, 046 (empty states), mac-345, 346 (machine colours on the Session View and the Arrangement), mac-341 to 343 redrawn (identity boxes, dB curve); mac-028 searches "feedback" now.
Frame times in the Mac gallery job (`Frame times, meters on and off`, run {GALLERY}): {FRAMES}
