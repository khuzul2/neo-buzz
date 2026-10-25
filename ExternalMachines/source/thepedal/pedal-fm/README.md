# Pedal FM v1.2

A 4-operator FM generator for ReBuzz, designed to be driven by Pedal Chord
but works fine standalone or from a regular pattern.

## What's new in v1.2

- **Glide / portamento** — sticky legato glide with quadratic curve, max
  ~1 second over 12 semitones. Engages only when the voice is already
  sounding (legato semantics).
- **LFO** — one free-running global LFO with rate, shape (sine, triangle,
  square, saw, S&H), pitch depth (vibrato up to ±1 semitone), and level
  depth (tremolo + timbre wobble up to ±50%).
- **Per-op velocity sensitivity** — four globals, one per op. High values
  on a modulator op make harder hits brighter; on a carrier they make
  velocity affect amplitude more strongly. Folded into op.Level at
  trigger time so it costs zero per-sample.
- **Control-rate sub-buffering** — when glide or pitch LFO is active, the
  render loop sub-divides into 32-sample blocks and recomputes phase
  increments per block. Smooth glides without per-sample MathF.Pow. When
  pitch is static (the common case), the sub-block layer collapses to a
  single pass and is identical to the v1.1 fast path.
- **Operator config refactored** — `RatioWithDetune` cached on the op so
  the control-rate Inc update is one mul + cast per op, no Pow per block.

## What's new in v1.1

- **Per-track Velocity** — sticky velocity column. Folded into the anti-click
  envelope so it costs zero per-sample.
- **Op1 Feedback** — DX7-style self-feedback on the first operator. Quadratic
  curve, 2-sample averaging on the feedback path. Adds saw-like character
  that pure forward-FM can't reach.
- **Compound Mod** — exposes the chained-modulator scale (was hardcoded to
  0.25 in v1.0). Default 32/127 ≈ 0.25 so existing presets are unchanged.
  Crank it higher to recover classic noisy DX behaviour.
- **Hand-unrolled 4-op render loop** — eliminates per-sample array indexing,
  hoists routing booleans out of the inner loop, and skips the SineTable
  lookup for ops with `Level == 0` (Sub Bass, Lead Hollow etc. now do less
  per-sample work).
- **`AggressiveInlining`** on `SineTable.Lookup` and `Envelope.Process` for
  good measure.

## Design

- **4 operators per voice** — DX21/TX81Z lineage. Enough for rich timbres
  (chord stabs, EPs, pads, brass, bells); much faster to finish than 6-op.
- **8 algorithms**, all forward-pass (no feedback in v1). Linear chain, dual
  chain, branched, stacks, mixed, additive. See `Algorithms.cs`.
- **16 voices, one per pattern track.** Fits the Pedal Chord use case where
  each chord note has its own track. No cross-track stealing — voice index
  = track index. Cap is configurable (`Voices` parameter).
- **Sine-only operators** (8192-entry LUT with linear interp). Harmonic
  richness comes from the modulation chain itself; non-sine shapes are a
  TX81Z-style extension for later.
- **Anti-click fade-cross retrigger** (PedalTracker §4 pattern). When a new
  note arrives on a still-sounding voice, gain ramps to silence (~1.5 ms)
  then re-attacks inline. Inaudible click, audible attack restart.

## File layout

| File                   | Purpose |
|------------------------|---------|
| `PedalFM.csproj`       | Build config; deploys `.dll` to ReBuzz Generators |
| `SineTable.cs`         | Static 8192-entry sine LUT |
| `Algorithms.cs`        | 8-algorithm modulator/carrier topology table |
| `Envelope.cs`          | Per-op ADSR (linear A, exp D/R, level S) |
| `Operator.cs`          | Phase accumulator + envelope + level |
| `Voice.cs`             | 4-op render loop, anti-click envelope, deferred-trigger |
| `PedalFMMachine.cs`    | `IBuzzMachine`, parameters, multi-track note polling, Work |

## Parameters

### Per-track (pattern column)
- **Note** — z=C-4, s=C#-4 etc. (standard Buzz piano grid)
- **Velocity** (0–127, sticky) — column entry persists for subsequent notes;
  default 127 = unity gain

