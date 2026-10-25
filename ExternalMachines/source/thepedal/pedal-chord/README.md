# Pedal Chord v1.6.0 — ReBuzz Managed Controller Machine

A chord and arpeggio trigger for ReBuzz. Write a root note in the pattern and
Pedal Chord fires the full chord (or arpeggiated notes) on any target generator
in your song. One instance controls one generator — use multiple instances to
control multiple generators simultaneously.

---

## Quick start

1. **Add to machine view** — drag from the Generators tab.
2. **Connect a target** — right-click → *Target Settings…* Select the generator
   and base track to use.
3. **Write notes** — open a pattern and enter root notes in the Note column.
4. **Press Play** — the chord fires on the target on every row that has a note.

The target machine must be connected to the Master (or an effect chain reaching
Master) so its audio is heard.

---

## Track parameters

| Parameter    | Range    | Description |
|--------------|----------|-------------|
| **Note**     | C-0–B-9  | Root note. Standard Buzz piano keyboard. |
| **Velocity** | 1–127    | Note velocity sent to the target machine. |
| **Chord**    | 0–50     | Chord type. Right-click → *Chord Reference…* for full list with hex values. |
| **Mode**     | 0–5      | Chord / Arp Up / Arp Down / Arp Up+Down / Arp Down+Up / Arp Random |
| **Speed**    | 1–1024   | Pattern ticks between arpeggio steps (1 tick = 1 pattern row). |
| **Length**   | 0–16384  | Note duration in ticks. 0 = no auto note-off. |
| **Octaves**  | 1–4      | Octave range for Oct Walk; or pre-expanded span when Oct Walk is Off. |
| **Step**     | 1–8      | Chord tones advanced per arp step. |
| **Oct Walk** | 0–2      | Off / Up / Ping-pong — octave cycling after each chord cycle. |
| **Swing**    | 0–100    | Shuffle. 0 = straight, 100 ≈ 2:1 triplet shuffle. |
| **Swing On** | 0–1      | Which beat gets the long wait (0 = 1st, 1 = 2nd). |
| **Humanize** | 0–100    | Random ±timing drift per arp step, scales with Speed. |
| **Hum. Vel** | 0–100    | Random ±velocity variation per arp step, scales with Velocity. |
| **Arp Reset**| 0–1      | Write 1 to restart the arp sequence from the first note on this step. |

---

## Speed and timing

Speed is in **pattern ticks** (rows) — exact regardless of BPM or buffer size.

---

## Swing

Long and short waits alternate, summing to exactly `2 × Speed` ticks per pair,
so average tempo is locked at all swing values and every tempo:
```
span       = 2 × Speed                       (ticks)
longTicks  = span × ratio / (ratio + 1)      (ratio = 1 + Swing/100)
shortTicks = span − longTicks
```
The step clock is measured in fractional ticks (`PosInTick / SamplesPerTick`),
so timing is tempo-locked by construction and independent of ReBuzz's **Sub-Tick
Timing** setting. Placement resolution is the audio chunk, so swing is smooth
even at low **Speed** (e.g. Speed 2). Swing=0 gives even spacing (long = short).
No parameter to set — it just tracks the song position. (Earlier versions placed
steps by counting host sub-tick edges; that drifted when Sub-Tick Timing was off
or the tempo changed — see the v1.6.0 changelog.)

---

## Step and Oct Walk

**Step** advances the arp index by N chord tones per tick rather than 1.

**Oct Walk** cycles the octave independently after each full chord cycle.
**Octaves** sets the range when Oct Walk is active.

---

## Arpeggio modes

| Value | Name         | Pattern |
|-------|--------------|---------|
| 0     | Chord        | All notes simultaneously |
| 1     | Arp Up       | Low → High, wraps |
| 2     | Arp Down     | High → Low, wraps |
| 3     | Arp Up+Down  | Ping-pong, starts ascending |
| 4     | Arp Down+Up  | Ping-pong, starts descending |
| 5     | Arp Random   | Random note each step |

Arp mode fires on a single target track. Chord mode uses consecutive tracks
(Base, Base+1… one per chord tone).

---

## Build from source

Requirements: .NET 10 SDK, ReBuzz at `C:\Program Files\ReBuzz`.
Built and tested against **ReBuzz 1827-preview**.

```powershell
dotnet build PedalChord.csproj -c Release
dotnet build PedalChord.csproj -c Release /p:BuzzDir="D:\ReBuzz"
```

Output: `<BuzzDir>\Gear\Generators\Pedal Chord.NET.dll`

---

## Changelog

