# History and snapshots

Status: HS1 in progress (this round). HS2 and HS3 are specified here and not started.

**Goal.** One history for the whole song that every view shares, and full-song snapshots kept beside the song. Neither may disturb a performance: nothing here runs on the audio thread, takes `AudioLock` longer than today, or allocates while audio plays. A snapshot can be restored while the song plays, without a click.

**Decision (2026-11-12).** Option A, all of it: global undo/history, full snapshots, and glitch-free restore while playing. Snapshots live in a history folder next to the song, with an "embed in song" option on save.

## 1. Where things stand

- One song stack: `BuzzGUI.Common.ManagedActionStack`, which is `SongCore.ActionStack`. The studio's edits (`ParameterEdits`, `MachineEdits`, `SequenceEdits`), the Modern Pattern Editor, the WPF editors and the agent layer use it. An agent call is one action group, and a failed call is rolled back.
- Separate stacks: the Session View's `SessionLayout.UndoStack` (reset per song) and AudioBlock's `WaveUndo` (one level).
- Not undoable: MIDI-controller parameter moves, tempo and loop, machine state blobs (Dye Essence samples and wavetables, Neo FM6 banks, Neo Wave tables), pattern renames from the Modern Pattern Editor. WPF Classic sliders are not undoable and stay so: Classic gets bug fixes only.
- Opening a song clears the history. Studio ⌘Z goes to a focused text box, then the view's `IStudioEditTarget`, then the song stack.
- Snapshots exist for agents only (`ReBuzz/Agent/StateTools.cs`: parameters, mute/bypass, amp/pan), in memory, with no UI.
- Edits run on the UI/engine thread. `lock(AudioLock)` covers transport changes only. Session launches are quantised on `SessionCore.LaunchQuantization` and the latency-compensated `HeardClock`.

## 2. Principles

1. **Live safety first.** History code runs on the UI/engine thread. The audio thread never reads or writes it. Capturing a step costs a reference copy (an immutable state object, a before/after pair), never a walk over audio data.
2. **A step is a user gesture.** One drag, one load, one agent call. Steps carry a name, a time and a source (Manual, Agent, MIDI).
3. **Performance is not editing.** MIDI controller moves are recorded while the transport is stopped; while the song plays they are performance and are not recorded. Scene tempo changes and launches are performance.
4. **Blobs are shared, immutable and counted once.** Samples, wavetables and wave data are held by reference. A blob that three steps hold is counted once against the cap.
5. **The oldest steps drop first**, whole groups at a time, never the newest step.

## 3. HS1: one history (this round)

### 3.1 Decision: the Session View joins the song stack

The Session View's edits go on `SongCore.ActionStack`. `SessionLayout.UndoStack` becomes a view of it.

Why not a coordinator over two stacks:
- Order. With two stacks, undo has to decide which stack holds the newest step. That needs a shared clock and a merge rule, and every undo-like feature (the History panel, jump, snapshots) needs the same logic again. One list is the order by construction.
- Mixed steps. A clip edit and a machine delete are one timeline for the user. A jump to "before the delete" has to pass both.
- Cost. The session's actions are already `IAction`s. Session identity (`SessionLayout.Identity.cs`) already follows renames and structure changes from every view across every snapshot, so interleaving does not make its snapshots stale. The one thing that moves is the refresh after an undo: `SessionLayout.Undo` called `ContentChanged` and `ReshapeLaunched`. The song stack now raises an event after every undo, redo and jump, and the layout does the same refresh when the group held one of its actions, whichever view asked.

What changes for the user: ↶ ↷ in the Session View, and Ctrl+Z in its window, undo the newest step of the song, not only the newest grid edit. Launch undo (Ctrl+Alt+Z) is separate and stays: launches are performance.

### 3.2 The stack

`ManagedActionStack` keeps its API (`Do`, `Undo`, `Redo`, groups, `PostDoOrUndoGroup`) and grows:
- Steps (`HistoryStep`: id, name, time, source, bytes), `Position`, `JumpTo(position)` (undo or redo to there), a `Changed` event and an `Applied` event (undo, redo, do) for views.
- `Record(action)`: an edit already made by someone else (a controller move, a state load) goes on the stack without running `Do` again.
- `RecordCoalesced(action, key, gap)`: merges into the newest step when it has the same key, nothing was done since, and the idle gap (500 ms) has not passed. Actions that implement `ICoalescingAction` merge into one action, so a 10-second knob sweep is one small step.
- `Source` and `Label(name)` for the group being built. The agent host sets Agent and "Agent: tool".
- Memory: `MaxBytes`, `UsedBytes`, shared-blob counting (`IHistoryAction.Blobs`), group-aware trimming.
- `Applying`: true while an action runs, so engine setters that record a step themselves (pattern rename) do not record during an undo.

### 3.3 Gaps filled

