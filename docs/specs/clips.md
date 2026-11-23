# Clips: one store for everything a clip holds — Proposal

| | |
|---|---|
| Status | **Proposal** (2026-11-23, revised the same day). Design only: no product code changed. D1 decided **B** (code identifiers too; §9.4–9.7). D2 answered **B, pending the loss analysis** (§7.5); D3 and D4 open (§12) |
| Decision (binding) | "Unify and only use one automation store for both tracker and clips. Tracker patterns should use the same exact store (also for notes, velocity, length as well as all parameters automation) as the clips and arrangement views. So effectively we unify patterns and clips which are used anywhere. We'll call them all 'Clips' everywhere." (the user, 2026-11-23) |
| Also decided that day | EV1 (parallel branch): an envelope lane in the Detail panel, read-only curves on clips, writes the engine automation columns. T2 (waits for this plan): a vertical envelope lane beside the tracker's columns, with a show/hide toggle on each parameter column. MT1 (parallel): tracks in the Machines view |
| Scope | Every store of clip content, every reader and writer of each, the one store, the tracker as a view of it, old songs, the rename, phases and gates |
| Code baseline | fork `4f0beba5` (origin/main, 2026-11-23) |
| Related | daw-modes.md (§3 the model, §8 AR0–AR6 as built), session-view.md (§4.2, 0.7), native-format.md (NF1, NF2 §11), agent-knowledge.md (§11), mcp-server.md |

---

## 1. Summary

**Verdict: the store already exists. It is the pattern's engine columns (`PatternColumnCore`). The work is to move everything into it, not to invent one.**

- **Today a clip's content is in up to five places** (§2): the engine MIDI column, the engine parameter columns, the Modern Pattern Editor's (MPE) own per-machine blob, the built-in Buzz rows an old song brings, and a foreign editor's opaque blob. Each has its own readers, its own timing and its own undo.
- **Every one of them is a list of `PatternEvent(time 1/240 tick, value, duration)`**, keyed by (machine, parameter, track). MPE's columns are the same type as the engine's. So the one store is the engine column list, and the work is mostly a change of owner, plus a few things the engine columns do not yet say (who plays a column, what a voice is, the row grid).
- **Recommendation**
  1. **The store is the clip's column list.** Notes of MIDI machines: the MIDI column (time, length, velocity, channel; the column's `Track` is the voice). Everything else, notes of classic Buzz machines included: parameter columns, steps or ramps. No new file section: TAP2 already writes events, durations and metadata (§5).
  2. **A column has one player**, named in its metadata (`Play=engine` or `editor`). Migrated tracker columns keep the editor as player until a separate, gated wave moves them into the engine (D3). That makes "bit for bit" true by construction in the first waves.
  3. **The tracker stays MPE, adapted** (§6): its data layer reads and writes the store through a small seam; only view state (column set, order, rows per beat, mutes) stays in its blob. A new tracker costs 8–12 pw more and gains nothing the seam does not.
  4. **Old songs convert on open, in memory**, by one pure converter, with the original file untouched (§7). Classic built-in rows, MPE and PXP blobs convert exactly. MPE's MIDI lanes become notes with exact time (equal in music, proven by the null test, with a timing probe first). Foreign editors are not converted unless the user asks (D2).
  5. **The rename (D1 B):** UI, browser, MCP (aliases for two releases), guides, docs, tests **and the code identifiers**, behind a compatibility layer: what a binary, a saved file or a reflection lookup stores keeps its name, the new names sit beside the old (§9.4–9.7). File formats keep "pattern".
- **Effort:** 13–19 pw in seven waves (§10; 11–16 before D1 B). The goldens come first (CU1), because the gate is "migrated songs render bit for bit as before". T2 can start after CU3.

### 1.1 Words

| Word | Means | Was |
|---|---|---|
| **Clip** | The unit of content: a length and its columns. Used by session slots, the arrangement and the tracker | pattern, and the session's clip |
| **Placement** | One run of a clip on an arrangement lane (a `clip id` in the code). The user sees a clip on the lane; "linked ×3" says the content is shared | the arrangement's "clip" |
| **Lane** | One column of a clip: notes, one parameter on one track | column |
| **Note lane** | The MIDI column of a clip | MIDI column |

The word "pattern" stays where it is a Buzz fact: `.bmx` sections, upstream, Classic, the loss report's "Patterns" row, the `IPattern` interface.

---

## 2. What exists: every store of clip content

### 2.1 The stores

| # | Store | What it holds | Who plays it | Saved in |
|---|---|---|---|---|
| S1 | **Engine MIDI column** (`PatternColumnType.MIDI`, `PatternCore.Columns`) | Events: time, packed message `status \| d1<<8 \| d2<<16`, `Duration` = note length (negative: no note-off). Notes (velocity = d2, channel in status), control changes, pitch bend (`ControllerPoint`). One column, `Track` 0 | `WorkManager.PlayPatternColumnEvents`, every active pattern, any machine | TAP2 (`.bmx`, `.nbl`), `.bmxml` |
| S2 | **Engine parameter columns** (metadata `SessionAutomation`) | Steps, or ramps (`Duration` > 0 to the next event, S3). Written by session takes, the arrangement recorder, the agent (`automation_write`), the lanes | Same, `EnginePlays`, `Parameter.SetValue(track \| 1<<16)` | TAP2 |
| S3 | **Engine Note and velocity track columns** | Note-ons and `Off` as steps, one column per track, for machines that take notes in tracks. Written by `MachineNotes.Replace` (`TrackNotes.Allocate`: lowest free track, refused beyond `MaxTracks`) | Same (they are S2 columns) | TAP2 |
| S4 | **MPE data**, `MPEPatternDatabase`, one blob per machine in the hidden editor machine (`\x01pe1`…), keyed by **pattern name** | Per pattern: rows per beat, then columns (machine name, parameter index, track, graphical flag, events at exact time and value, no duration, rows per beat of each beat). Columns may belong to **other machines** than the pattern's. Internal parameters for MIDI machines: Note, Velocity, Delay, Cut, Pitch Wheel, CC (index −128…−133). Column set and order, mutes. Written as MPE format (`0xFF`, v1) or PXP v3 (`PXPDataFormat`, quantized to rows) | `PlayRecordManager.Play`, from the editor machine's `Work`, after `CallTick`, for sequences of its machine | `PatternEditorData` blob (machine data) |
| S5 | **Foreign editors**: PatternXP (native, `ReBuzzPatternXP`), upstream's Jeskola Pianoroll prototype (`Machines/ModernPianoroll`, writes Note columns in its own store, `ExportMidiEvents` returns null), any other | Opaque. Read only through `PatternEditorMachineEvents` (columns as events) and `PatternEditorMachineMIDIEvents` | The editor | `PatternEditorData` blob |
| S6 | **Built-in Buzz rows** (`PATT`) | Old songs: rows × parameters per track, plus **input columns** (connection amp and pan per row). On load: converted to a PXP v3 blob for the editor (`BuiltInPattern.ToPXP`, rows per beat 4), where input columns are **dropped**; with no editor kept on the pattern (`BuiltInData`). `PATT` is written **empty** by every fork save (a PATT row is `NoValue`) | The editor | `PATT` (old songs), `XTAP` links patterns to the editor |
| S7 | **Session clip settings** (`SessionDocument.ClipSettings`, by (machine name, pattern name)) | Launch mode, quantization, legato, loop, start and loop markers, velocity sensitivity, follow actions; `Audio`: asset, tempo, warp mode, **warp markers**, gain, transpose, detune, fades, tail, reverse | `SessionCore` (launch), `SessionClipEngine` (audio) | `SessionView` subsection |
| S8 | **Arrangement placements**: `SequenceEvent` (`Offset`, `End`, `Gain`, `Clip` id), `ArrangementClips` loops, lane roles (main, take, automation), takes | Where a clip plays and how much of it | `WorkManager.UpdatePatternPositions` | `SEQU`, `ArrangementEvents`, `Arrangement` subsections |
| S9 | **Controller lane** (`MidiControllers`) | Not a store: CC and pitch bend points inside S1 | S1's player | S1 |
| S10 | **Copies of S4/S5 blobs** in clone, replace, delete-undo, templates, history snapshots (`LiveSong`) | The whole machine blob, or `EngineColumns` for S1–S3 | – | – |

