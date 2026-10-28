# Session View for ReBuzz — Specification

| | |
|---|---|
| Status | Draft v5 — 0.1 (#1, #2), 0.2 (#3) and 0.3 (#4) merged; 0.4 Mixer in review |
| Date | 2026-10-25 |
| Scope | An Ableton Live–style Session View (clip launcher) inside ReBuzz, delivered in usable increments from MVP to feature parity |
| Code baseline | upstream `wasteddesign/ReBuzz` @ `af162b9` (2026-10-24) |

---

## 1. Summary

Ableton's Session View is a grid: columns are tracks, rows are scenes, each cell holds a clip that loops when launched. Launches snap to a global quantization grid, each track plays one clip at a time, and launching a row (scene) launches every clip in it at once.

ReBuzz's data model already maps onto this almost one-to-one:

| Ableton | ReBuzz |
|---|---|
| Track | A machine's **sequence** (`ISequence`) — one row in the sequence editor |
| Clip | A **pattern** (`IPattern`) of that machine |
| One clip playing per track | One `PlayingPattern` per sequence |
| Clip launch | `ISequence.TriggerEvent(time, event, loop)` — "used for live pattern triggering" |
| Scene | A row of (sequence → pattern) slots — **new** |
| Global launch quantization | **New** (the Modern Sequence Editor has a local `ClickPlayPatternSyncToTick`) |
| Arrangement View | The existing Sequence View |

So the work is: a proper session engine in the core (the existing trigger has gaps, §3), a grid UI, and then the long tail of Ableton features, phase by phase.

Every phase below ships a version that is **complete and usable on its own** — nothing in phase N depends on phase N+1 to be worth using.

## 2. Goals and non-goals

**Goals**
- Live, loop-based composition and performance with ReBuzz machines, the way Session View works in Ableton.
- Works with existing songs: opening an old `.bmx/.bmxml` shows its machines and patterns in the grid right away.
- Songs saved with a session still open in upstream ReBuzz (session data is ignored there, the song plays as before).
- Engine changes are small, isolated and upstreamable as PRs.

**Non-goals (all phases)**
- Max for Live, Ableton's devices, Groove Pool (ReBuzz has `ReBuzzGroove`), Ableton project import.
- macOS/Linux. ReBuzz is Windows/WPF.
- Replacing the Sequence View. Session and Arrangement coexist, as in Live.

## 3. What exists today, and the gaps

### 3.1 Building blocks already in the code

- **Live triggering primitive** — `ISequence.TriggerEvent(int time, SequenceEvent e, bool loop)` (`ReBuzzGUI/BuzzGUI.Interfaces/ISequence.cs`), implemented in `ReBuzz/Core/SequenceCore.cs` and advanced each audio buffer in `WorkManager.UpdatePatternPositions` (`ReBuzz/Audio/WorkManager.cs`).
- **Pattern playback follows `PlayPosition`** — the Modern Pattern Editor plays whatever `seq.PlayingPattern` reports (`ModernPatternEditor/.../PlayRecordManager.cs`, `GetPositionInPattern`), so triggered patterns already sound for machines edited with it.
- **Click-to-play patterns** — the Modern Sequence Editor's pattern list launches patterns quantized to `ClickPlayPatternSyncToTick` (`ReBuzzGUI/ModernSequenceEditor/SequenceEditor.xaml.cs`, `UpdatePatternAmin`).
- **Song-embedded UI data** — `IBuzz.SaveSong` / `IBuzz.OpenSong` give components their own subsection in the song's `BGUI` section (`ISaveSong.CreateSubSection`, `IOpenSong.GetSubSection`). The sequence editors already use this. Old ReBuzz versions ignore unknown subsections.
- **Pattern management** — `IMachine.CreatePattern`, `ClonePattern`, `RenamePattern`, `DeletePattern`, `PatternAdded/Removed` events.
- **Mute/solo** — `IMachine.IsMuted`, `IsSoloed`.
- **MIDI in/out** — `IBuzz.MIDIInput` event, `IBuzz.SendMIDIOutput`, MIDI 2.0 via Windows MIDI Services.
- **Headless test driver** — `ReBuzzTests/Automation/Driver.cs` (NUnit) can load songs and render audio without the UI.
- **Prior art** — upstream ships `Jeskola Live.NET.dll` (closed source, in `RunTime/Gear/Generators`), a small pattern-launch grid machine. Worth trying for UX ideas; this spec supersedes it.

### 3.2 Engine gaps (must fix for the MVP)

| # | Gap | Where | Effect |
|---|---|---|---|
| G1 | Triggered pattern position is `songPlayPosition % pattern.Length`, not time since launch | `WorkManager.UpdatePatternPositions` | Clips start mid-pattern unless launch time is a multiple of the pattern length |
| G2 | The only clock is the song position, which wraps at `LoopEnd` (default 16 ticks) and stops playback at the loop end when looping is off | `SongCore.UpdatePlayPosition`, `ReBuzzCore` (`LoopEnd = 16`) | Session timing is bound to the arrangement loop |
| G3 | When a sequence has both an arranged and a triggered pattern, the arranged one wins (`PlayingPattern` checks arranged events first) and both get a play position | `SequenceCore.PlayingPattern`, `UpdatePatternPositions` | Launching a clip on a track with arrangement content is unreliable |
| G4 | Stop (`SequenceEventType.Break`) isn't handled by the trigger code, so stopping is immediate, never quantized | `UpdatePatternPositions` | No quantized stop |
| G5 | MIDI pattern columns are played only for arranged events | `WorkManager.UpdatePatternColumnEvents` | MIDI columns of launched clips are silent |
| G6 | A trigger starts only when `songPlayPosition == te.time` exactly | `UpdatePatternPositions` | A position jump (loop wrap, user seek) can skip the start tick and the clip never starts |
| G7 | No atomic multi-track launch | — | Scenes need all clips to start on the same tick |

Found while implementing (also fixed in 0.1):

| # | Gap | Where | Effect |
|---|---|---|---|
| G8 | PatternXP advances a pattern by itself once started and the host never tells it a pattern stopped | `NativeMachineFramework/MachineWrapper.cpp` | A clip stopped mid-pattern (or an arranged `Break`) keeps playing to the pattern's end |
| G9 | MIDI column events are played once per *arranged event*, not once per playing pattern | `WorkManager.UpdatePatternColumnEvents` | A pattern arranged N times in a sequence sends its MIDI events N times |
| G10 | MIDI column notes are only released once the play position passes their end | `WorkManager.UpdatePatternColumnEvents` | Notes hang when a pattern stops, is switched, loops, or restarts before the note ends |

### 3.3 PatternXP (resolved)

The concern was that machines edited with the native **PatternXP** editor might not follow `PlayPosition`. Reading the code settles it: `NativeMachineFramework/MachineWrapper.cpp` re-sends `PlayPattern(pattern, sequence, position)` to the editor every tick for each sequence whose `PlayingPattern` has a position, and `PatternCore.Sequences` lists every sequence of the pattern's machine (not only those that arrange it). So launched clips play in PatternXP as long as `SequenceCore.PlayingPattern` reports them, which 0.1 does. The one gap, stopping, is G8.

The default editor is the Modern Pattern Editor (`ReBuzzCore.DefaultPatternEditor`), which reads `PlayPosition` directly.

## 4. Architecture

```
┌────────────────────────── UI (WPF) ───────────────────────────┐
│ ReBuzz/AppViews/Session: SessionWindow ─ SessionView (grid)    │
│ ReBuzz/Core/Session: SessionLayout ─ SessionDocument (model)   │
│   reads/writes the layout, calls ISession                      │
└───────────────┬───────────────────────────────▲────────────────┘
                │ Launch/Stop/LaunchScene        │ state events (UI thread)
┌───────────────▼───────────────────────────────┴────────────────┐
│ Core (ReBuzz/Core/Session/SessionCore.cs)   implements ISession │
│   session clock · launch queue · per-track clip state ·         │
│   arrangement override                                          │
│   called from WorkManager at tick boundaries (audio thread)     │
└───────────────┬────────────────────────────────────────────────┘
                │ sets PatternCore.PlayPosition, SequenceCore state
        existing engine: WorkManager, pattern editors, machines
```

### 4.1 Session engine (core)

A new `SessionCore` owned by `ReBuzzCore`, exposed through a new interface so existing interfaces stay binary compatible:

```csharp
// ReBuzzGUI/BuzzGUI.Interfaces/ISession.cs (new)
public enum LaunchQuantization { None, Beat, Bar, TwoBars, FourBars, EightBars }
public enum ClipState { Stopped, Queued, Playing, Stopping }   // "empty slot" is a UI notion

public interface ISession : INotifyPropertyChanged
{
    long Clock { get; }                          // -1 while stopped; never wraps
    int BeatsPerBar { get; set; }                // Buzz has no time signature; default 4
    LaunchQuantization LaunchQuantization { get; set; }   // default Bar
    int GetQuantizationTicks(LaunchQuantization q);

    void Launch(ISequence track, IPattern clip, LaunchOptions? options = null);
    void LaunchScene(IEnumerable<(ISequence Track, IPattern Clip)> slots, LaunchOptions? options = null);
    void Stop(ISequence track, bool immediate = false);
    void StopAll(bool immediate = false);

    IPattern? GetPlaying(ISequence track);
    IPattern? GetQueued(ISequence track);
    ClipState GetState(ISequence track, IPattern clip);
    long GetClipStartClock(ISequence track);     // for progress display
    int GetClipPosition(ISequence track);

    bool IsArrangementOverridden(ISequence track);
    bool IsAnyArrangementOverridden { get; }
    void BackToArrangement();

    event Action<ISequence>? TrackStateChanged;  // raised on the UI thread
}

public interface ISessionHost { ISession Session { get; } }  // implemented by ReBuzzCore
```

`LaunchOptions` carries an optional per-launch quantization and `Loop` (default true; false = one-shot). The scheduling logic is a separate, dependency-free class (`ReBuzz/Core/Session/SessionScheduler.cs`) with its own fast test project (`ReBuzz.SessionTests`); `SessionCore` adapts it to ReBuzz.

Rules:

- **Clock.** `Clock` advances at each tick boundary alongside the song position, but never wraps and never stops at `LoopEnd`. It starts at the **song position** where the transport started, so the launch grid lines up with the arrangement's bars (until the arrangement loops on a non-bar length). Fixes G2.
- **Launch queue.** `Launch` computes `launchClock = next multiple of quantize strictly after Clock` (or `Clock + 1` if quantize is 0) and stores one pending launch per track; a later launch on the same track replaces it. Processed at tick boundaries (`PosInTick == 0`) in `WorkManager` before `UpdatePatternPositions`, starting when `Clock >= launchClock`. Fixes G6.
- **Clip position.** `(Clock − clipStartClock) mod pattern.Length` for looping clips; one-shot clips end at `pattern.Length`. Fixes G1.
- **Stop.** Quantized like launches unless `immediate`. When it takes effect the track's note parameters are set to Off (without recording into patterns) and MIDI "all notes off" is sent; PatternXP is told to drop the pattern (G8). Switching to another clip sends no note-offs, so Buzz-style legato between patterns still works; MIDI column notes of the old clip are released (G10). Fixes G4.
- **Scenes.** `LaunchScene` enqueues all slots under `ReBuzzCore.AudioLock` with one shared `launchClock`. Tracks absent from the scene keep playing, as in Live (Live's "stop clips not in scene" comes later as an option). Fixes G7.
- **Arrangement override.** Launching a clip marks its track as overridden; the arrangement's events for that track are ignored until `BackToArrangement()` (Phase 0.3 adds the button; in 0.1, stopping the transport clears overrides). `SequenceCore.PlayingPattern` asks the session first. Fixes G3.
- **MIDI columns.** `UpdatePatternColumnEvents` plays each pattern that has a play position exactly once, session clips included. Fixes G5 and G9.
- **Transport.** Launching while stopped starts the transport and launches on the first tick. Stopping the transport stops all clips. The song-end stop (`!Looping` at `LoopEnd`) is skipped while any clip plays. The arrangement then wraps to `LoopStart` and keeps playing on tracks the session hasn't taken over (Live would continue silently past the end). While recording into the arrangement (0.3) the song grows instead.
- **Compatibility.** `ISequence.TriggerEvent` keeps working and is re-implemented on top of `SessionCore`, so the Modern Sequence Editor's click-to-play and the session grid share one state.
- **Threading.** The scheduler has its own lock, not `ReBuzzCore.AudioLock`: the audio thread holds `AudioLock` while it synchronously invokes the UI thread at transport start, so a UI request waiting for `AudioLock` could deadlock. Lock order is `AudioLock` → session lock on the audio thread; UI requests take only the session lock; machines and the dispatcher are never called while it is held. Per-track reads are lock-free. State-change events are marshalled to the UI dispatcher. No allocation on the audio path in steady state (only on ticks where something changes).

### 4.2 Data model and persistence

`SessionDocument` is UI-owned layout data, serialized as XML into a `BGUI` subsection named `SessionView` via `IBuzz.SaveSong`/`OpenSong`:

```xml
<Session version="1" beatsPerBar="4" launchQuantization="Bar">
  <Scenes>
    <Scene name="Intro" color="#3A6EA5" />
    <Scene name="Drop" />
    <Scene />
  </Scenes>
  <Tracks>
    <Track sequence="0" machine="Juno 1">
      <Slot scene="0" pattern="00" />
      <Slot scene="1" pattern="01" />
    </Track>
  </Tracks>
</Session>
```

Scenes are listed in row order. `ReBuzz/Core/Session/SessionDocument.cs` is the pure model (no WPF, unit-tested in `ReBuzz.SessionTests`); `SessionLayout.cs` keeps it in step with the live song and does the subsection I/O. A song that never used the session is saved without the subsection.

- **Tracks are sequences**, not machines (a machine can have several sequences). `sequence` is the index in `ISong.Sequences`, `machine` is a check value used to re-match after edits. Follows `SequenceAdded/Removed/Changed`.
- **Clips reference patterns by name** within the track's machine. Renames are followed via `IPattern.PropertyChanged("Name")`; a deleted pattern leaves the slot empty.
- **Clips are shared, not copies.** Editing a clip edits the pattern, also where it's used in the arrangement or other slots. This differs from Live (where clips are independent) and is called out in the UI; "Duplicate as new pattern" (0.2) gives Live's behaviour when wanted.
- **First open of a song without session data** auto-fills the grid: one track per sequence, each machine's patterns down the rows in order.
- Clip and scene properties added in later phases are optional attributes, so files stay forward and backward compatible within the fork.

### 4.3 Where the UI lives

| Option | Pros | Cons |
|---|---|---|
| **A. Own window opened from the main window** (MVP) | Small `MainWindow` change; fast to iterate; easy to put on a second monitor | Not docked like the other views |
| B. Main-window view next to Machine/Pattern/Sequence (from 0.3) | Feels native; Live-like Session/Arrangement toggle | Touches `MainWindow`, `BuzzView`, toolbar, themes |
| C. A managed machine with a GUI (like `QuickNote`) | Zero core UI changes | Several instances per song possible; awkward persistence; engine changes are needed anyway |

Decision: **A for 0.1–0.2, B from 0.3.** All function keys are taken (F1–F12); the window toggles with **Ctrl+Tab** (from the main window and from the Session window), the toolbar's Session button (Default theme) and *View → Session View*. The UI lives in the ReBuzz project rather than a new ReBuzzGUI project, because it needs `SessionLayout` and `ReBuzzCore`; it can move once the layout has a public interface (0.3, docked view).

## 5. Phase plan

| Release | Name | You can… | Size |
|---|---|---|---|
| **0.1** | Launch grid (MVP) | Build a song in a grid of pattern clips and perform it with mouse: launch clips and scenes, quantized, saved in the song | M |
| 0.2 | Playable | Play it from the computer keyboard and any MIDI controller; edit, create, duplicate and colour clips without leaving the view | M |
| 0.3 | Session ↔ Arrangement | Record a performance into the Sequence View; go back to the arrangement; docked view | M |
| 0.4 | Mixer | Mix in the session: volume, pan, sends, meters, mute/solo/cue per track; group tracks | M |
| 0.5 | Clip & scene properties | Launch modes, per-clip quantization, legato, loop region and start offset, scene tempo | M |
| 0.6 | Performance | Follow actions, crossfader, select-on-launch, launch undo, tempo nudge/tap | M |
| 0.7 | Session recording | Record and overdub MIDI into empty slots, fixed-length recording, capture MIDI, metronome and count-in | L |
| 0.8 | Control surfaces & sync | Launchpad/APC/Push-style grid control with LED feedback and session ring; Ableton Link; MIDI clock | L |
| **1.0** | Audio clips (parity) | Audio clips in slots with warping, audio recording into slots | XL |

Sizes: S ≈ <500 LOC, M ≈ 500–2,000, L ≈ 2,000–5,000, XL > 5,000.

---

### 0.1 — Launch grid (MVP)

**Outcome.** Open any song, see its machines as tracks and their patterns as clips, perform it live with the mouse. A complete, small product.

**Features**
1. Session window: tracks as columns (header: machine name), scenes as rows, scene launch column on the right, track stop row at the bottom, *Stop All Clips*.
2. Clip slot shows pattern name, a playing indicator with loop progress, and blinks while queued. Empty slots show a stop button (stops that track), as in Live.
3. Click a clip: launch (quantized). Click the playing clip: relaunch from the start. Click a scene: launch the row.
4. Global launch quantization: None, 1 beat, 1 bar, 2, 4, 8 bars. Beats-per-bar setting (default 4).
5. Transport readout in bars.beats.ticks of the session clock.
6. Assign a pattern to a slot (dropdown of the track's machine patterns, or drag from the Sequence View pattern list). Clear a slot (Delete). Add/remove scene rows. Rename scenes.
7. *Add track* for a machine without a sequence (calls `ISong.AddSequence`).
8. Auto-fill on first open (§4.2). Saved in the song (§4.2).

**Engine.** §4.1 in full: clock, launch queue, position, stop, scenes, override, MIDI columns, transport rules, `TriggerEvent` on top of `SessionCore`. Plus the PatternXP spike (§3.3).

**Acceptance criteria**
- With 1-bar quantize at any tempo, a clip launched mid-bar starts on the next bar line at its first row, verified by an automated test rendering audio with `ReBuzzTests` (sample-accurate start within the tick boundary).
- A 12-tick pattern launched on a 16-tick grid loops every 12 ticks with no jump (G1 regression test).
- A new song (loop 0–16, looping off) can play a session clip for 5 minutes without stopping (G2).
- Launching a scene starts all its clips on the same tick (test asserts identical start clocks).
- Track stop is quantized, sends note-offs; no hanging notes in a 100-launch soak test.
- Save, close, reopen: grid identical. The same file opens in upstream ReBuzz and plays its arrangement.
- An existing song with 10+ machines opens with the grid auto-filled.
- No audio dropouts introduced: CPU per buffer with 16 playing clips within 5% of the same patterns playing from the arrangement.

**Out of scope.** Keyboard/MIDI control, mixer, recording, clip properties, follow actions.

**Status (merged).** Features 1–8 are in, with these 0.1 limits: slots are assigned from the right-click menu (dragging from the Sequence View pattern list is still open); clicking a playing clip relaunches it at the next launch point; the Session window is a separate window (§4.3). Tests: `ReBuzz.SessionTests` (scheduler and layout model), `ReBuzzTests/SessionEngineTests.cs` (engine through the audio loop), `ReBuzzTests/SessionViewTests.cs` (layout against a live song, and PNG renders of the grid that CI publishes to the `ci-artifacts` branch). Not yet covered by automated tests: the 5-minute and soak criteria, the CPU comparison, and opening the file in upstream ReBuzz.

---

### 0.2 — Playable

**Outcome.** The grid becomes an instrument: no mouse needed, and clip editing flows from the session.

**Features**
1. Keyboard navigation (arrows move a selection; Enter launches; Space starts/stops transport; number keys launch scenes 1–9; Backspace stops the selected track).
2. **MIDI mapping** — MIDI-learn any slot, scene, track stop, stop-all and quantize control. Mappings saved per user (not in songs), with optional per-song override.
3. Generic **note-grid mapping**: map a range of notes to an N×M block of slots, for pad controllers without a dedicated script.
4. Double-click a clip: opens it in the pattern editor (Pattern View, focused on that pattern).
5. Create clip in an empty slot (`IMachine.CreatePattern`, default length = 1 bar, setting), duplicate clip into the next slot (`ClonePattern`, Live-style independent copy), rename, delete pattern.
6. Clip and track colours; colours shown in the Sequence View pattern list too.
7. Track header mute/solo (→ `IMachine.IsMuted/IsSoloed`).
8. Undo/redo for grid edits (`BuzzGUI.Common` action stack).
9. Insert/delete/duplicate scene; drag clips between slots of the same track.

**Engine.** None beyond 0.1 (MIDI is handled via `IBuzz.MIDIInput`).

**Acceptance criteria.** A full set can be performed from a 4×4 pad controller via note-grid mapping and from the keyboard alone; MIDI-to-launch latency ≤ 1 audio buffer + quantize wait; all edits undoable.

**Status (merged, #3).** Features 1–9 are in:
- *Keyboard*: arrows, Enter, Space, 1–9, Backspace as specified; also Delete (remove from slot), Shift+Delete (delete pattern), Ctrl+Shift+M (create clip), Ctrl+D (duplicate), F2 (rename), Ctrl+I (insert scene), Ctrl+Z/Ctrl+Y, Esc (cancel MIDI learn). The list is under ⚙ → *Keyboard shortcuts*.
- *Mouse*: like Live, ▶ launches on mouse down and the rest of a clip selects, so clips can be dragged (move within the track, Ctrl copies) and double-clicked (edit; in an empty slot, create). An empty slot's small ■ stops its track. This replaces 0.1's click-anywhere-to-launch.
- *MIDI*: right-click a slot, scene, track stop, *Stop all clips* or the quantization box → *MIDI learn…*; ⚙ adds play/stop and note-grid navigation (up/down/left/right, so a 4×4 pad reaches the whole set, like Live's session ring, drawn as an outline). Note on and CC are supported; a quantization CC picks a value across its range, a note cycles. Mapped notes and CCs control the session only (they don't reach machines). The user's mapping lives in the registry (`Session\MidiMap`); ⚙ → *Store MIDI mapping in this song* gives a song its own copy (the `<Midi>` element of the SessionView subsection). Mappings are positional (grid coordinates), so they don't follow inserted or removed scenes.
- *Colours*: a clip's colour is its pattern colour, the same per-pattern choice the Sequence View shows (`SequenceEditor.ViewSettings.PatternAssociations`, saved by the sequence editor), drawn from the theme's PatternColors. Track and scene colours are stored in the layout (`colorIndex`).
- *Undo*: the session has its own undo history (↶ ↷ in the top bar, Ctrl+Z/Ctrl+Y in the window), reset with each song. Layout edits are recorded as snapshots; clip edits use BuzzGUI's pattern actions, so deleting a pattern can be undone (its data comes back; arrangement entries don't, which the confirmation says). Mute/solo, MIDI mapping and settings aren't undo steps.
- *New clip length*: ⚙ → *New clip length* (1, 2, 4 or 8 bars of the session's beats per bar), saved in the song (`clipBars`).

Tests: `ReBuzz.SessionTests/SessionMidiMapTests.cs` (mapping model), new cases in `SessionDocumentTests.cs`, and `ReBuzzTests/SessionPlayableTests.cs` (clip/grid edits with undo, MIDI learn and note grid through `SendMIDIInput`, song mapping, keyboard, a colour render). Not done: dragging patterns in from the Sequence View's pattern list (0.1 feature 6's alternative to the menu). Not covered by tests: drag and drop and the dialogs (manual), MIDI-to-launch latency (by construction: MIDI input reaches the session on the UI thread and launches at the next quantization point).

---

### 0.3 — Session ↔ Arrangement

**Outcome.** Jam in the session, then turn the performance into an arrangement, as in Live.

**Features**
1. **Arrangement record**: with record armed, every launch/stop is written into the sequences at the song position where it happened (`ISequence.SetEvent` with `PlayPattern`/`Break`).
2. **Back to Arrangement** button, lit while any track is overridden (§4.1).
3. **Scene to arrangement**: insert a scene's clips into the sequences at the cursor, for N bars.
4. **Docked view** (§4.3 option B): Session becomes a main-window view, with a Session/Sequence toggle. The standalone window remains available.
5. Sequence View indicators: overridden tracks greyed, as in Live.

**Engine.** Arrangement record hooks in `SessionCore` (write events on the UI thread from a lock-free queue filled at tick time). `BuzzView` gets a `SessionView` value (append-only, binary compatible).

**Acceptance criteria.** Record a 2-minute performance and play it back from the Sequence View with identical audio (render diff below noise floor). Back to Arrangement restores arranged playback on the next tick.

**Status (merged, #4).** Features 1–5 are in:
- *Arrangement record*: ReBuzz's Record (F7, or ● Rec in the Session View, lit red) arms it. While recording, every clip start (launch or loop restart) is written as a `PlayPattern` event at the song position where it happened, and every stop as an `Off` event (Buzz's "stop sound" event); arrangement events between those writes on the recorded tracks are removed, so the arrangement plays back what was heard. Clips already playing when recording starts are written from the start of their current loop. Tracks handed back with Back to Arrangement stop being recorded there. With looping off the song grows a bar at a time while recording (Live-style); with looping on the recording stays in the loop. A pass ends when recording or the transport stops and is one undo step in the Session View. `SessionCore` reports from the audio thread into a lock-free queue that `SessionRecorder` drains on the UI thread.
- *Back to Arrangement* is lit (orange) while any track is taken over.
- *Scene to arrangement*: scene right-click → *Insert into arrangement…*: at a bar (default: the bar of the play position), for N bars (default: the scene's longest clip, in whole bars), overwriting the scene's tracks or inserting (moving every track's later events back). Clips repeat to fill the length; a last repeat that runs past it is cut with a `Break`. Song and whole-song loop ends grow to fit. One undo step in the Session View.
- *Docked view*: `BuzzView.SessionView` (appended); the Session View is a main-window view next to the others (toolbar radio button in the Default theme, *View → Session View*); Ctrl+Tab switches between it and the Sequence View, like Live's Tab. *View → Session View in a Window* keeps the separate window. While docked, the view gets its keys before the main window's shortcuts (F-keys, Ctrl+Tab and Ctrl+S/N/O still reach the main window).
- *Sequence View indicators*: tracks the session has taken over are greyed in all three sequence editors (`BuzzGUI.Common.SessionOverrideIndicator`).
- Also: arrangement Mute/Thru/Off events no longer fire on tracks the session has taken over (they only skipped `PlayPattern` before).

Differences from Live: a recorded stop plays back as Buzz's `Off` (the machine's Stop), where the live stop sent note-offs, so release tails can be cut shorter on playback; and a recording can't start a pattern mid-way, so a clip is written from the start of its loop. Tests: `ReBuzzTests/SessionArrangementTests.cs` records a performance (launch, switch, stop, relaunch, past the song end) with the `Tone` test machine, whose output encodes which pattern plays and at what position, and plays it back from the arrangement: the audio matches sample for sample (84 ticks, about 10 seconds at the default tempo, rather than 2 minutes). Arrangement edits from the session go on the Session View's undo history, not the Sequence View's (each sequence editor has its own).

---

### 0.4 — Mixer

**Outcome.** Mix without leaving the session.

**Features**
1. Mixer strip under each track: volume, pan, peak meter, mute/solo, cue (pre-listen via `Polac Out`/`ReBuzzAudioOut` or a second output if available).
2. Sends to return tracks, where return tracks map to effect machines fed by the track's machine.
3. Group tracks from `IMachineGroup` (collapsible, group fold shows group clip launching).
4. Master strip.

**Engine.** Track volume/pan map onto the machine's output connection (`IMachineConnection` amp/pan) rather than new DSP, so the Machine View and the session agree. Sends create/adjust connections to return machines. Investigate reusing `ConnectionMixer`'s logic.

**Acceptance criteria.** Changing volume/pan in the session and in the Machine View is the same parameter (each reflects the other live); meters update at ≥ 30 fps without audio impact.

**Status (in review).** Features 1–4 are in. The mixer is pinned under the grid and scrolls sideways with it; **Mixer** in the top bar (or Ctrl+Alt+M) shows or hides it, and the choice is saved with the song.
- *Strips* (`SessionMixer`, no DSP of its own): a track's volume and pan are the amp and pan of the machine's *main outputs*, its output connections other than sends and cue (usually the one into the master or a bus). With several main outputs, volume moves them all by the same number of dB and pan sets them all. The fader uses the Machine View's amp knob scale (and ConnectionMixer's): linear in dB from -66 dB to +12 dB. The strips poll the graph, so a change in the Machine View shows within 50 ms, and a change in the session is the connection's amp, which the Machine View reads. Fader, pan and sends: drag (Shift for fine), wheel, double-click to reset. Each strip also has M (mute), S (solo) and C (cue) for its machine, as the track header has M and S.
- *Meters*: post-fader stereo peak of the main outputs, -60 … +6 dB, green/yellow (-12 dB)/red (0 dB), falling at ~60 dB/s. `MachineConnectionCore` measures the peak of each block on the audio thread only while a mixer is on screen (one pass over the block, no allocation, no locks); the view reads it on the render loop, throttled to one update per 25 ms (≥ 30 fps).
- *Returns*: ⚙ → *Return tracks* turns effect machines into return columns (A, B, …) after the tracks. A send is the connection from the track's machine to the return: raising it from off connects them (one undo step in the Machine View's history); a send that would make a feedback loop is greyed. Adding or removing a return is a Session View undo step; removing one leaves its connections alone.
- *Cue*: ⚙ → *Cue output* picks the machine the C buttons connect tracks to, at unity: an effect that plays to a second output (ReBuzzAudioOut or Polac Out set to other channels) and isn't connected to the master.
- *Groups*: each Machine View group (`IMachineGroup`) with a member that has a track gets a column before its first member: click the header to fold the members away (saved with the song), a group slot launches the members' clips in that scene together and shows them playing, the group stop stops them, and the group strip is the group's main output machine's volume, pan and meter.
- *Master strip* under the scene column: the master volume (the toolbar's) and ReBuzz's master VU meter.
- Also fixed: a connection's amp ramp (4 blocks after a change) moved the wrong way within each block and jumped back at the next, a click per block while an amp changed. Fader moves now ramp smoothly.

Tests: `ReBuzzTests/SessionMixerTests.cs`: the session's fader and pan are the connection's amp and pan both ways (also through the strip controls), balance across several outputs, sends and cue as connections (with undo), the post-fader peak (and nothing measured while no meter is shown), the amp ramp, group columns (launching, folding, keyboard skipping folded tracks), settings vs undo, and a render. Not automated: the frame rate on a real display, and listening for clicks.

---

### 0.5 — Clip & scene properties

**Outcome.** Live's clip view controls that matter for launching.

**Features**
1. Per-clip **launch mode**: Trigger, Gate (plays while held), Toggle, Repeat (retriggers at quantize rate while held).
2. Per-clip **launch quantization** override (Global, None, 1/16 … 8 bars).
3. **Legato**: new clip starts at the position the previous clip on the track had reached.
4. **Loop region and start offset** inside the pattern (play from row X, loop rows Y–Z); loop on/off (one-shot).
5. **Velocity** sensitivity for MIDI-launched clips (scales a chosen parameter, default volume).
6. **Scene tempo and beats-per-bar**: launching the scene changes tempo (`IBuzz.BPM`).
7. Clip view panel in the session window showing these properties.

**Engine.** `LaunchOptions` gains mode, quantize, legato, loopStart/loopEnd/startOffset; position formula generalised. Tempo change scheduled on the launch tick.

**Acceptance criteria.** Each mode behaves like Live's in a side-by-side test checklist; legato switching between two 1-bar clips is seamless (no restart click).

---

### 0.6 — Performance

**Features**
1. **Follow actions** per clip: Stop, Play Again, Previous, Next, First, Last, Any, Other, Jump to row; two actions with chance A/B and a follow time (bars.beats.ticks); "linked" (after N loops).
2. Scene follow actions (next scene after N bars).
3. **Crossfader** with per-track A/B assignment (scales track volume from 0.4).
4. Select-on-launch toggle; stop-clips-not-in-scene option; launch history with undo of the last launch.
5. Tempo nudge up/down and tap tempo.

**Engine.** Follow actions evaluated in `SessionCore` at tick boundaries with a seeded RNG (reproducible in tests).

**Acceptance criteria.** A 4-clip follow-action chain runs unattended for 10 minutes with the expected distribution of transitions (statistical test at 95%).

---

### 0.7 — Session recording

**Features**
1. Track **record arm**; record button on empty slots of armed tracks.
2. **Record MIDI into a new clip** from the machine's MIDI input (creates a pattern, writes notes via the pattern editor's existing recording path, `PlayRecordManager.RecordPlayingMidiNote`).
3. **Overdub** into the playing clip; **fixed-length** recording (1, 2, 4, 8 bars) that ends and loops automatically.
4. **Capture MIDI**: keep the last N bars of incoming MIDI per armed track and turn it into a clip on demand.
5. Parameter automation recording into the clip (knob moves → pattern parameter column).
6. Metronome and count-in.

**Engine.** Record windows aligned to the session clock; MIDI timestamps converted to pattern rows/sub-ticks via `SubTickInfo`.

**Acceptance criteria.** Notes recorded at 120 BPM land within ±1 sub-tick of their played time; fixed-length recording loops seamlessly on the first pass.

---

### 0.8 — Control surfaces & sync

**Features**
1. Controller scripts with LED feedback and a movable **session ring** (the "red box"): Novation Launchpad (Mini/X/Pro), Akai APC40/APC mini, and Ableton Push 2 in its generic MIDI mode.
2. Script API so new controllers can be added as small C# classes.
3. **Ableton Link**: tempo and phase sync with Live and other Link apps on the network.
4. MIDI clock out/in.

**Notes.** Link's SDK is GPLv2 (or commercial). Linking it into ReBuzz (MIT) means distributing the result under GPL terms, or running Link in a separate helper process. Decide before starting this phase.

**Acceptance criteria.** Launchpad shows clip states with correct colours within one tick; two ReBuzz instances and Live stay beat-aligned via Link for 30 minutes (phase error < 1 ms).

---

### 1.0 — Audio clips (feature parity)

**Features**
1. Audio clips: drag a WAV/AIFF/FLAC onto a slot. Playback through a per-track audio player machine (build on `AudioBlock`, or a new lightweight `ClipPlayer` machine).
2. **Warping**: Repitch first (cheap), then time-stretch modes (Beats, Complex) via a library — SoundTouch (LGPL) is licence-compatible; Rubber Band is GPL/commercial.
3. Warp markers, auto BPM detection, clip gain/pitch/transpose.
4. Audio recording into slots from an input (`ReBuzzAudioIn`).
5. Consolidate a recorded performance to audio.

**Acceptance criteria.** A loop at 100 BPM plays in time at 80–160 BPM in Beats mode; the Live parity matrix (§6) is all ✅ except the listed non-goals.

## 6. Parity matrix

| Ableton Session View feature | Phase |
|---|---|
| Clip grid, scenes, clip/scene launch | 0.1 |
| Global launch quantization | 0.1 |
| Track stop buttons, Stop All Clips | 0.1 |
| One clip per track, scene launches atomically | 0.1 |
| Arrangement override | 0.1 (auto-clear on stop), 0.3 (Back to Arrangement) |
| Keyboard/MIDI mapping of launch controls | 0.2 |
| Create/duplicate/colour/rename clips, edit in clip view | 0.2 (editing via pattern editor) |
| Insert/duplicate scenes, drag clips | 0.2 |
| Record session into arrangement | 0.3 |
| Session/Arrangement toggle | 0.3 |
| Mixer section, sends/returns, group tracks | 0.4 |
| Clip launch modes, per-clip quantize, legato | 0.5 |
| Clip loop/start markers | 0.5 |
| Scene tempo / time signature | 0.5 |
| Follow actions (clip and scene) | 0.6 |
| Crossfader | 0.6 |
| Session record, overdub, fixed length, Capture MIDI | 0.7 |
| Metronome, count-in | 0.7 |
| Controller grid with session ring | 0.8 |
| Ableton Link | 0.8 |
| Audio clips, warping, audio recording | 1.0 |
| Max for Live, Live devices, Groove Pool | Non-goal |

## 7. Testing strategy

- **Engine unit tests** (NUnit, `ReBuzzTests`): drive `SessionCore` with a fake clock; assert launch ticks, positions, stop behaviour, scene atomicity, follow-action transitions.
- **Render tests** with `ReBuzzTests/Automation/Driver`: load fixture songs, launch clips programmatically, render N buffers, compare sample output against expected (the suite already does sample-value assertions for machines).
- **Persistence tests**: save/load round-trip of `SessionDocument`; upstream-compatibility check (song without the `SessionView` subsection is byte-identical in its other sections).
- **Manual checklist per release** mirroring the acceptance criteria, run on the zip produced by `build.ps1`.
- **Soak test**: randomised launches for 30 minutes, checking for hanging notes, exceptions and audio dropouts.

## 8. Risks and open questions

| Risk / question | Mitigation |
|---|---|
| PatternXP patterns may not follow triggered play positions (§3.3) | Spike first in 0.1; add native message path if needed |
| Clips are shared patterns, unlike Live's independent clips | Explain in UI; Duplicate-as-new in 0.2 |
| Old native machines that misbehave when patterns jump (e.g. trackers expecting linear position) | Legato/relaunch tested against the upstream Gear set; gear.xml flags for problem machines |
| Audio-thread cost of the session queue | Fixed-size per-track state, no allocations, benchmark in 0.1 acceptance |
| Upstream merges touching `WorkManager`/`SequenceCore` | Keep engine changes small and send them upstream as PRs early (G1–G7 are bug fixes on their own) |
| Link licensing (GPL) | Decide process isolation vs. GPL distribution before 0.8 |
| Toggle shortcut: all F-keys taken | Decide in 0.1 (candidate Ctrl+Tab) |

## 9. Code pointers

| Area | File |
|---|---|
| Sequence + trigger | `ReBuzz/Core/SequenceCore.cs`, `ReBuzzGUI/BuzzGUI.Interfaces/ISequence.cs` |
| Per-buffer position update | `ReBuzz/Audio/WorkManager.cs` → `UpdatePatternPositions`, `UpdatePatternColumnEvents` |
| Song position and loop | `ReBuzz/Core/SongCore.cs` → `UpdatePlayPosition` |
| Pattern playback (managed editor) | `ModernPatternEditor/ModernPatternEditor.NET/PlayRecordManager.cs` |
| Existing click-to-play | `ReBuzzGUI/ModernSequenceEditor/SequenceEditor.xaml.cs` → `UpdatePatternAmin` |
| Song subsections | `ReBuzzGUI/BuzzGUI.Interfaces/ISaveSong.cs`, `IOpenSong.cs`; raised in `ReBuzz/Core/ReBuzzCore.cs` |
| Main window views | `ReBuzz/AppViews/MainWindow.xaml.cs`, `BuzzView` in `IBuzz.cs` |
| Managed machine with GUI (template) | `Machines/QuickNote/QuickNote.cs` |
| Test driver | `ReBuzzTests/Automation/Driver.cs` |
