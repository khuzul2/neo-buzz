# Neo Looper

| | |
|---|---|
| Status | Built (LOO1, issue #198): engine, editor, 8 presets, agent guide, gates in `NeoBuzz.MachineTests/NeoLooperTests.cs`, the .nbl round trip in `ReBuzz.Studio.Tests/NeoLooperNblTests.cs`, listening-pack generator `NeoLooperPack.cs`. The sound is for the user to approve. |
| Branch | `machines/neo-looper-loo1` |
| Code | `Machines/NeoLooper/` (`NeoLooper.cs`, `NeoLooperEditor.cs`, `Params.def`, `Presets.txt`, `Engine/LooperEngine.cs`, `Engine/LooperCodec.cs`), the two flavours in `Machines/Portable/NeoLooper` and `Machines/Studio/NeoLooper` |
| Builds on | `FxStage` (Mix and Amount), `SmoothRamp`, `PortableMath`, the song position the machine host gives (as Neo Chop and Neo Arp read it), the session's beats per bar |
| Clean room | Textbook DSP only: a sample buffer, raised-cosine and equal-power fades, linear interpolation. No code, table or preset from any other product. |

## 1. What it is

A live looper for one track, made to be played from a MIDI pad or a footswitch, in time with the song. It records what passes through it, plays it back in a loop and lets you stack more on top (overdub), make the loop 2, 3 or 4 times as long (multiply) and take passes back (undo, redo). Zero latency: the dry signal is never delayed.

### 1a. The one-button cycle and the other triggers

Seven switch parameters are **triggers**: a press is a change of the parameter from 0 to 1 (a pad sends 127 and then 0; the editor's big buttons write 1 and, 60 ms later, 0). Each is MIDI-learnable as any parameter is, and a clip column or the agent can press them the same way (`machine_set` to 1, then to 0). A footswitch that latches can use `Pedal` = Press and release, which fires on every change.

| Trigger | What it does | When (Sync) |
|---|---|---|
| Record | Empty to Recording to Playing to Overdubbing to Playing. In Stopped it plays. | the next bar or beat |
| Stop | Playing or Overdubbing to Stopped (the loop is kept; it fades out over the crossfade). | the next bar or beat |
| Play | Stopped to Playing, from the loop's first frame. | the next bar or beat |
| Multiply | The loop becomes `Multiply by` (x2, x3, x4) times as long, its content repeating. Ignored while an overdub is open, or when the longer loop would not fit `Max loop`. | the next bar or beat |
| Undo | Takes back the last overdub pass, multiply or clear (up to `Undo levels`, 1 to 8). | at once |
| Redo | Brings back what Undo took away, until the next new pass. | at once |
| Clear | Empties the loop. The old loop is the newest undo level. | at once |

The triggers act in the order they were pressed. A press while the song is stopped, or in Free, acts at once.

## 2. Timing

* **A bar is the song's bar.** The machine reads the session's beats per bar (`ISessionHost.Session.BeatsPerBar`, which follows the song's meter: a 3/4 song has 3-beat bars) every 64 blocks; with no host session it uses 4. Test: `ABarIsTheSongsBar`.
* **Sync** (the default). `Record`, `Stop`, `Play` and `Multiply` land on the exact sample of the next bar (`Quantise` Bar) or beat (Beat) of the song, found from the song position in beats (the first sample at or after the grid time, by `ceil`; a trigger that arrives in the block that starts on the line acts on its first sample). The first take is rounded to whole bars at the song's tempo: its length in frames is `round(bars x beats per bar x samples per beat)`, at least one bar, so a 2-bar take at 120 BPM is exactly 192,000 frames, 4.000 s. The loop is padded with silence or cut to that length.
* **The loop follows the song position.** In Sync the loop's phase is the song position modulo its length in beats. A seek, a song loop or a jump moves the loop with the song: the playhead is set from the song and crossfaded over the crossfade (test `ASeekKeepsTheLoopInTimeWithTheSong`: the song jumps 1.5 bars and the loop's frame is the one the song's position says).
* **A tempo change does not stretch the loop** (no time-stretch in this task). The loop plays at the speed it was recorded; at every wrap of the song's phase (every loop length in bars) it restarts in time, with a crossfade when it is not at its start. At a tempo that makes the bars shorter than the audio the end is cut at the bar; at one that makes them longer, the loop repeats and restarts at the bar.
* **Free.** A trigger acts on the first sample of the block it arrives in, as live MIDI input does today (parameters reach a machine between blocks of at most 256 samples: up to 5.3 ms at 48 kHz). The first take is the frames between the two Record presses, exactly. The loop is not tied to the song.
* **With the song stopped**, Sync works like Free (there is no grid to wait for).

## 3. The loop, overdub, undo

* **Whole-buffer layers.** The loop is one stereo float32 buffer in a pool of `Undo levels + 2` buffers of `Max loop` frames. An overdub, a multiply and a clear never change the buffer that plays: they write a new one, and the old one stays untouched as the undo level, restored bit for bit. Frames that a pass has not visited are copied from the old buffer in chunks of 32,768 per block; until then a read takes the frame from the buffer that has it. Nothing is allocated or copied in one go on the audio thread.
* **Overdub.** At each frame the new loop is `old x Feedback + input` (so 100 % keeps everything), written where the playhead reads it. The pass punches in and out over `Crossfade` (a raised-cosine ramp of the input and of the feedback's effect), so a pass starts and ends without a click. Test `OverdubUndoRedoAreSampleExact`: the pass equals the expected sum bit for bit where the ramp is 1, nothing outside it changes, undo restores the loop and redo the pass, bit for bit.
* **Multiply.** The new buffer is `old[i mod old length]`, filled by the same chunked copy; the playhead goes on from where it is, so there is nothing to hear. One undo step.
* **The loop point.** When a take is closed, the next `Crossfade` of input (the continuation of what was played) is blended into the first `Crossfade` frames of the loop (equal power), so the end of the loop runs into its start with no step. The loop comes in over the crossfade the first time it plays (from the monitored input).
* **Reverse** plays the loop from the end. **Half speed** reads half a frame per sample (linear interpolation), so the loop is exactly twice as long in time and an octave down; an overdub made at half speed writes the mean of two input samples to each frame, so it comes back an octave up at normal speed.
* **Undo, redo, clear** swap the playing buffer with a crossfade of `Crossfade` between the old and the new playhead.

## 4. Memory

Buffers are made when the machine is created, when `Max loop` or `Undo levels` changes (on a worker thread, only while the loop is empty; a pending pool is taken at the next block) and at load (a loop longer than the limit makes a pool that holds it). Buffers per pool: `Undo levels + 2`. A pool never passes **320 MB**: if `Max loop` and `Undo levels` would pass it, the undo depth is lowered.

| Max loop | one buffer, 48 kHz | 4 levels (6 buffers) | 8 levels (10 buffers) |
|---|---|---|---|
| 15 s | 5.8 MB | 35 MB | 58 MB |
| 30 s (default) | 11.5 MB | 69 MB | 115 MB |
| 60 s | 23 MB | 138 MB | 230 MB |
| 120 s | 46 MB | 276 MB (4 levels) | 320 MB cap: 4 levels |

(double at 96 kHz.) 0 bytes are allocated per block (2000 blocks of recording, overdubbing, undo, redo, multiply, stop, play and clear with every parameter moving: test `NothingIsAllocatedWhileRecordingAndOverdubbing`).

## 5. Saving

The loop is the machine's own data (`MachineState`, the XML kept in the song's machine data), so it is saved with the song and a song that is opened again has it.

* **Layout (version 1).** `LooperSaved { Version, Rate, Frames, LoopBeats, Data, Hash }`. `Data` is the loop's float32 samples, left then right, with the four bytes of each float split into four planes, deflated and written as base64: lossless, so a loop comes back **bit for bit** (no 24-bit rounding). `Hash` is the SHA-256 of the samples, checked on load: a damaged or cut loop loads as no loop, never as a wrong one. An empty loop is an empty string. The parameters are the machine's table (state version 0, `NeoBuzz.Looper 0 21` in `state-layouts.txt`).
* **What is saved.** The playing loop (the buffer before the pass while an overdub is open) and its length in beats. Not saved: the undo and redo levels, the playhead and the state: a song opens with the loop **stopped**, ready for `Play`.
* **Cost in the song.** Float audio does not deflate much: measured on our own Neo Drums loop and Neo Poly pad, about **0.37 to 0.39 MB of machine state for each second of loop** (stereo, 48 kHz; the raw floats are 0.38 MB a second), 1.5 MB for a 4 s loop, about 12 MB for a 30 s one, 48 MB for 120 s. The .nbl stores machine data as hashed chunks and deflates a chunk where that is smaller: measured in `NeoLooperNblTests`, a 4 s stereo loop (a sine pair with noise) makes a 1.19 MB .nbl, **0.30 MB a second of loop**, and a loop that did not change is the same chunks in every save. The editor shows what the loop adds ("saved with the song").
* **Presets** set the behaviour (the parameters), never the audio: loading a preset does not touch a loop that is recorded. `Max loop` and `Undo levels` change only while the loop is empty; with a loop held they keep their pool (the editor shows the memory in force).
* **Tests.** `TheLoopRoundTripsBitExactThroughTheSongsStateAndPlaysTheSame` (the state through the host's XML serialiser, a second machine plays the same bits), `ADamagedLoopLoadsAsNoLoop`, and in `NeoLooperNblTests` the real song: a loop and parameters saved in a `.nbl`, opened, bit for bit.

## 6. Editor

The loop's waveform (peaks, drawn again when the loop changes and ten times a second in an overdub) with bar lines, the play head and the two newest undo layers behind it; a **state banner** whose word and frame take the studio's reserved colours (EMPTY grey, RECORDING record red, PLAYING play green, OVERDUB override orange, STOPPED neutral) with the bar the loop is on ("bar 2 of 4") and what waits ("next: RECORD on the bar", the undo and redo depth); big pads (96 x 76 px, the Record pad 168 x 76) for the seven triggers, the first one's word following the cycle (REC, CLOSE, OVERDUB, PLAY); then Timing (Sync, Quantise, Multiply by, Pedal), Loop (Feedback, Loop level, Crossfade, Monitor, Reverse, Half speed), Memory (Max loop, Undo levels, the pool size in force and the loop's cost in the song) and Output (Amount, then Mix, two 40 px knobs). No literal colours; the Modulation accent.

## 7. Presets

Eight, by behaviour (the performance of the listening pack plays each one: record, overdub, multiply, undo): Bar Looper, Beat Catcher, Fading Layers, Endless Stack, Free Sketch, Octave Down Wash, Backwards Tape, Slow Decay Bed.

## 8. The gates (tests)

| Gate | Test | Result |
|---|---|---|
| Cycle: each state change on the right sample (Sync: the bar; Free: the trigger's block) | `SyncStartsAndStopsLandOnTheBarSample`, `SyncPlaysTheLoopFromTheLandingSample`, `FreeTakeSetsTheLengthExactly` | exact |
| Length: 2 bars at 120 BPM is 4.000 s; x3 is 12 s repeating the take | `ATwoBarTakeAt120BpmIsFourSecondsAndMultiplyRepeatsIt` | 192,000 and 576,000 frames; every frame equal |
| The song's bar, the song's position | `ABarIsTheSongsBar`, `ASeekKeepsTheLoopInTimeWithTheSong` | exact |
| Overdub, undo, redo sample exact | `OverdubUndoRedoAreSampleExact` (Feedback 100 % and 60 %) | bit for bit |
| No clicks at the loop point and every punch in and out | `NoClickAtTheLoopPointOrAnyPunchInOrOut` (Neo Drums loop and Neo Poly pad, monitor on and off, two scripts that use every trigger) | 0 clicks |
| Save and load | `TheLoopRoundTripsBitExact...`, `NeoLooperNblTests` | bit for bit |
| Audio thread | `NothingIsAllocatedWhileRecordingAndOverdubbing`, `TheLoopFeedbackTailHasNoDenormals`, the audio-path test (PortableMath only) | 0 bytes |
| Amount 0 % and Mix 0 % are the input | `ZeroIsTheInputBitForBitWithALoopPlaying` | bit for bit |
