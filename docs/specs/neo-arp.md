# Neo Arp

| | |
|---|---|
| Status | Built (ARP1, issue #183): engine, machine, editor, 8 presets, agent guide, gates in `NeoBuzz.MachineTests/NeoArp*.cs`. The sound is for the user's ear: the listening pack is local. |
| Branch | `machines/neo-arp-arp1` |
| Code | `Machines/NeoArp/` (`NeoArp.cs`, `NeoArpEditor.cs`, `Params.def`, `Presets.txt`, `Engine/ArpEngine.cs`), the two flavours in `Machines/Portable/NeoArp` and `Machines/Studio/NeoArp` |
| Clean room | Our own logic only; no existing machine's source or parameter table was read. No brand, model or artist is named anywhere. |
| Host | No host change was needed: Neo Arp uses the note-wire interfaces of NW1 (`INoteCapable`, `INoteReceiver`, `INoteEmitter`) and the host's `MasterInfo`, `Playing` and `PlayPosition` as they are. |

## 1. What it is

An arpeggiator with note-in and note-out and no audio. It takes the notes held on its input (a note insert before an instrument, or a free note wire) and sends them on one at a time, in the same block, at the sample they fall on. It is a control machine for the app's lists (category Control) with `MaxTracks = 0`; its output carries no audio (`Work` clears the buffer and returns false).

## 2. Parameters (`Params.def`, 51, state version 0)

| Group | Parameters |
|---|---|
| Pattern | Pattern (12), Octaves 1 - 4, Octave mode (Up, Down, Up-down), Repeats 1 - 4, Transpose -24 … +24 st |
| Rate | Timing (Song tempo, Free time), Rate 1/1 … 1/64, Feel (Straight, Dotted, Triplet), Free time 20 - 2000 ms, Swing 0 - 75 % |
| Gate | Gate 5 - 200 % |
| Velocity | Velocity (As played, Fixed, Accent), Fixed velocity 1 - 127, Accent 0 - 100 %, Humanize time 0 - 20 ms, Humanize velocity 0 - 30 % |
| Hold | Latch, Retrigger (Free running, On new note, On bar) |
| Steps | Step count 1 - 16, Step 1 … 16 (Off, On, Tie, Accent), Step 1 … 16 transpose -12 … +12 st |

All in real units through `Params.def` and `tools/gen-machine-params.py`. Milliseconds are linear-mapped (raw = ms) so that an agent's "125 ms" lands on 125. New parameters are appended with a version bump.

## 3. Patterns

The held notes are sorted by pitch (As played keeps the order of the key presses). With `n` notes, `s` the step number (0 at the pattern's start) and octave passes added on top:

| Pattern | Order |
|---|---|
| Up, Down | ascending, descending |
| Up-down (ends once) | 1 2 3 4 3 2 (the top and the bottom are played once per cycle) |
| Up-down (ends twice) | 1 2 3 4 4 3 2 1 (both ends repeat) |
| Down-up | the reverse of Up-down |
| Converge | outside in: lowest, highest, second lowest, second highest … |
| Diverge | inside out: the reverse of Converge |
| As played | the order the keys went down |
| Random | each step picks one of the held notes over all the octaves by a hash of the step number (and the repeat group): the same song plays the same |
| Random walk | starts in the middle of the held notes over all the octaves, then moves one place up or down (seeded), turning back at the ends; the only pattern that keeps a position, reset by a restart |
| Chord | every held note together on each step |
| Steps | the 16-step row: the held notes in order, advanced on each On or Accent step (a Tie holds the note before it, Off is a rest); the step's transpose is added |

Octaves repeat the whole cycle higher, in the order Octave mode says (Up, Down or Up-down over the passes). Repeats plays each note 1 to 4 times before the next. Transpose moves everything.

## 4. Time model

- A step happens at a time in the engine's domain: the song position in beats with Timing = Song tempo, a sample count with Free time. Step `s` that started at `T0` is at `T0 + (s + (s odd ? swing : 0)) * stepLength`, rounded to the sample.
- A step's length is the rate (1/1 … 1/64 of a whole note, 4 beats a whole note) times 1 (straight), 1.5 (dotted) or 2/3 (triplet).
- The host never lets a block cross a tick and the tempo changes only at ticks, so a block has one tempo. The machine reads `MasterInfo` and the song position (`PlayPosition` plus the position in the tick over the samples in the tick, as the host counts them, with the remainder carried) and gives the engine the beat at the block's first sample and the samples a beat has.
- Notes, their offs, humanized lateness and bar lines are scheduled in the same domain, so a tempo change or a loop jump never moves a step off the grid and never leaves a note out of its length. The patterns are functions of the step number (Random walk alone keeps a position), so a start in the middle of a bar or a jump finds the figure where the song position says.
- Retrigger: Free running follows the song position; On new note and On bar restart the figure at the next grid point (a key pressed between steps waits for the next one).
- Gate is a share of the step, measured from the note's own start (swing and humanize move the start and the off with it). Above 100 % notes of different pitches overlap; the same pitch is released before it is struck again.
- Humanize time adds a seeded lateness of up to the set milliseconds to the note-on and the note-off alike; Humanize velocity changes the velocity by up to the set share, seeded: the same song plays the same notes.
- Transport stopped: nothing is sent, except the note-offs of the notes sounding at the stop edge. A jump in the song position releases what sounds. A note-off is stored for every note-on in an active table, so none can be left on; Panic (a machine command) and an All Notes Off controller release everything. Other events (controllers, pitch bend, pressure) pass through while the transport plays.

## 5. Chain

Neo Arp is `INoteCapable` (note in, note out) and `INoteReceiver`. The host's note plan delivers the events of the block in `ReceiveNotes` and sends what the machine emits through `INoteEmitter` to the next insert or the instrument. Neo Poly (and the other instruments) apply the events at the start of each block, so a note sits within one host block of where Neo Arp put it. `MidiNote` and `MidiControlChange` (live MIDI to the machine) go through a short locked queue merged on the audio thread (the lock is held only for the swap).

## 6. Editor (`NeoArpEditor.cs`)

The Neo editor kit, one source for both flavours: Pattern and Rate sections on top; the Steps section with the 16-step row (a click on a step's cell cycles Off, On, Tie, Accent; a drag in the bar below sets its transpose, a double-click puts it to 0, the wheel steps it; the step playing is lit while the Steps pattern plays); then Gate, Swing, Velocity and Hold. Accent colour from the Control identity. The row is drawn dimmed beyond `Step count`, and a note says it is used by the Steps pattern.

## 7. Presets (`Presets.txt`, 8)

Runs — Straight 16ths Up, Runs — Triplet Cascade, Waves — Up-Down Two Octaves, Waves — Slow Converge, Rhythm — Swing Eighths, Rhythm — Gated Chord Pulse, Random — Random Sparkle, Steps — Step Bassline. Each is audible on its own through Neo Poly and clearly different from the others (a test measures both).

## 8. Gates and where they are tested

| Gate | Test | Result |
|---|---|---|
| Order of every pattern, octave modes, repeats, transpose, chord, seeded random, random walk, steps with ties and accents | `NeoArpTests` (order tests) | pass |
| Timing: 60, 120 and 197 BPM; blocks of 16, 64 and 256; swing, triplet, dotted, gates to 150 % | `StepsLandWithinOneSampleOfTheGrid` (9 cases, 4 settings each, every note-on and note-off) | within 1 sample |
| Tempo change, loop jump, start in the middle of a bar, Free time | `ATempoChange…`, `ALoopJump…`, `StartingMidBar…`, `FreeTimeIsExact…` | on the grid |
| No stuck notes: 3 seeds of 10,000 events (notes, tempo changes, stops, jumps, parameter changes) | `ASeededStressOfTenThousandEvents…` | every note-on has one note-off; 0 note-ons while stopped; `ActiveCount` 0 |
| Chain: insert before Neo Poly, and a free wire | `NeoArpChainTests.TheInstrumentPlaysTheArpeggio` (both cases) | the instrument's notes are the arpeggio's, on its steps; see 9 |
| Audio thread: 2000 blocks | `TheAudioPathAllocatesNothing` | 0 bytes |
| State and presets | `SavedStateKeepsEveryParameterAndTheStepRow`, `TheTableHasTheDocumentedShape`, `EveryMachineLayoutIsRecorded…`, the preset tests | pass; state version 0, 51 parameters |
| Catalogue, generators | `python3 tools/machine_catalog.py`, `gen-machine-params.py --all --check`, `gen-machine-presets.py --all --check` | 0 problems, up to date |
| Presets audible and different; no clicks | `EveryPresetIsAudibleThroughNeoPoly`, `ThePresetsAreClearlyDifferent…`, `ThePresetsRenderWithoutClicks` | pass; 0 clicks |

## 9. Limits and decisions

- The chain tests drive the engine through a stand-in for the host's block loop (`NeoArpRig`) and Neo Poly's engine: the test project links the engines only, not the machine classes, and the host's note plan is NW1's and tested there. The machine's own glue (`NeoArp.cs`) is exercised by the Studio gallery and CI builds, not by a host-level unit test.
- The Steps pattern advances through the held notes on On and Accent steps only. With one held note the row is a bassline; with a chord each sounding step plays the next chord tone.
- Random patterns are a function of the step number and a seed, so a loop plays the same way each pass; a jump back re-plays the same figure.
- The active table has one entry per note number (the channel it was struck on is kept), so a note struck again while it still sounds is released first (a note-off, then the note-on at the same sample).
- Not built: a swing by feel template, per-step gate or probability, a MIDI-clock free running mode (the Free time mode runs on sample time only).

## 10. Listening pack

Local, never committed (the brief's folder, `arp1-pack/` in the worker's scratchpad; copy it to `listening-packs/183/` to listen): 8 items of about 16.5 s, each preset playing the same four-bar chord progression twice through Neo Poly on a fixed plain patch (saw, 24 dB ladder at 3 kHz with 10 % resonance, 3 ms attack, 0.35 s decay, 45 % sustain, volume -6 dB) at 120 BPM, 48 kHz 16-bit WAV, with a README that lists each item's settings. Written by `ARP_PACK=<folder> dotnet test NeoBuzz.MachineTests --filter FullyQualifiedName~NeoArpPack`; the click detector (second difference against its local mean) finds 0 clicks in all eight.
