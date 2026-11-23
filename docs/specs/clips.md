# Clips: one store for everything a clip holds — Proposal

| | |
|---|---|
| Status | **Proposal** (2026-11-23). Design only: no product code changed. Four decisions wait for the user (§12) |
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
  5. **The rename:** UI, browser, MCP (aliases for two releases), guides, docs, tests. Code identifiers and file formats keep "pattern" (§9, D1).
- **Effort:** 11–16 pw in seven waves (§10). The goldens come first (CU1), because the gate is "migrated songs render bit for bit as before". T2 can start after CU3.

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
| **PatternXP and foreign editors (S5)** | Left as they are: the clip keeps `Owner=<editor>` and the blob. Other views show a read-only preview (`EditorPreviewNotes`) | n/a. *Convert to Clip* on demand through the bridges, reported as lossy (D2) |
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
| **Stays** | `IPattern`, `PatternCore`, `PatternColumnCore`, `.bmx` sections (`PATT`, `XTAP`, `TAP2`), `PatternEditorData`, MPE and PatternXP machine names and DLL names, Classic's UI, upstream-facing text (loss report categories, `Machine > Patterns` in Buzz's own dialogs) | D1 |

### 9.2 One new read

`clip_get(machine, clip)`: the clip's length, grid, its notes (the note model of §5.3), each parameter lane with its points and ramps, who plays each lane, and its launch and audio settings. It replaces the three reads an agent makes today (`notes_read`, `automation_read`, `session_get` for the clip) and is the first tool that sees tracker data.

### 9.3 Collisions to settle in the same wave

`session_clip_set`, `audio_clip_*`, `ClipSettings`, `ClipNotes` already mean the content. `arrangement_clip` and `SequenceEvent.Clip` mean the placement. After the rename the UI says "clip" for both and the shared badge says "linked ×N" (the clips that share the content). The code and the MCP descriptions say **placement** for the run on a lane.

---

## 10. Phases

Waves of about one branch. Gates in the second table apply to every wave from CU3.

| # | Wave | What | Size | After |
|---|---|---|---|---|
| **CU1** | Goldens and the timing probe | Tests and tools only. (a) The **corpus**: every repo song, plus a **generated** song for each feature: classic multi-track Note + velocity + command columns; TPB 1, 4, 8; 1-row and 256-row patterns; rows per beat 4, 6, subdivided beats; MIDI lanes with delay and cut; a Note column with explicit Off and an Off with no note before it; a parameter column of another machine; 8 tracks; a track-count change; a looped session clip with automation. (b) Goldens: SHA-256 of each render, per platform, in `eval/clip-goldens.json` (hashes only, no audio committed), made by `tools/clip-null.py` (after `tools/null-test.py`). (c) The timing probe of §5.6, written into this spec | 1–1.5 pw | now; parallel to EV1 and MT1 |
| **CU2** | The store learns what trackers need | Engine and `BuzzGUI.Common` only, no view, no behaviour change: `Play`, `Owner`, `Grid`, `Mute` metadata and `EnginePlays` honouring `Play`; explicit note-off events; `ClipMigration` (PATT / `BuiltInPattern`, PXP v1–3, MPE v1 → lanes); `ClipExport` (lanes → MPE blob and PXP, the double-play rule, the stamp). Round trips on the whole corpus: blob → store → blob has the same events | 2–3 pw | CU1 |
| **CU3** | The tracker reads and writes the store | The seam (§6), migration on open in Studio and headless, `ClipView`, `.nbl` v3, derived blob in `.bmx`/`.bmxml`, EV1's and the arrangement reader's one source. Editor plays `Play=editor` lanes. Foreign editors untouched | 3–4 pw | CU2 |
| **CU4** | One reader and writer layer | `SongNotes` for every machine; `Editor*Notes`, `ExportClipNotes` bridges and "Move notes" removed; `automation_read` and `notes_*` see all lanes; `clip_get`; clone, replace, snapshots on columns only | 1.5–2 pw | CU3 |
| **CU5** | The rename | §9, one wave, mechanical. May start as soon as EV1 and MT1 have merged (they add labels) and **before T2** | 1–1.5 pw | EV1, MT1 |
| **CU6** | One player (D3 A) | `Play=engine` for migrated lanes, with `Lag` if the probe says so; MIDI lanes become notes (*Convert to notes* by default); the editor's playing path deleted for the Studio build. Own goldens, own gate | 2–3 pw | CU4 |
| **CU7** | Retire | The Studio's legacy list store, `PXPDataFormat` write, the blob's event section; *Convert to Clip* for foreign editors (D2) | 0.5–1 pw | CU6 |

**T2 starts after CU3** (store-backed parameter columns) and runs in parallel with CU4–CU6.

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

**D1. How far does "Clips everywhere" go?**
- **A (recommended):** everything a person or an agent sees: Studio, browser, MCP (new `clip_*` names, old names as aliases for two releases, both argument names accepted), guides, docs, tests. A placed clip is also "a clip" in the UI; the code and tool descriptions say "placement". Code identifiers (`IPattern`, `PatternCore`), file sections, editor machine names and Classic keep "pattern", because they are upstream's and renaming them makes every upstream merge a rewrite.
- B: also the code identifiers and the interfaces (about 2,400 files; upstream merges end).
- C: UI only; MCP keeps `pattern_*`.

**D2. Songs whose clips use a foreign pattern editor (PatternXP and others)?**
- **A (recommended):** they stay as they are: their editor plays them, the other views show a read-only preview, and *Convert to Clip* turns one into lanes on request (through the bridges, with the loss listed). No song changes sound.
- B: convert on open (lossy; PatternXP's own playback is replaced by the engine's).
- C: stop supporting them in the Studio.

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