### Global (rack)
- **Algorithm** (1–8) — modulation topology
- **Output** (0–127) — master level
- **Voices** (1–16) — voice cap
- **Op1 Feedback** (0–127, default 0) — self-feedback on Op1
- **Compound Mod** (0–127, default 32) — strength of compound-modulator path
- **Glide** (0–127, default 0) — legato portamento time
- **LFO Rate** (0–127, default 60) — LFO speed, log scale ~0.05 to ~30 Hz
- **LFO Shape** (0–4, default 0) — Sine/Tri/Square/Saw/S&H
- **LFO Pitch** (0–127, default 0) — vibrato depth, max ±1 semitone
- **LFO Mod** (0–127, default 0) — tremolo / timbre wobble depth
- **Op1–4 VelSens** (0–127, default 0) — per-op velocity sensitivity

### Per operator (×4: Op1, Op2, Op3, Op4)
- **Ratio** (0–127) — frequency multiplier in 0.125 steps. Value 7 = 1.0,
  15 = 2.0, 31 = 4.0, 127 = 16.0.
- **Detune** (0–100, centred at 50) — ±50 cents fine adjustment
- **Level** (0–127) — operator output gain (linear). For a modulator,
  this controls FM depth; for a carrier, audio level.
- **Attack** (0–127) — 0 to 4 s, x² curve
- **Decay** (0–127) — 0 to 8 s, x² curve
- **Sustain** (0–127) — 0 to 1.0 (level, not rate)
- **Release** (0–127) — 0 to 8 s, x² curve

The default patch (Algorithm 1, linear chain) is a generic EP-ish bell.
For a punchy chord stab, try **Algorithm 4** (3-on-1) with all three
modulator levels around 60–90 and short decays (~30).

## Notes on the implementation

### Multi-track chord delivery
Pedal Chord fires multiple notes simultaneously across tracks. ReBuzz's
`parametersChanged` only retains the last writer per parameter, which would
silently drop all but one chord note. `SetNote` polls `pvalues` for sibling
tracks (Core §14) so every note in the chord arrives intact.

### Voice-per-track
With one voice per track and `MaxTracks=16`, the machine auto-scales to
the chord size Pedal Chord is firing. Track 0 always owns voice 0, etc.
If `Voices` is set lower than the chord size, tracks beyond the cap wrap
modulo the cap and steal each other (with anti-click).

### Audio scale
Output is written at ±32768 (PedalComp §1). Internal DSP is at ±1.0.

### Threading
- `Work` runs on the audio thread.
- All parameter setters run on the audio thread (via `Tick()`).
- No UI thread interactions in v1 (no GUI yet).
- No `Song.Machines` access from the audio thread.

## Known v1.2 limitations / future work

1. **Op feedback only on Op1** — DX7 has per-algorithm feedback positions.
   For our forward-pass renderer, Op1 is the natural choice.
2. **Op fixed-frequency mode** — placeholder field exists but no parameter
   exposes it. Useful for inharmonic bell tones.
3. **GUI** — none. Algorithm diagram + per-op envelope/ratio readouts
   would make patching much easier.
4. **One LFO** — second LFO (e.g. slow filter-style mod + fast vibrato
   simultaneously) would expand expression but doubles the param count.
5. **LFO Delay** — no fade-in time; LFO is at full depth from the start.
6. **LFO is buffer-rate** — pitch LFO updates per 32-sample block; level
   LFO updates per buffer (~5 ms). At very fast LFO rates (>20 Hz) the
   level path can sound stepped.
7. **Sub-tick timing** — notes are applied once at the start of each
   `Work()` buffer (typically a few ms quantisation).
8. **One-buffer velocity-sensitivity lag on retrigger** — when a deferred
   trigger fires inline mid-buffer, op.Level still reflects the *previous*
   note's velocity until the next `Work()` call. Audible only as a
   ~few-ms transient at low gain.

## Build

```
dotnet build PedalFM.csproj -c Release
```

The post-build step copies `Pedal FM.NET.dll` to
`C:\Program Files\ReBuzz\Gear\Generators\`. Restart ReBuzz to pick up
changes.
