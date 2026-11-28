# Tempo sync: one control for every musical timing

Every musical timing in a Neo machine (an LFO's cycle, a delay's time, an arpeggio's step, a chorus's sweep) is offered the same way:

- **Free**: a time in ms or a rate in Hz, as the machine's own parameter.
- **Sync**: a **division** from 8 bars down to 1/64 (8 bars, 4 bars, 2 bars, 1 bar, 1/2, 1/4, 1/8, 1/16, 1/32, 1/64).
- A **Feel**: Straight, Dotted (x 3/2), Triplet (x 2/3) or Quintuplet (x 4/5).

The same words in the same order in every editor, and the same words for the agent ("1/8 dotted", "1/16 quintuplet"). Things that are physical, not musical, stay in ms or Hz: envelopes, attack and release, drum decays, tape wow, a rotary speaker, a vibrato rate. A machine you write that has a musical timing uses everything on this page; do not write your own list.

## The parameters

A timing is two or three parameters in `Params.def`, in one of two shapes:

| Shape | Parameters | Used by |
|---|---|---|
| A: Free is an entry of the list | `Rate` (Hz or ms), `Sync` (`enum Free 8_bars 4_bars 2_bars 1_bar 1/2 1/4 1/8 1/16 1/32 1/64`), `Feel` (`enum Straight Dotted Triplet Quintuplet`) | Neo LFO, Neo Random, Neo Delay, Neo Poly, Neo Wave, Neo Ensemble, Neo Filter |
| B: a switch beside the list | `Sync` (`enum Song_tempo Free_time`), `Rate` (the divisions, no Free), `Feel`, `FreeMs` (ms) | Neo Arp, Neo Pulse, Neo Notes |

For a new machine use shape A in the order above (a new list has no history to keep), and give the Feel parameter the four names. Never reorder or rename a stored list: songs, presets, MIDI maps, automation and modulator depths store a position. A list that grows gets its new entries **at the end**; the picker shows them in musical order whatever the stored order is.

When a list grows or Feel gains Quintuplet, three things keep old data the same:

1. A **version** of the machine's state (`docs/specs/machines.md` section 3.2): a line in `Layouts/state-layouts.txt`, and a chain step when a parameter was appended (`Add("L1Feel", 0)` gives an old song Straight).
2. A **modulator pin**: add the parameter to `LegacyChoiceSpans.SyncPins` (`Machines/NeoBuzz.Dsp/ModTargets.cs`) with its old last choice, so a depth saved against the old range moves what it moved.
3. A **MIDI map pin**: the same pair in `ReBuzz/Midi/Mapping/LegacyChoicePins.cs` (a version 1 map has no raw range; a version 2 map stores it). A test keeps the two tables equal.

## The engine: `TempoSync`

`Machines/NeoBuzz.Dsp/TempoSync.cs` turns tempo, division and feel into numbers. No machine writes its own table of note values or its own x 1.5 and x 2/3.

```csharp
// Once, at type load: the beats of each entry of the list, read from its names (0 for Free).
static readonly double[] SyncBeats = TempoSync.BeatsTable(Params.Table[P.Sync].Names);

// Per block: the length in beats of the chosen entry with the Feel, then seconds or Hz.
double beats = TempoSync.Beats(SyncBeats[v.Choice(P.Sync)], v.Choice(P.Feel));
double seconds = TempoSync.Seconds(beats, bpm);     // beats x 60 / bpm
double hz = TempoSync.Hz(beats, bpm);               // bpm / 60 / beats
double samples = TempoSync.Samples(beats, bpm, sampleRate);
```

- It reads the names a list stores: `1/8`, `1 bar`, `2 bars`, `1/1` (a bar), and the older inline `1/16T`, `1/8D`, `1/2 dotted` (`TempoSync.TryParse`). An old list keeps its inline entries for the songs that use them; the arithmetic is the same bits as before.
- Straight returns the length untouched, so a straight timing is bit-identical to one with no Feel.
- Basic IEEE operations only, in a fixed order, so every platform gives the same bits (the audio-path test checks the file).
- Lock a cycle to the song by computing the phase from the song position in beats (`beats / cycleBeats`), never by adding sample steps: it does not drift.
- A timing with a longest time (a delay line, a pre-delay) clamps the result to it. The editor greys out the divisions that cannot fit at the song's tempo and the readout says "clamped".

## The editor: `ParamTempoTime`

One control in the editor kit (`Machines/NeoBuzz.Editor.Avalonia/TempoTime.cs`): the Rate (or Time) knob, a segmented Free | Sync switch under it (the style of Neo Filter's Retrig buttons) and one compact Feel picker beside or under it, greyed in Free. In Free the knob reads Hz or ms; in Sync the same knob steps through the divisions, longest first, and reads e.g. "1/8 dotted"; the tooltip has the time ("1/8 dotted = 375 ms at 120 BPM"). The division choices never include "Free". `Wide = true` puts the switch and the Feel on one line (the rest of the editor decides which fits); `KnobDiameter` sizes the knob.

```csharp
// Shape A (Free is an entry of the Sync list):
ParamTempoTime.Create(ctx, new TempoTimeSpec { Division = P.Sync, Feel = P.Feel, Rate = P.Rate, Hz = true });
// Shape B (a Song tempo / Free time switch): name it as Mode.
ParamTempoTime.Create(ctx, new TempoTimeSpec { Mode = P.Sync, Division = P.Rate, Feel = P.Feel, Rate = P.FreeMs, MaxSeconds = 2 });
```

- `Hz = true` for a rate in Hz (an LFO), otherwise the synced and free values read as times. `MaxSeconds` is the longest time the machine runs (0: none). `RateLabel` names the knob.
- What it shows and writes is decided in `Machines/NeoBuzz.Editor/Core/TimingModel.cs`, with no window, and tested there: an old inline entry (`1/16T`) reads as 1/16 + Triplet, picking another division keeps that Feel, setting a Feel first turns the entry into its plain division so the two never multiply, and picking Sync goes back to the last division.

## The agent

`ValueResolver.ResolveTiming` (`ReBuzz.Agent/Model/Values.cs`) reads a timing the way a person says it: `1/8 dotted`, `1/16 quintuplet`, `1 bar triplet`, `dotted 1/8`, `1/8t`. The division is matched by its length in beats (so `1 bar` finds `1/1`), the Feel comes back beside it for the caller to write, and a list's own words (`1/16T`, `Free`, `raw:3`, a number) work as before. `ParamTools` `param_set` uses it through the machine manifest's `timings` field: `Params.def` names each timing with a `timing <division> feel=<Feel> [mode=<switch>] rate=<time> [unit=hz]` line, `tools/gen-machine-params.py` writes `timings` into the `.machine.json` (parameter names), and `param_set` of the division writes the Feel and, for a Free time with a Song tempo switch, puts the switch on Song tempo. No name convention is used.

## Tests to write for a machine with a timing

1. The list's beats against a frozen copy (new entries only at the end).
2. The synced time at three tempos (90, 120, 174 BPM), for a straight, dotted, triplet and quintuplet entry, the shortest and the longest division.
3. An old song's values load to the same sound (the chain step), and the pins.
4. 0 bytes allocated per block with sync on.
