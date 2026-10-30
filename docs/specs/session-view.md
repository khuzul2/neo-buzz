# Session View for ReBuzz — Specification

| | |
|---|---|
| Status | Draft v7 — 0.1 (#1, #2), 0.2 (#3), 0.3 (#4), 0.4 (#5), 0.4.1 (#6), 0.5 (#7), 0.5.1 (#8), 0.6 (#9) and 0.6.1 A (#10) merged; next: 0.6.1 B, then 0.7 Session recording |
| Date | 2026-10-28 |
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
- **Threading.** The scheduler has its own lock, not `ReBuzzCore.AudioLock`, so a request never waits for a whole audio buffer. (Upstream's audio thread also held `AudioLock` while it synchronously invoked the UI thread at transport start, so a request waiting for `AudioLock` could deadlock; 0.4.1 removed that wait.) Lock order is `AudioLock` → session lock on the audio thread; requests (UI or MIDI thread) take only the session lock; machines and the dispatcher are never called while it is held. Per-track reads are lock-free. State-change events are marshalled to the UI dispatcher. No allocation on the audio path in steady state (only on ticks where something changes).

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
| 0.4.1 | Live hardening | Perform without the UI getting in the way: launches don't wait for the UI, land on the beat you hear, and the audio thread doesn't allocate or wait; dropouts are visible; unsaved work is autosaved | M |
| 0.5 | Clip & scene properties | Launch modes, per-clip quantization, legato, loop region and start offset, scene tempo | M |
| 0.5.1 | Faithful arrangement | Record and insert into the arrangement exactly what the session played: partial patterns, loop regions, legato, velocity | M |
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

**Status (merged, #5).** Features 1–4 are in. The mixer is pinned under the grid and scrolls sideways with it; **Mixer** in the top bar (or Ctrl+Alt+M) shows or hides it, and the choice is saved with the song.
- *Strips* (`SessionMixer`, no DSP of its own): a track's volume and pan are the amp and pan of the machine's *main outputs*, its output connections other than sends and cue (usually the one into the master or a bus). With several main outputs, volume moves them all by the same number of dB and pan sets them all. The fader uses the Machine View's amp knob scale (and ConnectionMixer's): linear in dB from -66 dB to +12 dB. The strips poll the graph, so a change in the Machine View shows within 50 ms, and a change in the session is the connection's amp, which the Machine View reads. Fader, pan and sends: drag (Shift for fine), wheel, double-click to reset. Each strip also has M (mute), S (solo) and C (cue) for its machine, as the track header has M and S.
- *Meters*: post-fader stereo peak of the main outputs, -60 … +6 dB, green/yellow (-12 dB)/red (0 dB), falling at ~60 dB/s. `MachineConnectionCore` measures the peak of each block on the audio thread only while a mixer is on screen (one pass over the block, no allocation, no locks); the view reads it on the render loop, throttled to one update per 25 ms (≥ 30 fps).
- *Returns*: ⚙ → *Return tracks* turns effect machines into return columns (A, B, …) after the tracks. A send is the connection from the track's machine to the return: raising it from off connects them (one undo step in the Machine View's history); a send that would make a feedback loop is greyed. Adding or removing a return is a Session View undo step; removing one leaves its connections alone.
- *Cue*: ⚙ → *Cue output* picks the machine the C buttons connect tracks to, at unity: an effect that plays to a second output (ReBuzzAudioOut or Polac Out set to other channels) and isn't connected to the master.
- *Groups*: each Machine View group (`IMachineGroup`) with a member that has a track gets a column before its first member: click the header to fold the members away (saved with the song), a group slot launches the members' clips in that scene together and shows them playing, the group stop stops them, and the group strip is the group's main output machine's volume, pan and meter.
- *Master strip* under the scene column: the master volume (the toolbar's) and ReBuzz's master VU meter.
- Also fixed: a connection's amp ramp (4 blocks after a change) moved the wrong way within each block and jumped back at the next, a click per block while an amp changed; fader moves now ramp smoothly. And a new connection started at unity and ramped to its amp, so a send made at a low level began with a short loud burst; new connections now start at their amp.

Tests: `ReBuzzTests/SessionMixerTests.cs`: the session's fader and pan are the connection's amp and pan both ways (also through the strip controls), balance across several outputs, sends and cue as connections (with undo), the post-fader peak (and nothing measured while no meter is shown), the amp ramp, new connections starting at their amp, group columns (launching, folding, keyboard skipping folded tracks), settings vs undo, and a render. Not automated: the frame rate on a real display, and listening for clicks.

---

### 0.4.1 — Live hardening

**Outcome.** The foundations for playing live: the engine never waits for the UI, launches land where you hear them, and what can't be heard can be measured. Done before 0.5 so later phases build on it.

**Items**
1. *No waiting for the UI on engine threads.* Transport start (audio thread, under `AudioLock`), the song-end stop, MIDI input, and MIDI focus and base octave changes from native machines (IPC thread) used `Dispatcher.Invoke`. They now post (`BeginInvoke`); `ReBuzzCore.RaiseOutsideAudioLock` raises property changes on the UI thread, never under `AudioLock`.
2. *No UI work under `AudioLock`.* Connecting and disconnecting machines (the mixer's sends and cue, during a performance), sequence edits (the arrangement recorder), sequence list changes, pattern clone and delete, and loop and song end changes now change their data under the lock and raise their events after releasing it.
3. *Session MIDI off the UI thread.* `SessionMidi.OnMidiThread` launches, stops and toggles play straight from the MIDI driver's thread, through a snapshot of what each slot, scene and track holds that the UI thread rebuilds when the layout changes. Learn, quantization and moving the note grid edit UI state and are posted. A busy UI no longer delays a pad.
4. *Dropouts visible.* Upstream counts audio callbacks that overran their own buffer and driver resets, but only showed them to an external profiler. The Session View's top bar now shows them ("No dropouts" / "3 dropouts", red, with the worst overrun; click resets).
5. *Starting a clip partway through.* `LaunchOptions.StartOffset` and scheduler launches with an origin before their launch tick. 0.5's legato and clip start markers build on it.
6. *A pattern plays in one place.* Buzz patterns have one play position, so the same pattern on two tracks (two sequences of one machine, or session and arrangement) used to play at whichever position was set last. Now a session clip keeps its pattern against another track's arrangement, and launching a pattern on a second track hands it over from the first when it starts there (no stop or note-offs, since the machine plays on; recorded into the arrangement as a `Break`).
7. *Quantized from what is heard.* The engine renders ahead of the speakers by the output latency (estimated from the driver's buffer, plus ReBuzz's fill-thread buffering). Launches, scene launches and stops now pick the grid point after what is heard, not after what has been rendered; if that point has been rendered already, the clip starts on the next tick in step with it (catch-up: only what already played is skipped). Without this, a pad hit within the latency before a bar line started a whole bar late. Quantization None doesn't catch up. ⚙ → *Time launches to what you hear* turns it off.
8. *Pattern positions in proportion to what plays.* Every 256-sample block, the engine cleared the play position of every pattern of every machine and walked every event of every sequence (with an allocating tree enumerator). Now it clears only the patterns it positioned and finds each track's current event by binary search over a sorted snapshot kept by `SequenceCore`. `ISequence.PlayingPattern` is the track's own current pattern (upstream returned the first event whose pattern had a position, possibly one another track was playing).
9. *Launch resolution documented.* Quantization None starts on the next tick (1/16 note at 4 ticks per beat): Buzz machines change on ticks. Raising ticks per beat gives finer timing (tooltip on the quantization box).
10. *Autosave.* `SongAutosave` saves a copy of a song with unsaved changes every two minutes while the transport is stopped, to `%LOCALAPPDATA%\ReBuzz\Autosave`, leaving the song's own file, name and modified flag alone. Saving, opening, a new song and a normal exit delete it; one left at start means ReBuzz didn't close normally, and the main window offers to recover it (as the original song, with unsaved changes).

Also: steady playback allocates nothing on the audio thread (removed a per-block read-only wrapper of pattern columns, a per-block song time object for the master meter, a per-tick dictionary wrapper per sequence, and a per-tick closure); a new pattern starts "not playing" (it was at position 0 until the next block).

**Acceptance criteria.** No synchronous UI call and no UI event under `AudioLock` on the engine paths above (tested with a dispatcher that queues instead of running); MIDI launches with the UI thread blocked; zero bytes allocated per buffer in steady playback; a randomised soak run keeps its invariants.

**Status (merged, #6).** Tests: `ReBuzzTests/EngineHardeningTests.cs` (transport and song end with a blocked UI; MIDI launch with a blocked UI; events of connections, sequences, patterns and loop changes raised outside the lock; one place per pattern against the arrangement and between tracks; heard-position launches with and without latency; arrangement lookup whatever the edit order; allocation per buffer (and a per-stage report: 0 bytes per buffer); timing of a heavy song (32 machines, 1,024 arrangement events, 16 session clips: 7.2x real time on the CI runner, median 0.8 ms and 99th percentile 1.7 ms per 5.8 ms buffer; one buffer in 2,000 took 18 ms there, a shared virtual machine); a randomised 2,000-step soak run with two tracks on one machine; autosave and crash recovery), and scheduler unit tests in `ReBuzz.SessionTests`. The Hum test machine (constant output, no host calls) measures the engine alone. Not covered by automated tests: real audio drivers (the latency estimate and the dropout counter only run with a device), and native pattern editors with a start offset (PatternXP gets the offset through `PlayPattern`, as for any position change).

---

### 0.5 — Clip & scene properties

**Outcome.** Live's clip view controls that matter for launching.

**Features**
1. Per-clip **launch mode**: Trigger, Gate (plays while held), Toggle, Repeat (retriggers at quantize rate while held).
2. Per-clip **launch quantization** override (Global, None, 1/16 … 8 bars).
3. **Legato**: new clip starts at the position the previous clip on the track had reached.
4. **Loop region and start offset** inside the pattern (play from row X, loop rows Y–Z); loop on/off (one-shot).
5. **Velocity** sensitivity for MIDI-launched clips: the launch velocity scales the clip's volume (decided in 0.5: volume only, not a chosen parameter).
6. **Scene tempo and beats-per-bar**: launching the scene changes tempo (`IBuzz.BPM`).
7. Clip view panel in the session window showing these properties.

**Engine.** `LaunchOptions` gains mode, quantize, legato, loopStart/loopEnd/startOffset; position formula generalised. Tempo change scheduled on the launch tick.

**Acceptance criteria.** Each mode behaves like Live's in a side-by-side test checklist; legato switching between two 1-bar clips is seamless (no restart click).

**Status (merged, #7).** Features 1–7 are in. A clip is a pattern, so its settings belong to the pattern (saved in the song's layout, following renames, copied by Duplicate, gone with the pattern). The clip panel beside the grid (**Clip** in the top bar, or *Clip settings* on a clip's menu) shows and edits the selected clip's settings; edits are Session View undo steps. Clips with settings show short marks in their slot (G/T/R for the mode, L legato, 1× one-shot, ◆ markers, v velocity, q quantization) and the settings in their tooltip.
- *Launch modes*, with Live's press and release: a slot's ▶ (mouse button down and up), Enter (key down and up; a held key doesn't repeat) and MIDI (note on and off; a CC's value 0 is the release). Trigger ignores the release. Gate stops on the clip's quantization after the release; released before it started, it plays one quantization step. Toggle stops on the next press. Repeat restarts the clip at its quantization rate while held and plays on after the release. A release doesn't cancel another clip waiting to replace the one released. Scenes launch their clips as Trigger, on the session's quantization, with each clip's legato, markers and velocity.
- *Quantization* per clip: Global, None, 1/16, 1/8, 1 beat, 2 beats, 1, 2, 4 or 8 bars (the session's box has the new values too). 1/16 and 1/8 are fractions of a beat in ticks: at 4 ticks per beat, 1/16 is one tick.
- *Legato*: the new clip starts where the clip it replaces on the track has got to (wrapped into its loop if it is shorter); with nothing playing, from its start. Switching sends no note-offs, so the machine plays on.
- *Region*: start marker, loop start and loop end in ticks (empty: the clip's end), loop on or off. Without loop the clip plays from its start marker to its end once and the track stops. Moving markers changes a playing clip at once.
- *Velocity*: the launch velocity scales the volume of the clip's machine (0%: none; at 100% the softest note is silent): its output connections play at that gain, ramped over a block like an amp change, without changing the connection's amp, so the mixer and the Machine View don't move. Back to full when the clip stops or the transport stops. The gain is per machine: two tracks of one machine share the last launched clip's. Mouse, keys and CCs launch at full velocity.
- *Scenes*: right-click → *Tempo…* and *Beats per bar…*; the scene button shows them ("140 BPM · 3/4"). They change on the scene's launch tick, also for a scene without clips. A time signature change restarts the bar grid there (the clock keeps counting bars). The latest scene launch wins: a scene still waiting to start loses its settings. The tempo isn't recorded into the master's pattern (`ReBuzzCore.SetBPMFromEngine`, which skips recording, since the audio thread sets it).
- *Engine*: `ClipShape` (markers and loop) and `LaunchSpec` (shape, legato, repeat, gain) in `SessionScheduler`; positions, ends, legato and repeats in its tick; a grid origin for time signature changes; `ISession.Release` and a `LaunchScene` overload with per-clip options and `SceneOptions`; `MachineCore.ClipGain` applied by `MachineConnectionCore`.
- *Limits* (lifted in 0.5.1): recording into the arrangement wrote a clip that starts after its first row (start marker, loop start, legato) from its first row, and didn't record velocity: a Buzz sequence starts patterns at their first row and has no gain. *Insert into arrangement* copied whole patterns.
- Also fixed: the arrangement recorder wrote a clip launched late (the catch-up of 0.4.1) only at its next loop wrap; launches are now written at their grid point, where they are heard to start. Undoing a marker change also changes the playing clip.

Tests (137/137 on CI, 0 bytes allocated per buffer in steady playback): `ReBuzzTests/SessionClipTests.cs` (13): each launch mode through the audio loop (mouse and keys via the layout and the view's Enter, and MIDI note on/off), per-clip quantization, legato (positions and the machine's output), start marker and loop region, moving markers while playing, one-shot clips, velocity scaling the output (MIDI velocity, sensitivity, back to full on stop and transport stop), scene tempo and beats per bar on the launch tick (and the engine's tempo), a scene replacing a waiting one, settings following renames, duplicates, deletes and undo, the clip panel's controls, and a render; scheduler and layout unit tests in `ReBuzz.SessionTests`. Not automated: a side-by-side check against Live, listening to legato switches, and native pattern editors with markers (positions reach them as for any jump).

---

### 0.5.1 — Faithful arrangement

**Outcome.** What the session plays, the arrangement plays back: recording a performance that uses 0.5's clip properties, or inserting a scene, gives an arrangement that sounds the same. Done before 0.6, whose follow actions and launch undo build on recording.

**Items**
1. *Arrangement events that play part of a pattern, at a volume.* `SequenceEvent` gains `Offset` (where the pattern starts playing), `End` (where it stops; 0 for its end) and `Gain` (0..1). The engine plays an event from its offset, and its span (`PlayedLength`) is the part played, cut at the next event as before. Song files keep them in an `ArrangementEvents` subsection (.bmx and .bmxml), written only when an event needs it; upstream ReBuzz skips the subsection and plays those events whole at full volume, which is what the fork did before.
2. *Recording writes what was heard.* A launch is written with the part it plays (from its start marker, or where legato picked it up, to its end marker) and its launch velocity; a loop wrap from the loop start; a late launch at its grid point; and when recording starts while clips play, each from where it is. The last event of a pass stops at the clip's end marker.
3. *Insert into arrangement follows clip markers*: the start marker, then the loop region over and over (once for a clip without loop), cut at the end. A scene's length is its clips' first pass.
4. *Velocity per machine, well defined.* A machine plays at the gain of the most recently started of the session clips and arrangement events playing on its tracks. Two tracks of one machine are one audio signal and can't have two volumes; when the newest stops, the other's gain applies again (in 0.5 the machine went back to full volume).
5. *Editing keeps them.* The sequence editors' copy, paste and undo, deleting a machine and undoing it, and replacing a machine keep an event's offset, end and volume. The editors label a partial event with the part played and its volume ("Verse 4–12 50%") and draw it from its offset (the modern editors' event hints and play position).

Also fixed: opening a .bmxml song lost all of its subsections (upstream: `BMXMLFile` cleared them before ReBuzz read them), so .bmxml songs lost their Session View layout, MIDI map and mixer settings; .bmx was not affected.

**Acceptance criteria.** A performance using start markers, loop regions, legato, one-shot clips and velocity, recorded into the arrangement, plays back sample for sample the same; files keep it; songs without these events are saved as before.

**Status (merged, #8).** Items 1–5 are in. Tests (147/147 on CI, 0 bytes allocated per buffer in steady playback): `ReBuzzTests/SessionArrangementTests.cs` (a performance with every 0.5 property played back sample for sample from the arrangement; recording that starts mid-clip; partial events playing; .bmx and .bmxml keeping event regions, volumes and the layout; plain songs writing no fork data; editor undo; scene insert following markers), `ReBuzzTests/SessionClipTests.cs` (two tracks of one machine; an arrangement event's volume), and a scheduler unit test. Not automated: the sequence editors' drawing of partial events, and PatternXP playing an event from an offset (it gets the position through `PlayPattern`, as for session clips).

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

**Status (merged, #9).** Features 1–5 are in, with Live 12's semantics, and every new control can be MIDI-learnt (right-click it, or ⚙ → *MIDI learn: performance*).
- *Follow actions* (the clip panel's *Follow action* section): off by default. Actions A and B are No action, Stop, Play again, Previous, Next, First, Last, Any, Other or Jump (to a scene row), with A's chance (B gets the rest). They are linked (at the end of the clip, after ×N passes of its loop) or come after a follow time in bars.beats.ticks. They move within the clip's group: the run of filled slots around it on its track, Next and Previous wrapping, Other never repeating the clip unless it is alone. They happen exactly at their time unless the target has its own quantization, and keep the target's legato. No action ends the chain until the clip is launched again; any launch on the track replaces a pending follow action. Slots with one show →.
- *Scene follow actions* (the clip panel's *Scene* section, for the selected slot's row, or *Follow action…* on a scene's menu): the same actions over the run of non-empty scenes, after a follow time from the scene's launch. They win over clip follow actions due on the same tick. The section also sets the scene's tempo and beats per bar.
- *Follow* in the top bar switches all follow actions on or off for the song (saved with it). A follow action skipped while off comes round again a period later.
- *Crossfader*: A/B chips on the mixer strips assign machines (saved with the song); the crossfader on the master strip fades between them. Right-click it for its curve: Dipless (both sides full in the centre), Constant power or Fast cut. It scales the machine's output connections as velocity does, without moving faders. From MIDI, a CC across its range moves it from the MIDI thread.
- *Select on launch* (⚙, a preference, on by default): launching from the mouse, keys or MIDI selects the slot, or the scene's row. *Scenes stop other clips* (⚙, saved with the song): a scene launch also stops the tracks it has no clip for, on the same launch point.
- *Launch undo*: **↶ Launch** in the top bar, Ctrl+Alt+Z, or MIDI undoes the last launch (a history of 32; the tooltip says which). A launch still waiting is cancelled. One that has started gives its tracks back what they played before, on the next launch point, in step as if it had never stopped; tracks that played nothing stop. A scene's stops are undone too.
- *Tempo*: the top bar shows the tempo. **◀ ▶** nudge it 4% slower or faster while held: ticks stretch or shorten, and the song's whole-number BPM doesn't change. **Tap** sets it from the last four taps, starting at the third; a pause of over 2 s starts again, and taps under 0.1 s apart (a bouncing pad) are ignored.
- *Engine*: follow actions run in `SessionScheduler`'s tick per track, with a seedable generator, and scene follow actions run in `SessionCore.OnTick`. An immutable `LaunchMap` (slots, scenes and their options), built on the UI thread, is shared with the MIDI and audio threads. `SessionCore` keeps the launch history. Crossfade gains are in `MachineCore.CrossfadeGain`, applied by `MachineConnectionCore`, and the nudge is `ReBuzzCore.TempoNudge`, applied by `WorkManager`.

Tests (158/158 on CI, 0 bytes allocated per buffer in steady playback): `ReBuzzTests/SessionPerformanceTests.cs` (11):
- follow chains on time, with the machine's output;
- the song's switch;
- an unlinked follow time, and a target's own quantization;
- scene follow actions on time, winning over a clip's;
- scenes stopping other clips, and undoing that;
- launch undo in step;
- crossfader gains for each curve, also from MIDI;
- nudged tick lengths, also held from MIDI;
- tap tempo, including a bouncing pad;
- undo and the follow switch from MIDI;
- the clip panel's follow and scene sections, with a render.

The acceptance test is a 4-clip chain of Other follow actions playing for 10 minutes at 120 BPM with a seeded generator. It made 1,196 transitions, each exactly at the end of a clip, with no silent tick; the machine's output matched the playing clip on every tick, and chi-square was 9.34 (95% critical value 15.51). Scheduler and document unit tests are in `ReBuzz.SessionTests`; the 15 follow action tests include a 20,000-transition chain (chi-square 6.94). Not automated: a side-by-side check against Live, and listening to the nudge.

---

### 0.6.1 — Solid ground

**Outcome.** A hardening pass before session recording. Three independent reviews covered:
- concurrency and real-time safety;
- launch timing;
- the model, persistence and architecture.

They found about 30 issues in 0.1–0.6. Locks are taken in the documented order, and nothing allocates per buffer. The defects were in four areas:
- how 0.6's features interact;
- stale references to deleted objects;
- identity (names and indices);
- undo.

Two PRs:
- **A** fixes the defects, with a regression test for each.
- **B** reworks the foundations 0.7 builds on.

**A: fixes**
1. *Follow actions and queued requests.* A follow action no longer replaces a stop, Stop all, a scene's stop or a launch that is already queued on its track. It waits a period instead. A scene's follow action ends when another scene is queued.
2. *Queued scenes.* Stop all and Back to arrangement drop a queued scene's tempo, beats per bar and follow action along with its clips. Undoing a clip launch leaves a queued scene's settings alone; only undoing a scene clears them.
3. *Where a clip is heard to start.* A launch now carries the grid point it is heard on, separately from the phase its position counts from. An undone launch then counts its follow action, repeats and recording from where it comes back in step, and so does a start offset (before, from its original start). A Gate release before a late start stops a step after the grid point.
4. *Stale requests.* An undo that puts back a queued launch whose time has passed plans it again on the grid, in step with when it was due. Undo history from before a transport restart is dropped.
5. *Late scenes.* A scene launched late (latency compensation) restarts the bar grid, and counts its follow action, from the grid point it is heard on. Changing beats per bar while playing restarts the grid on the bar playing now, as a scene's change does. The bar length changes under the session lock.
6. *Markers and length.* Changing a playing clip's markers or its pattern's length moves its linked follow action. If the loop is turned off, it follows where the clip ends instead of falling silent.
7. *One clip, two tracks.* A clip launched on two tracks on the same tick plays on the first, not on neither.
8. *Deleted patterns and removed sequences.* They are flagged under the audio lock (`PatternCore.IsDeleted`, `SequenceCore.IsRemoved`). The scheduler rejects launches of them from any stale reference (pads, follow columns, launch undo), and the undo history forgets them. The recorder never writes a deleted pattern, and .bmx files no longer write an invalid pattern index.
9. *Songs closing and loading.* `ReBuzzCore.SongLoading` is set for the duration. MIDI pads don't launch or start the transport meanwhile. The launch map is cleared at once, not when the UI is next idle.
10. *Transport stop.* The stop runs under the audio lock, so it can't land between the audio thread's playing check and the session's tick. Before, it could leave a recording pass open, the session clock running, or a gain behind.
11. *Undo keeps performance settings.* Undoing a grid edit no longer reverts the follow switch, *Scenes stop other clips*, crossfader assignments or the curve. The crossfader's position belongs to the engine: MIDI moves it, and the song sets it only when it is opened. The UI and the MIDI thread no longer race on its gains.
12. *Layouts.*
    - Jump targets follow scene rows as they are inserted, removed or duplicated.
    - A damaged layout loads safely: scene rows are capped at 4,096, NaN positions and volumes are ignored, and a load error is logged instead of stopping the song from opening.
    - Duplicating a clip whose name ends in a long number no longer throws.
    - A song only looked at in the Session View saves no layout; it is saved once something is edited.
13. *Files.*
    - .bmx sequences use 4-byte positions when an event lies past 65,535 ticks. Before, those positions were truncated, which takes about 17 minutes at 32 ticks per beat.
    - An autosave that can't be opened is kept, not deleted.
    - Scene follow times use the scene's own beats per bar.

**Status A (merged, #10).** Tests (169/169 on CI):
- `ReBuzz.SessionTests/SessionHardeningTests.cs` (11 scheduler regressions; each one fails on the 0.6 scheduler);
- two document tests (jump targets, damaged input);
- `ReBuzzTests/SessionSolidGroundTests.cs` (11 engine regressions): queued scenes against Stop all and undo, a late scene's grid, beats per bar changed while playing, undo keeping performance settings, the crossfader's position, a deleted pattern against undo and a follow action with a stale map, MIDI during a load, plain songs, .bmx past 65,535 ticks, and an autosave that won't open.

Known limits (unchanged): no autosave while the transport plays (saving talks to native machines); a solo pattern holds back session launches; with the real WPF dispatcher, upstream allocates one dispatcher operation per tick.

**B: foundations**
1. *Identity follows the song exactly* (`SessionLayout.Identity.cs`). The layout keeps names, as song files do, and applies every change in the song to the layout and to every undo snapshot.
   - Sequences added, removed or swapped move tracks by their exact index. Deleting one of a machine's two sequences removes that track, not the other (before, tracks were matched by index first).
   - Renames from any view rename everywhere, so an undo after a rename in the Machine View keeps the clip settings.
   - A deleted pattern's or machine's name is *retired*: snapshots keep it under a private-use marker, and files never contain it. A new pattern or machine reusing the name inherits nothing (no crossfader side, no clip settings, no slots).
   - Changes made under the audio lock reach the current layout at once; snapshots and the launch map follow when the lock is released. Deleting a machine deletes its sequences and patterns under that lock, so heavy layout work never holds up the audio thread.
2. *Object-bound session actions.* Creating, duplicating, deleting, renaming and colouring clips hold the pattern object. When the session's own undo makes a pattern again, its slots and settings come back from a *grave* and the actions follow the new object, even when another clip took the name meanwhile (it comes back under a fresh name). Undo never deletes or overwrites another editor's pattern of the same name, and does nothing for a clip whose machine was deleted since.
3. *Settings apart from the layout.* `SessionSettings` holds the time signature, quantization, clip length, the song's MIDI map, the follow switch, *Scenes stop other clips*, the crossfader, view state, folded groups and the cue output. Undo snapshots share the live instance, so undo never changes a setting. This replaces A's keep-list. The cue output no longer edits the returns list behind undo's back.
4. *One launch path.* Mouse, keys, MIDI and follow actions all launch what the launch map says. The map is rebuilt synchronously after every edit, and once after a burst of song changes. Meanwhile the liveness flags keep stale entries harmless, and a UI clip launch falls back to the clip's own settings.
5. *Recording undo by difference.* Undoing a recording pass or a scene inserted into the arrangement takes out only the events it wrote, if they are unchanged, and puts back what they replaced, where nothing has been written since. Later Sequence View edits stay. The loop and song end go back only if nothing changed them since.
6. *Threads.*
   - Sequence events have a leaf lock for readers that can't take the audio lock (span refreshes on the IPC thread), and list swaps take it too.
   - A transport stop from another thread (a native machine's host message) goes through the UI thread, so that thread never waits for the audio lock.
7. *0.7 redesigned* around the session's own recorder (see 0.7): timestamps on the MIDI thread, record windows on the audio thread, pattern writes on the UI thread, a take as one object-bound undo step.

An independent review of A and B confirmed fixes 1–13 and found new defects in B's first version, all fixed before merging:
- Matching objects that another view's undo brings back, by name and contents, can't tell them from new look-alikes: new empty clips, duplicates, session-only tracks, re-added machines. That matching was removed, so names are retired and only the session's own undo brings clips back.
- Heavy work ran under the audio lock.
- Transport stops from the IPC thread waited for the audio lock.
- Pattern actions ran on deleted machines.
- A redo recreated a pattern at its old length.
- A Gate request restored by undo lost its stop.

Known limits:
- Undoing, in another view, the deletion of a pattern, sequence or machine brings the object back without its slots or settings.
- Undo snapshots don't keep a removed sequence's track.
- A recording pass's undo also takes back Sequence View edits made during the pass on the tracks it wrote.
- Undocumented upstream: ReBuzz without the fork drops the fork's subsections when it saves a song, after which partial arrangement events play whole patterns.

### 0.7 — Session recording

**Features**
1. Track **record arm**; record button on empty slots of armed tracks.
2. **Record MIDI into a new clip** from a keyboard played into the armed track's machine: a new pattern (the take) is created in the slot, and notes are written into its note columns by the session's own recorder (see Design).
3. **Overdub** into the playing clip; **fixed-length** recording (1, 2, 4, 8 bars) that ends and loops automatically.
4. **Capture MIDI**: keep the last N bars of incoming MIDI per armed track and turn it into a clip on demand.
5. Parameter automation recording into the clip (knob moves → pattern parameter column).
6. Metronome and count-in.

**Design (revised in 0.6.1).** The first draft planned to record through the pattern editor's `PlayRecordManager.RecordPlayingMidiNote`. That can't work:
- it is internal to the ModernPatternEditor plugin;
- it writes into every playing pattern of whatever machine the Pattern View has selected, through that editor's own undo stack;
- upstream disabled it ("ToDo: only record to MIDI columns").

It serves neither per-track arm, nor several armed tracks, nor Capture MIDI. Instead, the session records by itself, over three threads, each doing only what it must.
- *MIDI thread: timestamp and queue.* `SessionMidi` sees each message first, as it does for mapped controls. For an armed track's machine it stamps the message with the heard session position: clock, tick fraction and sub-tick, from the same rendered-position and latency estimate that launches use (0.4.1). It then pushes the message onto a preallocated lock-free ring, with no allocation and no locks. The message still goes on to the machine for monitoring. Routing changes so that armed tracks' machines receive input, not only the MIDI-focus machine.
- *Audio thread: record windows.* `OnTick` opens and closes record windows on the launch grid, like launches. A window starts on a grid point after the count-in and ends after its fixed length, after which the take plays on as a looping clip. The metronome is a preallocated click mixed into the master during count-in and recording; it is not a machine.
- *UI thread: writing patterns.* A drain posted from the queue writes events into the take's pattern through `IPatternColumn.SetEvents`, independently of the Pattern View:
  - notes go to the machine's note columns, one track per held note, with a note-off at its release;
  - velocity goes to a velocity column if the machine has one;
  - sub-tick positions use `SubTickInfo`.
  
  Overdub writes at the position modulo the clip's loop. Capture MIDI is a per-track ring of the last N bars, always running while armed, turned into a take on demand.
- *A take is one undo step* in the session's history. It consists of an object-bound pattern creation (`SessionCreatePatternAction`), its slot, and its events (a diff action on that pattern, as recording into the arrangement does since 0.6.1). Takes get fresh names. Deleted takes' names are retired, so a later take never inherits their slots or settings.

**What 0.6.1 provides for it.**
- Identity that follows the song exactly, with retired names and object-bound pattern actions.
- Liveness flags, so a take deleted while recording is never written again.
- One launch path, through a launch map rebuilt synchronously.
- `SongLoading`, so nothing records into a song while it loads.
- Recording that is serialized with the tick.
- Diff-based arrangement undo.
- The leaf lock on sequence events.

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
| Clips are shared patterns, unlike Live's independent clips | Explain in UI; Duplicate-as-new in 0.2; a pattern plays in one place at a time (0.4.1) |
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
