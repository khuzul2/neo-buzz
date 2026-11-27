# Notes

An instrument takes notes, a note machine takes notes and sends notes, and a control machine can listen to the notes another machine plays. This page covers the three, and the tests every instrument must pass.

## Note input

A Neo instrument derives from [`NeoSynthBase`](../../Machines/NeoBuzz.Machine/NeoSynthBase.cs) and gets its notes from a [`NoteInput`](../../Machines/NeoBuzz.Machine/NoteInput.cs). Notes arrive on **whatever thread they are made on** and are **queued**. The engine takes them at the start of the next block:

```csharp
public bool Work(Sample[] output, int n, WorkModes mode)
{
    // ...sample rate as in the tutorial...
    BeginBlock(Engine);          // transport stop, then every queued note and controller go to the engine
    Engine.Process(left, right, n);
    // ...
}
```

`Engine` is an `INoteSink`: `NoteOn(key, note, velocity)`, `NoteOff(key)`, `ModWheel`, `PitchBend`, `Sustain`, `Aftertouch` and `AllNotesOff(hard)`. The **key** names a note so that its note-off finds it again: a track (0 to 15) for a note from a clip's tracker track, or `1000 +` the MIDI note for a note from MIDI.

Where notes come from:

| Source | How it reaches the machine |
|---|---|
| A clip's tracker track | The generated `Note` and `Velocity` track parameters (`tracks 16` in `Params.def`, [params.md](params.md#track-parameters)) call `Notes.SetNote` and `Notes.SetVelocity`. |
| MIDI, the on-screen keyboard, the piano roll and clips | `MidiNote(channel, note, velocity)` and `MidiControlChange(...)`, which the base class passes to the queue. Velocity 0 is a note-off. |
| A note wire | `INoteReceiver.ReceiveNotes`, which `NeoSynthBase` already implements: it queues the events as MIDI. |

The queue holds 4096 events and keeps the last 256 places for note-offs, so a flood of note-ons can never leave a note stuck. Dropped events are counted (`NoteInput.Dropped`).

### The MIDI route and the tracks route

A song stores a clip's notes in one of two places, and the machine decides which. [`SongNotes.For`](../../ReBuzz/Core/Notes/SongNotes.cs) is the one reader and writer of a clip's notes, for the piano roll, the session view, the arrangement, the agent, drops, import and export. [`KeyboardRoute`](../../ReBuzz/Core/Keyboards/KeyboardRoute.cs) decides how the keyboard under a machine's window plays it.

- **The MIDI route** (the usual one): a managed machine that has `MidiNote` takes MIDI, and the clip's notes are in its engine MIDI column. A Neo instrument takes this route.
- **The tracks route:** a machine that takes its notes only in its `Note` track parameter (no `MidiNote`) gets a note per track, and the clip's notes are in its Note columns. A machine that plays a sample for each note (Neo Sampler's tracker mode) keeps its notes there too, because the sample, offset and tune are track parameters beside the note.

You do not choose in code: define `MidiNote` through `NeoSynthBase` for the first, or only the track parameters for the second.

### The note map

A machine whose keys make different sounds (drums) says which keys play and what to call them, so the piano roll can name its rows and fold to the keys that play. There are two ways:

