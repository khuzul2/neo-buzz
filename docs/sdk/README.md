# Writing a Neo machine

This is the SDK of NeoBuzz Live: everything you need to write a machine of the Neo family, in plain English and in real units. It describes what the code does today. Where a page says "the code", it names the file, so you can read it.

If you want one page to start with, read the [tutorial](tutorial.md). It builds a small tremolo effect from nothing, and every code block in it is real code that the test project compiles and tests.

## What a Neo machine is

A Neo machine is a C# plug-in for the NeoBuzz Live engine. It is a *managed machine*: one class that implements `IBuzzMachine`. It has four parts:

| Part | What it is | Where |
|---|---|---|
| Parameters | One text file, `Params.def`, says what the parameters are: names, ranges, defaults, units. A generator turns it into C# and a JSON manifest. | [params.md](params.md) |
| Engine | The DSP, as a plain C# class with no Buzz and no UI types in it. It takes blocks of `float` samples in ±1.0. | [audio-thread.md](audio-thread.md) |
| Machine class | A thin class that gives the engine its samples, notes and sample rate. | [tutorial.md](tutorial.md) |
| Editor | Optional but expected: a window drawn with the editor kit. | [editor.md](editor.md) |

A Neo machine renders the same bits on Windows, Linux and macOS, because its audio code uses `PortableMath` instead of the system's maths library (see [audio-thread.md](audio-thread.md)). The app opens only Neo machines and its internal ones: there is no code for old Buzz machines ([rules.md](rules.md)).

### The four kinds

| Kind | Audio | Notes | Base class | `[MachineDecl]` | Studio project `NeoKind` | Examples |
|---|---|---|---|---|---|---|
| Generator (an instrument) | out | in (MIDI, and Note and Velocity tracks) | `NeoSynthBase` | `InputCount = 0, OutputCount = 1`, `MaxTracks` up to 16 | `Generators` | Neo Poly, Neo Drums, Neo Sampler |
| Effect | in and out | none | `NeoMachineBase` | `InputCount = 1, OutputCount = 1` | `Effects` | Neo Drive, Neo Comp, Neo Utility |
| Control | none | none (it listens to the notes of a machine) | `NeoModulatorBase` | `InputCount = 0, OutputCount = 0` | `Generators` | Neo LFO, Neo Envelope, Neo Step, Neo Macro |
| Note machine | none | in and out, on note wires | `NeoMachineBase` with `INoteCapable` and `INoteReceiver` | `InputCount = 0, OutputCount = 1` | `Generators` | Neo Chord, Neo Arp |

