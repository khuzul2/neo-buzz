# DAW modes: tracker, arrangement and session over one song — Proposal (decided)

| | |
|---|---|
| Status | Decided 2026-11-03: D1 A1 (§3.1), D2 B, D3 A, D4 B, D5 A, all as recommended. **AR0 built** (2026-11-03, §8.1): the song meter, clip ids, automation ramps and note sources. AR1–AR2 next, after P3's exit, beside plugin-host H1 |
| Date | 2026-11-03 |
| Scope | One song and one machine graph, seen and edited in three modes that stay in sync: **tracker** (patterns with columns), **arrangement** (a horizontal timeline with clips, a piano roll and automation lanes) and **session** (clip launching). The model, the views, compatibility, engine seams and a phased plan |
| Code baseline | fork `90b0850` (main, 2026-11-03). Branches `platform/p3-s4-sequence-editor` and `platform/p3-s6-pattern-editor` (not merged) read the same day |
| Related | session-view.md (0.3, 0.4.1, 0.5.1, 0.7, 0.7.1), studio-mvp.md (§3.5, §3.6, S4–S6), cross-platform.md (P3, P4), mcp-server.md (§5.5), plugin-host.md (H1–H3 timing) |

---

## 1. Summary

**Verdict: no new song model.** The three modes can share Buzz's own model: machines and wires, patterns, sequences. What's missing is in notes, automation and time, not in clips.

- **Clips mostly exist already.** Patterns hold content, sequences place them in time, and the Session View's layout places them in a grid. Session clips are patterns, shared rather than copied (session-view.md §4.2). Recording a performance into the sequences works (0.3). Since 0.5.1 the recording is exact: a `SequenceEvent` can play part of its pattern at a volume (`Offset`, `End`, `Gain`).
- **An arrangement clip is a placed pattern:** a run of sequence events of one pattern.
  - Trim uses `Offset` and `End`, which exist.
  - A loop is written as repeated events, as *Insert into arrangement* already does, so upstream ReBuzz plays it too.
- **The real gaps:**
  - *Notes live in two stores.* One is the pattern's engine MIDI column, used by the piano roll, session takes and the agent. The other is the pattern editor's own data (the tracker). The tracker doesn't show the first, and the piano roll edits only the first. Today's bridge is a manual "Move notes to/from the Pattern View".
  - *Automation has no song-level home.* Patterns hold automation, but a 16-bar sweep over a looped 1-bar clip has nowhere to live.
  - *Two bar grids.* The sequence editors have their bar list (`TimeSignatureList`); the session has its own beats per bar.
- **Recommendation:**
  1. Clips are placed patterns, shared as session clips are, with *Make unique*. A copy-drag (Ctrl/Cmd) makes an independent copy, an Alt/Option-drag a linked one (D1 A1).
  2. **One note store:** the engine MIDI column. The tracker shows it as note lanes computed when drawn, so a note drawn in the piano roll or recorded in the session shows in the tracker without a move (D2 B).
  3. A classic Buzz machine's own Note columns stay tracker data. The piano roll edits them with tracker rules (D3 A).
  4. Song-level automation as **automation clips**: automation-only patterns on an extra sequence of the machine, an old Buzz trick, drawn as curves with ramps (D4 B).
  5. One `PianoRoll` control, taken out of the Session View, hosted in the session, the arrangement and the Pattern tab.
  6. Views in the studio only. WPF plays and keeps everything until P4 (D5).
- **Seams:** six small ones in the engine, and one large one in MPE (§8). Only automation ramps touch the audio path.
- **Effort:** 12–18 pw in seven phases (§9). AR0 (the seams) can start now; the views follow S4 and S6.

### 1.1 Decisions