- a fixed map, in `Params.def`, with `notekey 36 "Kick" Drums` lines ([params.md](params.md#the-lines-of-paramsdef)); no code is needed;
- a map that follows the machine's settings: override `BuildNoteMap(int channel)` in the machine class to return the playable keys now, each a `NoteMapEntry(key, name, group)`. `NoteMapVersion` changes when the answer would, so the studio redraws.

Both run on the **UI thread**. A provider must not take a lock the audio thread holds.

### Listening to the notes a machine plays: the note tap

A control machine such as Neo Envelope or Neo LFO can follow the **notes** another machine plays, as it follows its parameters. The machine it listens to implements `INoteTapSource` (the core does), and the modulator implements `INoteTap`:

```csharp
void TapNoteOn(int note, int velocity);   // 1 ... 127
void TapNoteOff(int note);
```

A tap is told of every note the machine is given, **whatever its source**: the clip player, the piano roll and the session view, live MIDI and the keyboards, a note insert or a note wire, another machine. It is called on the MIDI or audio thread and **must not allocate, lock or wait**: keep a counter or a flag, and read it in the next block. `NeoModulatorBase` already implements `INoteTap` and registers itself when its listened machine is set, so a modulator needs no code of its own.

## Note output on wires

A **note wire** carries notes from one machine to another's note-in port. Neo Chord (notes in, chords out) and Neo Arp (notes in, arpeggio out) are note machines: they make no sound, and the notes they send play the instrument they are wired to.

A note machine is a `NeoMachineBase` that implements three things:

```csharp
[MachineDecl(Name = "Neo Chord", ShortName = "Chord", Author = "neo-buzz", MaxTracks = 0, InputCount = 0, OutputCount = 1)]
public partial class NeoChordMachine : NeoMachineBase, IBuzzMachine, INoteCapable, INoteReceiver
{
    INoteEmitter emitter;
    // ...
    bool INoteCapable.AcceptsNotes => true;     // a note-in port
    bool INoteCapable.SendsNotes => true;       // a note-out port
```

1. **`INoteCapable`** says whether the machine has a note-in port (`AcceptsNotes`) and a note-out port (`SendsNotes`). Both may change while it runs.
2. **`INoteReceiver.ReceiveNotes(ReadOnlySpan<NoteMessage> events)`** is called by the engine on the audio thread just before `Work`, with every event the wires carried this block, sorted by `Offset` (samples from the start of the block). Do not keep the span. A machine that is also played by MIDI, not on a wire, queues those events itself under a lock and applies them in `Work` (Neo Chord's `MidiNote` does).
3. **`INoteEmitter.EmitNote(in NoteMessage)`** sends an event down the machine's wires, to arrive **in the same block at the same offset**. The core implements it: get it with `host.Machine as INoteEmitter` in `Work`, once.

```csharp
public bool Work(Sample[] output, int n, WorkModes mode)
{
    emitter ??= host.Machine as INoteEmitter;
    // ...
    var events = engine.Process(n);            // the notes this block makes, with their offsets
    if (emitter != null)
        for (int i = 0; i < events.Length; i++)
        {
            ref readonly var e = ref events[i];
            emitter.EmitNote(new NoteMessage { Offset = e.Offset, Kind = (NoteMessageKind)e.Kind, Channel = e.Channel, Data1 = e.Data1, Data2 = e.Data2 });
        }
    Array.Clear(output, 0, n);
    return false;
}
```

A `NoteMessage` is a note-on (`Data1` note, `Data2` velocity), a note-off (`Data1` note), a controller (`Data1` number, `Data2` value), a pitch bend (`Data2` 0 to 16383, 8192 at the centre) or pressure (`Data2` 0 to 127). `NoteMessage.On` and `NoteMessage.Off` make the first two.

The conventions both machines follow:

- **Every note you send ends.** Keep a count of the notes you have started, and send a note-off for each when its source ends, even if the machine's settings changed in between. Neo Chord counts references to each output pitch, so two chords that share a pitch send one note-off, when the last ends.
- **Stop and the transport ending release everything** (`Stop()` and `TransportStopped()`): send the note-offs, then panic.
- **Everything from `EmitNote` allocates nothing.** The engine returns its events in a buffer it made at the start. Neo Chord's holds 8192.
- **The machine makes no audio.** It clears the output and returns `false`. A generator kind (`InputCount = 0`) is used for a note machine in its Studio and Portable projects (`NeoKind` is `Generators`).
- **Random choices are seeded** (a humanize, a random strum), so two renders of a song are the same samples.

## The note contract tests

Every Neo instrument passes the same contract, in [`NoteContractTests`](../../NeoBuzz.MachineTests/NoteContractTests.cs). There is **one row for each machine** in `Rows`; a new instrument is covered by adding one line. A row gives the test an engine, driven the way the machine drives it (`Rig`: its `INoteSink`, its process call, how many notes are held and how many voices are sounding). The tests:

- **Velocity sets each note's level.** A chord of four notes at velocities 30, 60, 90 and 127 gives four voices; each note's level, alone at the same pitch, rises with its velocity in that order, and 127 is at least 6 dB over 30. A row can say why not, in `VelocityBlind` (velocity does not set the level by design) or `NotMonotone`.
- **Note-off releases only its own note** (`NoNoteOff` names a one-shot machine, which has no release).
- **Polyphony.** Sixteen notes at once give sixteen voices, or the machine's stated maximum (`Max`); twenty notes past a maximum steal without a crash or a click.
- **Allocation.** Nothing is allocated per block while notes start and end.

An instrument's own tests cover what it adds (steals, glide, legato). A note machine adds tests of what it sends, such as that strum offsets land on the exact sample at every block size and that every note-on gets its note-off (see `NeoChordTests`, `NeoArpTests` and `NeoArpChainTests`).