Audio clips are clips of the Session Clip Player: S7 holds their audio settings and warp markers, S1–S3 their automation of the player's parameters. Nothing else of theirs is a lane.

### 2.2 Readers and writers

| Who | S1 MIDI col. | S2/S3 engine columns | S4 MPE | S5 foreign | S6 built-in | S7/S8 |
|---|---|---|---|---|---|---|
| **Playback** | `WorkManager` | `WorkManager` | `PlayRecordManager` (editor's `Work`) | the editor | – (converted) | `SessionCore`, `UpdatePatternPositions` |
| **Arrangement automation lanes** | – | read and write (`ArrangementAutomation`) | read only, `PatternEditorMachineEvents` | – | – | – |
| **Piano roll, `NoteSource`s** | `MidiColumnNotes` | `TrackerColumnNotes` | `EditorTrackerNotes`, `EditorLaneNotes` (S4 notes as rows) | `EditorPreviewNotes` (read only) | – | `SessionClipNotes` (session roll) |
| **Tracker** | AR3 computed note lanes (display; edits become notes) | not shown | MPE (read and write) | – | – | – |
| **Recorders** | session and arrangement takes write S1, S2 | same | MPE records its own (`RecordControlChange`) | – | – | takes: `Arrangement` roles |
| **MCP** | `notes_read`, `notes_write`, `arrangement_get` | `notes_*` (tracks mode), `automation_write`, `automation_read` (**only S2**), `arrangement_automation` | none: **the agent cannot read or write tracker data** | – | – | `session_clip_set`, `audio_clip_*`, `arrangement_clip`, `arrangement_place`, `takes` |
| **Save and load** | TAP2 / `.bmxml` | TAP2 | blob in machine data (per machine, 7z in templates) | blob | `PATT` empty on save | subsections |
| **Buzz export (NF2)** | kept in TAP2, upstream plays it | kept, never played upstream | kept; names ASCII (a Latin-1 rename can lose it) | kept | – | trims, gain, ids not baked |
| **Classic (WPF, frozen)** | plays (engine) | plays S2 | MPE (WPF build of the same sources) | PatternXP etc. | – | no new UI |
| **Upstream ReBuzz** | **plays** | keeps, never plays | plays through MPE (its own editor) | plays if it has the editor | – | plays sequences; loses trims, gain, ids, roles |
| **Clone, replace, undo of delete, snapshots** | `EngineColumns` | `EngineColumns` | whole blob | whole blob | – | – |

### 2.3 Found in the code

1. **MPE's columns are engine `PatternEvent`s.** The two stores differ in owner, player and key, not in shape. MPE's undo is already the song's (`EditContext.ManagedActionStack`).
2. **Two players, two timings.** The engine plays S1–S3 in `UpdatePatternColumnEvents`, *before* `CallTick`, in the block that holds the event. MPE plays in its `Work`, *after* `CallTick`, the window [previous play position, play position), that is the previous block's events (`daw-modes.md` §8.5: "a tracker column lands after the engine's in a block"; read from the code, not measured). For a classic machine that reads its parameters at `Tick`, a row-aligned value may arrive one tick apart. **Unverified. The first task of CU1 measures it** (§5.6).
3. **MPE keeps a full default column set per pattern**, empty or not. The two `.nbl` templates carry 3,648 and 4,052 columns with 0 events, 9 and 10 blobs of 2.8–40 KB. The store creates a column on its first event.
4. **The blob is keyed by pattern name and by machine.** A rename, a Latin-1 export or a replace can lose it; `PatternRenamed` patches only the live case.
5. **The agent is blind to tracker data**: `automation_read` lists only S2 columns, `notes_read` reads S1 or S3.
6. **Input columns are lost on open today** (Windows with MPE): 2 of 18 songs have them (136 cells). A host with no editor keeps them in `BuiltInData`.
7. **Chords are an input aid, not data.** They are not in the Studio build (`Chords/**` is excluded from the portable build). There is no effect-command store: "Command", "Effect" and "Cmd/Arg" are ordinary track parameters.
8. **MPE hard-codes 4 ticks a beat.** A row is a tick at 4 rows per beat; the song's TPB does not enter.
9. **The MIDI message's high byte is free** (`MIDI.Decode*` mask each byte), but the column's own `Track` is the cleaner voice (TAP2 and `EngineColumns` already keep it).
10. **A live restore swaps column lists on the audio thread** (`SwapEvents`, HS3). Tracker data, as a blob, is outside that today.

---

## 3. How often (the repo's songs)

Scanned with small local scripts (not committed) over `.bmx`, `.bmw`, `.nbl` in the repo.

| | Count |
|---|---|
| Song files | 20 old (`.bmx` 19, `.bmw` 1: Buzz-era demos in `ExternalMachines` and `Other`), 2 `.nbl` (the Bass House and Tech House templates) |
| Parsed fully (PARA present) | 18 of 20 (FSM_Kick_Demo and Makk_M4 lack PARA) |
| Songs with an editor blob, XTAP or TAP2 | **0 of 18 old** (all classic: patterns in `PATT`). Both templates: MPE blobs (10 and 9, rows per beat 4, **0 events**) and TAP2 with MIDI columns only (10 each, 86 and 147 events, no parameter column) |
| Patterns / machines with patterns | 154 / 50 |
| Note cells / Off cells | 782 / 159 (four notes in five have no Off in their pattern: they end at the next note or ring on) |
| Parameter cells | 722 on tracks, 752 global |
| Multi-track machines | 10 of 50 (the most: 8 tracks, Dynamite6; 6, Infector) |
| TPB | 4 in 16 songs, **1** (Dynamite6), **8** (11Stereo) |
| Pattern lengths (rows) | 1, 16, 32, 64, 128, 256 (two songs have 1-row patterns) |
| Effect-command columns | 7 machines (Infector ×5 "Command 1/2", Hih.TR and Clap.TR "Effect / Effect Data") |
| Input columns (amp/pan per row) | 2 songs, 136 cells (Masterizer_test 134, Elak_SVF 2) |
| MPE-only features (delay, cut, MIDI lanes, per-beat subdivisions, rows per beat ≠ 4, chords) | **0 songs.** They exist in the tests' generated songs only |

**Reading.** The corpus proves the classic path (PATT rows → Note, velocity and parameter columns, TPB ≠ 4, 1-row patterns, 8 tracks, command columns) and nothing of MPE's own features. CU1 therefore builds a **generated corpus**, one song a feature (§10), and records goldens for both. Of the old songs, only the Windows package job has their real machines: on Linux and macOS the machines are placeholders, so goldens are per platform.

---

## 4. Why not keep two stores

- Two note stores, two parameter stores and two players mean that the piano roll, the arrangement lanes, the agent, the session takes and the tracker each see part of a clip (§2.2). The Detail panel's envelope lane (EV1) and T2's lane would show different curves for one parameter.
- AR3's computed note lanes and the 0.7.1 "Move notes" commands are bridges between stores; every new feature needs a bridge pair.
- The blob's identity (a pattern name) is the weakest key in the song.

---

## 5. The one store

### 5.1 Shape

A clip is `IPattern` (length in ticks) with its `Columns`. A **lane** is a `PatternColumnCore`: machine, parameter, track, events, `BeatSubdivision`, `Metadata`. Nothing new in the file: TAP2 writes all of it.

| Lane | Events | Plays as |
|---|---|---|
| **Notes** (MIDI column; `Track` = voice) | `time`, packed message (note-on with velocity and channel; note-off; CC; pitch bend), `Duration` = length in 1/240 tick; negative = open (no note-off); 0 = a trigger or an explicit off | `SendMidiInput`; a note-on with a duration schedules its off (`playingEvents`) |
| **Parameter** (any parameter of any machine, global or one track) | `time`, value; `Duration` > 0 on an event = a ramp to the next (S3) | `Parameter.SetValue(track \| 1<<16)` |
| **Note parameter** (a track machine's Note, its velocity) | Note-ons and `Off` (`BuzzNote.Off`) as steps | the same, by the machine's tick |
| **Trigger** | Parameters without `State`: events heard once | the same |
| **Wire** (not built: connection amp and pan, S6 input columns) | – | listed in §11 |

### 5.2 Metadata (all optional; absent means what it means today)

| Key | Values | Meaning |
|---|---|---|
| `SessionAutomation` | `1` | **Kept as is.** The engine plays the column. Older builds and upstream read it the same way |
| `Play` | `engine` (default for new), `editor` | **Who plays the column.** Absent on a parameter column: the editor that owns it (today's rule for unmarked columns). The engine skips `editor` columns, as it skips unmarked ones now. Exactly one player a column |
| `Owner` | an editor machine's name | Which editor plays an `editor` column (MPE for migrated ones) |
| `Grid` | `rpb=4;sub=3:6,7:3` | Rows per beat of the lane and the beats whose rows differ (MPE's `BeatRowsList`). View only: never changes a time |
| `Mute` | `1` | The tracker's column mute (MPE `ParamterMuted`) |

View state that is not music (column order, which columns show, collapsed sets, T2's lane toggles) goes in a new `ClipView` subsection keyed by (machine, clip name), as `ArrangementStore` does: losing it loses a layout, never a note. Rows per beat per clip is `Grid` on its lanes and `ClipView` for empty clips.

### 5.3 Notes: one model, two shapes by route

One **note model** for every view and tool: `ClipNote` (time, length, pitch, velocity, voice). It is read and written by one `NoteSource` per route, chosen by `KeyboardRoute` (existing):

| Route | Stored as | The roll's rules |
|---|---|---|
| **MIDI** (Neo machines, managed `MidiNote`, manifest note input) | Notes lane: exact time, length, velocity | no limit; the tracker draws virtual voices (AR3) |
| **Tracks** (classic Buzz machines: a Note track parameter, no MIDI) | Note and velocity parameter columns, **voice = track**, allocated when written (§3.3 of daw-modes.md: lowest free track, refused beyond `MaxTracks` with the reason; a moved note keeps its track) | as D3 A today; `TrackerColumnNotes` |

This is D4 A (§12). D4 B (one list for every machine, the engine derives Note/Off/velocity at play) is the alternative with its costs.

### 5.4 What a tracker needs that a note list does not

| Tracker construct | In the store | Exact? |
|---|---|---|
| **Off** | MIDI machines: note length, or an explicit note-off event (duration 0) for an off with no note before it in this clip (a note rings across clips). Track machines: `Off` in the Note column | yes |
| **Cut** | MIDI lanes: the note's length. Track machines: no cut | yes (MIDI) |
| **Delay** | MIDI lanes: the note's time (row start + delay), exact in 1/240 tick | time yes, scheduling see §5.6 |
| **Legato, retrigger** | Open length (negative duration); the next note on the voice is the end. Track machines: Note events with no Off between | yes |
| **Velocity** | MIDI: in the message. Track machines: the machine's velocity column (by name, as `MachineNotes.For`) | yes |
| **Effect commands** (Cmd/Arg, Effect/Effect Data) | Ordinary track parameter columns; stay aligned to the voice because the voice is the track | yes |
| **Chords** | Several notes at one time; no data of their own | n/a |
| **Pitch bend, CC** | S1 events | yes |
| **Rows per beat, subdivisions** | `Grid` | view only |
| **Values off the grid** (the agent's quarter-tick steps, ramps) | A row shows the last event in `[row start, next row start)` marked as "more"; an edit rewrites only the events of that cell | the AR3 rule, generalized |
| **Input columns (amp, pan)** | Not stored (they are not today) | §11 |

### 5.5 Parameters

- A parameter lane of any machine is a column of the clip; the tracker's "other machine" columns are the same thing.
- A state parameter (`ParameterFlags.State`) is a curve (steps or ramps); a trigger is a marker. EV1 and T2 draw the same lane the tracker column edits.
- One writer rule stays: two lanes writing one parameter in one place are overlaps, found and marked, never hidden (AR4).

### 5.6 What the engine plays

**MIDI machines.** The clip's Notes lane, as today: `SendMidiInput` at the start of the block that holds the event, the note-off scheduled from `Duration`. Parameter lanes with `Play=engine`: `SetValue` before `CallTick`, ramps on tick and subtick (S3).

**Classic Buzz machines.** The Note and velocity parameter columns, as parameter steps. There is no derivation at play: what the machine plays is what is stored (D4 A). Voice allocation onto `MaxTracks` happens when a note is written (§5.3), never in the audio path.

**Migrated tracker lanes keep `Play=editor, Owner=MPE`.** MPE reads the events from the store instead of its own list (§6) and fires them as today. The sound is the same by construction: same events, same algorithm, same place in the block. The engine skips them.

**Timing probe (CU1, before any migration).** A song whose parameter step and note sit on rows, played by (a) MPE, (b) the same events as `Play=engine` columns; record, for a classic machine and a MIDI machine, at which `Tick` and in which block the machine sees each value, and the null difference. Written into this section. What the probe decides:

| Result | Consequence |
|---|---|
| Identical | CU6 (one player) is a flag flip, gated by the goldens |
| A tick or block apart | The engine's player gets a per-column `Lag` that reproduces MPE's window, so a migrated lane can move to the engine and stay bit for bit; new lanes use exact timing |

**Zero allocation.** The store's `GetEventsInto` is the audio path's read (already used). MPE's read after the seam is the same call. The seam adds no allocation per buffer; `AudioHygieneTests` and `EngineHardeningTests` get a migrated tracker song (§10 gate).

### 5.7 Everything else a clip holds

- **Launch settings, audio settings, warp markers** stay in `ClipSettings` (S7), keyed by (machine, clip name). They are clip properties already. No change in CU.
- **Placements** (S8) stay: `SequenceEvent` with `Offset`, `End`, `Gain`, the clip id of a run, lane roles, takes.
- **Identity** stays the (machine, name) pair everywhere. A stable clip id is debt: noted in §11, not done here.
- **Undo and history.** One `ColumnEventsAction` a tracker edit (MPE's actions already run on the song's stack). Snapshots and live restore (HS3) swap whole columns, so tracker data restores glitch-free for the first time. Clone, replace and delete-undo copy columns (`EngineColumns`), and the blob copies shrink to view state.

---

## 6. The tracker as a view of the store

### 6.1 The options

| | (a) Adapt MPE: a store-backed data layer | (b) A new Studio tracker |
|---|---|---|
| What it is | `MPEPatternColumn`'s event list and lock become a seam (`IColumnStore`): its own list for Classic's WPF build, `PatternColumnCore` for the portable and Avalonia build. `MPEPattern` keeps the view state. The AR3 note-lane host becomes the general note view | A new grid, editor and playback-free model over the store |
| Size today | The Studio build compiles about **9.0k** shared MPE lines, 1.8k portable and 2.2k Avalonia (the WPF-only 3.7k and chords 0.6k are out) | Rewrites most of those 13k lines |
| Keyboard parity | Free: `PEKeys.xml`, `KeyboardMapping`, `Digit`, `Selection` untouched | Rebuild and re-test every key (`EditorKeys` 115, `KeyboardMapping` 153, `Digit` 323, `Selection` 203) |
| Rows per beat, subdivisions, column sets, mutes | Free (view state in the blob) | Rebuild |
| Hex and decimal entry | Free (`Digit` by parameter type) | Rebuild |
| Multi-track machines | Free (columns per track) | Rebuild |
| Room for T2 | A lane strip beside the column: `PatternGrid` (908 lines) gains a strip; the column already knows its lane | Designed in from the start |
| Classic | Not touched: its WPF build keeps the list store | Not affected |
| Cost | **3–4 pw** (CU3) | **8–12 pw more** than (a), then CU3's migration on top |
| Risk | The shared sources: the seam must not change Classic. Guarded by `#if WINDOWS` and Classic's tests | Parity regressions in a tool people type in all day |

### 6.2 Recommendation: (a)

Revisit (b) only if T2 or T3 shows that the grid code blocks the lane, not before. Under (a):

- **What leaves MPE:** its event lists and `PlayRecordManager`'s *read* of them (it reads the store); `ExportMidiEvents`/`ImportMidiEvents`/`ExportClipNotes`/`ImportClipNotes` bridges and the 0.7.1 "Move notes" commands (CU4); the editor-side copy of notes in `NoteLaneColumns` (it keeps one display role).
- **What stays:** playing of `Play=editor` lanes, recording (`RecordControlChange`, step input), chords in WPF, the digit and key code.
- **The seam in detail:** `GetEvents`, `GetEventsInto`, `SetEvents`, `Clear`, `HasEvents`, `EventsChanged`. The store's `EventsChanged` reaches the tracker, so a lane drawn by EV1, a take, or the agent shows at once; today only the AR3 note lanes poll.
- **Columns that do not exist yet in the store** are created on first write (`SessionLayout.ColumnFor(create: true)`), so the 4,000 empty default columns of a template disappear.

---

## 7. Old songs

### 7.1 On open

One pure converter, `ClipMigration`, runs after all machines are loaded (as MPE's own `ProcessEditorData` does), on every open path (Studio, headless, agent, `song_open`). It never writes the original file: a song opened from `.bmx`, `.bmw` or `.bmxml` has `LegacyOrigin` and Save asks for a name (native-format.md §7).

| Source | Converts to | Exact? |
|---|---|---|
| `PATT` rows (S6) and `BuiltInData` | Note and parameter lanes, `Play=editor, Owner=MPE`, event time = row × 240, `Grid rpb=4` | **Yes.** Same events the PXP v3 path makes now |
| MPE blob (S4, MPE format v1), parameter and Note columns | The same lanes, exact times, `Play=editor` | **Yes** |
| MPE blob, PXP v3 | The same, at the rows PXP holds | **Yes** (PXP is rows already) |
| MPE blob, **MIDI lanes** (Note, Velocity, Delay, Cut, Pitch Wheel, CC) | Phase CU3: kept as lanes (`Play=editor`), shown as notes by `EditorLaneNotes`. CU6 or *Convert to notes*: Notes lane, time = row start + delay, length from cut, off or next note, velocity, voice = lane track (`TrackerNotes.FromRows`, as `ExportClipNotes`) | Events yes; scheduling: the timing probe says (§5.6) |
| MPE columns of **other machines** | Parameter lanes on that machine, same `Play` | Yes |
| MPE column set, order, mutes, rows per beat of an empty clip | `ClipView` and `Grid` | View only |
| **PatternXP-format editors and foreign editors (S5)** | D2 is open between conversion on open and leaving them alone; §7.5 lists exactly what each conversion keeps and loses. Until the user decides, the plan below keeps the clip with `Owner=<editor>` and the blob, and the other views show a read-only preview (`EditorPreviewNotes`) | See §7.5 per editor |
| **Input columns** (S6) | Not converted (they are dropped today; converting changes the sound) | §11 |
| Engine columns S1–S3 | Nothing to do | – |
| `ClipSettings`, placements | Nothing to do | – |

**Order on open:** load machines → load engine columns (TAP2) → run `ClipMigration` for every machine whose editor is MPE or none → mark the song as migrated (in memory) → build the editor view from the store and `ClipView`. A clip whose lanes are already in the store (a song saved after CU3) skips its blob.

### 7.2 On save

| Target | What is written |
|---|---|
| `.nbl` | TAP2 with every lane and its metadata, `ClipView`. `formatVersion` 3. A song that holds migrated lanes writes `minReaderVersion` 3 (an older build refuses and names the version rather than drop tracker data, as NF3 does for audio); any other song keeps writing 2 or 1. The MPE blob is written as **view state only** (no events) |
| `.bmx`, `.bmxml` (the full, non-lossy old kind) | TAP2 as above **and a derived MPE blob** (`ClipExport`) with a stamp (hash of the store content it came from), so Classic and upstream still play the tracker |
| Export for Buzz/ReBuzz (NF2) | As `.bmx`, plus the loss report below |

**Derived blob, one rule against double play.** Notes of **MIDI machines stay in the MIDI column only** (upstream plays that column); the blob gets no MIDI lanes for them. Notes and parameters of track machines go in the blob (upstream's tracker plays them); their TAP2 columns carry no `SessionAutomation`, so upstream never plays them either.

**Classic and stale blobs.** If a song is edited in Classic and saved, its blob changes and the columns do not. On the next open in the Studio the stamp does not match: **the blob wins**, the clip is migrated again from it, and a message says so.

### 7.3 What upstream loses (additions to NF2's report)

| Category | Item | What happens upstream |
|---|---|---|
| Tracker ramps | machine / clip / parameter: K ramps | Written as steps at rows (sampled), shape lost |
| Off-grid events | K events between rows | Rounded to the row before |
| Notes of MIDI machines in the roll | clip: K notes | Played from the MIDI column (as now); not shown in upstream's tracker |
| Voices | clip: K notes | Track kept as the column's `Track`; upstream ignores it |
| Unchanged | trims, gain, clip ids, roles, ramps (steps), subsections | as NF2 §11.2 |

### 7.4 Songs saved by builds before this

A `.nbl` saved before CU3 (`formatVersion` 2) carries MPE blobs: `NblMigrations` step 2 to 3 does nothing to the manifest and the open-time converter runs as for any legacy blob. The first save writes 3.

### 7.5 D2 loss analysis: what a conversion keeps and loses, editor by editor

Asked for by the user (2026-11-23): "Analyze which data is lost exactly on conversion". Evidence: the code (`BMXFile.cs`, `Gear.cs`, `ReBuzzCore.cs` `CreateEditor`, `ReBuzzPatternXP`'s `SetPatternEditorData` and its `patternxp` sources, `PatternEditorUtils.cs`, `Machines/ModernPianoroll`, `RunTime/Gear/gear.xml`) and one test on real data (below). **No audio was rendered**: every statement about sound is read from code and marked *unverified* until CU1's goldens.

**What "convert" means.** The target is the clip's lanes (§5): a Note or parameter column per (machine, parameter, track), events at time = row × 240, `Play=editor|engine`. The converter is the same one old songs already go through when no editor DLL is present (§7.1).

**Two facts that change the question.**

1. **Conversion through MPE is already today's behaviour wherever the original editor is missing.** `BMXFile` (around line 474) replaces a missing pattern editor by the Modern Pattern Editor, and keeps the blob only if `Gear.HasSameDataFormat` says the formats match (all four editors named in `gear.xml` are marked `DataFormat="PatternXP"`, so they match); any other editor name has its blob **cleared**. The Studio, headless, macOS and Linux never have `ReBuzzPatternXP`'s DLL, so for them PatternXP songs are already read by MPE's `SetPatternEditorData` (v1–3 rows, MPE 255 v1). On Windows the same happens for *Jeskola Pattern XP* and *Jeskola Pattern XP mod* unless `ReBuzzPatternXP.NET` is built.
2. **No repo song uses any pattern editor at all** (§3: 0 of 18 old songs have an editor blob, XTAP or TAP2; the two `.nbl` templates have MPE blobs with 0 events). So D2 decides how *the user's own songs* and old songs outside the repo behave, and nothing in the repo can show a difference. That is why the analysis below says "unverified" for audio.

**The editors** (everything the package ships in `RunTime/Gear/gear.xml` plus what a song can name):

| # | Editor (name in a song) | Data format | Ships? | Repo songs |
|---|---|---|---|---|
| E1 | none: Buzz's own pattern rows (`PATT`) | rows × parameters per track, plus input columns | in every song | 18 of 18 old songs (154 patterns) |
| E2 | **Modern Pattern Editor** (MPE, WPF and portable) | MPE format `0xFF` v1 (exact times, per-beat rows, no durations) or PXP v3 | yes (`ModernPatternEditor`) | 0 (2 templates, empty) |
| E3 | **Modern Pianoroll** (`Machines/ModernPianoroll`, upstream's prototype) | MPE-format copy; its `Editor.ExportMidiEvents` returns null; marked `PatternXP` in gear.xml | yes | 0 |
| E4 | **Jeskola Pattern XP** (native, `ReBuzzPatternXP`) | PXP v1–3 (rows; v2 adds rows per beat; v3 the graphical flag), plus its SP columns | native build only | 0 |
| E5 | **Jeskola Pattern XP mod** | the same format and DLL family | native build only | 0 |
| E6 | Any other editor a song names (Buzz's own, third-party) | opaque | no | 0 |

**Features, per editor (E4/E5 are the ones that lose something).**

| Feature | E1 Built-in rows | E2 MPE | E3 Pianoroll | E4/E5 PatternXP | E6 other |
|---|---|---|---|---|---|
| Note, velocity and track-parameter cells | **Exact.** Same events the PXP v3 path makes now. Checked on real data: all 18 songs' patterns, **2,415 cells** compared PATT → PXP column mapping, **0 mismatches** (local script `pxp_check.py`; the rows→events step is the code the editor path already runs) | **Exact** (time, value) | Exact, if its data reads as MPE (same structures; **unverified** on a real blob: none exists) | **Exact** (rows; row × 240) | **Lost**: the blob is cleared today on open if the data format differs; the converter has nothing to read |
| Global parameters, columns of other machines | Exact | Exact | Exact | Exact **if the machine's parameter layout is the one the blob was written for**: the index is positional; a changed machine misaligns, as it does today | Lost |
| Event durations / ramps | n/a (none) | n/a (none in v1) | Pianoroll notes have lengths; **converted as note-on and off** (**unverified**) | none | Lost |
| Rows per beat of a clip, column order, mutes | View state, kept as `ClipView` / `Grid` | Kept as view | Kept | v2/v3 rows per beat kept; column order kept | Lost |
| Per-beat subdivisions | none | **Kept** as `BeatSubdivision` (MPE-only) | n/a | **PatternXP ignores them**: no data | Lost |
| Delay and cut (MPE internal parameters −130, −131) | none | Kept in the lane; as notes: start + delay, length from cut (CU6) | Pianoroll: "Todo: delay & cut" in its export | PatternXP's own delay and cut (scale **96**) are *effect* data inside the SP columns, see next row | Lost |
| **PatternXP SP columns** (internal parameters −10…−112: sub-pattern **triggers** and **play mode**, **offset**, **diatonic transpose**, **modulators**, loop and reverse) | none | none | none | **Lost, in every conversion, and already lost today** whenever MPE reads such a blob (`GetParameter` returns null for the SP indices; the columns are dropped). A pattern that is only a trigger for sub-patterns converts to **an empty clip** | Lost |
| **Input columns** (connection amp and pan) | Lost today on open; **not converted** by design (§11) | – | – | – | – |
| Chords | not data | not data | not data | not data | – |
| Machine-side notes (MIDI machines: Note, Velocity, Delay, Cut, Pitch Wheel, CC) | n/a | Lanes kept; played as today until CU6 | n/a | n/a | – |

**Sound after conversion, if the engine replaces the editor's own playing.** Applies to E4/E5 only (E1 and E2 already play as now, §5.6 and D3):

| Playback detail of PatternXP (`patternxp/PatternXP.cpp`, `MachinePattern.h`) | What the engine would do instead | Status |
|---|---|---|
| **Subtick timing**: events fall on sub-tick boundaries by its own rounding | The engine puts an event at its exact 1/240-tick time inside the block | Differs by under a sub-tick; **unverified** by ear; CU1's probe measures the block |
| **Delay and cut scale 96** vs the engine's note model (MPE: 95 in its own fields) | The store has the real time, so a delayed note starts where the row plus fraction says | Differs by 1/96 vs 1/95 of a tick on a delayed note; **unverified** |
| **Monophonic note ordering**: a new note on a track ends the previous one at its subtick, in PatternXP's order within the tick | Note and Off are separate events; an Off and a Note on the same tick keep the order the store has | Edge case only (two events in one tick on one track); **unverified** |
| Loop, reverse, play-mode of sub-patterns | Not in the store (they are the SP columns) | **Lost**, see above |

**What each choice would do to the user's songs**

| Choice | PatternXP songs (E4/E5) | Other editors (E6) | MPE (E2) and rows (E1) |
|---|---|---|---|
| Convert on open (D2 B) | Note and parameter content exact; SP columns lost; the sound shifts by the playback details above (**unverified**); the original blob is kept only inside the `.bmx` as a derived export (so a saved-back song loses the SP data for good) | Cleared as today (no change) | Converted (the rest of this design) |
| Leave alone (D2 A) | No change of sound; the clip is visible only as a read-only preview outside the editor; **cannot be edited in the Studio** (no editor DLL there) | Cleared as today | – |
| Convert on request (*Convert to Clip*) | Same as B, only when the user presses it, with the loss listed | Same | – |

**Sub-choices for D2** (the user chose B; "after the analysis" means choosing among these, at most three):

- **D2-a (recommended): convert on open, keep the original.** The converter runs in memory as for old songs. PatternXP's SP columns, and the blob, stay in the clip as an opaque, unplayed `Owner=<editor>` record so they are not destroyed on save; the loss report names each clip that had SP columns ("clip X: 3 sub-pattern triggers not converted; they stay in the song file, silent"). Nothing the user wrote is deleted; sound can differ by the details in the table above.
- D2-b: convert on open, keep nothing. The simplest code; SP columns are gone on the next save. It is today's Studio behaviour for these songs, now made explicit.
- D2-c: convert only when the clip has no SP columns (a pattern with only notes and parameters, which is exact in the data); a clip with SP columns stays as it is, read-only preview, no conversion.

**Verified and unverified, in one list.** Verified: the PATT→PXP column mapping (2,415 cells, 0 mismatches); what `gear.xml`, `BMXFile.cs` and `Gear.cs` do with a missing editor and a data-format mismatch; that `PatternEditorUtils.GetParameter` drops the SP indices; that no repo song carries an editor blob. **Unverified** (no PatternXP song, no PatternXP DLL on this machine, no renders): every sound difference, the Pianoroll's blob, and a parameter-index misalignment on a real changed machine.

---

## 8. Readers and writers after

| Who | After |
|---|---|
| Playback | The engine plays `Play=engine` lanes; the editor plays `Play=editor` lanes (until CU6). One player a lane |
| Arrangement automation lanes, EV1, T2 | One reader: the lane's events, whoever wrote them (the "tracker columns" read-only source goes away) |
| Piano roll | `SongNotes.For` for every machine; `Editor*Notes` and the roll's Tracker / MIDI lanes / Song lane switch go away (one store, one source) |
| Tracker | MPE over the store (§6) |
| Recorders | Unchanged (they write S1, S2) |
| MCP | `notes_*`, `automation_*`, `arrangement_automation` see all lanes; `automation_read` lists tracker automation; one new read, `clip_get` (§9) |
| Save, load, export, Classic, upstream | §7 |

---

## 9. The rename

### 9.1 Where "pattern" shows

| Place | Count (grep, 2026-11-23) | Action |
|---|---|---|
| Studio UI strings with "pattern" | 106 in 32 files (`PatternView.cs`, the Sequence tab's list, menus, tooltips, `AutomationProperties.Name`) | "Clip". The Pattern tab becomes the **Clip** tab; "Tracker | Piano roll" stays |
| Browser | `browser_search` kind "patterns", the Patterns section | "Clips" |
| MCP tools named pattern | `pattern_create`, `pattern_delete`, `pattern_duplicate`, `pattern_list`, `pattern_rename` | New `clip_create`, `clip_delete`, `clip_duplicate`, `clip_list`, `clip_rename`; the old names stay as aliases (same handler, "deprecated: use clip_…" in the description) for **two releases**, listed in `agent-coverage.md` as aliases |
| MCP tools taking a `pattern` argument | 25 of 151 tools (`notes_*`, `automation_*`, `arrangement_*`, `session_*`, `audio_*`, `takes`, `select`, `view_show`, `window_layout`…), 101 `"pattern"` argument keys in 11 files | Accept `clip` and `pattern` (either); outputs carry `clip` and, for two releases, `pattern` too |
| `arrangement_clip` and the arrangement's "clip id" | A **placement** today | Keep the tool name; argument `clip` stays the placement id and `at`; the description says "placement". `arrangement_get` adds `placements` beside `clips` for two releases |
| Agent guides | 183 lines in `ReBuzz.Agent/Guides`, 81 markdown files mention "pattern" (guides, machine pages, docs) | Reword; the guide `concepts.md` gains the glossary line |
| Tests keyed on labels | 482 string literals with "pattern" in `ReBuzz.Studio.Tests`, `ReBuzz.SessionTests`, `ReBuzz.AgentTests` | Change with the labels, one wave |
| Specs | 34 docs | `clips.md` is the vocabulary; older specs get a one-line pointer, not an edit of history |
| Code identifiers | `IPattern`, `IPatternColumn`, `PatternCore`, `PatternColumnCore`, `IMachine.Patterns`, `CreatePattern`, `PatternEvent`…: 650 `.cs` files, 33 `.cpp`, 42 `.h`, 27 `.xaml` mention "pattern" (D1 B) | Renamed, behind a compatibility layer, §9.4–9.7 |
| **Stays (by interface or by file, not by choice)** | `.bmx` section names (`PATT`, `XTAP`, `TAP2`) and `.bmxml` element names; machine and DLL names a song stores (`Modern Pattern Editor`, `Jeskola Pattern XP`, `ReBuzzPatternXP`); the Buzz SDK header `MachineInterface.h` and the `CPattern` vtable; the reflection names the managed host looks up on a machine (§9.4); Classic's UI text (frozen); upstream-facing text (loss report categories) | §9.4 |

### 9.2 One new read

`clip_get(machine, clip)`: the clip's length, grid, its notes (the note model of §5.3), each parameter lane with its points and ramps, who plays each lane, and its launch and audio settings. It replaces the three reads an agent makes today (`notes_read`, `automation_read`, `session_get` for the clip) and is the first tool that sees tracker data.

### 9.3 Collisions to settle in the same wave

`session_clip_set`, `audio_clip_*`, `ClipSettings`, `ClipNotes` already mean the content. `arrangement_clip` and `SequenceEvent.Clip` mean the placement. After the rename the UI says "clip" for both and the shared badge says "linked ×N" (the clips that share the content). The code and the MCP descriptions say **placement** for the run on a lane.

### 9.4 D1 B: which renamed names an outside binary or source binds to

D1 is decided **B** (2026-11-23): the code identifiers are renamed too. Four kinds of code that we do not build bind to names. For each: what breaks if the name changes, and the way to keep it working.

| Interface | Bound by | Names that would change | What breaks if renamed | Keep working by |
|---|---|---|---|---|
| **`BuzzGUI.Interfaces.dll`** (managed API, `ReBuzzGUI/BuzzGUI.Interfaces`) | Managed machines compiled against it: the **7 shipped** (`Machines/AudioBlock`, `EnvelopeBlock`, `ModernPianoroll`, and others), every third-party managed machine, and any managed machine **cherry-picked from upstream** | `IPattern` (17 mentions in its file), `IPatternColumn` (11), `IMachine.Patterns`/`CreatePattern`/`GetPattern`, `ISequence`'s pattern members (13), `IBuzz`, `ISession` | A .NET binary references types and members by assembly, namespace and **name**. A renamed `IPattern` makes the machine fail with `TypeLoadException` or `MissingMethodException` the first time it touches it. **Type forwarders do not help**: they redirect a type that *moved to another assembly*, not one that was *renamed* | **Do not rename the public names in that assembly. Add the new names beside them** and make the old ones aliases: `interface IClip`, `IClipColumn`; `interface IPattern : IClip` (kept, marked `[Obsolete(error: false)]`); the old members stay on `IMachine` and forward to the new ones (default interface members, supported by the .NET runtime the engine uses). `PatternCore` becomes `ClipCore : IClip, IPattern`. The assembly keeps its name `BuzzGUI.Interfaces`, so the binaries load unchanged |
| **Reflection names the host looks up** (`ManagedMachineHost.cs` lines 254–268) | A managed machine that *implements* them as plain methods: `PatternEditorControl`, `SetEditorPattern`, `CreatePatternCopy`, `SetPatternEditorData`, `GetPatternEditorData`, `GetPatternEditorMachineMIDIEvents`, `SetPatternEditorMachineMIDIEvents`, plus the fork's `ExportClipNotes`, `ImportClipNotes`, `EditTrackerEvents` | Those method names | The host finds them with `GetMethod("<name>")`; a renamed host lookup silently finds nothing and the editor stops working, with no error | The host probes the **new name first, the old name second**, for the whole list. The machine author's side does not change. Documented as the managed machine ABI in `docs/specs/` and covered by one test per name |
| **Native machines** (`MachineInterface.h`, `NativeMachineFramework/`, `ReBuzzPatternXP/`) | Buzz's machine SDK: `CPattern`, `CMachineInterface`, `CMICallbacks::GetPatternName`, `CreatePattern`, `GetPatternLength`… | The **callbacks and `CPattern`**, if we renamed them | A native machine is bound by **vtable order and struct layout**, not names, so the binary would still run; but anyone recompiling a machine against our header breaks, and the header is Buzz's SDK, kept to stay compatible with every Buzz machine source | **Keep `MachineInterface.h` and the callback names as they are** (they are the SDK). Rename only our own wrapper classes (`PatternManager` in `NativeMachineFramework` and `ReBuzzPatternXP`: 189 + 52 mentions, 33 `.cpp`, 42 `.h` in all), internal to the DLL |
| **Plugin export** (`PluginExport/`, CMake; 1 file mentions it) | The plugins we export (VST/CLAP descriptors, `plugins.json`), and any DAW state they saved | Descriptor and state keys, if any contain "pattern" | A host that saved plugin state under a key would lose it | Not renamed: state keys and descriptor ids are persisted names, like section names. Check in CU5 (**unverified**: the one mention was not read) |
| **MCP and agent surface** | Agents, eval scenarios (`ReBuzz.AgentEval`), saved agent notes | `pattern_*` tools, 25 tools' `pattern` argument, 101 argument keys | Already settled in §9.1: **aliases for two releases**, both argument names accepted | §9.1 |
| **File formats** | Every song ever saved; upstream ReBuzz reading our `.bmx` | Section and element names (`PATT`, `XTAP`, `TAP2`, `<Pattern>` in `.bmxml`), the TAP2 key names, `.nbl` manifest keys | Old files stop opening; upstream stops reading our exports | **Not renamed.** Readers accept old keys forever; a new field gets a new name and `.nbl` formatVersion rules (native-format.md) apply |

**Rule.** *A name that a binary, a saved file or a reflection lookup stores stays; the new name is added next to it.* Everything else is renamed.

### 9.5 The rename, re-estimated

Counts (grep, 2026-11-23, `.cs`, `.cpp`, `.h`, `.xaml` that mention "pattern" in any case): **752 files** (650 `.cs`, 33 `.cpp`, 42 `.h`, 27 `.xaml`), plus 105 `.md`, 35 `.py`, 49 `.json`. By area: `ReBuzz` 131, `ReBuzzGUI` 100, `ModernPatternEditor` 80, `Machines` 77, `ReBuzz.Studio.Tests` 63, `ReBuzz.Studio` 61, `ReBuzzTests` 47, `ExternalMachines` 45, `ReBuzz.Agent` 17, the rest under 10 each. The earlier §9.1 estimate (UI, MCP, guides, tests: about 1–1.5 pw) was for a text change. The code rename is a **mechanical symbol rename** (a Roslyn rename, not a text replace, so strings and file formats are untouched) followed by review.

| Wave | What | Files (about) | Size | Gate |
|---|---|---|---|---|
| **CU5a** compatibility layer first | New names beside old in `BuzzGUI.Interfaces`; `ClipCore : IClip, IPattern`; the host's two-name probe; a test machine compiled against the **old** assembly (a checked-in binary) that must load and play | 15 | 0.5–1 pw | Old binary machine loads; the 7 shipped machines pass; native framework unchanged |
| **CU5b** engine and GUI interfaces | Core types and members inside the engine: `PatternCore`→`ClipCore`, `PatternColumnCore`, `WorkManager`, `BMXFile`, `ReBuzzCore`, `ReBuzzGUI` | about 230 | 1 pw | CI full; renders bit for bit (the CU1 goldens); `.bmx`, `.nbl`, `.bmxml` byte-equal round trips |
| **CU5c** editors and machines | `ModernPatternEditor` (WPF, portable, Avalonia), the 7 shipped managed machines (they may keep using the old names: they are a test of the layer), `ExternalMachines`, native wrapper classes | about 200 | 0.5–1 pw | Same; Classic's tests unchanged |
| **CU5d** Studio, agent, tests, text | UI strings (§9.1), MCP, guides, 482 test literals, specs | about 320 plus text | 1–1.5 pw | Studio tests, agent eval |
| **Total** | | 752 files | **3–4.5 pw** (was 1–1.5) | |

### 9.6 What it does to cherry-picking from upstream (the 2026-11-15 rule)

The policy is: upstream is **cherry-picked, not merged** (`FORK.md`, last merge base `af162b9a`). Measured on the base: 2,384 files; of the 752 files that mention "pattern" today, **279 also exist in upstream's base**. After a rename, a cherry-pick of an upstream commit conflicts wherever its hunk touches a renamed line in those 279 files (new files are not affected).

- **Machine fixes and new machines** (what the policy says to take) compile against `BuzzGUI.Interfaces`, which keeps the old names, so they apply and build **unchanged**. This is the strongest reason for the compatibility layer in §9.4.
- **Loader and core fixes** (`BMXFile.cs`, `ReBuzzCore.cs`, `WorkManager.cs`) hit renamed names, and will conflict wherever they mention a pattern. Cost: the conflict is only names, so resolving takes minutes, but it is every such pick.
- **Mitigation**: keep the identifier map as a file (`tools/clip-rename-map.txt`, old name → new name) and a small script that applies it to an upstream patch before `git am -3`. A pure identifier map is mechanical, so most picks apply with no hand work. Strings and format names are not in the map.
- **The rule needs one sentence added** in `FORK.md`: *"Cherry-picks run through `tools/clip-rename-map` first; machines that compile against `BuzzGUI.Interfaces` need no change."*
- **Decision cost.** The rename is paid once (3–4.5 pw). The cherry-pick cost is small per pick and stays for as long as the fork takes upstream's fixes. A fork that never takes upstream core fixes pays nothing more.

### 9.7 Order and risks of the rename

- **Order:** CU5a (layer) can start any time after CU1; CU5b–c should be a **quiet window**: no other branch open on `ReBuzz/`, `ReBuzzGUI/` and `ModernPatternEditor/`, because a rename touches 750 files and every open branch conflicts. Do it **after** EV1 and MT1 have merged (they add labels) and **before T2**, but **after CU4** if the readers and writers are being rewritten at the same time, so the rewrite is not made twice (§10).
- **Risks:** a renamed reflection lookup that silently finds nothing (§9.4: the two-name probe and a test per name); a renamed string that a saved file or the agent eval depends on (the Roslyn rename leaves strings alone; the map lists the few string keys that do change); Classic is **frozen** but shares the sources: its tests run in every sub-wave.

---

## 10. Phases

Waves of about one branch. Gates in the second table apply to every wave from CU3.

| # | Wave | What | Size | After |
|---|---|---|---|---|
| **CU1** | Goldens and the timing probe | Tests and tools only. (a) The **corpus**: every repo song, plus a **generated** song for each feature: classic multi-track Note + velocity + command columns; TPB 1, 4, 8; 1-row and 256-row patterns; rows per beat 4, 6, subdivided beats; MIDI lanes with delay and cut; a Note column with explicit Off and an Off with no note before it; a parameter column of another machine; 8 tracks; a track-count change; a looped session clip with automation. (b) Goldens: SHA-256 of each render, per platform, in `eval/clip-goldens.json` (hashes only, no audio committed), made by `tools/clip-null.py` (after `tools/null-test.py`). (c) The timing probe of §5.6, written into this spec | 1–1.5 pw | now; parallel to EV1 and MT1 |
| **CU2** | The store learns what trackers need | Engine and `BuzzGUI.Common` only, no view, no behaviour change: `Play`, `Owner`, `Grid`, `Mute` metadata and `EnginePlays` honouring `Play`; explicit note-off events; `ClipMigration` (PATT / `BuiltInPattern`, PXP v1–3, MPE v1 → lanes); `ClipExport` (lanes → MPE blob and PXP, the double-play rule, the stamp). Round trips on the whole corpus: blob → store → blob has the same events | 2–3 pw | CU1 |
| **CU3** | The tracker reads and writes the store | The seam (§6), migration on open in Studio and headless, `ClipView`, `.nbl` v3, derived blob in `.bmx`/`.bmxml`, EV1's and the arrangement reader's one source. Editor plays `Play=editor` lanes. Foreign editors untouched | 3–4 pw | CU2 |
| **CU4** | One reader and writer layer | `SongNotes` for every machine; `Editor*Notes`, `ExportClipNotes` bridges and "Move notes" removed; `automation_read` and `notes_*` see all lanes; `clip_get`; clone, replace, snapshots on columns only | 1.5–2 pw | CU3 |
| **CU5** | The rename (D1 B: UI, MCP **and code identifiers**) | §9, four sub-waves: **a** compatibility layer (new names beside the old in `BuzzGUI.Interfaces`, host probes both names, an old-binary test machine), **b** engine, **c** editors and machines, **d** Studio, agent, tests, text. Mechanical (symbol rename) but 752 files: needs a quiet window. **a** can start after CU1. **b–d** after EV1 and MT1 have merged and after CU4 (the readers and writers are rewritten there, so the rename is not done twice), **before T2 starts its views** | **3–4.5 pw** (was 1–1.5) | a: CU1; b–d: EV1, MT1, CU4 |
| **CU6** | One player (D3 A) | `Play=engine` for migrated lanes, with `Lag` if the probe says so; MIDI lanes become notes (*Convert to notes* by default); the editor's playing path deleted for the Studio build. Own goldens, own gate | 2–3 pw | CU4 |
| **CU7** | Retire | The Studio's legacy list store, `PXPDataFormat` write, the blob's event section; *Convert to Clip* for foreign editors (D2) | 0.5–1 pw | CU6 |

**T2 starts after CU3** (store-backed parameter columns) and runs in parallel with CU4–CU6. With D1 B, T2's new views use the **new names** from the start (`ClipCore`, `IClip`), so CU5a should have merged before T2 writes its first class; CU5b–d then rename what T2 added along with the rest.

### 10.1 Gates (every wave from CU3; CU1 sets them)

| Gate | How |
|---|---|
| **Migrated songs render bit for bit** | Every corpus song and generated song: open (migrated), render, SHA-256 equal to the CU1 golden. Windows package job for the real machines; Linux and macOS against their own goldens (placeholders). A difference fails the wave. The one allowed exception is a documented, probe-explained scheduling shift of a MIDI-lane clip, which needs the user's word (D3) |
| **Open → save → open → save is stable** | `.nbl` A == B byte for byte; `.bmx` and `.bmxml` core sections byte-equal (`tools/song-roundtrip.py --compare`); the derived blob's events equal the original blob's events on the corpus |
| **0 bytes per buffer** | `AudioHygieneTests` and `EngineHardeningTests` extended with a migrated tracker song (notes, automation, ramps, a 4-machine mix) playing, and a take recording over it |
| **Undo and redo** | Each tracker, roll, lane and agent edit is one history step; undo restores events and `ClipView` exactly; a migration is not an undo step; undo across a clone and a delete keeps lanes (`EngineColumns`) |
| **Old behaviour kept** | Classic's tests pass unchanged; the loss report's old rows unchanged; CI full |
| **Cost** | Open time of the largest song (Dye stress, FSM_Infector) within the NF1 measures ×1.3; the templates open faster (no empty columns) |

---

## 11. Risks and loose ends

| Risk | Handling |
|---|---|
| Timing (§2.3 item 2) differs between the players | CU1 probe first; `Lag` or a user decision (D3) |
| The shared MPE sources change under Classic | `#if WINDOWS` keeps the list store; Classic's tests in every wave |
| Pattern-name identity | Unchanged here. A stable clip id (`ClipView`, `ClipSettings`, TAP2 all key by name) is debt; do it with CU7 if renames still lose data |
| A blob edited in Classic after a Studio save | The stamp (§7.2): the blob wins, with a message |
| Notes doubled upstream | One-place rule for MIDI machines (§7.2); tested by the NF2 simulated upstream read |
| Store size: a 12-byte event, a dense agent ramp is many events | Unchanged from today (S2); EV1 and the agent write ramps as single events |
| Input columns (wire amp and pan): lost today on open, 2 of 18 songs | **Not part of CU.** Restoring them changes how those songs sound. A `Wire` lane kind, listed in §5.1, is the place for them; the user decides separately |
| Chords | Stay WPF-only (`Chords/**`); a Studio chord tool is a feature, not a store question |
| TAP2 has no per-column version | `Play` and `Owner` are metadata, which TAP2 already stores, so no change |

---

## 12. Decisions for the user

**D1. How far does "Clips everywhere" go?** **Decided B** (the user, 2026-11-23): "Rename the code identifiers too, not only the UI."
- Plan in §9.4–9.7: a compatibility layer keeps what a binary, a saved file or a reflection lookup stores (the old `IPattern` and friends in `BuzzGUI.Interfaces` stay as aliases, file section names stay, the SDK header stays); everything else is renamed (752 files, 3–4.5 pw in four sub-waves, CU5a–d). Upstream cherry-picks keep working through an identifier map.
- **One thing to confirm, because it follows from B** (not a new question): the old names stay forever as obsolete aliases in `BuzzGUI.Interfaces`. Without that, third-party managed machines and machines cherry-picked from upstream stop loading. If you would rather break them, say so; the plan then drops CU5a and the cost of every machine pick goes up.

**D2. Songs whose clips use a foreign pattern editor (PatternXP and others)?** **Answered B (convert on open), after an exact analysis** (the user, 2026-11-23): "Analyze which data is lost exactly on conversion and ask me again for a more informed decision." The analysis is §7.5. Headline: no repo song uses any pattern editor; for PatternXP's four editors, notes and parameters convert **exactly**; PatternXP's **sub-pattern trigger and effect columns are lost in every conversion (and already today whenever MPE reads such a blob)**; sound differences are small, from code only, **unverified**; other editors are already cleared today. **Asking again, pick one:**
- **D2-a (recommended):** convert on open and keep the original data in the clip, silent, so nothing is deleted (the loss report names the clips that had SP columns).
- D2-b: convert on open, keep nothing (today's Studio behaviour made explicit; SP data gone on the next save).
- D2-c: convert only clips with no SP columns; the others stay as they are, read-only.

**D3. One player or two?**
- **A (recommended):** two players until CU6: the editor keeps playing migrated tracker lanes (bit for bit by construction), then a gated wave moves them into the engine, using the CU1 probe.
- B: stay at two players for good (least risk, MPE's playing stays forever, EV1 and T2 still see one store).
- C: one player from CU3 (fastest to one engine path; the MIDI-lane clips can shift by a block; needs your word that a shift is acceptable).

**D4. Notes for classic Buzz machines (tracks)?**
- **A (recommended):** the Note and velocity columns are the store, one note model reads them (what plays is what is stored; no derivation in the audio path; the roll refuses more voices than tracks, with the reason).
- B: one note list for every machine; the engine derives Note, Off and velocity onto tracks at play (voice allocation in the audio path, new code on it, stray offs and legato rows need markers). Polyphony beyond `MaxTracks` by voice stealing. Choose it only if you want chords bigger than the tracks on a classic machine.

---

## 13. Sources

Read for this document: `ReBuzz/Core/PatternColumnCore.cs`, `PatternCore.cs`, `EngineColumns.cs`, `ArrangementAutomation.cs`, `Notes/*`, `Session/SessionDocument.cs`, `ReBuzz/Audio/WorkManager.cs` (block order, `UpdatePatternColumnEvents`, `UpdatePatternPositions`), `ReBuzz/FileOps/BMXFile.cs` (`LoadPatterns`, `CreatePATTSection`, `CreateTAP2Section`), `BuiltInPattern.cs`, `BuzzExport.cs`; `ModernPatternEditor/ModernPatternEditor.NET` (`MPEStructures`, `PlayRecordManager`, `PatternEditorUtils`, `NoteLaneColumns`, actions), `Portable/`, `Avalonia/`; `ReBuzz/Agent/*Tools.cs`, `MachineNotes.cs`; `docs/specs/daw-modes.md`, `session-view.md`, `native-format.md`, `agent-knowledge.md`, `agent-coverage.md`; the repo's songs (§3).