| Gap | How |
|---|---|
| Tempo and loop | `TransportEdits` for the studio transport bar's tempo and loop button, and the Session View's tempo box. One step per gesture; tempo changes coalesce (500 ms). |
| MIDI controller moves | `MidiControllerAssignments` records a move through `RecordCoalesced`, keyed by parameter and track, only while the transport is stopped. A binding's soft-takeover stays as is. |
| Machine state blobs | `StateEditAction` holds a machine's `MachineState` object before and after, and sets it back through the machine's own property. Dye Essence's user tables and samples, Neo FM6's bank and Neo Wave's user table use it. Their state classes are copy-on-write, so a step holds a reference. Dye Essence's routes and FX rack are parameters and were already steps. |
| AudioBlock waves | `WaveUndo` pushes a step on the shared stack instead of keeping one level. The wave data is a float array captured once (the "after" is captured on the first undo, as before) and counted as a blob. |
| Renames | `MachineCore.RenamePattern` records a step unless the stack is applying. Machine and group renames already were steps. |

Not covered, by choice: mute/solo toggles in the Session View mixer (performance), Session settings (view, quantisation, MIDI map), WPF Classic sliders.

### 3.4 Memory cap

`HistoryMemoryMB` (engine setting, default 256) per song. The estimate is the sum of each action's `HistoryBytes` (default 256 B) plus each distinct blob once. When over, the oldest groups drop first. The newest step is never dropped, so a single huge edit still undoes. A dropped step cannot be jumped to.

### 3.5 History panel and agent tools (HS1b)

- A History tab in the studio inspector: the steps, newest at the top, with name, time and source; the current position marked; click to jump; steps after the position are dimmed (redo side). `StudioTokens` only.
- Agent: `history_list` (steps with id, name, source, time, current) and `history_jump` (to an id). `undo` and `redo` stay as they are. The agent guides and the tool reference say that one agent call is one step and that history is shared with the person's edits.

### 3.6 Tests

`ReBuzz.Studio.Tests/HistoryTests.cs`: each new edit kind; undo and redo across views (Session, Machine, Sequence) in the order done; coalescing; the cap; blob counting; jump; agent tools. HS1b adds the live test: while a big song plays headless, 1,000 edits plus undo and redo, with no audio-thread allocation (`AllocationWindow`) and the worst-block time within the throughput guard's tolerance. Songs load and render bit-identically; the null tests are unchanged.

## 4. HS2: full snapshots

**Capture.** A consistent copy on the UI thread: parameters, patterns, sequences, connections, machine state objects, wave references, the Session layout, tempo. This is a copy of references and small values. Then a background task serialises it into a content-addressed chunk store in `<song>.history/` (chunks named by hash, so a sample or wavetable used by 50 snapshots is stored once). A snapshot is a small manifest of chunk hashes plus a name, time, trigger and thumbnail data.

**When.** On save; every N minutes of editing (setting, default 10) when the song changed; before big agent or song-wide operations (replace machine, import, a song-wide edit tool, restore).

**Embed on save.** An option puts the snapshots in the song file as a new section that other Buzz versions ignore (as MID2 is). A song opened without its folder still has its history; one with both uses the folder.

**UI.** List, name, delete, diff (machines, parameters, patterns, sequence), A/B toggle between two snapshots, restore. Restore is itself one history step, so it can be undone.

**Agent.** `snapshot_*` tools use the full snapshots; `history_list` and `history_jump` cover the step history.

## 5. HS3: glitch-free restore while playing

- **Parameters and patterns** are applied together at the next beat or bar on the Session launch clock (`SessionCore.LaunchQuantization`, the latency-compensated `HeardClock`), with parameter glides so nothing steps.
- **Structure.** New machines are created and prepared off the audio path, then swapped in at the boundary with a 10-20 ms crossfade; the old ones fade and are disposed later.
- **When a machine cannot be prepared safely while playing** (native machines with unknown state, a machine that allocates on create), the restore waits until the transport stops and says so.
- **Guards.** No audio-thread allocation. Worst-block time unchanged on the big test song while a restore happens. Restore then render is bit-identical to the original.

## 6. Decisions and risks

| Decision | Why |
|---|---|
| Song stack, no coordinator | One order; fewer states (§3.1). |
| State objects by reference | Copy-on-write states make a step a pointer; serialising would copy megabytes per step. |
| Chunk store, not a file per snapshot | A sample is stored once; snapshots stay small. |
| Source recorded per step | The panel and agents can tell a person's step from an agent's. |

| Risk | Measure |
|---|---|
| Session snapshots grow with the layout | Counted by size against the cap; the layout is small. |
| An undo that touches a deleted object | Each action finds its object when it runs (by name if the object is gone), as the existing actions do. |
| Controller moves flood the stack | Coalesced; recorded only while stopped. |
| Restore while playing clicks | HS3 guards; defer to stop when not provably safe. |
| Snapshots of machines with state that cannot be captured (native) | Parameters and the machine's saved data (`Data`) are captured; a machine that cannot restore live is deferred. |
| A new undo history changes what ↶ does in the Session View | Said in §3.1 and in the Session View's tooltips. |
| Memory cap drops steps the user wanted | A setting; the panel shows how much is kept. |

## 7. Measures

- History code on the audio thread: none (reviewed, and the HS1b live test).
- Cost of a step: a controller sweep of 1,000 moves is one step of a few hundred bytes; a sample load holds one reference.
- Steps kept within the cap and the dropped count, shown in the panel.
