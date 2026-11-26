# Neo Chop

| | |
|---|---|
| Status | Built (CHP1, issue #184): engine, editor, 8 presets, agent guide, gates in `NeoBuzz.MachineTests/NeoChopTests.cs`, listening-pack generator `NeoChopPack.cs`. The sound is for the user to approve before the machine merges. |
| Branch | `machines/neo-chop-chp1` |
| Code | `Machines/NeoChop/` (`NeoChop.cs`, `NeoChopEditor.cs`, `Params.def`, `Presets.txt`, `Engine/ChopEngine.cs`, `Engine/ChopGrid.cs`), the two flavours in `Machines/Portable/NeoChop` and `Machines/Studio/NeoChop` |
| Builds on | `FxStage` (Mix and Amount), `SmoothRamp`, `PortableMath`, `LevelMeter`, the note wire (`docs/specs/note-wires.md`), the Neo machine framework (`docs/specs/machines.md`) |
| Clean room | Textbook DSP only: a ring buffer, smoothstep crossfades, Hermite interpolation, a trapezoidal state-variable filter, a stateless integer hash for the seeded chances. No code, parameter table or preset from any other product or legacy machine was read or used. |

## 1. What it is

An effect for one track that cuts up the audio passing through it in time with the song. Six modes, each with its own section, any number of them on at once:

| Mode | What it does |
|---|---|
| Stutter | Captures the last slice (1/4 to 1/64, straight, dotted or triplet) and repeats it 1 to 16 times (or Hold), each repeat with its own Pitch (-12 to +12 st), Decay (dB) and Filter (octaves, a low-pass that closes or a high-pass that opens). |
| Repeat | Every Interval (1 to 8 bars), with a Chance, loops a Grid-sized slice for a Length (1/16 to 4 bars). Variation cuts some loops of the slice to half or a quarter, from a seeded random. |
| Retrigger | Jumps back, on a grid, to the start of the beat (or bar) the song is in. |
| Tape | Slows the audio to a stop over a time (milliseconds or a note value) with a Curve and holds it silent; the start spins it back up. |
| Gate | Sixteen steps (off, on, accent), a Rate from 1/4 to 1/64, Attack and Release in ms, Depth, Accent in dB. |
| Pan | An auto-pan: a Rate (synced to the song or in Hz), Depth, Shape (sine, triangle, smoothed square) and Phase. |

### 1a. The order of the modes

The editor lists the modes as Stutter, Repeat, Retrigger, Tape, Gate, Pan. The audio goes through them in this order:

1. **The four cutting modes share one read of the audio and take over from each other by priority: Tape, then Stutter, then Repeat, then Retrigger (highest first).** A mode that starts while a lower one runs fades the lower one out over the Smoothness; a lower mode that would start while a higher one runs waits for its next window. Retrigger is the lowest, Tape the highest.
2. **The Gate** shapes the result of that.
3. **The Pan** is last.

The gate and the pan are not cuts: they work on whatever the cutting modes made.

## 2. Triggers, the grid and timing

Each cutting mode has a Trigger: **Synced** (from the song position), **Note** (a note in through a note wire, live MIDI or the track's note column) or **Both**. The gate and the pan are always synced to the song, or Free.

* **Synced windows end on a period.** Stutter every 1 beat to 8 bars, Retrigger the same, Tape the same, Repeat every 1 to 8 bars. A window ends exactly on the period's end and starts a Lead before it: a stutter's Repeats times its Slice (Hold: half the period), a repeat's or retrigger's Length, a tape stop's stop time. The first period is the first one that the Lead fits in.
* **4/4 is assumed.** A bar is four beats; a beat is the song's beat (ticks per beat from the host). The note values are fractions of a bar in 4/4 (a quarter note is a beat).
* **The grid is exact.** A slice, repeat, retrigger or gate step starts on the sample at or after its grid time: the first whole sample `n` with `n >= grid time`, found by `ceil`, independent of the block size. The gate for this is within 1 sample at 60, 120 and 197 BPM and blocks of 16, 64 and 256; a tempo change moves the grid with the tempo, and a loop jump or a start from the middle of the song restarts the schedule from the new position and fades what was running.
* **Cuts are anticipated.** A cut that begins on the grid starts its crossfade (Smoothness long) just before the grid time, so that the fade ends on the grid time and the first sample of the slice is the one on the grid. A note cannot be looked ahead of: a note-started cut begins its fade on the note's own sample.
* **Nothing runs while the song is stopped**, except a gate or a pan set to Free. A note while the song is stopped does nothing.
* **A cut never reads from before the first sample the machine has seen.** A stutter or retrigger whose slice or jump would reach back past the start of the stream waits for its next window (the first bar after the machine starts or the song position jumps back has no history).

### 2a. Notes

A note through a note wire, live MIDI or the track column starts the modes whose Trigger is Note or Both, on its exact sample: the mode's blend begins on the note's sample, and the first slice or jump is read as if the cut started there. The note's end ends a **Hold** stutter (and a note-started tape stop, which spins up on it). A note-started stutter, repeat or retrigger ends after its own Repeats, Length or window. With **Note picks slice** on, the key picks the slice or grid, by the key's place in the octave (C = 0):

| Key | C | C# | D | D# | E | F | F# | G | G# | A | A# | B |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| Slice, in beats | 1 | 3/4 | 1/2 | 1/3 | 3/8 | 1/4 | 1/6 | 3/16 | 1/8 | 1/12 | 1/16 | 1/24 |

(a quarter, a dotted eighth, an eighth, a triplet eighth, a dotted sixteenth, a sixteenth, a triplet sixteenth, a dotted thirty-second, a thirty-second, a triplet thirty-second, a sixty-fourth and a triplet sixty-fourth). The retrigger takes nothing shorter than a thirty-second.

## 3. Every boundary is crossfaded

Every slice, repeat, retrigger and gate edge is a smooth move, never a step: the cuts crossfade over the **Smoothness** (a smoothstep, so the slope is zero at both ends), the gate's open value moves on a smoothstep over its Attack or Release (never shorter than 2 ms) and the accent is a separate smooth curve that meets the gate's without a corner, the pan's square has its edges rounded over the Smoothness. A fractional read (a pitched repeat, the tape) uses a four-point Hermite interpolation and never reads a sample later than the one being made.

**Smoothness runs from 2 to 20 ms (the brief said 1 to 20): below 2 ms a crossfade of a low sine can no longer keep its second difference down, and 2 ms is what the brief asks every boundary to have as a least.** Default 4 ms.

## 4. Mix and Amount

Both through the shared helper (`FxStage`). **Mix** is the dry/wet; the machine has no latency, so there is nothing to compensate (`GetLatency` is 0, the dry is not delayed). **Amount** (0 to 200 %, 100 % = the settings as they are, 0 % = bit-exact dry) scales these, by the same rule of the shared `AmountRule`:

| Parameter | At 50 % | At 200 % |
|---|---|---|
| Pitch per repeat, Decay per repeat, Filter per repeat | half | double (clamped to the range) |
| Chance (repeat) | half | double (at most 100 %) |
| Gate Depth, Pan Depth | half | double (at most 100 %) |

Mix 0 % and Amount 0 % pass the input bit for bit; at 100 % the engine reads the parameters unscaled.

## 5. Gates (`NeoBuzz.MachineTests/NeoChopTests.cs`)

| Gate | Test | Result |
|---|---|---|
| Timing | `EveryStutterSliceStartsWithin...`, `...RepeatStarts...`, `...RetriggerJump...`, `...GateStep...`, `ATapeStopEndsOnTheBar...` at 60, 120, 197 BPM x blocks 16, 64, 256; `ATempoChangeStaysOnTheGrid`; `ALoopJumpStartsTheSchedule...` | every start within 1 sample of its grid time (the read position of the first slice sample is exact, checked on a ramp signal) |
| Clean | `EveryBoundaryIsCrossfadedOnASineAndOnADrumLoop` | max \|second difference\| within +-3 ms of every boundary at most 1.5 times the largest from 3 to 30 ms either side (worst measured 1.48 at 197 BPM), and the project's click detector finds 0 new clicks, on a sine and a drum loop, for seven settings of every mode |
| Mix and Amount | `AmountZero...`, `MixZero...`, `AmountAtOneHundred...`, `AmountGlidingToZero...` | Amount 0 and Mix 0 bit for bit with every mode on; Amount 100 % equals the settings; 50 % and 200 % scale as in section 4 |
| Notes | `ANoteStartsTheStutterOnItsExactSample`, `ANoteOffEnds...`, `TheKeyOfANoteCanPickTheSlice`, `NothingRunsWhileTheTransportIsStopped...` | the stutter's start sample equals the note's sample (blocks 16, 64, 256, note at 0, 1, 37, 130 into the block) |
| Audio thread | `NothingIsAllocatedPerBlock...`, `NoDenormalsRingOut...` | 0 bytes over 2000 blocks with every mode busy and parameters moving; no denormals or non-finite values after a loud burst |
| State | `EveryParameterAndTheStepRowSurvive...`, `StateVersionTests`, `PresetTests` | every parameter and the 16 steps keep their values; the layout line in `state-layouts.txt`; the presets load and sound sensible |
| Presets | `EveryPresetIsClearlyAudible...` | each of the 8 presets differs from the dry by more than -12 dB (0 dB = as loud as the drums) on a full-scale drum loop, no two presets within -12 dB of each other, 0 clicks |

## 6. Parameters, presets, agent

65 parameters in `Params.def` (version 0), in real units: Smoothness 2 to 20 ms, Stop time 20 to 4000 ms, Start time 0 to 4000 ms, Gate attack 2 to 200 ms, Gate release 2 to 400 ms, Pan rate 0.05 to 20 Hz, Pan phase 0 to 360 degrees, Pitch -12 to +12 st, Decay 0 to 12 dB, Filter -2 to +2 octaves, Accent 0 to 12 dB, Amount 0 to 200 %. The sixteen gate steps are appended after the gate's controls as `Step 1` to `Step 16` (off, on, accent); new parameters are appended, never inserted. Eight presets in `Presets.txt` (Stutter — Sixteenth Stutter, Repeat — Bar-End Repeat, Stutter — Triplet Glitch, Retrigger — Half-Bar Retrigger, Tape — Tape Stop at the Bar, Gate — Pumping Step Gate, Pan — Wide Auto-Pan, Fill — Chopped Vocal Feel), each setting every mode explicitly. The agent guide is `ReBuzz.Agent/Guides/machines/Neo Chop.md`.

## 7. Limits

* 4/4 only; a song in another time signature still cuts on its beats, with a bar of four beats.
* The buffer holds 8 seconds of audio (it is made when the sample rate is set, a power of two long, never at run time); a slice, a jump or a repeat can read back at most 7.95 s. The brief asked for a buffer for the highest sample rate: it is sized for the sample rate the machine is set to, which allocates nothing per block.
* Block sizes above 256 samples are refused (the host's are 256 or less).
* A cut waits when it would read before the first sample the machine has seen (section 2).
* The Gate Attack and Release and the pan's square edges are never shorter than 2 ms.
* The retrigger and the repeat read the audio of the same track; there is no sample memory to fill or empty.
