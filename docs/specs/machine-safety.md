# Machine safety: unhealthy machines, a guard for every machine, and a safe master

| | |
|---|---|
| Status | Decided 2026-11-03: H1 a global safeguard for every machine, plus a safety limiter on the master output with a clip indicator; H2 A; H3 A; H4 A. **§3.2 and §3.3 built** (§3.6); §3.1 and §3.4 built 2026-11-03 (U2 health); **§3.5 built** (§3.7); §4's first successor, **Neo Ensemble, built** 2026-11-04, and the second, **Neo EQ's graphic mode, built** 2026-11-04, with Joachims Hyperion's `machine_replace` pair |
| Scope | What the profiler's "unhealthy" means; how ReBuzz keeps any machine from blowing up the song or the speakers; how agents stay in safe ranges; which machines get Neo successors |
| Related | mcp-server.md §12 (machine upgrades U1–U7, `docs/machine-health.md`), machines.md (Neo machines), studio-mvp.md (the studio), daw-modes.md (AR0 runs beside this), plugin-host.md (plugins get the same guard) |

---

## 1. What "unhealthy" means today

The profiler (U1, `tools/machine-profiler.py`) calls a machine unhealthy when it doesn't load, crashes or hangs the server, or puts out NaN, infinity or a peak above +24 dBFS. It tests at the defaults, with test signals, and with **every parameter at its minimum, then at its maximum, all at once**. That last test is blunt. Of the four machines it flagged, read from their parameter ranges (to be confirmed per parameter, §3.1):

| Machine | Source | Found | Likely cause | Verdict |
|---|---|---|---|---|
| Jeskola Flanger | closed | +157 dBFS at all-min, +149 at all-max | Feedback runs −100 %…+100 %: at either end the loop never decays | **A real fault**, at the ends of one parameter |
| Joachims Hyperion | closed | +221 dBFS at all-max | A 30-band graphic EQ: every band at full boost, at the widest bandwidth, adds up | **The test.** Found hot per band, not diverging (§3.1); measured on Windows (§4), every band is a stable cookbook bell, the 20 kHz one included, and the bands simply add (every band at +6 dB at the default width is +43 dB). **Successor built:** Neo EQ's graphic mode, with a `machine_replace` pair (§4) |
| Jeskola Bass | closed | +25 dBFS at all-max (Volume ≈198 %, resonance max); +2 dBTP at the defaults | Loud, bounded, 1 dB over the line | **Hot, not diverging** |
| Info (WDE, `Info.NET.dll`) | binary only | doesn't load headless | A WPF rich-text song-notes machine: it needs a window | **Not an audio fault** |

The 14 warnings are mostly the profiler's own limits: drum machines played by triggers, audio inputs, a VST host with no plugin, a tracker without waves.

## 2. Decisions

| # | Decision | Decided |
|---|---|---|
| H1 | Protect against a machine blowing up | **A global safeguard for every machine** (§3.2), **plus a safety limiter on the master output** with a clip indicator that shows overs above +X dB, as Ableton's master meter does (§3.3) |
| H2 | Joachims Hyperion | **A:** a graphic mode for Neo EQ (31 bands at the ISO centres), with a mapping for `machine_replace` |
| H3 | Info | **A:** a Song Notes panel in the studio, saved with the song, importing Info machines' text |
| H4 | Timing | **A:** layers 1–3 (§3.1–§3.4) now, beside AR0 (≈2 weeks). Successors (§4) with the next machine batch, Neo Ensemble first |

## 3. Now

### 3.1 Layer 1: measure precisely (profiler, part of U2)

- **Each parameter alone** at its minimum and maximum, the others at their defaults, for every machine that misbehaved all at once (and every effect with a feedback, resonance, gain or Q parameter by name). The draft records which parameter and which end.
- **Diverging, hot or needing a window**, instead of one "unhealthy" label:
  - *diverging:* NaN or infinity, or a level that keeps growing over a 4 s render (the last second louder than the second by more than 6 dB, or above +60 dBFS);
  - *hot:* loud but steady (above 0 dBFS, under +60 dBFS, not growing): a warning with the setting, not unhealthy;
  - *needs a window:* a managed machine whose construction fails without a UI (WPF types in the exception, or a known GUI-only machine): its own status, not unhealthy, and `gear_list` says "works in ReBuzz on Windows with its window".
- **`safe_range` per parameter** in the draft (min and max raw values) where a sweep finds the edge, with the reason ("feedback at ±100 % diverges").
- Re-profile the natives in the Windows package run and the Pedal machines on Linux; `docs/machine-health.md` shows the three categories.

**Built** (2026-11-03, `tools/machine-profiler.py`). A *level test* renders 4 s of the machine alone: effects fed white noise from a Neo Poly (peaks near −9 dBFS), generators holding A4 as they take notes. It runs at the defaults, with every global parameter at its minimum and at its maximum at once, and, for machines that misbehave that way (diverging or hot) and effects with a feedback, resonance, gain or Q parameter, each parameter alone at each end (global and track parameters). Details:

- Each change gets a 0.5 s render first, so machines that smooth a parameter only while sound flows (Neo Utility's gain) get there before the test.
- A level that grew over 4 s is rendered again for 30 s: an attack settles; a runaway keeps growing (the last 10 s more than 6 dB over the 10 s before). A long attack at its maximum (Neo Wave's, with every parameter at its maximum) otherwise reads as growth.
- With the engine's guard (§3.2, §3.6) no NaN or level above +60 dBFS reaches a render: `render` lists the machine as diverged, the profiler counts it as diverging (with the guard's reason) and resets it (`machine_set(reset=true)`).
- **Never decays** (decided after the first run): a steady end whose level still rose 2 dB or more over the 4 s render is rendered for 30 s; when its peak rises 2 dB or more per doubling of time from 4–8 s to 8–16 s and again to 16–30 s, the loop never decays (a lossless loop, feedback at ±100 %). That end gets a safe range too, for agents, with the reason "never decays"; the machine stays hot (a warning), not unhealthy.
- Where an end diverges, never decays or crashes the server, a bisection from the default finds the edge: the draft's `safe_range` (raw `min`, `max`, the machine's texts there and the reason). A render that diverges leaves the machine's state running away, so the song is built again after one.
- A machine that diverges at its defaults is tried in a second fresh instance, which tells a fault from one that varies with uninitialised state.
- *Needs a window:* WPF names in the exception (the server now reports the cause inside reflection's TargetInvocationException), or a known GUI-only machine (Info).
- The drafts carry `health.diverging`, `health.hot`, `health.needs_window`, `profile.level_test` and `profile.sweeps`. Neo machines keep their generated manifests: their findings are in the summary and the report only.

What it found (89 machines on Windows, 29 on Linux, with the guard on; `docs/machine-health.md`: 4 diverging, 39 hot, none needing a window, 5 safe ranges):

| Machine | Expected (§1) | Found |
|---|---|---|
| Jeskola Flanger | Diverges at feedback ±100 % | **Diverges at +100 %** (the guard trips at +60 dBFS); `safe_range` −100 % … 98.4 %. At −100 % alone it is hot (+24 dBFS) and settles over 30 s, so it keeps that end. With every parameter at its minimum at once (−100 % feedback, no delay) it diverges: a warning, as no single range covers a combination |
| Joachims Hyperion | Hot or diverging per band | **Hot per band**: each band at +18 dB alone peaks at +3 to +7 dBFS; no band diverges. Every band at +18 dB at the widest bandwidth at once: +226 dBFS before the guard, a warning (the test, not the machine) |
| Jeskola Bass | Hot | **Hot**: Resonance at its maximum, +16 dBFS. At its defaults it peaks at 0.0 dBFS |
| Info | Needs a window | **Healthy since a theme fix.** It builds itself from `Themes/<theme>/Gear/Info/RTFBoxInfo.xaml`, falling back to the Default theme's, but only Mire (extended) had one: under the Default theme it failed on that file (`WebException: Could not find a part of the path …RTFBoxInfo.xaml`), not for want of a window. With the Mire (extended) theme's `Gear/Info` folder copied into `RunTime/Themes/Default` (which the build copies whole), it loads in `ReBuzz.exe --agent` and profiles clean: a control machine (its own flag) with no audio output |

Found besides:

- **Joachims Neptune 1.2** puts out infinity with Threshold at its minimum (−100 dB): `safe_range` from −97.7 dB. With every parameter at its minimum it puts out NaN (U1 read that as silence). The server bug U1 found, infinities in `render`'s JSON, came from this.
- **Jeskola Delay** never decays with Feedback at −100 % (+30 dBFS after 30 s and rising): `safe_range` −98.4 % … 100 %, hot. At +100 % it settles; so do Jeskola Cross Delay, NiNjA dElaY, Chorus, Flanger and FSM PhatMan at both ends (hot, no range).
- **FSM WahPro2** put out infinity on noise at its defaults in one run and was clean in the others (U1's and two since): it varies from instance to instance. Hot now, no range.
- **Pedal Plate** diverged with Feedback above 78 (at its maximum, 95, the guard trips): the outer feedback around its Dattorro tank, whose output taps sum to more than 1/Feedback (slowly from about 50 at the default Decay, 10 at full Decay). **Fixed:** Feedback is capped at 0.98 · (1 − d²) / (0.35 · (1 + 3d)), d the tank's decay, under the tank's worst-case gain; re-profiled clean, no `safe_range`.
- **Neo Verb** diverged with High damping below 1.17 kHz (slowly up to 1.4 kHz, 2.9 kHz with Low decay ×4). Its lines are scaled so the decay is exact at 1 kHz, dividing out the high shelf's gain there; with the shelf's corner near 1 kHz that lifts the band below it over unity. **Fixed:** each line's loop gain is held below 1 at every frequency, from its shelves' exact peak (machines.md, Neo Verb); re-profiled clean. Its Shimmer loop ran away too, above an edge that falls with the decay (Ambient — Shimmer included, after a few seconds): **fixed** by a shimmer guard, a shadow of the loop that holds the shimmer down wherever the loop would ring more than 3× longer than the set decay, the rule the damping fix keeps for the network, and only there (machines.md; the user's decision S1, over a fixed fade of 0.5 dB/s).
- **Hot**, a warning naming the settings: most gains, makeups and outputs at their maximum (Neo EQ, Comp, Drive, Utility, Pedal EQ, MComp, Shaper, Jeskola EQ-3, Gain…), Jeskola Filter and Filter 2 with Resonance at its maximum (+44 dBFS), the delays and choruses with feedback at ±100 %, and a few at their defaults (Jeskola Distortion +6 dBFS, FSM WahManPro +7, WahMan, NiNjA dElaY, Chorus, Flanger, Organ).

### 3.2 Layer 2: a guard on every machine (engine, Windows and macOS)

After each machine's `Work`, before its output reaches any input, the engine checks the block it produced:

- **Trips on:** any NaN or infinity, or any sample above **+60 dBFS** (1000 × full scale; nothing musical gets there).
- **Then:** that block of the machine's output becomes silence, the machine is stopped (Buzz's `Stop`, which clears most machines' buffers) and **muted until cleared**. The machine is marked *diverged*:
  - the studio draws it red in the machine view, with "Diverged: muted" in its tooltip and the status line; *Reset and unmute* is on its menu;
  - WPF shows it muted, with a status-bar message;
  - agents see `diverged: true` with the time and the reason in `machine_describe` and `studio_status`, and `machine_set_state` clears it.
- **Never touches normal audio.** A block without a trip passes bit for bit, so every song renders exactly as before (the null and bit-identical tests prove it). No allocation on the audio thread; one pass over each block (max-abs and NaN in one loop, vectorised).
- Covers every machine: native, managed, Neo, placeholders, and the plugin host's machines later (plugin-host.md H1).
- A setting (on by default) can turn it off for a song's session, with a confirmation; it's never saved off.

### 3.3 Layer 2b: a safe master, and a clip indicator (H1)

The final output to the audio device gets a **safety limiter**:

- **Scrub:** NaN and infinity become 0 before anything else, so the device never sees them.
- **Limit:** a zero-latency peak limiter (instant attack, about 50 ms release, then a hard ceiling) at the **ceiling, 0 dBFS by default**. It does nothing below the ceiling, so normal playback is untouched.
- **Where:** on the device path only. Renders and exports (`render`, offline export, the null test) get the true signal, so they stay bit-identical and can be measured; they report their overs instead.
- **Clip indicator:** the master meter (the studio's transport area and the Master machine) turns red when the signal **before** the limiter goes above the **clip threshold, 0 dBFS by default**. It latches the highest over ("+3.2 dB") until clicked, as Ableton's does. A "limiting" lamp shows when the limiter reduces gain.
- **Preferences** (Audio): the clip threshold (+X dB, default 0 dBFS), the limiter's ceiling (default 0 dBFS) and the limiter on or off (on by default; turning it off asks for confirmation).
- **Agents:** `meters_read` and `studio_status` report the latched over and whether the limiter acted; `render` reports overs as now.
- WPF on Windows gets the limiter (it's in the engine) and a minimal clip lamp next to its master volume; the full meter is the studio's.

### 3.6 As built (§3.2, §3.3)

**The guard** (`ReBuzz/Audio/MachineGuard.cs`, called from `MachineWorkInstance` after each `Work`, before `UpdateOutputs`):

- One vectorised compare per block (`|x| ≤ +60 dBFS`; NaN fails it too). A block that passes is not written to. A trip clears the block (every output channel of a multi-I/O machine), and only then is the reason worked out: NaN, infinity, or the block's peak ("+66.4 dBFS").
- It checks native, managed and Neo machines, and the pass-through of bypassed machines and placeholders. The Master has no `Work`; the safe master covers it.
- A diverged machine isn't worked and sends silence. Its `IsMuted` reads true until it is cleared, so every view shows it muted, and a song saved meanwhile saves it muted.
- Buzz's `Stop` and the views' notice run on the UI thread, as the transport's Stop does, not inside the audio pass. The trip posts that one job; nothing allocates per block.
- **Clearing it is unmuting it**: the studio's *Reset and unmute*, WPF's mute toggle, Ctrl+U, `machine_set(reset=true)` or `machine_set(mute=false)`. It is one undo step, and `Stop` runs again first. If the machine is still broken, the guard trips again.
- The studio draws it red with "diverged" and gives the reason in its tip, the status line ("Diverged: muted: Bass (NaN)") and its menu. WPF shows it muted, with a status-bar message.
- Agents: `machine_list` and `machine_describe` give `diverged`, `diverged_at` and `diverged_reason`; `studio_status` lists them under `safety.diverged`. There is no `machine_set_state` tool, so `machine_set` took the `reset` option. A render that trips a machine leaves it muted and says so: `diverged` (machine and reason) and a warning. The profiler reads that as "NaN or runaway output" and resets the machine (`tools/machine-profiler.py`), since a render no longer shows NaN or more than +60 dBFS.
- The off switch is in the studio's Preferences ▸ Audio, after a confirmation. A new or opened song turns the guard on again. WPF has no switch: the guard is always on there.

**The safe master** (`ReBuzz/Audio/SafeMaster.cs`, called at the end of `CommonAudioProvider.Read`):

- Every audio driver (PortAudio, WASAPI, ASIO) reads through `CommonAudioProvider.Read`. Renders (`render`, WPF's offline export through `ReadOverride`, the null test, the bit-identical tests) call `WorkManager.MainAudioFillBuffer` directly, so they never pass the limiter.
- One pass scrubs NaN and infinity and measures the peak. The limiter pass runs only while a frame is over the ceiling or the gain is still coming back. All device channels share one gain. The release is exponential with a 50 ms time constant. Once the reduction is under 10⁻⁶ (about 0.65 s after 6 dB), the gain is exactly 1 and the samples pass untouched.
- The clip indicator latches the highest peak before the limiter that went over the threshold. Without an audio device (headless, tests) nothing reaches it; renders report their overs as before.
- Preferences (registry, Settings: `SafeMasterLimiter`, `SafeMasterCeiling` and `ClipThreshold` in tenths of a dB): the studio's Preferences ▸ Audio ▸ Safety has the limiter (turning it off asks first), the ceiling (0 to −6 dBFS) and the clip threshold (−6 to +12 dBFS). WPF reads the same settings.
- Studio: a master meter in the transport bar shows two peak bars before the limiter, the clip indicator ("+12.0 dB", click to clear) and a LIM lamp. The Master machine gets a red frame and the over, and "Clear clip indicator" on its menu. WPF: a clip lamp and a LIM lamp next to the master volume in both toolbar themes; a click clears the clip lamp.
- Agents: `studio_status.safety` gives the guard, the diverged machines, the limiter (on, ceiling, and `acted`: it caught a peak over the ceiling since the indicator was cleared; its release doesn't count) and the clip indicator (threshold, latched over). `meters_read` returns `safety` (latched over, whether the limiter acted during the window) and takes `clear_clip`.
- Upstream's opt-in "burst protection" (DC block, slew limit, soft clip; Preferences ▸ Engine) is separate and stays off by default.

**Tests** (§5): `ReBuzz.Studio.Tests/SafetyTests.cs` on Linux and macOS, with a test machine built into the tests' gear (`TestMachines/GuardTest.cs`): a feedback loop at gain 1.01 and NaN after N blocks trip the guard while a steady sine plays on; the views, the status line, the menu, Reset and unmute, its undo and the agent tools follow it. Passing audio is bit-identical with the guard on and off, and the guard's check and the safe master allocate nothing. A +12 dBFS sine read through `CommonAudioProvider.Read` peaks at the ceiling, latches +12.0 dB (transport, Master, `studio_status`) and still renders at +12 dBFS. With a −3 dB ceiling the output stays under it; with the limiter off it doesn't. The preferences ask before switching off. `tools/headless-live.py` limits a hot master on a real device (Linux null sink, the macOS runner). The null test, the bit-identical renders and the 0.4.1 allocation test (`ReBuzzTests`, Windows) run unchanged with the guard in the path.

### 3.4 Layer 3: safe ranges for agents

- `param_set` keeps an agent inside a parameter's `safe_range` (from the draft or a curated manifest). Outside it the call is refused with the reason, unless the agent passes `allow_unsafe: true`. People keep the full range in every UI.
- `gear_list` and `machine_describe` show the safe ranges; `machine_add` warns only for diverging machines.

**Built** (2026-11-03). `safe_range` is read from any manifest (`ManifestParameter.SafeRange`), so curated ones can carry it too.

- `param_set` and `automation_write` (decided after the first run: every agent tool that writes parameter values) refuse a value outside the range: the whole call fails, nothing changes, and the error names the value, the range in the machine's texts and raw values, and the reason ("Feedback = 95 (raw 95) is outside its safe range, 0 … 78 (raw 0–78): it diverges at its maximum (95: …). Choose a value inside it, or pass allow_unsafe=true…"). With `allow_unsafe: true` the value is set and the change marked `unsafe` with why (`automation_write`: a problem line). A ramp between two safe points stays inside. Presets, snapshots and machine data imports are left as they are.
- `machine_describe` gives each ranged parameter a `safe_range` (`range`, `min_raw`, `max_raw`, `reason`), and the health its `diverging`, `hot` and `needs_window`. `gear_list` adds `safe_ranges`, `diverging` and `needs_window` ("Works in ReBuzz on Windows with its window…"), and lists machines that need a window after the drafts and before the unhealthy ones. The machine manual (`guide(machine=…)`) has a "Safe ranges" section, and the troubleshooting guide says what the categories mean.
- `machine_add` warns only for diverging gear, naming its safe ranges. Gear that needs a window says so when it can't be built.
- Safe ranges now: Jeskola Flanger (feedback, +100 %), Joachims Neptune 1.2 (Threshold, −100 dB), Jeskola Delay (Feedback at −100 %, never decays). Not Pedal Plate or Neo Verb: their loops are fixed in their source. Above all of them the guard (§3.2) stops what gets through.

### 3.5 Song Notes (H3)

- A **Song Notes** panel in the studio (a tab or a side panel): rich text (bold, italic, lists, links), saved in the song as a "SongNotes" subsection, in .bmx and .bmxml.
- **Import:** opening a song with Info machines shows their text in Song Notes (read from the placeholder's kept data), marked as imported, with the machine left as it is so the song saves back unchanged.
- MCP: `song_notes_read` and `song_notes_write` (or fields on `studio_status` and `song_set`), so an agent can leave notes.
- Windows' WPF UI keeps the Info machine; the panel comes with P4.

### 3.7 As built (§3.5): Song Notes

**Info's saved format** (WDE's "Info View", `RunTime/Gear/Generators/Info.NET.dll`, gear name `Info`, a net8.0-windows managed generator: `WDE.Info.InfoMachine` with a WPF `RTFBoxInfo`). Read from its metadata and IL, then checked on Windows CI with the DLL itself (§5):

- Info's `MachineState` property is a `WDE.Info.InfoMachine+State` with `Text` (string), `Background` and `Foreground` (`System.Windows.Media.Color`, kept as ARGB). Its getter fills `Text` from the box: `TextRange.Save(DataFormats.Rtf)`, so the notes are **RTF as WPF's RichTextBox writes it**.
- ReBuzz saves any managed machine with a `MachineState` as its machine data (`MachineManager.GetMachineData`): **byte 2** (the version), **int32 size**, then **the State as `XmlSerializer` writes it** (UTF-8 with a BOM, CRLF, indented): `<State xmlns:xsi=… xmlns:xsd=…><Text>{\rtf1 …}</Text><Background><A/><R/><G/><B/><ScA/><ScR/><ScG/><ScB/></Background><Foreground>…</Foreground></State>`. Opening, `SetMachineData` skips the 5 bytes and deserializes the rest.
- WPF's RTF: a font and colour table (colour 0 is black), `\listtable`; each paragraph a group ending in `\par`, with `\fsN` and `\cfN`; `\b`, `\i`, `\ul` groups; lists as `{\pntext \'B7\tab}` or `{\pntext 1.\tab}` with `\li720` per level; links as `{\field{\*\fldinst { HYPERLINK "url" }}{\fldrslt …}}`; `\line`, `\tab`; non-ANSI characters as `\'hh` (code page 1252) or `\uN?`. Fixture: `ReBuzz.Studio.Tests/Fixtures/InfoMachineState.xml`, what Info's own box and its State type gave on Windows CI.
- Info needs WPF: its `RTFBoxInfo` is a WPF control, and it builds itself from `Themes/<theme>/Gear/Info/RTFBoxInfo.xaml` (in `RunTime/Themes/Default` too since U2 health, §3.1). So it runs in ReBuzz on Windows; off Windows it is a placeholder that keeps its data.

**The notes** (`ReBuzz/Core/Notes/`):

- `SongNotesStore` (the engine, every platform; `ReBuzzCore.SongNotes`): the notes are **Markdown** (`NotesMarkdown`): paragraphs (a line break is a line break; an empty line starts a paragraph), `#`–`###` headings, `-` and `1.` lists nested by two spaces, `>` quotes, `---` rules; `**bold**`, `*italic*`, `~~strike~~`, `` `code` ``, `[links](…)` and bare `https://` links, and three HTML tags notes need: `<u>`, `<span style="color:#rrggbb">`, `<br>`. Markdown because agents read and write it as they are, it diffs in `.bmxml`, and it reads as text anywhere.
- **Saved** in a `SongNotes` subsection, in `.bmx` (the `IUGB` section) and `.bmxml` alike: `<SongNotes version="1" format="markdown"><Text>…</Text><Imported kind="info-machine" machine="Info" hash="…"/></SongNotes>`. A song without notes (and nothing imported) has none, so it saves as upstream does. Upstream ReBuzz reads the subsections it knows and skips the rest; it doesn't write this one back.
- **Edits** (`Write`, and `Type` for typing) are steps on the song's undo history and mark the song modified. A run of typing is one step until a pause of 1.5 s, a new line, or another step.
- **Import** when a song opens (`OpenSong`, after the machines are loaded): each Info machine's text (`InfoMachineState.Read` of `machine.Data`: off Windows the placeholder's kept bytes, on Windows the machine's own state, the same format), through `RtfToMarkdown`: paragraphs and line breaks, bold, italic, underline, strike-through, colour (not the colour most of the text has, nor the machine's foreground, nor a link's), font size (a paragraph ≥ 1.15×, 1.3×, 1.6× the body's size is `###`, `##`, `#`), lists (`\pntext`, `\listtext`, `\ls`/`\ilvl` or `\li`), hyperlink fields; code pages and `\uN`. Fonts, styles, tables, pictures and spacing are left out. **The song's own info text** (Buzz's `BLAH` section, ReBuzz's Info view on Windows) comes in too, as plain text.
  - Each comes in below the notes (under a `###` heading with its source when there is more than one text) and is **marked as imported**: the tab shows which until dismissed, and agents see `imported_on_open`. Importing isn't an edit: the song isn't modified.
  - **The machine and the info text stay as they are**: the song saves back byte for byte the same in every section but the subsections (tested in `.bmx`; `tools/song-roundtrip.py --compare` in both formats).
  - The subsection remembers what came in (a hash of each text as Markdown), so it comes in once. A text changed since (Info edited in ReBuzz on Windows, which keeps the machine) comes in again, below. Markdown, not RTF, is hashed: Info's box may write the same notes a little differently each time it saves.
- **The studio**: the Song Notes tab (studio-mvp.md §7). **Agents**: `song_notes_read`, `song_notes_write` (mcp-server.md §5.2). **WPF on Windows** keeps the Info machine and has no panel (P4); its engine keeps and imports the notes all the same, so they survive a save there and agents can read them.

**Tests:**
- `ReBuzz.Studio.Tests/SongNotesTests.cs` (Linux, macOS): notes typed with the keyboard, formatted with the toolbar and the keys, as they read (`song-notes.png`); undo and redo of typing runs, formats and agent edits on the song's history; save and open in both formats; no subsection without notes; a Windows-made `.bmx` with an Info machine (the fixture's data on a placeholder, `song-notes-imported.png`) opens with its text imported and marked, saves back with every core section byte-equal, opens again without importing twice, and passes `song-roundtrip.py --compare` as `.bmx` and `.bmxml`; a text changed since comes in below; the song's info text comes in as plain text; links open; the agent's tools.
- `ReBuzz.SessionTests/NotesFormatTests.cs` (Linux, seconds): the Markdown subset, escaping, the RTF reader (code pages, `\uN` and `\ucN`, destinations, sizes, colours, Word's and WPF's lists, links, broken RTF), Info's state from the fixture, and bounded time on 200 000 characters of openers that never close.
- `ReBuzzTests/SongNotesTests.cs` (Windows): the real `Info.NET.dll`: its box's RTF and its `State`, serialized as ReBuzz does, read by the importer (saved again by the box, the RTF is the same). A live Info machine (added through the agent, unconnected: it has no audio output) with text in its box: the song saved as `.bmx` and opened again has the text in the notes, imported from the machine's own state, once. The agent's notes save and open in both formats.
- `InfoMachineState.Write` (tests and fixtures) writes what Info's serializer does, to the scRGB digits (`NotesFormatTests`, against the fixture).

**Decided (by the user, N1 and N2):**
- **The notes are Markdown, not a WYSIWYG document** (N1). Avalonia's free controls have no rich-text editor (the Pro one is excluded, studio-mvp.md §1), so the tab is an editor with a toolbar and the format keys beside the notes as they read. A WYSIWYG editor would be a control of our own (caret, selection, IME, undo); not planned.
- **The song's info text (`BLAH`) is imported, never written** (N2). Writing the notes back into it would show them in Buzz and in WPF's Info view, but would change a core section; not planned.

## 4. Next machine batch: successors (H2, H4)

| Closed machine | Successor | What |
|---|---|---|
| Jeskola Flanger (and Chorus, FSM Chorus2, Pedal Chorus) | **Neo Ensemble** (chorus, flanger, phaser) | A new Neo machine, first in the batch. Feedback bounded (±99 %) with a saturating loop, so it can't run away. **Built** 2026-11-04 (below; machines.md, Neo Ensemble) |
| Joachims Hyperion | **Neo EQ, graphic mode** | 31 bands at the ISO centres, constant-Q, in Neo EQ (H2 A), and a Hyperion mapping for `machine_replace`. **Built** 2026-11-04 (below; machines.md, Neo EQ), with its pair file, Hyperion's bands measured on Windows (below) |
| Jeskola Bass (1) | **Neo Poly**, mono/slide preset | No new machine; a mapping (cutoff, resonance, waveform, volume, slide), as for Bass 2/3. **Built** (U5): `Machines/Manifests/Successors/Jeskola Bass to Neo Poly.successor.json`, and Bass 2 and 3 beside it |

**Neo Ensemble, as built.** Chorus (1–4 voices), flanger and phaser (2–12 stages) on one LFO, in Hz or synced to the tempo, zero latency, the dry signal bit for bit at Mix 0 %. Feedback runs −99 … +99 % and can't run away at any setting, however fast the LFO sweeps: the delay loop's reads and its tone filter are convex combinations of past samples (each pass at most 0.99 of the largest), the phaser's stages lossless lattices (the loop's energy gain at most 0.99), and a saturator caps what goes round at +6 dBFS. Its tests: the loop gain below 1 at every frequency over 864 settings (ends included, at most 0.99), and every parameter at each end and the worst combinations at ±99 % under 30 s of noise: bounded (+10.1 dBFS at most, before Output's gain), levelling off and decaying after. Its Linux profile is clean but for Output at +12 dB (hot), so it needs no safe range (machines.md, Neo Ensemble).

It succeeds four machines. Its parameters' roles (from `ManifestRoles`), for `machine_replace`, and how each closed machine's parameters land on them:

| Neo Ensemble | Unit | Role | Jeskola Flanger | Jeskola Chorus | FSM Chorus2 (track) | Pedal Chorus |
|---|---|---|---|---|---|---|
| Mode | Chorus, Flanger, Phaser | `fx.mode` | Flanger | Chorus | Chorus | Chorus |
| Rate | Hz | `mod.rate` | freq ÷ 2 (measured: the LFO runs at half its text) | freq ÷ 2 (measured) | Mod Rate (Hz) | Rate (`mod.rate`, Hz) |
| Sync | Free, 1/32 … 16 bars | `lfo.sync` | Free | Free | Free | Free |
| Shape | Sine, Triangle | `lfo.wave` | Triangle (measured) | Triangle (measured) | Sine | Sine |
| Depth | % (100 %: 1, 2 or 3 octaves either side) | `mod.depth` | from depth and min. delay | from depth and min. delay | from Mod Depth (ms) and Min Delay | from Depth (`mod.depth`, ± ms) and Delay |
| Spread | ° | `mod.spread` | 0° | 0° | Stereo (°) | from Spread (`mod.spread`, %) |
| Delay | s (the sweep's geometric centre) | `mod.delay` | from min. delay (0–5 ms) and depth | from min. delay (0–50 ms; to 30 ms) | from Min Delay and Mod Depth | Delay (`mod.delay`) |
| Voices | 1–4 | `voices` | — | 1 | Fatness (1–6; to 4) | 4 |
| Centre | Hz | `filter.cutoff` | — | — | — | — |
| Stages | 2–12 | — | — | — | — | — |
| Feedback | % (−99 … +99) | `mod.feedback` | feedback (±100 %; to ±99 %) | feedback (±100 %; to ±99 %) | — | — |
| Tone | Hz (open at its top) | `mod.tone` | open | open | open | Tone (`mod.tone`) |
| Mix | % | `mix.wet` | wet out ÷ (dry out + wet out) | wet out ÷ (dry out + wet out) | — | Mix (`mix.wet`) |
| Output | dB | `level` | dry out + wet out | dry out + wet out | — | — |

**Built (U5).** The four mappings are in `Machines/Manifests/Successors/` (`sweep` and `dry_wet` rules, [mcp-server.md](mcp-server.md) §12.10). Each sweep's ends land on Neo Ensemble's Delay (their geometric centre) and Depth (half their ratio in octaves, over what 100 % spans), the levels on Mix and Output. FSM Chorus2's and Pedal Chorus's sweeps come from their source; Jeskola Flanger's and Chorus's were measured (the delay read from input and output over a render, `ReBuzzTests/DelaySweep.cs`): a triangle from min. delay up by depth × 9.92 ms, its period twice what freq says, mono, dry and wet straight in amplitude. A negative dry or wet out (Jeskola Flanger's default wet out is −100 %) has no counterpart: the level is kept, not the polarity, and the result says so; a sweep from no delay keeps its top at full depth (16 : 1 in Flanger mode); Neo Ensemble's Delay stops at 30 ms (Jeskola Chorus's min. delay goes to 50 ms).

**Neo EQ's graphic mode, as built.** `Mode` Graphic and 31 sliders, `G 20 Hz` … `G 20 kHz` at the ISO centres, ±18 dB in 0.1 dB steps, added after Output so songs and presets keep their values; in Parametric mode, the default, Neo EQ renders as before, bit for bit (40 renders pinned). The bands are constant-Q bells (2/3 octave between their half-gain points at every gain), matched to the analog prototype up to Nyquist, so the 16 and 20 kHz bands keep their shape at 44.1 and 48 kHz (within 1.03 dB of it; the bilinear bell misses by 8.6 and 14.8 dB). Unlike Hyperion's, the bands' overlap is compensated: their gains are solved for when a slider moves (the sliders' median as a plain gain, a weighted least-squares fit by Gauss–Newton), so the response passes through the sliders, within 0.19 dB at every centre over 1000 random settings across ±18 dB, and every slider at +X is exactly +X. Every slider at +18 dB is +18 dB, where Hyperion reached +221 dBFS; with Output at +24 dB as well, +42 dB (+32.8 dBFS in the Linux profile, from noise peaking near −9 dBFS). TPT sections in double whose gains glide in 16-sample segments across blocks: sliders jumping every 32 samples for 20 s stay within 11.2 dB of the input's peak, blocks of 256 and 117 give the same output, and nothing allocates. Its Linux profile is hot as before (B4–B7 gain and Output at their maximum), with no safe range (machines.md, Neo EQ).

Its parameters' roles (from `ManifestRoles`), for `machine_replace`, and where Hyperion's parameters land (Hyperion's from its profiled draft, `Machines/Manifests/Effects/Joachims Hyperion.machine.json`, and measured, below):

| Neo EQ (graphic) | Unit | Role | Joachims Hyperion | How |
|---|---|---|---|---|
| Mode | Parametric, Graphic | `fx.mode` | — | set to Graphic |
| G 20 Hz … G 20 kHz | dB, ±18 in 0.1 dB steps | — (no role in the vocabulary names a graphic band; the pair file maps them by name) | its 30 band sliders, `20 Hz` … `20000 Hz` (±18.0 dB in 254 steps of 0.142 dB; raw 128 is 0 dB) | Hyperion's curve read at the 31 ISO centres (below) |
| Output | dB, ±24 | `level` | `Input gain.` (a table, −118.7 … +7.6 dB; raw 95 is 0 dB) | dB to dB (in a linear EQ a gain before it is a gain after it), plus the curve's move when it leaves the sliders' range; beyond ±24 dB the output connections take the rest |
| (the machine's bypass) | | | `Enable` (OFF, ON) | OFF bypasses Neo EQ (U5's `bypass` rule, inverted: an enable switch) |
| — | | | `Bandwidth` (displayed 0.2 … 2.3 octaves in 16 steps, 1.0 at its default; measured 0.37 … 4.51 octaves between half-gain points, 2.0 at the default) | no control of its own: folded into the sliders' values (below) |
| — | | | `Highpass` (displayed 5 … 100 Hz from raw 1; raw 0, the default, off; measured a second-order Butterworth at 1.28 times the display) | into the lowest sliders, after the curve is placed (below) |

**The bands.** Hyperion's 30 centres are not ISO's: they step by 1000^(1/29) (0.344 octave) from 20 Hz to 20 kHz, 20 · 1000^(i/29) Hz exactly (its names round them: "900 Hz" is 903.9 Hz): 20, 25, 32, 41, 52, 66, 84, 106, 134, 171, 217, 275, 350, 440, 560, 710, 900, 1150, 1460, 1850, 2340, 2970, 3800, 4800, 6100, 7700, 9800, 12400, 15800 and 20000 Hz. Each lies within 0.17 octave of an ISO centre (20 → 20, 32 → 31.5, 560 → 500, 710 → 800, 900 → 1 kHz, 15800 → 16 kHz …); 630 Hz has none of its own.

**Hyperion, measured.** `HyperionCalibration` (explicit, run by name on the Windows package run: 41783615135) reads Hyperion's transfer on noise at 6 points an octave, a Welch estimate from its input and output (`ReBuzzTests/FineTransfer.cs`): at its defaults; one band at every Bandwidth step and at −12, +6, +12 and +18 dB; two neighbours; the 6.1 kHz, 20 kHz and 20 Hz bands; every band at ±6 dB at four widths; two zigzags; Highpass at raw 64 and 127; Input gain at raw 60. Each band is the Audio EQ Cookbook's peaking filter at 44.1 kHz: its gain (raw − 128) × 0.1417 dB, constant-Q (2.0 octaves between half-gain points at −12, +6, +12 and +18 dB alike), its bandwidth warped by ω0/sin ω0 as the cookbook does, and its alpha capped at 1.5, which only the 20 kHz band reaches (uncapped it would be about ten times wider than a low band). Bandwidth is the octaves between half-gain points, twice what Hyperion displays: 0.37, 0.50, 0.66, 0.83, 1.02, 1.24, 1.47, 1.73, 2.01 (its default), 2.30, 2.62, 2.96, 3.31, 3.69, 4.09 and 4.51 octaves over its 16 steps. The bands don't compensate their overlap; they add in dB: two neighbours at +12 dB make +23.3 dB between them, and every band at +6 dB is +10.8 dB at the narrowest width, +43 dB at the default and +81 dB at the widest. That model is within 0.3 dB of all 34 measurements from 40 Hz up, within 0.05 dB at the default width; below 40 Hz within 1.1 dB, where the measurement's 2.7 Hz resolution blurs bands a few hertz wide. Highpass is a second-order Butterworth (Q 0.705 fitted) at 1.28 times its display (28.8 Hz at "22 Hz", 127 Hz at "100 Hz"); raw 0, its default, is off. Input gain is its table (raw 60: −12.0 dB, flat). Hyperion is stereo: right equals left, and its defaults are flat within 0.01 dB. So the "+221 dBFS" of §1 is the test: no band is unstable, they add up.

**The conversion, as built** (the `graphic` rule, [mcp-server.md](mcp-server.md) §12.10; `Machines/Manifests/Successors/Joachims Hyperion to Neo EQ.successor.json`):

1. Hyperion's curve, its bands as the cookbook's bells at 44.1 kHz with Bandwidth's measured width (the pair's `describe`), summed in dB, is read at the 31 ISO centres: those are the G sliders. Graphic mode's response passes through them (within 0.19 dB).
2. Where the curve leaves ±18 dB it moves as a whole, the least that brings it inside, and Output takes the move with Input gain (beyond ±24 dB, the output connections); a curve spanning more than 36 dB keeps its middle and its ends are clamped, with a note.
3. Highpass's Butterworth goes into the sliders after that, so it never moves Output; a cut deeper than −18 dB stops there, with a note, and below 20 Hz graphic mode settles at the sliders' median instead of cutting.
4. Enable OFF bypasses Neo EQ (the `bypass` rule, inverted).

**30 bands onto 31, and what it costs.** Reading the summed curve at the ISO centres, rather than each Hyperion slider onto its nearest ISO one, costs little where the curve is smoother than the sliders' spacing. Modelled (Hyperion's measured model against graphic mode's design, third octaves of white noise, 25 Hz – 16 kHz): at the default Bandwidth (2.0 octaves) every third octave within 0.1 dB for one band at +12 dB, every band at +6 dB or a zigzag, and within 0.4 dB for random settings within ±6 dB; one band at +12 dB at Bandwidth step 2 (0.66 octave) within 0.7 dB, and at the narrowest (0.37 octave, step 0) within 1.8 dB: a peak narrower than the sliders' spacing, which graphic mode's 2/3-octave bells can't make (the 1 kHz slider reads 7.3 dB where Hyperion peaks at 12 dB at 904 Hz). Nearest centre instead (630 Hz halfway between its neighbours) misses by 6 to 10 dB for one band and by up to 37 dB for every band at +6 dB, leaving Hyperion's overlap out. Past the sliders' reach the cost is the clamp: a curve spanning more than 36 dB (every band at +6 dB from Bandwidth step 10, 2.6 octaves, up) loses its ends.

**Measured** (`NativeMachinesMoveOntoTheirSuccessorsWithinTolerance`, Windows package run 41785493029, Neo Poly's noise in chords into it, each side against its input; the pair's tolerance ±1 dB loudness and ±2 dB in the third octaves from 31.5 Hz to 16 kHz): a smile at the default Bandwidth (20–52 Hz and 7.7–12.4 kHz +3 dB, 440–900 Hz −2 dB) −0.03 dB in loudness, every third octave within 0.2 dB; the same with Highpass 22 Hz −0.03 dB, within 0.1 dB; every band +3 dB (+21.5 dB summed, so the sliders move down 3.5 dB and Output takes it) with Input gain −12 dB −0.01 dB, within 0.1 dB; a zigzag ±6 dB at Bandwidth step 2 −0.02 dB, within 0.3 dB. All four are asserted. One band +12 dB at the narrowest Bandwidth −0.09 dB, within 1.6 dB (at 1 kHz; 1.7 dB modelled), is reported, not asserted. Outside the tolerance, by the model: a boost past about +13 dB at Bandwidth step 0 or +17 dB at step 1 (+18 dB at step 0: 3.2 dB), a curve spanning more than 36 dB, a Highpass above about 70 Hz (the lowest third octaves cut deeper than −18 dB), and Hyperion at another rate than 44.1 kHz, whose high bands move with it.

`machine_replace` (U5, built: [mcp-server.md](mcp-server.md) §12.10) moves a song over: parameters by unit through a mapping file per pair, patterns' columns and notes mapped, the arrangement and the Session View following, a render before and after compared against the pair's stated tolerance, one undo step; in the machine menu as "Replace with <successor>..." in the studio and ReBuzz.exe. The original stays installed so old songs play. On the Mac every closed native is a silent placeholder, so a successor and `machine_replace` are how those songs play there: its comparison then says the old machine was silent (there is nothing to measure against), and the conversion itself is the same. Neo Ensemble's four mappings are in (above), and Joachims Hyperion's onto Neo EQ's graphic mode.

## 5. Tests

- A test machine that diverges on purpose (a feedback loop at gain 1.01, and one that writes NaN after N blocks): the guard trips, mutes and flags it; the rest of the song keeps playing; *Reset and unmute* brings it back.
- The null test and the bit-identical render tests stay byte-equal with the guard and limiter on.
- 0 bytes allocated per buffer with the guard and limiter on (the 0.4.1 allocation test).
- The limiter: a +12 dBFS sine reaches the device at no more than the ceiling; the clip indicator latches +12 dB; a render of the same song still measures +12 dBFS.
- The profiler: Jeskola Flanger diverging at feedback ±100 % (its `safe_range` excludes both ends); Hyperion hot or diverging per band; Jeskola Bass hot; Info "needs a window". *Built:* the Windows package run and Fork CI profile them, `tools/test_machine_infer.py` tests the categories and the edge's bisection, `DraftManifestTests` the parsing, `param_set`'s refusal and the statuses, and `tools/headless-manifests.py` the tools end to end (Pedal Plate's real range; synthetic ones in an app copy). Found: the Flanger diverges at +100 % only, and Info is healthy since its theme fix (§3.1).
- Song Notes round-trips in both formats; an Info machine's text is imported and the song saves back byte-equal in its core sections.