- A **generator** plays notes. Its notes come from clips, the piano roll, Session, MIDI keyboards and note wires, all through one intake: see [notes.md](notes.md).
- An **effect** has a `Work(Sample[] output, Sample[] input, int n, WorkModes mode)` and must have Mix and Amount ([tutorial.md](tutorial.md#step-5-mix-and-amount)).
- A **control** has no audio. Once per block it moves up to 8 parameters of other machines. Its base class does the finding, the smoothing and the saving of its targets: read [NeoModulator.cs](../../Machines/NeoBuzz.Machine/NeoModulator.cs).
- A **note machine** takes notes and sends notes, sample-accurately, and makes no sound itself: see [notes.md](notes.md#note-output-on-wires).

## The folder layout

A machine named *Neo Tremolo* lives in `Machines/NeoTremolo/`:

```
Machines/NeoTremolo/
  Params.def                  the parameter table: the one source of truth (you write it)
  Presets.txt                 factory presets, in real units (you write it)
  NeoTremolo.cs               the machine class (you write it)
  NeoTremoloEditor.cs         the editor (you write it)
  Engine/
    TremoloEngine.cs          the DSP (you write it)
    Params.g.cs               generated: indices P.*, and the table Params.Table
  Machine.Params.g.cs         generated: the [ParameterDecl] properties
  Neo Tremolo.machine.json    generated: the manifest for the agent server
  Neo Tremolo.prs.xml         generated: the factory presets
Machines/Studio/NeoTremolo/NeoTremolo.Studio.csproj       the build with the editor (Avalonia)
Machines/Portable/NeoTremolo/NeoTremolo.Portable.csproj   the build without an editor (headless, tests, Mac engine)
```

The shared code is **linked as source** into every machine, not shipped as a library:

| Folder | What is in it |
|---|---|
| [`Machines/NeoBuzz.Dsp/`](../../Machines/NeoBuzz.Dsp) | Pure DSP: filters, delay lines, oscillators, smoothers, `PortableMath`, `FxStage` (Mix and Amount), `ParamDef` and `ParamValues`, `StateMigrations`. |
| [`Machines/NeoBuzz.Machine/`](../../Machines/NeoBuzz.Machine) | The Buzz glue: `NeoMachineBase`, `NeoSynthBase`, `NoteInput`, `NeoModulatorBase`. |
| [`Machines/NeoBuzz.Editor/`](../../Machines/NeoBuzz.Editor) | The editor kit's toolkit-free part: canvas, tokens, graphs, `EditorContext`. |
| [`Machines/NeoBuzz.Editor.Avalonia/`](../../Machines/NeoBuzz.Editor.Avalonia) | The editor kit on Avalonia: knobs, switches, meters, `NeoEditor`. |
| [`NeoBuzz.MachineTests/`](../../NeoBuzz.MachineTests) | The Linux test project. It compiles every machine's `Engine/` folder and tests it without ReBuzz. |

The two project files are three lines each: they set `NeoFolder`, `NeoName` and `NeoKind` and import the shared build file. The generators read the gear name and kind from them.

## Build and test

You need the .NET 10 SDK and Python 3.

```bash
python3 tools/gen-machine-params.py Machines/NeoTremolo      # Params.def -> the three generated files
python3 tools/gen-machine-presets.py Machines/NeoTremolo     # Presets.txt -> the .prs.xml
dotnet build Machines/Studio/Studio.proj -p:GearDir=/tmp/gear            # every machine, with editors
dotnet build Machines/Portable/Portable.proj -p:GearDir=/tmp/gear-portable   # every machine, no editors
dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~Tremolo"       # the engine tests
```

CI checks that every generated file is up to date (`--all --check` on both generators), so commit the generated files with your change.

## What a new machine needs

[AGENTS.md](../../AGENTS.md) (section 4) lists everything: the folder, the two projects, the solution entry, the manifest, the catalogue and profile entries, factory presets, the editor and its gallery shots, the agent guide, the tests, and a row in [docs/specs/machines.md](../specs/machines.md) and in `FORK.md`. The [tutorial](tutorial.md) walks through each one except the agent guide.

## The pages

| Page | What it covers |
|---|---|
| [tutorial.md](tutorial.md) | Build a tremolo effect step by step: parameters, engine, Mix and Amount, presets, editor, entries, tests. |
| [outputs.md](outputs.md) | A machine with several outputs: `OutputCount`, `[NeoOutputs]` names, how the studio routes them. |
| [inputs.md](inputs.md) | A generator that takes audio: `Generator = true` with `InputCount`, how its `Work` reads the input, how the studio wires it. |
| [params.md](params.md) | The `Params.def` syntax (every map kind, enums, switches, groups, how and sound lines, track parameters, appending) and the `Presets.txt` syntax. |
| [editor.md](editor.md) | The editor kit: controls, binding, theming, the gallery. |
| [notes.md](notes.md) | How notes reach a machine, how a machine sends them, and the note contract tests. |
| [state.md](state.md) | Versioned state, migrations, `state-layouts.txt`, and the golden and null tests. |
| [audio-thread.md](audio-thread.md) | No allocation, no locks, denormals, smoothing, and how each is tested; listening packs and the click detector. |
| [export.md](export.md) | Exporting a machine as a CLAP, VST3 or AU plug-in. |
| [rules.md](rules.md) | Clean room, no trademarked names, licences. |

## Checked like code

The tests in [`SdkDocTests.cs`](../../NeoBuzz.MachineTests/Sdk/SdkDocTests.cs) keep these pages honest: every code block of the tutorial equals a region of the fixture machine (`NeoBuzz.MachineTests/Sdk/TutorialTremolo/`), every relative link on these pages resolves, and this page links every other page.
