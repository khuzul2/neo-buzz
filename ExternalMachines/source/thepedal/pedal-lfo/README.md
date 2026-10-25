# Pedal LFO

A managed **control machine** for ReBuzz that drives any global parameter on
any other machine with a configurable LFO. One LFO per instance, one target —
add multiple instances when you need multiple modulators.

## What it does

You drop a Pedal LFO into the song, click **Configure target…** in its GUI,
pick a machine and one of its global parameters, and the LFO continuously
modulates that parameter while transport plays. Shape, rate, depth, center,
phase offset, and tempo-sync are all rack parameters — fully automatable
from patterns, recallable per preset.

Because it's a control machine (parameterless `Work()`), it lives in the
**Generators** tab of the machine browser.

## Build

The project targets `net10.0-windows` (matching ReBuzz 1817+ itself, which
is built against .NET 10) and references the single ReBuzz assembly
`BuzzGUI.Interfaces.dll`. The `Buzz.MachineInterface` namespace lives
inside that DLL, not in a separate assembly. The csproj defaults to
`C:\Program Files\ReBuzz` for both the reference and the deploy path —
override with the `ReBuzzPath` MSBuild property if you've installed
elsewhere:

```
dotnet build -c Release -p:ReBuzzPath="D:\Tools\ReBuzz"
```

The default `Release` build copies `Pedal LFO.NET.dll` into
`<ReBuzzPath>\Gear\Generators` automatically. The `.NET` suffix on the
assembly name is mandatory — that's how ReBuzz's loader routes it through
the managed-machine path (Build §2).

Restart ReBuzz (or rescan machines) and "Pedal LFO" will appear under
**Generators**.

## Parameters

| # | Name   | Range      | Notes                                                      |
|---|--------|------------|------------------------------------------------------------|
| 1 | Shape  | 0–5        | Sine, Triangle, Square, Saw Up, Saw Down, S&H              |
| 2 | Period | 2–16384    | Cycle length in ticks (Sync=Free). 16 = 1 bar at 4 TPB     |
| 3 | Depth  | 0–100 %    | Swing amplitude as % of target's full range                |
| 4 | Center | 0–100 %    | Center of swing as % of target's full range                |
| 5 | Phase  | 0–127      | Phase offset 0°–360°                                       |
| 6 | Sync   | 0–7        | Free, 1/1, 1/2, 1/4, 1/8, 1/16, 1/4T, 1/8T                 |
| 7 | Bypass | 0/1        | Stops writing to target without losing target assignment   |
| 8 | Start  | 0/1        | Switch — 1 = run / retrigger from phase 0; 0 = stop        |

Parameter declaration order is fixed by the preset format — never reorder
or insert (Build §3.3). New parameters in future versions get appended
(`Start` was added in v1.1 at the end for exactly this reason).

## Output mapping

The LFO produces a normalised value in `-1..+1`, then maps to the target
parameter's integer range as:

```
output = round( target.Min + (Center/100) * range + lfo * (Depth/100) * range * 0.5 )
```

clamped to `[target.Min, target.Max]`. So with Center=50, Depth=100 the LFO
swings the full range; with Depth=20, Center=70 it swings ±10% of the range
around 70%.

## Timing model

Updates happen **per tick** (typically ~500 Hz at 126 BPM), not per audio
buffer. Tick boundaries are detected from `Buzz.Song.PlayPosition` changes
(PedalTracker §2.3), which sidesteps the unreliable `PosInTick` arithmetic
near tick edges. Writes use `IParameter.SetValue` followed by
`IMachine.SendControlChanges` so the new value reaches the target within the
same audio buffer (Core §4) — no one-tick latency.

- **Free mode** advances a phase accumulator by `1/Period` each tick.
  Period is in ticks (e.g. 16 = one bar at 4 TPB). Loop-wrap immune; the
  wave keeps moving smoothly across loop boundaries regardless of where
  the loop point sits.
- **Sync modes** derive phase directly from `PlayPosition mod ticksPerCycle`,
  so the LFO locks to bar/beat regardless of how the song is navigated.

When transport stops, `PlayPosition` doesn't advance, so the LFO **freezes**
at its last value. This matches the expected DAW behavior; restart the
transport (or click **Reset phase**) to nudge it.

## Start parameter (v1.2)

`Start` is a switch that drives the LFO's run state, automatable from any
pattern row.

- `Start = 1` — LFO running. A 0→1 transition retriggers the wave from
  phase 0 (plus any `Phase` offset).
- `Start = 0` — LFO stopped; target held at Center.

This is what makes Pedal LFO usable as a per-pattern trigger source.
Typical pattern use:

```
row 00 :  Start = 1     ← begin LFO from phase 0
row 04 :  --
row 08 :  --
row 12 :  Start = 0     ← stop, target snaps to Center
row 16 :  Start = 1     ← retrigger from phase 0 again
...
```

Because ReBuzz suppresses consecutive identical writes for global
parameters, retriggering requires interleaving a `Start = 0` row between
`Start = 1` rows. The 0→1 edge is what fires the retrigger.

The default value is `1`, so a freshly-added Pedal LFO runs immediately
without needing a pattern entry.

The **Reset phase** GUI button is equivalent to a manual retrigger: it
zeroes the free-mode accumulator and rebases sync mode to the current
song position. It does not toggle the Start switch.

## Bypass vs Start

Both stop the LFO from animating, but the target behavior differs:

- **Bypass = 1** — LFO stops writing entirely. Target retains whatever
  value the LFO last wrote, or its pre-LFO value if Bypass was on from
  the start. Useful for A/B testing.
- **Start = 0** — LFO actively writes Center to the target. Target snaps
  to a known state regardless of what was previously there. Useful for
  start/stop within a song.

## GUI

The embedded GUI shows:

- The current target (`MachineName → ParamName`, or `(unassigned)`).
- A live shape preview (1 cycle wide), reflecting current Shape / Depth /
  Center, with a gold dot showing real-time phase + value.
- A **Configure target…** button (opens picker) and a **Reset phase**
  button (re-zeros the free-mode accumulator now).

The picker dialog runs on its own STA thread to avoid nested
message-pump deadlocks under ReBuzz's host dispatcher (Core §23). Targets
are filtered to **global parameters** of other machines that have at least
one global with a non-degenerate range.

## Persistence

Target assignment survives song save/load via `MachineState` — only the
machine **name** and parameter **name** are stored, so renaming the target
machine after saving will leave the LFO unable to resolve. The GUI shows
`(not found)` in that case; reopen Configure to re-pick.

## Limitations / future ideas

- **Globals only.** Per-track parameters don't map cleanly to a single LFO
  with one target slot. Workaround: add multiple instances of Pedal LFO.
- **No MIDI/external sync.** Sync is song-position based.
- **Static phase on transport stop.** Free-mode currently freezes alongside
  sync mode because we drive everything off `PlayPosition`. A wall-clock
  fallback for free-mode would let the LFO keep moving while paused.
- **Sample-and-hold rate.** S&H draws fresh randomness once per LFO cycle
  (not per tick). Per-tick S&H would need a separate dedicated mode.
- **Single target per instance.** A possible v2 expansion is multi-slot
  routing (one LFO, several targets sharing the same shape) — but the
  current "one LFO, one target" model keeps the parameter list clean.