| # | Decision | Options | Recommended | Decided |
|---|---|---|---|---|
| D1 | What a clip is | **A** a placed pattern, shared (Buzz, FL Studio, Renoise aliases, today's session), with Make unique · **B** arrangement-owned note clips (Live, Bitwig) · **C** each placement gets its own pattern, made automatically | **A** | **A1** |
| D2 | Where piano-roll notes live, and how the tracker sees them | **A** two stores, moved by hand (today) · **B** the MIDI column is the store, and the tracker shows it as computed note lanes · **C** the tracker's note lanes are the store, and the piano roll writes them on each edit | **B** | **B** |
| D3 | Notes for machines that don't take MIDI (classic Buzz generators) | **A** the piano roll edits their Note columns with tracker rules (voices ≤ tracks) · **B** the engine plays MIDI-column notes onto their Note and velocity parameters (one store for every machine) · **C** no piano roll for them | **A**; B only if the song census shows heavy use | **A** |
| D4 | Song-level automation | **A** per pattern only (Renoise) · **B** automation clips on an automation lane of the machine (FL Studio; Buzz's second sequence) · **C** a new song-level curve store (Live, Bitwig) | **B** | **B** |
| D5 | When, and on which UI | **A** AR0 now; views in the studio only: AR1–AR2 after P3's exit, beside plugin-host H1; AR3–AR5 before P4 · **B** everything after P4 · **C** also build the views on WPF | **A** | **A** |

## 2. What exists

### 2.1 The song model

| Thing | Type | Where its data lives | Who plays it |
|---|---|---|---|
| Pattern | `IPattern` / `ReBuzz/Core/PatternCore.cs` | Name, length in ticks, engine columns | – |
| Tracker data | the machine's pattern editor (default: Modern Pattern Editor, MPE) | The editor's own store (`MPEPatternDatabase`), keyed by pattern name, saved as one blob per machine (`IMachine.PatternEditorData`, PXP format, `PatternEditorUtils.CreatePatternXPPatternData`) | The editor: MPE's `PlayRecordManager`; PatternXP through `MachineWrapper.cpp`'s `PlayPattern` |
| Engine MIDI column | `PatternColumnCore`, `PatternColumnType.MIDI` | `PatternEvent`s: time in 1/240 tick, a MIDI message, a duration. Saved in TAP2 (.bmx) and .bmxml | `WorkManager.UpdatePatternColumnEvents` |
| Engine automation column | parameter column tagged `SessionAutomation` (`PatternColumnCore.EnginePlays`) | Values as steps | Same, through `IParameter.SetValue` |
| Sequence | `ISequence` / `SequenceCore` | One machine. Events at ticks: PlayPattern, Break, Mute, Thru, Off, plus `Offset`/`End`/`Gain` (fork, `ArrangementEventStore` → "ArrangementEvents" subsection) | `WorkManager.UpdatePatternPositions` |
| Session grid | `SessionDocument` | "SessionView" subsection. Tracks are sequences; slots name patterns; clip settings per pattern | `SessionCore` |
| Bar list | `TimeSignatureList` (ticks per bar from a tick on) | `SequenceEditor` subsection: the classic editor's, and the studio's `SequenceSongState` (S4) | – |
| Session bar | `SessionSettings.BeatsPerBar` | SessionView subsection | `SessionCore` |
| Markers | `SongMarkerStore` | "SongMarkers" subsection | – |

### 2.2 How it plays

- Each block, `WorkManager` runs:
  1. engine columns (`UpdatePatternColumnEvents`);
  2. `CallTick`;
  3. `ReadWork`, editor machines first;
  4. then `UpdatePatternPositions`.
- `UpdatePatternPositions` positions session clips first. Then, for each sequence, it finds the event at the song position by binary search (`SequenceCore.Arranged.IndexAtOrBefore`, 0.4.1).
- A pattern has one play position (0.4.1): it plays in one place at a time.
- An event plays its pattern **once**, from `Offset` to `End`, cut at the next event. Nothing loops inside an event.
- Engine MIDI events go out at the start of the block they fall in. Automation events set values as steps.

### 2.3 What the Session View already gives an arrangement

- *Arrangement record* (0.3): launches and stops written into the sequences.
- *Faithful arrangement* (0.5.1): partial patterns, loop regions, legato and velocity, written as `Offset`/`End`/`Gain` events. Copy, paste and undo in the sequence editors keep these fields.
- *Insert into arrangement* (`SceneToArrangement`, `SessionArrangement.cs`): from the start marker, then the loop region repeated, with the last repeat cut. This is the clip loop this proposal reuses.
- *Back to Arrangement* and the per-track override (`ISession.IsArrangementOverridden`). Overridden tracks are greyed in the sequence editors (`SessionOverrideIndicator`).
- *Notes with lengths:* `ClipNote`/`ClipNotes` (pure edits), `SessionLayout.EditNotes`, and the piano roll on WPF and the studio (S5).
- *Tracker conversion:* `TrackerNotes` and `RowGrid` (BuzzGUI.Common, pure). They lay notes on tracker rows and read them back, keeping times to 1/95 of a row (0.7.1, 0.7.2).
- *Takes, overdub, Capture and automation recording* (0.7): `SessionCore.Recording.cs`, `TakeWriter`, `SessionLayout.Takes.cs`.
- *Mixer on connections, with no DSP* (`SessionMixer`), and audio clips as patterns of the Session Clip Player (1.0), which the arrangement already plays.

### 2.4 Views today

| View | WPF (Windows) | Studio (Avalonia) |
|---|---|---|
| Sequence | Classic (`BuzzGUI.SequenceEditorExtended Rebuzz`), ModernSequenceEditor (vertical, the default), ModernSequenceEditorHorizontal: two near-copies, about 10.6k LOC with their interfaces | S4 (branch, about 3k LOC plus tests): the horizontal editor, with blocks that preview notes, edge resize that sets the pattern's length, the bar list, markers and the play cursor; undo on the song's history |
| Pattern | MPE; PatternXP (native); Modern Pianoroll (upstream prototype, 2025-02) | S6 (branch): MPE's machine side portable, with an Avalonia view |
| Piano roll | Session View, 0.7.1 | S5 (merged): a port |
| Session | 0.1–1.0 | S5 (merged), without audio clips |

### 2.5 Found in the code (worth knowing)

- **A piano-roll pattern editor is already in the tree.** `Machines/ModernPianoroll` (upstream commit `7c20794`, "for development purposes only") is a pattern editor machine. It reuses a copy of MPE's structures and writes each note into a machine track's Note column, as a note followed by an Off (`Pianoroll.GUI/Pattern.cs`, `AddNote`). It's incomplete: `ExportMidiEvents` returns null, and it's WPF only. It isn't a base, because as a pattern editor it would replace the tracker for that machine (§5.1).
- **MPE assumes 4 ticks per beat** (`BUZZ_TICKS_PER_BEAT = 4`, in `MPEPatternColumn` and `PatternControl`; `RowGrid.BeatLength = 4 × 240` in `TrackerNotes`). The session uses the song's `TPB`.
- **MPE's `ExportMidiEvents`**, the source of the sequence editors' note previews, uses velocity 70 and ignores delay and cut ("Todo").
- **Managed machines show whether they take MIDI** (`ManagedMachineHost.MidiNoteFunction` is null if not). Native machines don't: the native interface's `MidiNote` has an empty default.
- **Buzz flags state parameters** (`ParameterFlags.State`), which tells a level (a curve) from a trigger (an event).
- **Agent automation is dense steps:** linear ramps become events every quarter tick (mcp-server.md, A2–A3).

### 2.6 Other DAWs, where they inform the design

| DAW | Model | What we take |
|---|---|---|
| FL Studio | Patterns hold notes and automation; pattern clips in the playlist; a piano roll per channel per pattern; automation clips | The closest analogue: shared patterns placed as clips; automation clips |
| Renoise | Pattern sequence and matrix; aliases are linked copies; automation is per pattern, as envelopes | Tracker first; per-pattern automation; aliases ≈ Buzz's shared patterns |
| Ableton Live | Session and arrangement over one device chain per track; arrangement clips are independent; Back to Arrangement; take lanes and comping | Back to Arrangement (have it); take lanes |
| Bitwig | Arranger and launcher on the same tracks, with "completely separate data"; one is active per track; clips copy between them | The per-track override (have it) |
| Jeskola Buzz, ReBuzz | Patterns per machine; sequence tracks per machine (several allowed); pattern editor machines (PatternXP, Jeskola Pianoroll) | Several sequences per machine become lanes |

## 3. The model

### 3.1 What a clip is

| Option | For | Against |
|---|---|---|
| **A Placed pattern** (a run of sequence events); the piano roll is one more editor of the pattern | No new model. Upstream plays it; the session, tracker and MCP see it | Shared: editing one placement changes all. One play position per pattern |
| B Note clips owned by the arrangement, beside patterns | Independent clips, as in Live | A second model to sync with the session and tracker; upstream can't play it; MCP needs a second path |
| C Each placement its own pattern, made automatically | Live's independence on Buzz data | Patterns multiply in the tracker and session; breaks the Buzz habit of one pattern used many times |

**Recommended: A.** B's best part is already in A: a pattern's engine MIDI column *is* a note list with lengths (§3.2).

**Decided: A1** (2026-11-03). A, with the copy gesture most DAWs use:
- A copy-drag (Ctrl on Windows and Linux, Cmd on macOS) makes an **independent copy**: a new pattern (*Make unique* at the drop), named after the original ("Bass 2").
- An Alt/Option-drag makes a **linked copy**: another placement of the same pattern, as Buzz and FL Studio do.
- The repeats of a looped clip are always the same pattern. Paste follows the copy-drag (independent); *Paste linked* is beside it.
- Both are the same data: the default can become a preference later (A2 was the linked default).

**The clip, precisely:**
- A clip is one or more consecutive PlayPattern events of one pattern on one sequence, joined by a **clip id**, a new fork field (§8 S2):
  - the first event plays from the start offset;
  - the following ones play the loop region;
  - the last one is cut at the clip's end.
- Trim uses `Offset` and `End`; volume uses `Gain`. All three exist.
- A loop is more events, so upstream ReBuzz plays it. Upstream doesn't know the clip id; it plays each event, cut at the next.
- An event without a clip id (old songs, edits in upstream) is a clip of its own. Nothing is lost, and a *Join* can group events later.
- The pattern's length is the clip's content. Resizing a clip trims or loops it; changing the pattern's length is a separate gesture (§4.2).

**One source of truth:**

| Mode | Edits | Stored in |
|---|---|---|
| Tracker | Pattern contents, sequence events | The pattern editor, engine columns, sequences |
| Arrangement | Clips, notes, automation | Sequences (+ clip ids), engine columns, the editor (Note columns), automation clips |
| Session | Slots, clip launch settings, launches | `SessionDocument`, engine |

All three modes use the same patterns and sequences. A pattern's session clip settings (markers, loop) seed a clip's region when it's dragged or recorded into the arrangement, as scene insert does today.

### 3.2 Notes: one store

| Option | How | Against |
|---|---|---|
| A Two stores (today) | MIDI column for the piano roll; the editor's lanes for the tracker; moved by hand | Not seamless: a piano-roll note isn't in the tracker |
| **B MIDI column is the store** | The tracker draws the MIDI column as note lanes | MPE work (§8 S7) |
| C Tracker lanes are the store | Each piano roll edit rewrites the editor's lanes (`ImportClipNotes`) | Rounds every note to rows; voices capped by tracks; only for MPE machines; the agent and session takes use the MIDI column |

**Recommended: B.**
- For machines that take MIDI, the pattern's engine MIDI column is the note store. The piano roll, session takes, the agent and the arrangement all write it.
- MPE draws the column as note lanes, computed with `TrackerNotes`: rows, velocity, delay, cut. They go in a column set of their own ("Notes"), one virtual voice per overlapping note, not in the machine's tracks. This is display only: the engine plays the column, and MPE doesn't play these lanes.
- Typing in those lanes turns the edited lane back into notes (`TrackerNotes.FromRows`), and only notes that changed are written. Untouched notes keep their exact time and length.
- MPE's own MIDI lanes in old songs stay as they are. *Move notes from the Pattern View* (0.7.1) turns them into MIDI-column notes.
- Upstream ReBuzz plays MIDI-column notes too: its engine plays the MIDI columns of arranged patterns (session-view.md G5).

### 3.3 Where tracker and piano roll don't map one to one

| Case | Tracker | Piano roll | Rule |
|---|---|---|---|
| Polyphony vs tracks | Voices are the machine's tracks (`MaxTracks`) | Any number of overlapping notes | MIDI-column notes: no limit, since the projection adds virtual voices. Note-column notes: a note that needs more tracks than the machine has is refused, with the reason (as 0.7.1's move does) |
| Length vs note-off | A note rings until the track's next note, an off or a cut | An explicit length | *To rows:* an off on the end row; a cut if the note ends in its own row; nothing if the track's next note starts then (`TrackerNotes`, 0.7.2). *From rows:* the length runs to the off, cut or next note. A note with none rings to the pattern's end and is drawn open-ended; editing its length writes an off |
| Timing | Rows (rows per beat, subdivided beats), plus a delay in 1/95 row | 1/240 tick | The store keeps exact times. Edits in the tracker round to 1/95 row, and only for the notes edited there |
| Velocity | MIDI lanes: a velocity column. Note columns: the machine's own volume or velocity parameter, if any | 1–127 per note | Note columns: velocity maps to a parameter chosen per machine (from the manifest for Neo machines, by name otherwise; the user can change it). With none, velocity is hidden |
| Same-track legato | A note on a track with no off before it: legato or slide on mono machines | Separate notes | Note-column notes keep their track as a note attribute, and moves keep it. The roll colours notes by track |
| Track effect columns | Per-row commands (slide, retrigger, cutoff) | – | Not notes: shown as markers under the roll, edited in the tracker |
| Editor-specific data | MPE: rows per beat, subdivisions, column sets, chords, PXP. Native editors: opaque | – | Never converted. Read through the bridges (`PatternEditorMachineMIDIEvents`, `ExportClipNotes`); written only where the editor has an import call (MPE). Other editors give a read-only preview |
| Parameter columns vs curves | A value at a row holds; "no value" means no change | Points with ramps | §3.4 |
| No note columns | Effects, control machines, Master | – | No piano roll; automation lanes only. Master's lane is tempo (BPM and TPB are its parameters) |
| Beat length | MPE: 4 ticks a beat | The song's TPB | At TPB 4 they agree. Otherwise the projection follows MPE's rows. *To check:* how many songs use TPB ≠ 4 |

### 3.4 Automation

Today automation lives in two places:
- tracker parameter columns, in the editor: step values at rows;
- engine automation columns, from session recording and the agent: steps, with ramps as dense events.

| Option | How | Against |
|---|---|---|
| A Per pattern only (Renoise) | Lanes show the patterns' columns | A sweep longer than a looped clip can't be written |
| **B Automation clips** (FL Studio; Buzz's second sequence) | Automation-only patterns on an *automation lane*: a sequence of the machine with an automation role | Two patterns can write one parameter |
| C A song-level curve store (Live, Bitwig) | New data, saved in a subsection | Upstream drops it; a second playback path; the session can't launch it |

**Recommended: B, with A's view.**
- Lanes draw state parameters (`ParameterFlags.State`) as curves, and triggers as markers.
- Along the timeline, a lane shows the clip automation of the patterns under it. That is read-only for editor columns and editable for engine columns. The lane also shows the automation clips on its automation lane.
- **Ramps** (new, §8 S3): an engine automation event with `Duration` > 0 ramps linearly to the next event, evaluated at each subtick. This is exact, and the agent's quarter-tick steps become single ramps.
- Tracker columns → lane: exact (steps). Lane → tracker column: only by an explicit move, sampled at rows, so ramps lose their shape.
- *Two writers in one tick* (a tracker column and a lane): as read in `WorkManager`, the pattern editor's value lands last. It sets parameters in its `Work`, after the engine's column events. *Unverified.* Overlaps are marked in the lane.

### 3.5 Time

- **One bar list per song, in the engine** (`SongMeter`, §8 S1). The arrangement, the session (its bar where the clock starts) and MCP all read it.
  - It's stored as today, in the `SequenceEditor` subsection (`TimeSignatureList` format), so old songs and upstream's editor keep it.
- Scene time signatures (0.5) stay in session time, as a performance; they reach the meter only when recorded.
- **Grid:** bars and beats from the meter and TPB; rows from the pattern editor; ticks; and 1/240 tick for notes off the grid. At TPB 4, triplets fit notes (320 units) but not ticks.
- **Tempo:** the Master's BPM in its patterns, as in Buzz. A tempo lane shows it.

## 4. The arrangement view

### 4.1 Timeline and tracks

| Option | For | Against |
|---|---|---|
| One track per sequence (today) | Buzz as is | A machine with three sequences is three unrelated rows; nowhere for automation |
| **One track per machine, with lanes** | Machine-centred, as Buzz's graph is; room for takes and automation | A header layer to build |
| Lanes per pattern | Shows reuse | Doesn't scale; not how Buzz plays |

**Recommended: a track per machine, with lanes.**
- **A track is a machine.** Its header has the name, mute, solo, arm and the machine's mixer strip (`SessionMixer`, the same connections as the Machine View).
- **Its lanes are its sequences.** Lane 1 is the main one; more lanes hold layers, takes (§4.4) and automation clips (§3.4).
  - A lane's role (main, take, automation) is fork data (§8 S5).
  - The Session View hides take and automation lanes.
- Automation lanes for chosen parameters go under the track.
- **Master is on top,** with the tempo lane, markers (`SongMarkerStore`), loop and song end.
- Machines without a sequence (most effects) appear once a lane is added (`ISong.AddSequence`), or with *Show all machines*.
- Order and folding follow the Machine View's groups (`IMachineGroup`), as the Session View's columns do.

### 4.2 Clips

- **Draw:** a double-click on a lane makes a pattern and its clip. The default length is the session's clip length setting.
- **Move, copy, delete:** move by drag. Ctrl/Cmd-drag copies independently (a new pattern), Alt/Option-drag copies linked (the same pattern; D1 A1). *Split* at the cursor makes two events with offsets set. *Join* merges contiguous events of one pattern.
- **Edges:**
  - The left edge trims the start (`Offset`).
  - The right edge adds repeats when the clip loops, and trims the end (`End`) when it doesn't.
  - The pattern's length changes with a modifier, or in the clip panel (the Buzz habit). In tracker mode, an edge sets the length, as in S4 today.
- **Loop region per clip** (start, loop start, loop end), seeded from the pattern's session clip settings.
- **Shared badge:** how many places use the pattern, in the arrangement and the session. *Make unique* clones the pattern and points this clip at the copy, as the session's Duplicate does (`ClonePattern` plus `EngineColumns`).
- **Preview:** notes as S4 draws them (from the MIDI column, or the editor's export); automation as a line.
- Break, Mute, Thru and Off stay as markers, as in S4.
- Snap to the grid (§3.5); Shift turns it off.

### 4.3 Automation lanes

- Add a lane from the track header (a parameter picker), or by moving a knob while the track is armed for automation.
- Draw points and lines, select and move them. Values are in units through manifests (Neo machines), raw otherwise.
- Drawing where there's no clip makes an automation clip on the machine's automation lane.
- **Recording:** knob moves while recording go into the automation clip under the play position. This is the session's automation recording (0.7, `SessionCore.OnParameterChange`) with another target.

### 4.4 Recording and comping

- **Session → arrangement:** exists (0.3, 0.5.1).
- **MIDI into the arrangement:** an armed track records at the play position.
  - Over a clip, with overdub on: into its pattern, at pattern time. A shared pattern gets a warning.
  - Otherwise: a new pattern, placed as a clip from where recording started. It grows as it records (0.7.1) and ends where recording stops.
- **Loop recording:** each pass is a new take, its own pattern, on a take lane.
- **Comping:** select a range on a take lane, and *Comp* puts a trimmed event of that take's pattern on the main lane (`Offset`/`End`). No notes are copied. It's late in the plan (AR5), and worth it because it costs little here.
- **Audio:** audio clips are patterns of the Session Clip Player, which the arrangement already plays (1.0). The studio hasn't ported the player yet (S5, "not ported"), so arrangement audio waits for that port.
- **Reuse:** the session's recorder (`SessionCore.Recording.cs`: MIDI-thread stamps and record windows; `TakeWriter`; `SessionLayout.Takes.cs`). It counts in session time; arrangement takes count in song ticks, so the writer gets a target (a pattern and an origin) instead of a slot (§8 S6).

### 4.5 Extend the horizontal sequence editor, or a new view?

**Extend it.** Build on the studio's S4 view: `ReBuzz.Studio/Views/Sequence` (`SequenceCanvas`, `SequenceGeometry`, `SequenceEdits`, `SequenceSongState`).
- It already has tracks, blocks with previews, the bar list, markers, the play cursor and undo on the song's history.
- Add a mode switch:
  - **Tracker** keeps Buzz's rules: one row per sequence, edges set the pattern's length, vertical in P4.
  - **Arrangement** adds clips, lanes, automation and the piano roll panel.
- Use one canvas with an orientation, not WPF's two near-copies. The vertical orientation is P4's (studio-mvp.md §3.5).
- **WPF gets no arrangement view.** Its editors keep the new fork fields on copy and paste, as 0.5.1 did for `Offset`/`End`/`Gain`.

## 5. The piano roll

### 5.1 Where it lives

| Option | For | Against |
|---|---|---|
| A panel in the arrangement | Edits in context, as in Live and Bitwig | – |
| A toggle in the Pattern tab, beside the tracker | The same pattern, two editors, as in FL Studio | – |
| A pattern editor machine (Modern Pianoroll's way) | Buzz's mechanism | A machine has one pattern editor (`IMachine.PatternEditorDLL`), so choosing it takes the tracker away from that machine |

**Recommended:** one control in three hosts: the Session View (as now), the arrangement's panel and the Pattern tab's toggle. Not a pattern editor machine. Modern Pianoroll stays a legacy item for P4 parity (cross-platform.md).

### 5.2 What it reuses

- `ClipNotes` and `ClipNote` as they are: pure draw, move, resize, velocity, quantize, hit test and box select.
- `PianoRoll`: WPF `ReBuzz/AppViews/Session/PianoRoll.cs` (700 LOC) and studio `ReBuzz.Studio/Views/Session/PianoRoll.cs` (725 LOC).
  - Both depend on `SessionLayout` for `NotesOf`, `EditNotes`, `PreviewNote`, `Current`, `IsRecordingClip` and undo.
  - These go behind a small interface, `INoteSource` (§8 S4). The session passes its layout; the arrangement and the Pattern tab pass a source on the song's history.
- `TrackerNotes` and `RowGrid` for the tracker side.
- `ReBuzz.Agent/Model/Theory.cs` (pure: scales, chords) for scale highlighting and chord tools.

### 5.3 Features

- **Editing:** as 0.7.1, plus split, glue, duplicate (Ctrl+D), legato and a chord stamp.
- **Context:** the clip's loop region and its repeats; other clips on the track as ghosts; other tracks' notes optionally.
- **Per note:** a velocity lane under the roll, and a channel per note.
- **Controllers:** pitch bend and CCs in the MIDI column get lanes. Today they're kept but not shown.
- **Per-note expressions** (MIDI MPE, CLAP and VST3 note expressions, plugin-host.md §3.5): later. The MIDI column has no note IDs.
- **Scale:** key and scale highlighting, snap to scale, fold to the notes used or to the scale. Drum names when a manifest names the notes.
- **MIDI:** step input (each key writes at the cursor and steps by the grid; held keys make chords), real-time recording into the clip (§4.4), and key previews (`PreviewNote`).
- **Note-column notes** (D3 A): drawn by track; edits follow tracker rules (§3.3).

## 6. Session ↔ arrangement

- **Recording the session into the arrangement** exists (0.3, faithful since 0.5.1). With S2, the recorder also writes clip ids.
- **Scene into the arrangement** exists (*Insert into arrangement*). With S2, it writes clip ids too.
- **Back to Arrangement** exists. The arrangement greys taken-over lanes, as the sequence editors do.
- **Launch from the arrangement:**
  - A clip's menu has *Launch in Session*: `ISession.Launch` on its sequence, with its region as `LaunchOptions`.
  - Dragging a clip onto a slot gives the slot the pattern (shared).
- **Slot → arrangement:** dragging a slot onto a lane makes a clip, with the pattern's clip settings as its region (as scene insert does).
- **One clip in both modes:** the same pattern. A pattern has one play position (0.4.1), and the session clip keeps it, as today. Both views show the shared badge and offer *Make unique*.
- **Per-track toggle** (Bitwig's): the existing override. The arrangement's track header shows it.

## 7. Compatibility

### 7.1 Songs (.bmx, .bmxml)

- **No change to the core sections** (MACH, PATT, SEQU, PXP data, TAP2). New data goes in optional subsections.
- **Old songs open as they are:**
  - every event is a clip;
  - the bar list comes from the `SequenceEditor` subsection;
  - notes stay where they are (MPE lanes or Note columns).
- **Upstream ReBuzz plays:** loops (they are events), MIDI-column notes, and automation clips' patterns.
  - Upstream doesn't play engine automation columns, so it doesn't play automation. It keeps them, since TAP2 writes every column with its events, durations and metadata.
- **Upstream loses:**
  - trims: it plays from the pattern's start (0.5.1's known limit);
  - `Gain`, clip ids and lane roles;
  - the meaning of ramps;
  - subsections, when upstream saves (session-view.md 0.6.1).
- **Tests:** as in 0.5.1 and S6. Every phase round-trips songs in .bmx and .bmxml with their core sections byte-equal (`tools/song-roundtrip.py`, `SessionArrangementTests`).

### 7.2 The WPF app (until P4)

- The seams are engine code, so WPF plays everything.
- Its sequence editors keep clip ids and lane roles on copy and paste, and show a clip's events separately.
- Its Session View hides take and automation lanes; the layout does this, and is shared.
- WPF's `PianoRoll` moves onto `INoteSource`, as the studio's does. No behaviour changes.
- WPF's MPE doesn't draw the computed note lanes (D5 A). The 0.7.1 moves stay there.

### 7.3 rebuzz-headless and MCP

The seams are portable, since `ReBuzz.Engine` links `ReBuzz/Core`. After S6, MPE's machine side runs headless, so the tracker bridges work headless too. Tool names below are to settle in each phase:

| Tool | Change |
|---|---|
| `arrangement_get` | Tracks per machine with their lanes and roles. Clips: pattern, start, length, offset, loop region, gain, shared count. The meter and the tempo lane |
| `arrangement_place` | `lane`, `length_beats` (loops to fill), `offset_beats`, a loop region; writes a clip id |
| `arrangement_clip` (new) | Trim, loop, gain, split, join, make unique |
| `automation_write` | `shape: step \| linear` (exact ramps); `where: pattern \| song` (song makes an automation clip) |
| `notes_read` | `source: midi \| tracker` (MPE lanes and Note columns) |
| `notes_write` | `target: tracker` for machines without MIDI (D3 A) |
| `meter_get`, `meter_set` (new) | The bar list |

## 8. Engine seams

| # | Seam | Files and types | Size |
|---|---|---|---|
| S1 | **Song meter.** The bar list in the engine, as `PatternColours.Source` is for colours (`ReBuzz/Core/ViewState.cs`). The sequence editors still write it into their `SequenceEditor` subsection; the session, arrangement and MCP read it. *To check:* the WPF classic editor owns its list today and must mirror the engine's | new `ReBuzz/Core/SongMeter.cs`; `SessionCore` (default bar); studio `SequenceSongState`; `BuzzGUI.SequenceEditorExtended Rebuzz` | S |
| S2 | **Clip ids on events.** `SequenceEvent.Clip` (int, 0 for none) in the copy constructor; a clip's loop region in the "ArrangementEvents" subsection; recorders and editors keep it | `ReBuzzGUI/BuzzGUI.Interfaces/ISequence.cs`, `ReBuzz/Core/ArrangementEventStore.cs`, `SessionArrangement.cs` (`SessionRecorder`, `SceneToArrangement`), BuzzGUI.Common sequence actions, S4 `SequenceEdits` | S |
| S3 | **Automation ramps.** In engine automation columns, an event with `Duration` > 0 ramps to the next event at each subtick. Zero allocation, with the 0.4.1 allocation test | `ReBuzz/Audio/WorkManager.cs` (`PlayPatternColumnEvents`), `PatternColumnCore` | S |
| S4 | **Note source.** `INoteSource`: notes, edit (removed, added), preview, length, whether and why it's editable, changed. Implementations: `SessionLayout` (its own undo), `MidiColumnNotes` (the song's undo), `TrackerColumnNotes` (Note columns, D3 A), `EditorPreviewNotes` (read-only, `PatternEditorMachineMIDIEvents`) | new `ReBuzz/Core/Notes/`; both `PianoRoll.cs` | S–M |
| S5 | **Lane roles and arrangement view state.** Per sequence: main, take or automation. Per track: automation lanes shown, heights, folds. The session's auto-fill and track list skip take and automation lanes | new `ReBuzz/Core/ArrangementStore.cs` ("Arrangement" subsection); `SessionLayout.Identity.cs` | S |
| S6 | **Arrangement takes.** A take target (pattern, origin in song ticks) instead of a slot; `ArrangementRecorder` beside `SessionRecorder` | `SessionCore.Recording.cs` (`RecordWindow`), `SessionTake.cs` (`TakeWriter`), `SessionLayout.Takes.cs` | M |
| S7 | **MPE (editor side, not the engine).** Computed note lanes over the engine MIDI column: read through `IPattern.Columns`, edited through `IPatternColumn.SetEvents` with a song undo action, never played by MPE. The Note-column bridge `ExportTrackerNotes`/`ImportTrackerNotes`, beside `ExportClipNotes`/`ImportClipNotes`, reached through `MachineManager` | `ModernPatternEditorMachine.cs`, `PatternEditorUtils.cs`, `PatternEditor.Core.cs` (S6), the Avalonia `PatternGrid` | L |

Sizes as in session-view.md: S < 500 LOC, M 500–2,000, L 2,000–5,000.

### 8.1 As built (AR0)

**S1 Song meter** (`ReBuzz/Core/SongMeter.cs`).
- The engine owns the bar list: `ReBuzzCore.SongMeter`, the rules and file format of `TimeSignatureList`. As `PatternColours` gives the engine the editor's colours, this gives it the bars; but here the editors use the engine's list, not the other way round.
- `SongMeterStore` reads the bars from the `SequenceEditor` subsection when a song opens, and resets them for a new song.
- It also writes that subsection when the song is saved. A sequence editor that writes its own (WPF's, the studio's) subscribed later, so its copy replaces this one. Where there is none (rebuzz-headless), the song keeps its bars, the editor's zoom and the pattern colours. Headless saves now have this subsection, as upstream's do.
- The studio's sequence editor edits `SongMeter` itself. Its port of `TimeSignatureList` moved into the engine.
- The WPF editors keep their list (`ViewSettings.TimeSignatureList`; the modern editors use the classic one's). `ReBuzz/AppViews/SequenceEditorMeter.cs` mirrors it both ways and follows the list when the editor replaces it (a song opened, a new song).
- The Session View: until the song saves a layout of its own, its bar is the meter's first bar, in beats (12 ticks at TPB 4: 3 beats). A bar that isn't whole beats, and Buzz's untouched default (16 ticks), keep 4 beats, as before at any TPB. A saved layout keeps the bar it was given.
- MCP reads it (`arrangement_get`'s `meter`). Bar.beat positions still use the session's beats per bar.

**S2 Clip ids** (`SequenceEvent.Clip`, `ReBuzz/Core/ArrangementClips.cs`).
- `Clip` is an int on `SequenceEvent`, 0 for none, kept by the copy constructor, the four `EventRef`s (BuzzGUI.Common's and the three WPF editors'), `SequenceEventRef` (machine delete and undo) and machine replace.
- `ArrangementClips` (`ReBuzzCore.Clips`) gives new ids and keeps each clip's loop: `ClipLoop(Start, End, On)`, in ticks into the pattern, End 0 for its end. The clip's start is its first event's `Offset`. Ids are never reused while a song is open, so an undo that brings events back never finds their id taken.
- **Reading clips** (`ArrangementClips.Of`): consecutive PlayPattern events of one pattern with the same id, each playing on to where the next starts, are one clip. Any other event (a Break at the end, say) or a gap ends it. An event without an id is a clip of its own.
- The "ArrangementEvents" subsection is version 2: a `clip` attribute on events, and a `Clip` element (id, loop start, loop end, on) per clip in the song. Version 1 readers skip both.
- The recorder writes a launch and its loop wraps as one clip, with the session clip's loop; a relaunch, or recording that starts mid-clip, begins a new one. *Insert into arrangement* writes each track's clip with its loop.
- Copy and paste in the sequence editors keep the id, so a pasted clip shares it (and its loop). AR1's copy gestures will give copies their own.

**S3 Automation ramps** (`PatternColumnCore`, `WorkManager.PlayPatternColumnEvents`).
- In an engine-played automation column, an event whose `Duration` is above 0 ramps in a straight line to the next event in the column. The Duration's value doesn't matter; writers can set the gap to the next event.
- The value is worked out at the start of each tick, and of each subtick when subtick timing is on: where machines take parameter values. It is rounded to the nearest whole value, and set only where it changed, so a machine isn't told the same value again. On an event's own position the event plays, as before; after the column's last event the value holds.
- Columns without a Duration above 0 (`HasRamps`, kept as events change) skip all of this, so every song before ramps plays exactly as before.
- Ramps allocate nothing of their own. *Found:* any parameter change on the audio thread allocated, for steps as for ramps: it reached the machine through `MachineCore.parametersChanged`, a `ConcurrentDictionary` whose entries (after each tick's `Clear`) and enumerators were new objects. Measured on Windows CI with four machines automating a parameter: about 3.6 KB per 256-frame buffer, steps and ramps alike. The 0.4.1 test played no automation, so it stayed at 0 bytes. **Fixed** (engine hardening): changes are bits made once (`MachineCore.ParameterChanges`, `ReBuzz/Core/ParameterChanges.cs`: one a track on each parameter, as its pvals, and one a group and track on the machine), set atomically by `SetValue` and taken by the machine's tick, which sends them in Buzz's order (globals by index, then tracks; the dictionary's was by hash), one call for each parameter and track with the last value, as before. Pattern editors' path (`machine.ParameterGroups[g].Parameters[i]`, which built new collections on every call) and an input's amp and pan (`Inputs`) allocate nothing either. `ReBuzzTests/EngineHardeningTests.cs` asserts 0 bytes a buffer for four machines playing steps or ramps on a global and four tracks, values set as the Modern Pattern Editor sets them, and a mapped MIDI controller. Before the fix it measured 0.4 KB a buffer for the steps, 7.8 KB for the ramps (set on every subtick), 14.7 KB for the pattern editor's way and 2.5 KB for the controller; 40 bytes of every value sent were a closure in `ParameterCore.InvokeEvents`, made on each call. Most of the 3.6 KB first measured here was something else: the first audio a process plays runs tier-0 code, which boxes `Enum.HasFlag` in the work loop. The ramps test now reads past that before it measures, as the 0.4.1 test does.
- Upstream keeps the durations (TAP2 writes them) and plays the events as steps.

**S4 Note sources** (`ReBuzz/Core/Notes/`).
- `INoteSource`: `Pattern` (as it stands now), `IsLive`, `Notes()`, `Length`, `WhyNotEditable` (null, or why not in words), `Edit(removed, added)` (null when made, else why not), `Preview`, `Undo`, `Redo`, and `Changed`. `Changed` is raised after the source's own edits, undos and redos; hosts poll for changes made elsewhere, as the piano roll windows already do.
- `SessionClipNotes`: SessionLayout's calls, on the session's own undo.
- `MidiColumnNotes`: the engine MIDI column, each edit one `ColumnEventsAction` on the song's history (it finds the pattern by name, so undo works after a machine is made again).
- `TrackerColumnNotes` (D3 A): the pattern's engine-played Note columns, a voice per track, as the agent writes them for machines that take notes in their tracks (`TrackNotes`). A note that would need more tracks than the machine has is refused, with the reason. A moved note keeps its track; a new one takes the lowest free track. Velocity goes to a track parameter named "vel…". *Deviation:* the Modern Pattern Editor's own Note columns aren't reachable before S7, so this source covers the engine's Note columns now; S7 adds the editor's through `ExportTrackerNotes`/`ImportTrackerNotes`.
- `EditorPreviewNotes`: the pattern editor's `PatternEditorMachineMIDIEvents`, read only.
- Both `PianoRoll`s (WPF and the studio) take a source; their old constructor passes `SessionClipNotes`, so the Session View behaves as before.

**MCP:** `arrangement_get` gives each place's clip id, the part of the pattern it plays (`part_beats`) and its volume (`gain`); each lane's `clips` (id, pattern, start, end, start in the pattern, places, loop); and the song's `meter` (mcp-server.md §5.5).

**Found on the way:**
- BMX songs loaded a pattern's engine columns out of order: the TAP2 loader put the second column first (upstream's out-of-range fix clamped to Count − 1). A pattern with a MIDI column and an automation column, as session takes with automation have, swapped them on every open, so a song never saved back the same. Fixed in `BMXFile.LoadTAP2`.
- `tools/song-roundtrip.py --compare` flagged parameter names that BMX can't hold ("Vel → amp" is "Vel ? amp" in Latin-1). It now compares names as BMX writes them.

**Audio thread hygiene** (after AR0; `ReBuzz/Audio/RenderJobs.cs`, `AudioWarmUp.cs`, `ReBuzz/Core/UiEventQueue.cs`).
- Nothing is allocated per buffer on the audio or render threads, idle or playing (was about 1.3 KB a 256-frame buffer with an empty song and 2.6–4 KB with one synth, in the studio). The work algorithms' Tasks, `Task.WaitAll` and LINQ sorts became reused jobs on the engine's own render threads, and `HasFlag` bit tests.
- UI events raised on the audio thread (parameter values and descriptions, the play position, machine activity) set a pending flag and go on one preallocated queue per engine, drained on the UI thread: a cached post, or in the studio and WPF a 60 Hz poll, as their posts allocate. Listeners see every final value; changes between drains coalesce.
- A warm-up thread compiles the engine, BuzzGUI and each machine's assembly ahead (`PrepareMethod` after `GetFunctionPointer`, which .NET 9 and later need for interface methods), and the audio and render threads' loops are optimized at once (no OSR), as are the machines' long DSP loops and the engine's other audio-path loops (the sync clock's line fit, a wave layer's copy: `AggressiveOptimization`, compiled by the warm-up; their tier-0 code was replaced on the stack on the audio thread, as Neo Multiband's on CI's first play, studio-mvp.md §7). First play and first stop compile nothing on the audio thread (were 23 methods in 5.6 ms, and 29). The device's first buffers still compile about 20 BCL generics in 4 ms (were 111 in 46 ms).
- The Modern Pattern Editor plays without allocating, in its shared sources (WPF and portable).
- **Quick JIT for loops is off** in every app and test host the audio thread runs in (`tools/msbuild/AudioThreadRuntime.props`, imported by ReBuzz.csproj (NeoBuzzLive.exe), the studio (NeoBuzz Live.app), rebuzz-headless, NativeMachineFramework (its runtimeconfig ships beside the exe), ReBuzzTests, ReBuzz.Studio.Tests and ReBuzz.SessionTests; NeoBuzz.MachineTests and PedalTests turn tiering off altogether): a method with a loop is compiled fully optimised the first time, never at tier 0, so nothing is ever replaced on the stack, ours or a machine's, marked or not. The `AggressiveOptimization` marks stay. CI checks every runtimeconfig.json beside NeoBuzzLive.exe in the package, rebuzz-headless's and the studio's, and the installed app's. Measured on one build in one job each, `DOTNET_TC_QuickJitForLoops` (which overrides the runtimeconfig) alternating on (1, .NET's default, as before) and off, run by run (Linux: AMD EPYC 9V74, 4 cores; macOS: Apple M1 (virtual), 3 cores; Windows: AMD Zen 5, 4 cores; two rounds, runs 42184901107/42184903866/42184906850 and 42193169588/42193224571/42193173261; start-ups the median of 10 each way, machines and CPU of 3–5; the second Windows runner was slower all round):

  | | Linux | macOS | Windows |
  |---|---|---|---|
  | first play of every Neo machine (17, all created just before, after the warm-up), compiled on the audio and render threads, play + stop | 1 + 1–2 → 0 + 0, every run (12 of 12 over the two rounds) | 1 + 1–2 → 0 + 0 (8 of 8) | — (the Studio tests run on Linux and macOS) |
  | the same with the runtime's call-counting delay at 0 | 1 + 0–1 → 0 + 0 (12 of 12) | 1 + 0–1 → 0 + 0 (8 of 8) | |
  | on-stack replacements in the process meanwhile | 1–3 → 0 | 1–3 → 0 | |
  | on-stack replacements over the null-test renders (`DOTNET_JitDisasmSummary`) | 616 in 91 renders → 0 | 160 in 23 → 0 | 622 in 91 → 0 |
  | methods compiled fully optimised at once there | 9,409 → 182,688 | 2,958 → 48,272 | 30,892 → 234,866 |
  | start-up, rebuzz-headless --agent to its initialize answer | 0.462 → 0.632 s; 0.467 → 0.639 | 0.525 → 0.692 s; 0.543 → 0.737 | NeoBuzzLive.exe --agent: 0.968 → 1.169 s; 1.575 → 1.820 |
  | start-up, the studio's window drawn (--smoke) / the WPF window up (its title set) | 1.600 → 2.038 s; 1.582 → 1.996 (Xvfb) | 1.897 → 2.746 s; 2.193 → 2.391 (spread 1.4–5.7 both ways) | 0.81 → 0.89 s; 2.90 → 3.24 |
  | the 17 Neo machines added, each the first of its kind | 1.59 → 2.50 s; 1.63 → 2.51 | 1.54 → 2.24 s; 1.51 → 2.38 | 1.75 → 3.19 s; 3.49 → 5.23 |
  | their first render, 2.5 s of audio | 0.267 → 0.114 s; 0.272 → 0.118 | 0.285 → 0.127 s; 0.394 → 0.163 | 0.19 → 0.094 s; 0.42 → 0.177 |
  | the warm-up's own compile time (AudioWarmUp) | 2.7–3.1 → 4.7–5.0 s | 2.6–3.7 → 4.8–5.4 s | |
  | steady state, ms of a machine's work per second of audio (`render` with `cpu`, the third of three renders a second apart in one process, so that tiering has finished): Neo Poly (`long-poly`) | 30.4 → 31.5 (+3.8 %) | 31.1 → 40.1 (+29 %; runs 30.0–35.0 and 34.7–42.7) | 40.7 → 42.3 (+4.0 %) |
  | Neo FM6, Neo Sampler | 64.4 → 65.9 (+2.3 %), 15.2 → 15.4 (+1.6 %) | 48.9 → 49.3 (+0.8 %), 12.0 → 11.8 (−1.4 %) | 76.0 → 77.1 (+1.5 %), 20.4 → 20.7 (+1.6 %) |
  | Neo Verb, Neo Ensemble (after Neo FM6) | 30.9 → 30.5 (−1.4 %), 2.38 → 2.58 (+8.2 %) | 28.6 → 32.0 (+12 %), 2.13 → 2.39 (+12 %) | 31.6 → 33.2 (+5.1 %), 3.07 → 3.40 (+11 %) |
  | Neo Filter, Neo Multiband (mid/side), Neo EQ graphic | 16.4 → 16.4 (+0.1 %), 12.7 → 12.2 (−4.5 %), 8.49 → 8.64 (+1.8 %) | 22.8 → 22.3 (−2.3 %), 16.0 → 15.0 (−6.2 %), 12.7 → 12.8 (+0.9 %) | 19.2 → 19.5 (+1.8 %), 15.7 → 15.9 (+1.3 %), 10.4 → 10.0 (−3.5 %) |

  The one method the audio thread compiled at first play was Neo Analyzer's `LoudnessHistogram.Gated` (its integrated loudness), replaced on the stack (tier 5) as its loop ran long, then `Range` and `AtRank` in the stop: the audio path's long loops not marked. With the setting nothing can be. In steady state code with a loop is compiled once, fully optimised but without the profile (PGO) tier 1 would have used: most machines are within a few per cent either way, Neo Ensemble's loops 8–12 % slower and Neo Poly's on Apple silicon 29 % (4 % on x64), still 3–4 % of a core for 16 voices. The bigger cost is at start: every method with a loop costs a full compile, which the engine's warm-up and each machine's assembly (compiled on the thread making the machine) pay, so a start takes 0.08–0.85 s longer (10–45 %) and the first machine of each kind 30–90 ms more; the first renders are faster (no tier-0 code). Not added, as options: ReadyToRun images of our assemblies (start-up compiled ahead; tiering and PGO stay for hot code, which goes to tier 1 on the runtime's own thread, and R2R code has no on-stack replacement), compiling a machine's assembly but its audio path on the warm-up thread instead of the one making it, or the setting left on with the analysis library's loops marked too.
- First play allocated on the audio thread too, the same with the setting either way: 340 KB with every Neo machine, Neo Wave 221 KB of it and Neo Comp 127 KB (each alone, a Release build; the others 640 bytes together). Fixed 2026-11-06 (fix/followups-1), 0 bytes now: Neo Wave's voices asked for the sub oscillator's table every block through a method whose lambda captured its parameter, and C# makes such a closure as the method starts (24 bytes a call); Neo Comp, the Neo machine with several inputs, read its channel sets with `Count()` and `ElementAt()`, boxing an enumerator each time (about 320 bytes a buffer: `MachineConnectionCore`'s channels are arrays for the audio thread now); Neo FM6's DX7 tables and its voices' carrier gains, and Neo Drums' tables, are static, made by the first note (their constructors run them now); the engine's pattern lists grew on the first play (sized ahead); and the transport's Playing event was posted as a closure at every start (216 bytes; it goes through the UI event queue now). Each machine's first Work after it is made still sets it up for the rate on the audio thread (about 3 MB with all of them: Neo Verb 1.5 MB, Analyzer 950 KB, the others 30–100 KB): left, since moving that changes when their state starts. Tests: `AudioHygieneTests.TheFirstPlayOfEveryNeoMachineAllocatesNothing` (every Neo machine made, then the first play and stop: 0 bytes; 488 were left once Neo Wave and Neo Comp were fixed) and `NeoBuzz.MachineTests/FirstNotesTests.cs` (each synth engine's first 400 blocks from its construction, notes coming and going: 0 bytes; Neo Wave 737,640 and Neo FM6 1,144 before).
- Idle, the studio allocates 120–180 KB/s (was 250–700 KB/s), all UI-side. Under Xvfb most GCs are gen2 background GCs that Avalonia's software framebuffer induces (memory pressure), 1–2 a second idle. `SustainedLowLatency` was already on while the device is open.
- *Left:* a UI or MIDI write between a tick reading its pval and clearing it is lost, as upstream (`ParameterChanges.Release`). Closing it means holding `workLock` across the tick, which `SendGUIMessage` holds across a native machine's UI round trip. *Upstream, left:* an MPE MIDI-lane note-off on the pattern's last tick never fires.
- Tests: `ReBuzz.Studio.Tests/AudioHygieneTests.cs`. 0 bytes a buffer for an empty song (each work algorithm, multithreaded or not) and an MPE pattern playing notes and automation; automation with listeners allocates 0 and they see the final values; after the warm-up the first play and stop compile nothing.

**Exit check:**
- `ReBuzz.Studio.Tests/DawModesSeamTests.cs` (Linux CI): the meter, clip ids through edits, undo and both formats, the recorder and scene insert, ramp values against steps, each note source, both rolls on a source. And the song: clips, loops, a ramp and a meter. It round-trips in .bmx (A and B the same bytes) and .bmxml (the same but for the stamps), and `song-roundtrip.py --compare` passes for both. Its core sections are byte-equal to those of the same song without clip ids and meter; only the subsections differ.
- `ReBuzzTests/DawModesSeamTests.cs` (Windows, release build): computing and setting ramps allocates 0 bytes (playing them whole is reported; `EngineHardeningTests.ParameterChangesAllocateNothing` asserts 0 bytes for it); ramps move on subticks; the WPF editors' bars mirror the engine's; without a sequence editor the engine keeps the bars in both formats; the WPF roll on a source. The piano rolls' own tests (`PianoRollTests`, the studio's `SessionViewTests`) pass unchanged.

## 9. Phases

Effort in person-weeks (pw), with coding agents doing most of the typing, as in the other specs. Each phase also updates the MCP tools for what it adds, within its pw.

| Phase | When | Delivers | Exit check | pw | Risk |
|---|---|---|---|---|---|
| **AR0 Seams** (built, §8.1) | now, beside P3 | S1–S4; MCP reads clips and the meter | ReBuzzTests and studio tests green. A song with clips, loops, ramps and a meter round-trips in .bmx and .bmxml, core sections byte-equal. 0 bytes per buffer. Both piano rolls behave as before | 1.5–2 | low |
| **AR1 Arrangement view** | after P3's exit (S9) | The Sequence tab's Arrangement mode: tracks with lanes; clips (draw, move, copy, split, join, trim, loop, make unique, shared badge); the meter grid; Master with tempo and markers; overridden lanes greyed; S5 | Build an 8-bar song with looped and trimmed clips by mouse (headless). It renders the same as the equivalent plain events, and the same as upstream's playback apart from trims | 2–3 | medium |
| **AR2 Piano roll everywhere** | after AR1; the Pattern tab toggle after S6 | `PianoRoll` in the arrangement panel and the Pattern tab; velocity and CC lanes; scale and fold; step input; context ghosts | A bass line drawn in the arrangement plays in its session clip. The Pattern tab's toggle edits the same notes. Undo is on the song's history | 1.5–2 | low–medium |
| **AR3 The tracker sees the notes** | after S6 merges | S7: MPE's computed note lanes and the Note-column note source (D3 A) | A take recorded in the session shows in the tracker at once. Editing a row changes only that note. In a random round trip of 1,000 notes through tracker edits, untouched notes stay exact. Old songs' MPE data stays byte-equal | 3–4 | high |
| **AR4 Automation** | after AR1 | Lanes for state parameters; clip automation stitched along the timeline; automation clips on automation lanes; S3 ramps drawn; knob moves recorded into lanes | A 16-bar filter sweep over a looped 1-bar clip renders as drawn. Tracker columns show as steps. Overlaps are marked | 2–3 | medium |
| **AR5 Recording and links** | after AR2 | S6: MIDI recording into the arrangement; loop recording to take lanes; comping; launching from the arrangement; dragging between session and arrangement | Record 3 loop passes and comp one bar from each: the render equals the comp. Launch an arrangement clip in the session, then go back to the arrangement | 2–3 | medium |
| **AR6 Tracker mode** | with P4 | The canvas's vertical orientation (Buzz's default sequence view). WPF is retired, so its compatibility work ends | The WPF vertical editor's parity checklist | 1 | low |

**Total: 12–18 pw** (AR0–AR6). P3 itself was planned at 10–15 pw.

Order (D5 A):
1. AR0 now; it touches only the engine and the piano roll classes.
2. AR1 and AR2 after P3's exit, beside plugin-host H1. Different code: views here, a machine host there.
3. AR3–AR5 before P4. AR3 can go first if the tracker matters most to you.

## 10. Risks

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| MPE's computed note lanes are the hardest piece: a 12k-LOC editor, three builds, its own undo | high | AR3 slips | AR3 stands alone; the 0.7.1 moves remain meanwhile. Plan-then-write as in 0.7.2 (`PlanClipNotes`, `WriteClipNotes`) |
| Shared patterns surprise Live users | low (D1 A1) | an edit in one place changes another | Copies are independent unless Alt/Option-dragged; shared badge with a use count; *Make unique* on every clip menu |
| One play position per pattern | certain | a shared pattern can't play on two lanes at once | Overlaps warned; *Make unique* |
| Upstream plays trims whole | certain | songs differ in upstream | Documented since 0.5.1; nothing to do within upstream |
| A tracker column and a lane write one parameter | medium | an unexpected value | Overlaps marked; precedence checked in AR4 (§3.4) |
| Native machines' MIDI support can't be detected | medium | a silent piano roll on some machines | Managed: detected. Native: a test note and a per-machine switch; the song census (cross-platform.md §8) |
| TPB ≠ 4 | low (unmeasured) | tracker and roll grids disagree | Count in the census; the projection follows MPE's rows |
| Avalonia drawing of long arrangements with previews | medium | a sluggish view | Layers as in S4; previews cached per pattern |
| Several undo histories (song, session, MPE, sequence editors) | medium | confusing undo | studio-mvp.md §3.10's rule; the arrangement and piano roll use the song's history. Done in [`history-snapshots.md`](history-snapshots.md) HS1: the Session View's edits are steps of the song's history too, so there is one |
| Scope creep toward "a full DAW" | high | never finished | Each phase usable alone, with an exit check |

## 11. Sources

**Repository** (fork `90b0850` and the S4 and S6 branches, read 2026-11-03):
- Interfaces: `ReBuzzGUI/BuzzGUI.Interfaces/` (`IPattern.cs`, `IPatternColumn.cs`, `ISequence.cs`, `ISong.cs`, `ISession.cs`, `IParameter.cs`, `IBuzz.cs`).
- Song model and stores: `ReBuzz/Core/` (`PatternCore.cs`, `PatternColumnCore.cs`, `SequenceCore.cs`, `EngineColumns.cs`, `ArrangementEventStore.cs`, `SongMarkerStore.cs`, `ViewState.cs`).
- Session: `ReBuzz/Core/Session/` (`ClipNotes.cs`, `SessionLayout.Notes.cs`, `SessionLayout.Takes.cs`, `SessionArrangement.cs`, `SessionCore.Recording.cs`, `SessionTake.cs`, `SessionMixer.cs`).
- Engine: `ReBuzz/Audio/WorkManager.cs`, `ReBuzz/MachineManagement/MachineManager.cs`, `ReBuzz/ManagedMachine/ManagedMachineHost.cs`.
- Files: `ReBuzz/FileOps/BMXFile.cs` (PATT, TAP2), `BuiltInPattern.cs`.
- Tracker side: `ReBuzzGUI/BuzzGUI.Common/TrackerNotes.cs`; `ModernPatternEditor/ModernPatternEditor.NET/` (`MPEStructures/`, `PatternEditorUtils.cs`, `PlayRecordManager.cs`, `ModernPatternEditorMachine.cs`); `Machines/ModernPianoroll/`.
- Sequence editors: `ReBuzzGUI/ModernSequenceEditorHorizontal/` (`TimeSignatureList.cs`, `SequenceEditor.xaml.cs`), `ReBuzzGUI/BuzzGUI.SequenceEditorExtended Rebuzz/SequenceEditor.xaml.cs`.
- Studio: `ReBuzz.Studio/Views/` (`StudioViews.cs`, `Session/PianoRoll.cs`); S4 `Views/Sequence/`; S6 `ModernPatternEditor/Avalonia/`, `Portable/`.
- Agent: `ReBuzz/Agent/PatternTools.cs`, `ReBuzz.Agent/Model/Theory.cs`.

**Web:**
- FL Studio, playlist patterns: https://www.image-line.com/fl-studio-learning/fl-studio-online-manual/html/playlist_patterns.htm
- Renoise, Pattern Matrix (aliases, muting): https://tutorials.renoise.com/wiki/Pattern_Matrix
- Bitwig, One DAW, Two Sequencers: https://bitwig.com/userguide/latest/one_daw_two_sequencers
- Ableton Live 12, Comping (take lanes): https://www.ableton.com/en/live-manual/12/comping/