### v1.6.0
- **Tempo-locked step clock (fixes tempo/sub-tick drift).** The arp step clock
  now advances by the musical fraction `PosInTick / SamplesPerTick` instead of
  counting host sub-tick edges. This is tempo-independent by construction and
  works the same whether ReBuzz Sub-Tick Timing is on or off. The previous
  scheme keyed off `SubTicksPerTick`/`CurrentSubTick`, but ReBuzz keeps
  reporting `SubTicksPerTick > 1` and advancing `CurrentSubTick` even when
  Sub-Tick Timing is **off** — only the alignment that makes those edges land
  `R` per tick is tied to the setting. So with Sub-Tick Timing off the edge
  count no longer matched `R` and the arp drifted (tempo-dependently); changing
  tempo or toggling the setting threw the timing. The fraction clock removes
  that dependency entirely. Swing still splits a 2-step span exactly, with
  smooth placement at low Speed (resolution = the audio chunk), so this
  supersedes the v1.5.5 sub-tick mechanism without losing fine swing — and the
  v1.5.6 re-seed behaviour is preserved (a re-seed re-anchors cleanly to the
  grid). Regression test: `tools/timing_test.py`.

### v1.5.6
- **Re-seed timing fix.** When the arp was re-seeded mid-stream by a NoteOn
  (a fresh root every bar) the first gap after the seed could come out one
  (sub)tick short — `Speed − 1` instead of `Speed` at Swing 0. A held note
  that free-ran was always correct, so a one-chord test missed it. The seed
  step and the per-step countdown could both consume the same step edge; a
  per-edge guard now makes them mutually exclusive, so a re-seeded arp plays
  the same gaps as a free-running one. Applies to the explicit Arp Reset
  entry point too. No change to free-run timing, Swing, sub-tick behaviour,
  or any parameter. Regression test added (`tools/reseed_timing_test.py`).

### v1.5.5
- **Sub-tick swing.** When ReBuzz Sub-Tick Timing is enabled, the arp step
  clock advances per sub-tick instead of per tick, giving `R×` finer swing and
  humanize placement (R = SubTicksPerTick). Fixes the low-Speed granularity
  limit — swing is now smooth at Speed 2–3, not just Speed 4+. Tempo stays
  locked (long + short = period exactly); Swing=0 is unchanged; with Sub-Tick
  Timing off, behaviour is identical to v1.5.4. No new parameter. Note-off
  (Length) timing stays on the tick clock.

### v1.5.4
- Rebuilt and retested against **ReBuzz 1827-preview**. No behavioural change.
- Verified clean on 1827: tick-boundary detection (`MasterInfo.PosInTick`) is
  unaffected by SubTickTiming — `PosInTick` stays tick-relative and resets only
  at the tick boundary, so the arp still advances exactly once per tick.
- Unaffected by the 1827 `pvalues` field-shape change (single-voice since v1.5,
  no multi-track reflection poll) and by the MasterTap GUI-thread event change
  (Pedal Chord doesn't hook MasterTap).

### v1.5.2
- Fixed: target assignments not restored on song load. `Song.Machines` is not
  fully populated when `MachineState` is first set during load. Now retries
  resolution via `DispatcherTimer` with increasing delays until the target
  machine appears (up to 10 attempts over ~4 seconds).

### v1.5.1
- Fixed: freeze/crash when using 6+ instances simultaneously. `EnsureTrackCount`
  was being called from the audio thread on 64-bit native machines (e.g. Infector),
  triggering cross-process IPC that deadlocked. Now runs only in `ResolveCache`
  on the UI thread, pre-expanding track count at assignment time.
- Fixed: `_buildBuf` scratch array made per-instance (was incorrectly `static`).

### v1.5
- **Architecture simplified to single-voice/single-target** — one Pedal Chord
  instance controls one generator. For multiple generators, use multiple
  instances. Eliminates all multi-voice complexity and instability.
- All reflection hacks (`pvalues`, `VoiceCache`, `TryInitPValues`) removed.
- `MaxTracks = 1` — clean single pattern track.
- Target settings simplified: one machine name + base track.
- Performance: target machine + parameters cached, invalidated on settings
  change and every 100 ticks.

### v1.4
- **Step** (1–8), **Oct Walk** (Off/Up/Ping-pong)
- Multi-voice simultaneous trigger fix (superseded by v1.5 simplification)
- Length 0–16384 (0 = sustain)

### v1.3
- **Swing**, **Swing On**
- Fixed Arp Random (Mode=5)
- Chord Reference shows hex values

### v1.2
- **Velocity**, **Humanize**, **Hum. Vel**, **Arp Reset**

### v1.1
- Swing timing (superseded)

### v1.0
- Initial release: 51 chords, 6 arp modes

---

## Architecture notes

Control machine (`void Work()` with no parameters) — ticked first each audio
buffer. Timing is tick-accurate via `MasterInfo.PosInTick`.

Target machine and note parameter are cached after first use. Cache invalidates
on settings change and every 100 ticks. `IBuzzMachine.Tick()` is not called for
managed machines — all logic is in `Work()`.
