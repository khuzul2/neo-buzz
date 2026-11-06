# Session View for ReBuzz — Specification

| | |
|---|---|
| Status | Draft v7 — 0.1 (#1, #2), 0.2 (#3), 0.3 (#4), 0.4 (#5), 0.4.1 (#6), 0.5 (#7), 0.5.1 (#8), 0.6 (#9), 0.6.1 (#10, #11), 0.6.2 (#12), 0.6.3 (#13), 0.7 (#14), 0.7.1 (#15), 0.7.2 (#16), 0.8a (#17), 0.8b (#18), 1.0 (#19) and 1.0.1 (#20) merged |
| Date | 2026-10-31 |
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
| 0.7.1 | Recording without its limits | Piano roll; notes to and from the Pattern View; takes growing as they record; Capture while stopped; native editor moves recorded | M |
| 0.7.2 | Hardening before 0.8 | Fixes from a review of 0.7.1 and of the MIDI and tempo paths 0.8 builds on | S |
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

**Status B (merged, #11).** Tests (178/178 on CI):
- `ReBuzzTests/SessionIdentityTests.cs` (10): the right track survives when one of a machine's two sequences is removed; undo after a Machine View rename; a new machine with a deleted one's name inherits nothing; session undo never touching another editor's same-named pattern; a Pattern View delete leaving nothing to a new clip of the same name; the session's undo bringing back its own clip after a newcomer took the name; a new sequence starting empty; no ghost pattern for a deleted machine; recording undo keeping later edits.
- Document tests for track operations and retired names.

The allocation test (`SteadyPlaybackAllocatesNothing`) failed once on CI with 40–80 bytes per buffer and passed when rerun. It needs watching.

Later (2026-11), on the Windows package runs, it met a one-off allocation after its warm-up now and then: 848, 1512 and 2048 bytes in one buffer, mid-tick, at ticks 15 to 18 of the loop (runs 41916587037, 41966650618 and 41912541139), and nothing in the 20000 buffers its failure message read on afterwards; it passed on rerun. Reading on can't name a one-off, so each measured buffer is now kept with what the thread did in it (`ReBuzzTests/Automation/AllocationWindow.cs`): the methods the JIT compiled on it, the GCs, its time, and, from a listener on the runtime's own events running through the window, the runtime's events on that thread (JIT with each method and tier, type loads, exceptions, contention, waits, allocation ticks) placed in their buffers; a failure lists them for the buffer that allocated. `ParameterChangesAllocateNothing` measures the same way; the assertions are unchanged (0 bytes). `SteadyPlaybackAllocationSoak` (explicit, run by name) repeats the window on 150 fresh rigs: none allocated in run 41987319033, so the one-off needs the full suite before it (it never came in isolation); the next failure in a package run names it.

An independent review of A and B confirmed fixes 1–13 and found new defects in B's first version, all fixed before merging:
- Matching objects that another view's undo brings back, by name and contents, can't tell them from new look-alikes: new empty clips, duplicates, session-only tracks, re-added machines. That matching was removed, so names are retired and only the session's own undo brings clips back.
- Heavy work ran under the audio lock.
- Transport stops from the IPC thread waited for the audio lock.
- Pattern actions ran on deleted machines.
- A redo recreated a pattern at its old length.
- A Gate request restored by undo lost its stop.

Known limits (the first three were lifted in 0.6.2):
- Undoing, in another view, the deletion of a pattern, sequence or machine brings the object back without its slots or settings.
- Undo snapshots don't keep a removed sequence's track.
- A recording pass's undo also takes back Sequence View edits made during the pass on the tracks it wrote.
- Undocumented upstream: ReBuzz without the fork drops the fork's subsections when it saves a song, after which partial arrangement events play whole patterns.

---

### 0.6.2 — Exact restorations

**Outcome.** 0.6.1's three known limits are gone.

1. *Undo in any view brings back what the session had.* Undo actions that bring back a pattern, sequence or machine they deleted report the old object and the new one standing for it (`BuzzGUI.Common.Actions.Restorations`). The same goes for a redo that makes again what its undo deleted. Reporting actions:
   - the pattern editors' `DeletePatternAction` and `CreatePatternAction`;
   - the sequence editors' `DeleteSequenceAction` and `AddSequenceAction`;
   - the Machine View's `DeleteMachinesAction` and `CreateMachineAction`;
   - the session's own pattern actions.

   The layout acts only on these reports, never on guesses by name or contents.
   - A deleted pattern's slots and clip settings stay in the layout and every snapshot under its retired name. Everyone sees them as empty slots, so a newcomer with the name inherits nothing. They move with every edit and transform meanwhile, so when an undo brings the pattern back they reappear where they are now, in the undo history too.
   - A removed sequence's track is kept as the layout and each undo snapshot had it. Each gets its own copy back.
   - A deleted machine's crossfader side, returns, cue and clip settings come back with it.
   - Unrestored places are forgotten after 256 deletions.
2. *Recording undo is exact.* A recording pass lists every event it wrote: what was there before its first write, and what it wrote last. Undo takes back exactly those, where they are unchanged, so edits made in the Sequence View during the pass stay, even on the tracks it recorded.
3. *The allocation test.* It failed now and then with 40–80 bytes per buffer, in an identical pattern each time. That fits tiered compilation still running some methods in their first compiled form on a busy CI machine, where a short-lived object isn't yet kept off the heap. The test now waits until a stretch of 100 buffers allocates nothing (logging a notice if it had to) before measuring steady playback. If it ever fails, it reports the types being allocated.
4. Also fixed in passing:
   - Deleting machines without a sequence editor (headless, tests) no longer throws.
   - Undo of the Machine View's machine delete no longer modifies the pattern-colour table while enumerating it.

### 0.6.3 — Foundations for recording

**Outcome.** A fourth review, of the paths 0.7 builds on, found defects there; they are fixed.

1. *Engine MIDI columns play each event once, at its position.* `WorkManager` read each block's events from the play position to a window end that counted the part of the tick before the block twice. Windows overlapped, so events late in a tick were sent up to a dozen times. A window is now exactly its block; one block's end is the next one's start. (Upstream code; it affected Note Matrix too.)
2. *Engine columns survive.*
   - Deleting a machine deleted, in every pattern, the columns of the pattern's own machine (upstream compared with the wrong machine). Now it deletes only those of the deleted machine.
   - Undo made patterns again without their engine columns: upstream's `PatternClip` fills only columns that exist, and a new pattern has none. `EngineColumns` copies them, events and metadata included. It is used by the machine delete undo, the session's pattern actions, and the session's duplicates.
   - Columns are copy-on-write: the audio thread never walks a list being changed. `SetEvents` applies a batch under one lock.
3. *Machine delete undo is exact for several machines.* Sequence indices are captured before any machine goes, so every track comes back with its clips. The undo reports the machine it made, not whichever has the name.
4. *Undo never makes a second pattern of one name.* Pattern editors keep data by name, and a machine accepts a taken name. Undo and redo in other views (`DeletePatternAction`, both `CreatePatternAction`s) make a pattern again under a free name if its own was taken meanwhile, and act on the pattern they made (`PatternNames`).
5. *A MIDI release does what its press did.* A table of held notes records whether each press went to machines or to the session, and which clip it launched. A key learnt while held, a map edited, or a grid moved no longer hangs a note or a Gate clip. Controllers record only launches, since 0 is a knob value.
6. *Removed tracks kept for undo follow the song.* Their copies follow renames, retirements and restorations, as the layout does. Snapshots made after the removal get the track too. A restoration counts only for the same machine, or the one an undo made in its place, so a newcomer with the old name inherits nothing.
7. Also:
   - Native machines' renames (IPC thread) reach the layout on the UI thread.
   - Short ticks can't make 0 sub-ticks; that divided by zero on the audio thread.
   - Deleted patterns leave the song's events at once. Upstream relied on a finalizer that never ran.
   - The layout leaves the static restoration bus when ReBuzz closes.
   - Retired machines are bounded.
   - Hidden Session Views rebuild when shown, not on every change.
8. *The allocation test's occasional failure is explained.* Its type sampling (0.6.2) caught the culprit: the work loop's `workSet.First()` (LINQ on a `HashSet`) boxed an enumerator, 40 bytes once or twice per block. The JIT's profile-guided optimisation usually removed the box, so the test failed only on some runs. The work loop now takes the first machine through the set's own enumerator.

---

### 0.7 — Session recording

**Features**
1. Track **record arm**; record button on empty slots of armed tracks.
2. **Record MIDI into a new clip** (a take) from a keyboard played into the armed track's machine.
3. **Overdub** into the playing clip; **fixed-length** recording (1, 2, 4, 8 bars) that ends and loops automatically.
4. **Capture MIDI**: keep the last bars of incoming MIDI per armed track and turn it into a clip on demand.
5. Automation recording into the clip.
6. Metronome and count-in.

**Design (revised in 0.6.1, settled in 0.7).**

*Where notes go.* The first draft planned to record through the pattern editor's `PlayRecordManager.RecordPlayingMidiNote`. That can't work:
- it is internal to the Modern Pattern Editor plugin;
- it writes into every playing pattern of whatever machine the Pattern View has selected, through that editor's own undo stack;
- upstream disabled it ("ToDo: only record to MIDI columns").

The Modern Pattern Editor also keeps every pattern's notes in its own store, keyed by name, which nothing else can write. So a take goes into the clip's *engine MIDI column*: a `PatternColumnType.MIDI` column of `PatternCore`, the one Note Matrix uses.
- `WorkManager` plays it itself, to any machine that takes MIDI, with note-offs at each note's end. 0.6.3 made it play each event once.
- Songs save it (.bmx TAP2 section, .bmxml).
- Undo keeps it (`EngineColumns`, 0.6.3).
- The pattern editors don't show it. The clip panel shows the count of recorded notes, and the slot menu clears them. (0.7.1: the piano roll edits them, and the slot menu moves them to the Pattern View's note lanes and back.)

*Three threads, each doing only what it must.*
- *MIDI thread.* `SessionMidi` sees each message first, as it does for mapped controls. What it doesn't take goes to `SessionCore.OnMidiInput`:
  - Armed tracks take the keyboard: their machines play it, directly rather than through their editors, and the MIDI focus routing is skipped.
  - A release goes where its press went (0.6.3's rule), so arming or disarming mid-note hangs nothing. The sustain pedal pairs the same way, per channel.
  - While a track records, each message is stamped under the gate: the heard session time (1/240 tick, from the same rendered-position and latency estimate that launches use, 0.4.1), and where the track's clip was then (`SessionScheduler.TryGetPlayingAt`). The stamp goes into a preallocated ring guarded by a leaf lock. Several devices call from their own threads; the audio thread never touches the ring. A drain is posted to the UI thread.
- *Audio thread.* Takes launch like clips, looped from their start, on the launch grid. From a stopped transport a take waits its count-in (`SessionScheduler.Start` places launches queued while stopped relative to the first tick). The metronome is a decaying sine (`MetronomeClick`), not a machine. It is mixed into the output after the master tap, so it is heard and never recorded, on each beat while it is on and during a count-in.
- *UI thread.* The drain turns stamps into events (`TakeWriter`) and writes them through `IPatternColumn.SetEvents`:
  - a note is one event from its press to its release, cut at the end of the clip's region;
  - controllers, pitch bend and aftertouch are events of no length.

*Record windows.* A track records while its window is open (`SessionCore.RecordWindow`, immutable, replaced by the UI thread):
- *A take* records its first pass only: its length in bars, or until stopped (64 bars in 0.7; growing as it records since 0.7.1). A take recorded until stopped is cut to the bars recorded, rounded up, so it loops on in step. It may be stopped by launching it, a stop (quantized or not), Stop all, or the transport. The engine notes the tick each clip stops on, so the length counts to there, and notes still held end there.
- With Session Record on, a take goes on after its first pass as an overdub, without a gap. Each later pass is its own undo step.
- Takes are polled by the layout's own timer, whether a Session View shows or not.
- *Overdub* (Session Record, Live's) records into whatever clip the track plays. An armed track playing nothing gets a take in its first empty slot from the selected scene.

*Capture MIDI.* Every armed track keeps the last 4096 messages it was played, with when they were heard, recording or not. Capture makes each track's phrase a clip in its first empty slot from the selected scene:
- it starts at the bar line before the phrase's first note (looking back 8 bars at most);
- it is 1, 2, 4 or 8 bars long;
- it starts playing on the next tick, in phase with what was played (`SessionCore.LaunchInPhase`).

All of it is one undo step. In 0.7 Capture needs the transport running, since a phrase is placed by the session clock (0.7.1 lifts this). What was played before the transport last started or jumped is forgotten.

*Automation.* A parameter moved in ReBuzz while its machine's track records goes into the clip's automation column for it. Moves in ReBuzz are those made in a parameter window or by a MIDI controller mapping: ParameterCore.SetValue without its "don't record" flag, on the UI thread, reaches `SessionCore.OnParameterChange`. Machines moving parameters themselves aren't recorded: peer controllers and LFOs on the audio thread, native editors on their own thread (0.7.1 records moves in a native machine's own window). An automation column is a parameter column marked `SessionAutomation`, marked before the engine can see it.
- The engine plays it, with the "don't record" flag, so playback is never recorded again.
- Other parameter columns belong to the pattern editors, which play their own data.
- MIDI controllers, pitch bend and aftertouch played on an armed track are recorded as MIDI events in the MIDI column.

*Undo.*
- A take is one undo step: its pattern, made with its MIDI column (`SessionCreatePatternAction`), and its slot. Undo keeps what it recorded, for a redo.
- Each overdub pass is one step per clip: a `ClipEventsAction` holding the clip itself.
- Takes get fresh names; deleted takes' names are retired (0.6.1).

**What 0.6.1–0.6.3 provide for it.** Identity that follows the song, with object-bound pattern actions. Liveness flags, so a take deleted while recording is never written again. One launch path. `SongLoading`. Diff-based undo. Engine MIDI columns that play once and survive deletes and undo. A MIDI release that does what its press did.

**Acceptance criteria.** Notes recorded at 120 BPM land within ±1 sub-tick of their played time; fixed-length recording loops seamlessly on the first pass.

---

### 0.7.1 — Recording without its limits

**Features**
1. A **piano roll** for a clip's notes: Ctrl+double-click a slot, or *Edit notes (piano roll)…* on its menu.
2. **Notes in the Pattern View**: the slot menu moves a clip's notes onto the Modern Pattern Editor's MIDI note lanes, and back.
3. Takes recorded until stopped grow as they record, past 64 bars.
4. **Capture MIDI with the transport stopped.**
5. Knob moves in a native machine's own window are recorded as automation.

**Design.**

*Piano roll.* Its notes are the note-ons of the clip's engine MIDI column (where takes go), each with its length.
- `ClipNotes` (pure) makes the edits. A drawn note snaps down to the grid and is one step long. A moved group snaps its first note and keeps its shape. Everything stays inside the clip and the MIDI range. Quantize moves starts by a strength, and ends if asked.
- `SessionLayout.EditNotes` makes each edit one Session View undo step (`ClipEventsReplaceAction`, holding the clip). Drawing into a clip without a MIDI column gives it one. Controllers in the column stay, unshown.
- Mouse: double-click adds or deletes a note; drag moves (Shift: off the grid); a note's right edge resizes; Alt+drag changes velocity; dragging on empty space selects; the keys play their note.
- Keys: Del, Ctrl+A, arrows (Shift: an octave), Ctrl+Up/Down for velocity, Q to quantize, Ctrl+Z/Y.

*Pattern View.* The Modern Pattern Editor keeps its notes in its own store. It gained two calls, `ExportClipNotes` and `ImportClipNotes`, which ReBuzz reaches through its managed editor bridge, as it does `GetPatternEditorMachineMIDIEvents`. Notes cross as int triples: time, note-on, length.
- `BuzzGUI.Common.TrackerNotes` (pure) lays notes on the lanes and reads them back. Each voice gets a track; the machine gets more tracks if it can take them. A note sits on a row with its velocity and its delay (in 1/95 of a row). A note ending in its own row gets a cut; otherwise a note-off goes on its end row, unless the track's next note starts by then. A round trip keeps notes to 1/95 of a row.
- *Move notes to the Pattern View* puts the clip's notes beside the lanes' own. If they need more voices than the machine has tracks, nothing moves and the menu says why.
- *Move notes from the Pattern View* empties the lanes into the clip's MIDI column, where the piano roll edits them.
- Each move is one undo step (`ClipNotesMoveAction`), refused while the clip records. Tracks the editor gained stay. The editor's own undo doesn't see the move. (0.7.2: undo and redo move only those notes, and notes above B7 don't fit.)

*Takes grow.* A take recorded until stopped starts with 64 bars of room. The layout's poll grows it 8 bars ahead of where it plays, up to 4,096 bars. It is cut to the bars recorded when it stops, as in 0.7.

*Capture while stopped.* With the transport stopped, the MIDI thread stamps messages with the wall clock (`Stopwatch`) instead of the session time. Capture times the phrase at the song's tempo: the notes after the last silence of a bar or more, within 8 bars (0.7.2). The clip starts at the phrase's first note, is 1, 2, 4 or 8 bars long, and doesn't launch: there is nothing to be in phase with.

*Native editor moves.* A native machine's own window moves a parameter through an IPC message on its UI or host channel (0.7.2: only those; its MIDI handler's moves come on the MIDI channel and aren't recorded). ReBuzz marks that move (`SessionCore.MoveFromEditor`, per thread), so it records like a move in a parameter window. Moves the machine makes while processing (the audio channel) still aren't recorded.

**Tests.**
- `ReBuzz.SessionTests`: `ClipNotesTests` (9) and `TrackerNotesTests` (5, including a random round trip).
- `ReBuzzTests/PianoRollTests.cs` (6): one undo step per edit; drawing into a clip without a MIDI column; quantize; the control's commands and a render; moving notes to the Pattern View and back, with undo and playback; notes that don't fit staying in the clip.
- `ReBuzzTests/SessionRecordingTests.cs`: a take growing past its room; Capture with the transport stopped.
- The fake Modern Pattern Editor exchanges notes as the real one does. The real one's lane code is compiled on CI but not run there.

---

### 0.7.2 — Hardening before 0.8

An independent review of 0.7.1 and of the paths 0.8 builds on (MIDI in and out, tempo). Every finding below was checked against the code before it was fixed.

**Pattern View notes**
- *Deadlock.* Moving notes could give a native machine more tracks while the editor's lock was held. The track count takes the audio lock, and the audio thread holds that lock while it runs the editor, which takes the editor's lock. The move is now planned first, changing nothing (`PatternEditorUtils.PlanClipNotes`). The tracks are set outside the lock, and only then are the lanes written (`WriteClipNotes`).
- *Notes above B7 (MIDI 119)* threw half way through, after the lanes had been cleared. They are now counted as not fitting, and nothing moves.
- *Chords played as one voice.* The editor's playback gave every MIDI-lane event track 0, so each note ended the one before and note-offs on other tracks were lost. Each event now carries its lane's track (`PlayRecordManager`, an upstream bug that this feature exposed).
- *Undo restored whole lanes,* wiping Pattern View edits made after the move. A move's undo and redo now add and remove only the notes it moved (`ClipNotesMoveAction`, `TrackerNotes.Without`/`Added`). If the lanes can't take the change any more, neither side changes.
- *Lanes of other machines* in the same pattern were exported and wiped. Only the clip machine's lanes count now.
- *Rows.* Rows are placed as the editor places them (`RowGrid`: beat + beat × row / rows, rounded down), including rows per beat that don't divide the beat (7, 9, …) and beats split their own way. Lanes whose beats are split differently from track to track aren't written. A note ending on a row line before a note delayed into that row now keeps its note-off: the delayed note takes another track. Two notes never share a row of a track.

**Piano roll**
- The window follows its clip through a delete and its undo (`layout.Current`). While the clip is gone or recording, it says so and doesn't edit.
- It closes when a song is opened or a new one started (`SessionLayout.Resetting`). Before, it kept the old song's undo and pattern alive.
- A click that wobbles a pixel is no longer a drag that snaps the note to the grid.
- Edits wait while the clip records: an overdub pass is undone by value when it ends, so an edit made under it would outlive that undo. `EditNotes` refuses them, as the moves do.

**Recording**
- *Capture while stopped* now takes the phrase only: notes after the last silence of a bar or more (no note held or played), within the last 8 bars. Before, a test note played a minute earlier started the clip, and the phrase fell past its end.
- *Native editor moves.* Only moves on the UI and host channels are recorded. A machine's MIDI handler moving a parameter (the MIDI channel) is caused by a note or CC that is recorded already.

**Paths 0.8 builds on**
- *Exact tempo.* A tick is a whole number of samples, and the fraction used to be dropped. At 44.1 kHz and 120 BPM a tick is 5,512.5 samples, so the engine ran 163 ms ahead of true time in 30 minutes. The fraction is now carried to the next tick, from 0 at each start, so playback is the same every time.
- *MIDI devices.* The device watcher's thread, the UI and the audio thread all used the device tables, and none of them took a lock. Each table now has its own lock (MIDI out's after `AudioLock`). Devices open and close outside the locks, so a send never waits for a driver. A disconnected device is closed, not just forgotten. Opening a device again closes the old handle first; before, the second open failed.
- *MIDI input.* Active sensing (0xFE) is dropped. Clock and other real-time bytes still reach machines, but no longer post work to the UI, which happened 48 times a second under MIDI clock.

**Tests.**
- `TrackerNotesTests`: 9 tests, adding the review's row-line case, the editor's range, uneven rows per beat, and notes taken off and put back one for one.
- `PianoRollTests`: undo keeping Pattern View edits; the piano roll following its clip through a delete and its undo; the phrase played while stopped.
- `SessionRecordingTests`: ticks keeping the exact tempo over 400 ticks at 120 BPM.

**Known, left for later.**
- MIDI devices are still keyed by their WinMM index, which shifts when devices are plugged in.
- The MIDI input's timestamps are still dropped. 0.8 stamps its clock input itself.

---

### 0.8 — Control surfaces & sync

**Features**
1. Controller scripts with LED feedback and a movable **session ring** (the "red box"): Novation Launchpad (Mini/X/Pro), Akai APC40/APC mini, and Ableton Push 2 in its generic MIDI mode.
2. Script API so new controllers can be added as small C# classes.
3. **Ableton Link**: tempo and phase sync with Live and other Link apps on the network.
4. MIDI clock out/in.

**Notes.** Link's SDK is GPLv2 (or commercial). Linking it into ReBuzz (MIT) means distributing the result under GPL terms, or running Link in a separate helper process. Decided: a helper process.

**Acceptance criteria.** Launchpad shows clip states with correct colours within one tick; two ReBuzz instances and Live stay beat-aligned via Link for 30 minutes (phase error < 1 ms).

#### 0.8a — Sync: Ableton Link, MIDI clock in and out

**Design.**

*Link in its own process.* `LinkHelper/` is a small C++ program, `ReBuzzLink.exe`, under GPL-2.0-or-later as Link is. It links Link 4.1.1, included as a git submodule. `build.ps1` builds it with CMake and puts it next to `ReBuzz.exe`, with its source and Link's licence in `LinkHelper\`. ReBuzz stays MIT: it starts the program and exchanges lines of text with it over standard input and output.
- ReBuzz sends commands: `enable`, `startstop`, `quantum`, `tempo`, `beat <beat> <µs>` and `play <0|1> <µs>`.
- The helper answers with `state <µs> <beat> <bpm> <peers> <playing> <enabled>`, a hundred times a second.
- It exits when ReBuzz closes its input, so it never outlives ReBuzz.
- Times are host microseconds. On Windows, Link's clock is `QueryPerformanceCounter`, the clock behind .NET's `Stopwatch` (`HostClock`), so a time means the same moment in both processes. (On Linux, where the pure tests run, Link uses `CLOCK_MONOTONIC_RAW` and `Stopwatch` `CLOCK_MONOTONIC`; they differ only by NTP's slewing.) Two helpers on one machine agreed within 10 µs.

*Following a clock.* WorkManager asks `SyncClock` how long each tick is.
- The audio clock is learnt against host time (`HostTimeFilter`, as in Link's examples). It is a least-squares line through the last 512 callbacks: the frame count at each callback's start against the time then. This averages out when callbacks run, and learns the device's own rate.
- A tick is heard when its first sample is rendered plus the output latency (0.4.1's estimate).
- While a clock is followed, each tick's length is set so the next tick starts, as heard, where the clock's bar phase says (`PhaseLock`). Bars come from the session's grid origin and beats per bar, which is the Link quantum.
  - Each tick aims at an absolute time, so whole-sample rounding never adds up.
  - A tick is at most 8% longer or shorter than the clock's own, so a large error closes smoothly: half a bar of 4/4 at 120 BPM in about 12.5 seconds.
  - Groove and the nudge give way while following.
- A start waits until the clock's phase is the session's at its start position (the session clock starts at the song position, its bars at 0), and the tick before it ends right then; the transport starts on that tick (`StartDue`). From a bar's start that is Link's next bar line, as in Live. MIDI clock's bars count from its Start (beat 0, the pulse after it) and its song positions. Its beat 0 comes one pulse after Start, nearly always sooner than a start can be heard, so a start up to a quarter beat late goes at once and the lock catches up; a later one waits for the bar.
- The session's own beat line, as heard, is published to MIDI clock out and the Session View (`SyncClock.Ours`, a sequence lock: no allocation, no lock on the audio thread).

*Link (`SessionSync`, `LinkClient`).*
- With peers, the session follows Link's exact tempo and bar phase. The song's whole-number BPM follows the tempo rounded (`SetBPMFromEngine`, not recorded); the ticks follow the exact tempo.
- Alone, Link follows the session. The session's tempo is sent when it changes by more than half a BPM, so a rounded tempo never echoes. While the transport plays, the session's beat is sent four times a second as a `beat` request, so peers that join find it in phase.
- The tempo is set before Link is enabled, so joining a session takes that session's tempo.
- Start/stop sync (Live's) shares play and stop with the peers that have it on. A start is sent with the time it was heard (it landed on Link's bar line), so peers start on that bar. ReBuzz acts on changes of Link's play state, so a start of its own waiting for the bar line isn't undone by a state line from before it.
- The BPM set from Link is never sent back (ReBuzz's 16-500 may have clamped it).
- A helper that stops talking (no state line for a second) isn't followed. A helper that exits turns Link off and says so.

*MIDI clock in (`MidiClockReceiver`).* When the session follows MIDI clock in, ReBuzz takes the real-time messages from any open MIDI input before routing them.
- A least-squares line through the last two beats of pulses gives the tempo and beat. Its axis is the pulse count, so Start and Song Position Pointer don't bend it, and a pause of half a second starts it over.
- Start projects beat 0 onto the next pulse and starts the transport. Continue starts it from the position. Stop stops it. While stopped, pulses keep the tempo but not the position. A Stop still on its way through the UI holds a Start or Continue behind it (a master relocating sends Stop, position, Continue).
- The song's whole-number BPM shows the clock's tempo.
- One clock source at a time: clock from two inputs would count twice.
- Link and MIDI clock in are one or the other.

*MIDI clock out (`MidiClockSender`, `MidiClockOutput`).* A thread of its own sends to the chosen MIDI outputs.
- Start goes 1 ms before beat 0. Then a pulse goes every 1/24 beat, at the time the session's beat line says, as heard.
- It sleeps on a high-resolution waitable timer and spins the last 0.6 ms. It looks at the beat line again on each wake, so tempo and phase changes are followed at once.
- Joining while playing sends the Song Position Pointer (the next 16th) and Continue; past the pointer's 16,383 16ths, Continue alone. When the beat line jumps by more than a beat (a restart elsewhere in the song), Stop, then the position and Continue from where it is. Stop goes when the transport stops.

*Sync menu* (⚙ › Sync).
- Ableton Link (with its peers), start and stop with Link peers, follow MIDI clock in, and send MIDI clock to each open MIDI output.
- These settings are the user's, kept in the registry.
- Beside the tempo, the Session View shows what is synced. Its tooltip says how far ahead of or behind the clock the session is.

**Tests.**
- `ReBuzz.SessionTests`:
  - `SyncTimeTests` (7). It includes the acceptance run, simulated: 30 minutes against a clock at 120.37 BPM, starting 1.3 beats out of phase, through ±1 ms callback jitter and a device clock 30 ppm slow, with a tempo change at 10 minutes. The worst error after locking was 210 µs.
  - `SyncClockTests` (5): without a source; a start landing on the clock's bar line within a sample; a start from the middle of a bar waiting for the clock to be there too; a start a little late for MIDI clock's beat 0 catching up; following at 127.3 BPM within 1 ms.
  - `MidiClockTests` (9): tempo through jitter; Start making the next pulse beat 0; the song position; pulses while stopped keeping it; sending Start then a pulse every 1/24 beat; joining while playing; a jump of the beat line; past the song position's range; the Link protocol's lines.
- `ReBuzzTests/SessionSyncTests.cs` runs on CI with the helper `build.ps1` built:
  - two Link helpers finding each other and agreeing within 1 ms;
  - the session following a Link peer at 123.4 BPM, rendering in real time: its tempo, the song's BPM (123), and its bar phase within 1 ms;
  - MIDI clock in starting and stopping the transport;
  - MIDI clock out timing on a real thread.

  Where the runner allows no multicast, the Link tests report inconclusive rather than failing.

**Known.** A restart while playing and following (F5 at a song position off the bar) isn't re-aligned like a start: the lock pulls the session back into phase over a few seconds.

**Not automated.** A 30-minute run against Live (the simulation and the real-time test stand in). Real audio devices' latency reports, since the latency is 0.4.1's estimate.

#### 0.8b — Control surfaces

**What it does.** ⚙ › Controllers lists the controllers found among the MIDI ports, each with a checkbox.
- Turned on, a controller gets its ports to itself, and a session ring: a red box in the Session View round the slots its grid plays.
- Its pads launch the clips in the ring. An empty slot of an armed track records a take; any other empty slot stops its track. Releasing a pad releases a Gate or Repeat clip.
- Its scene buttons launch scenes. Its arrows move the ring, a page at a time with shift.
- Its lights follow the session (Live's conventions):
  - clips: their colour;
  - playing: pulsing green;
  - waiting: their colour flashing green;
  - recording: pulsing red;
  - an armed track's empty slots: dim red;
  - arrows: lit where the ring can go.
- Controllers turned on are remembered, and connected whenever their ports appear.
- Turned off (or when ReBuzz quits), a controller's lights go out and it gets its own mode back.

| Controller | Ports | Grid and buttons |
|---|---|---|
| Launchpad Mini MK3, X | "LPMiniMK3 MIDI", "LPX MIDI" (not DAW) | Programmer mode. Pads: notes 11–88. Scenes: CCs 89…19. ▲▼◀▶: 91–94. Session (95) is shift: shift with a pad stops its track, with a scene stops all. |
| Launchpad Pro MK3 | "LPProMK3 MIDI" | As above. Shift is 90, ◀▶ are 91–92 and ▲▼ are 80 and 70. The bottom row (1–8) stops tracks; Play is 20. The left and bottom rows' numbers aren't in Novation's manual's text, so check them on hardware. |
| APC mini mk2 | "APC mini mk2" (not MIDIIN2) | Pads: notes 0–63. The bottom row stops tracks, or with Shift moves the ring (104–107). The right column launches scenes, or with Shift stops all. Pad lights take a channel for brightness or animation. |
| APC40 mkII | "APC40 mkII" | Ableton Live mode (introduction SysEx). A 5 × 8 grid (notes 0–39, top row 32–39). Scenes: 82–86. Clip Stop (52) and Record Arm (48) are on the track's channel. Also Stop All, bank arrows, Shift, Play and Session Record. |
| Push 2 | User port (MIDIIN2/MIDIOUT2) | User mode. Pads: notes 36–99. Scenes: CCs 43–36. Below the display (20–27) stops tracks; above it (102–109) arms them. Also arrows, Shift, Play, Record (Session Record) and Stop Clip (stop all). The shared palette's entries are written into its palette when it connects. |

**Design.**

*Scripts* (`ReBuzz.Control`, pure and public). A script says:
- which ports are its (`MatchInput`, `MatchOutput`: a score);
- what to send when it connects and leaves (`Hello`, `Goodbye`);
- what a MIDI message means (`Read`: a `ControlEvent`, such as Pad (x, y), Scene, TrackStop, TrackArm, StopAll, the arrows, Play, SessionRecord or Shift);
- what its lights show for a state of the session (`Render`: a `SurfaceState` in, a `LedFrame` of messages out).

`PaletteScript` covers controllers that take a palette colour as velocity, with a channel for still, pulsing or blinking. `ControllerPalette` holds the palette Novation and Akai share: greys, then families of four (light, full, dim, dark) round the colour wheel from red at 4. Clip colours go to the nearest family.

*The manager* (`SessionControllers`).
- It finds controllers by matching every script against the MIDI port names. It opens a controller's ports itself, with NAudio, apart from ReBuzz's MIDI settings.
- *Presses* are read on the driver's thread. Launches, stops and play go straight to the session engine through the layout's launch map, as mapped MIDI does (`SessionMidi.ExecuteNow`, 0.4.1), so a busy UI doesn't delay them. Arming, recording, Session Record and moving the ring are posted to the UI.
- *Lights.* Every 20 ms (a tick at 120 BPM is 125 ms), the UI thread fills each controller's `SurfaceState` from the layout and the engine. The script renders it, and only the lights that changed are sent.
- A controller that stops answering is let go and connected again when its ports come back. Every 2 s one light is sent again, so an unplugged controller is noticed even while nothing changes.
- A pad's release goes to the slot its press played, even if the ring moved or shift was pressed in between. Pads outside the session do nothing.
- A script that throws (matching, saying hello, lighting) is let go and logged, never taking ReBuzz down. Let go, every light it was sent is turned off before its own mode comes back.

*New controllers.* Any class deriving from `ControllerScript` in a .dll in the `Controllers` folder next to `NeoBuzzLive.exe` (the studio: next to its executable) is loaded at start. Build it against `ReBuzz.dll`. For example, a 4 × 4 pad box:

```csharp
using ReBuzz.Control;

public sealed class MyPadsScript : ControllerScript
{
    public override string Name => "My pads";
    public override int Width => 4;
    public override int Height => 4;
    public override int MatchInput(string port) => Has(port, "My Pads") ? 10 : 0;
    public override int MatchOutput(string port) => MatchInput(port);

    public override ControlEvent Read(int message, bool shift)
    {
        int n = Data1(message) - 36;                         // pads: notes 36-51, from the bottom left
        if (!IsNote(message) || n < 0 || n >= 16) return ControlEvent.None;
        return new ControlEvent(ControlKind.Pad, n % 4, 3 - n / 4, IsPress(message), Data2(message));
    }

    public override void Render(SurfaceState s, LedFrame leds)
    {
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                int note = 36 + (3 - y) * 4 + x;
                int value = s.Pad(x, y) switch { PadState.Playing => 127, PadState.Stopped => 20, _ => 0 };
                leds.Set(note, Note(0, note, value));
            }
    }
}
```

**Tests.**
- `ReBuzz.SessionTests/ControllerTests.cs` (8): the palette; only changed lights sent; ports matched; each built-in script's messages for its grid, buttons, lights and SysEx, against the published protocols.
- `ReBuzzTests/SessionControllerTests.cs` (4), through a fake port:
  - a Launchpad launching the clips in its ring, its lights following (waiting, playing), the ring moving down, and shift stopping a track;
  - an APC40 arming a track and recording into an armed empty slot;
  - pads outside the session doing nothing, and a release going where its press went;
  - scripts picking their ports among many.

**Not automated.** The real devices. The scripts follow the makers' documents but haven't met the hardware.

---

### 1.0 — Audio clips (feature parity)

**Features**
1. Audio clips: drag a WAV/AIFF/FLAC onto a slot. Playback through a per-track audio player machine (build on `AudioBlock`, or a new lightweight `ClipPlayer` machine).
2. **Warping**: Repitch first (cheap), then time-stretch modes (Beats, Complex) via a library — SoundTouch (LGPL) is licence-compatible; Rubber Band is GPL/commercial.
3. Warp markers, auto BPM detection, clip gain/pitch/transpose.
4. Audio recording into slots from an input (`ReBuzzAudioIn`).
5. Consolidate a recorded performance to audio.

**Acceptance criteria.** A loop at 100 BPM plays in time at 80–160 BPM in Beats mode; the Live parity matrix (§6) is all ✅ except the listed non-goals.

**Decisions.** A new machine, the **Session Clip Player**, rather than AudioBlock (an arrangement editor of its own, with its own timeline). The warping is our own code (`ReBuzz/Core/Clips/`): no library, so no licence question, and it runs on the audio thread without allocating. Consolidating is done Live's way in the Session View: by **resampling**, recording the master into an audio track's slot.

#### What it does

*Audio tracks.* An audio track is a track of a Session Clip Player (`Machines/SessionClipPlayer`, in Gear\Effects). "+ Track" › New audio track makes one, connected to the master.
- Dropping audio files on a slot puts them there as audio clips (several go down the track). On a track that isn't an audio track, or anywhere else in the view, a new audio track gets them, named after the file. An empty slot's menu has Import audio… too.
- The file is loaded into a free wave of the song's wavetable (the song keeps it, as it keeps any wave). The clip is a pattern of the player, as long as the audio in beats; its slot shows ∿.
- The clip's tempo is found from the audio (`AudioAnalysis.Tempo`). A loop is a whole number of bars, which puts it in 80–160 BPM exactly; that is taken when the tempo of its transients agrees within 4% (or they have none clear). Otherwise, the transients' tempo.
- Audio clips launch, stop, loop, follow and record into the arrangement as any clip does; the launch settings (region, legato, follow actions…) apply. Clips in the arrangement play too.

*The clip panel's Audio section.*
- Warp mode (Live's): **Beats** (the default: drums, loops), **Complex** (any material), **Re-Pitch** (a turntable), **Off** (its own speed from the clip's start).
- What Beats keeps: the transients (and a beat grid where they are further apart than a beat), or a 1/4, 1/8 or 1/16 grid.
- Tempo, with ÷2, ×2 and Detect. Without warp markers the clip's length follows it: 4 beats at 120 BPM are 8 at 60.
- Gain (dB), transpose (semitones) and detune (cents).
- Warp markers…: the warp marker editor.
- Each change is one undo step of the session.

*Warp markers* (the editor, Live's). The audio drawn along the clip's beats as it plays, with its transients, the playing position and the bar grid. A marker pins a moment of the audio to a beat; the audio between two markers is stretched to fit.
- Double-click adds one where the audio already is (nothing moves until it is dragged).
- Dragging moves it along the beats (snapped to 1/16 beat; Shift for none), between its neighbours.
- Right-click removes one; Clear markers goes back to straight, at the clip's tempo.
- The clip's length follows its last marker.

*Recording* (Live's). Arm an audio track, connect what it records to it (ReBuzz Audio In, or any machine), and record a take: its empty slots' ●, ◉ Session record, or a controller's pad.
- A take is a new audio clip, launched on the grid (from a stopped transport, after the count-in). It records the record length, or until stopped and then whole bars (it records on to the end of the last bar).
- A take of a hardware input (ReBuzz Audio In, through any machines) starts a round trip after its launch (the output latency, when launches are timed to what is heard), so it starts where it was played. The master and the engine's own machines are in time with what it renders: no round trip.
- A take whose launch came late (the grid point heard was already rendered) starts into its pattern; the frames it missed stay silent, so its audio keeps the pattern's time.
- When the take is done its audio goes into a wave of the song, at the tempo it was recorded at, and the clip plays it from its next pass. The take is one undo step.
- Monitor (a parameter of the player): **auto** hears the input while the track is armed, **in** always, **off** never.
- Session record never overdubs audio: an armed audio track playing a clip records a new take in the next empty slot.

*Consolidating: resampling.* "+ Track" › New resampling track makes an audio track whose takes record the master output (its Input parameter: in or master; the track's menu switches it), armed. Record a take on it while the session plays: what was heard becomes an audio clip.

#### Design

*The machine.* Managed machines are separate DLLs loaded with `Assembly.LoadFile`, so the player can't live in ReBuzz.dll. Its Work hands the block to ReBuzz through a static hook in BuzzGUI.Common (`SessionClipHost`). `SessionClipEngine` renders, on the audio thread, every pattern of the machine that plays in the block (`SongCore.PositionedPatterns`: the session's clips and the arrangement's). Its beat is exact: the tick of its play position plus the place in the tick (`PosInTick / SamplesPerTick`), over ticks per beat.

*How a clip plays.* The layout gives each audio clip's pattern a `ClipPlayback` after every edit (with the launch map) and whenever a wave changes. It holds how the clip plays now (`AudioClipParams`: the audio, read once from the wave; the warp map; Beats' slices; gain; pitch) and its `ClipVoice`. The parameters are swapped whole; the audio thread never sees them half made.

*The voice* renders a block from the beat it starts on and how fast beats go.
- Repitch reads the audio where the warp map puts each beat (cubic Hermite).
- Off reads it at its own speed from where it started.
- Beats plays the slice a beat falls in from its start, at the audio's own speed times the transposition, so each attack lands on its beat whatever the tempo. A slice that runs out before the next one starts loops its last 80 ms or less, crossfaded where it goes round. At a new slice the last fades out under it.
- Complex is a phase vocoder (`PhaseVocoder`): frames of 2048 every 512 samples. Each frame reads the audio around where the warp map puts that moment, and measures each bin's frequency from two analysis frames a hop apart, so any stretch keeps the pitch. Phases are locked to their nearest spectral peak (Laroche and Dolson's identity locking). Both channels follow the phase of their sum. Transposing (and another sample rate) moves each peak, with the bins round it, to its frequency times the ratio.
- A block that doesn't go on from the last (a launch, a loop going round, new settings) starts again there. What sounded fades out over 64 samples (1.5 ms) as the new start fades in, so nothing clicks. A clip that stops fades out the same way.
- Nothing allocates after construction.

*Analysis* (`AudioAnalysis`, when a clip is made, not on the audio thread). Transients are peaks of spectral flux (log magnitudes) over a running median. Each is placed on the steepest rise of the waveform's energy near it, 2 ms early so its attack is kept whole.

*Recording.* A take's audio goes into an `AudioCapture`: chunks of 65,536 frames that the UI thread makes ahead of it (8 seconds ahead, polling as MIDI takes do). The audio thread writes into them and never allocates or waits. A take of the input records in the player's Work. A take of the master records in WorkManager's master tap, after the mix and before the metronome (heard, never recorded). The take's wave slot is kept with a frame of silence until its audio fills it.

**Tests.**
- `ReBuzz.SessionTests/AudioClipTests.cs` (37):
  - warp maps both ways; reading between samples; the FFT;
  - transients at each click; the tempo of 1, 2 and 4-bar loops and of audio that isn't bars long; slices filling the beat grid;
  - **the acceptance test**: a loop of clicks at 100 BPM played in Beats mode at 80, 100, 120 and 160 BPM, every click within 5 ms of its beat and its pitch kept;
  - Repitch faster and higher; Off at its own speed; Complex keeping 440 Hz at 80 and 160 BPM, transposing ±12 semitones, at its level, and resampling 48 kHz audio;
  - blocks of any size playing the same in every mode; a loop going round without a click in every mode; stopping fading out; nothing allocated while playing;
  - the mixer fading out what stops; audio settings through XML; captures skipping the round trip, keeping their length, needing their chunks ahead, and allocating nothing.
- `ReBuzzTests/SessionAudioTests.cs` (13), with the real Session Clip Player compiled from its source:
  - a loop at 100 BPM in time at 80, 120 and 160 BPM through the audio loop;
  - stopping without a click and falling silent; clip settings (gain, tempo and the length following it, undo);
  - Monitor (auto, in, off);
  - songs keeping their audio clips (.bmx and .bmxml);
  - dropping a WAV file: a new audio track at its tempo, in its slot, one undo step (inconclusive where libsndfile can't load);
  - four clips in every warp mode playing without allocating;
  - a take recording an armed track's input and playing it from its next pass; a take until stopped keeping whole bars; a resampling track recording the master.

**Known.**
- A hardware take's round trip is taken as the output latency; the input's own latency isn't known.
- A song imported into another (rather than opened) can move its waves to other slots; its audio clips keep the old numbers.
- Complex's transients are softer than Beats' (as Live's are), and softer again where a loop goes round (its frames overlap there rather than starting fresh); its transposition moves partials whole (no formant keeping). Launching many Complex clips in one block computes four frames each at once.
- Changing ticks per beat changes audio clips' length in beats, as it does every pattern's; their tempo (×2, ÷2, Detect) sets it again.
- The tempo found for a one-bar loop faster than 160 BPM is half its own (×2 puts it right).
- Undoing a dropped file takes its clip away; a new audio track made for it and the wave it was loaded into stay (the song's undo removes the track).

#### 1.0.1 — Hardening (an independent review of 1.0)

- A loop a sample longer than its bars (as exporters round) was a tick longer, drifting against the bars: the tempo found is kept exact, and a length within a fiftieth of a tick is that tick.
- Session undo of a clip's length (its tempo changed) after the audio track was renamed threw, and every later undo with it: the length is an undo step that holds the pattern, as the session's others do.
- A take launched late recorded its audio from the pattern's start, a tick or more early: it starts into its pattern now. Resampling, and machines the engine plays, no longer drop the output latency's worth of their start.
- A new take on a track whose take recorded until stopped was finishing lost that take's audio: it keeps what it has.
- The first time a Beats slice ran out it jumped back without the crossfade, and a new slice's first sample missed the old one's fade: both are smooth now. Steady sound no longer counts as transients (leakage far under what sounds was read as change).
- Complex no longer computes a fresh start at every loop wrap (four frames in one block, for every clip looping in step).
- Dropping files no longer holds up the program they came from, and decoding a file no longer holds the audio lock (the wavetable's own load does); files are read at most 30 minutes long.
- An edit of a wave in place (normalize, fade) reaches its clips.
- A muted audio track's take keeps its time (silence while the machine doesn't work).
- Launching the take being recorded ends its recording, as a MIDI take's does.
- A gain change kept a length set by hand no longer; the clip panel writes only the boxes typed in (rounded values aren't written back). Off mode goes on through a change of gain or pitch rather than jumping to where the beat is. Re-Pitch shows no transposition (its tempo is its pitch).
- Wave slots audio clips play aren't reused by imports and takes; a take stopped before it began records nothing; a take whose track went keeps what it had.
- Complex mode's vocoder is made only for clips that use it.
- Tests: `AudioClipTests` (+4): a late take starting into its pattern, a Beats slice running out without a click, Off going on through a gain change, steady sound without transients. `SessionAudioTests` (+6): a loop a sample longer than its bars, undo after a rename, a gain change keeping a hand-set length, a late take in the pattern's time, a new take keeping the one finishing, a wave edited in place. CI's totals name tests that neither passed nor failed, and the tests get libsndfile (the import test ran inconclusive without it).

**Not automated.** How it sounds: the measures above stand in for listening. Real audio inputs. Drag and drop from Explorer, and the warp marker editor's mouse handling.

## 6. Parity matrix

| Ableton Session View feature | Phase | Status |
|---|---|---|
| Clip grid, scenes, clip/scene launch | 0.1 | ✅ |
| Global launch quantization | 0.1 | ✅ |
| Track stop buttons, Stop All Clips | 0.1 | ✅ |
| One clip per track, scene launches atomically | 0.1 | ✅ |
| Arrangement override | 0.1 (auto-clear on stop), 0.3 (Back to Arrangement) | ✅ |
| Keyboard/MIDI mapping of launch controls | 0.2 | ✅ |
| Create/duplicate/colour/rename clips, edit in clip view | 0.2 (editing via pattern editor) | ✅ |
| Insert/duplicate scenes, drag clips | 0.2 | ✅ |
| Record session into arrangement | 0.3 | ✅ |
| Session/Arrangement toggle | 0.3 | ✅ |
| Mixer section, sends/returns, group tracks | 0.4 | ✅ |
| Clip launch modes, per-clip quantize, legato | 0.5 | ✅ |
| Clip loop/start markers | 0.5 | ✅ |
| Scene tempo / time signature | 0.5 | ✅ |
| Follow actions (clip and scene) | 0.6 | ✅ |
| Crossfader | 0.6 | ✅ |
| Session record, overdub, fixed length, Capture MIDI | 0.7 | ✅ |
| Metronome, count-in | 0.7 | ✅ |
| Controller grid with session ring | 0.8 | ✅ |
| Ableton Link | 0.8 | ✅ |
| Audio clips, warping, audio recording | 1.0 | ✅ |
| Warp markers, tempo detection, clip gain and transpose | 1.0 | ✅ |
| Resampling (consolidating a performance to audio) | 1.0 | ✅ |
| Max for Live, Live devices, Groove Pool | Non-goal | Non-goal |

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
