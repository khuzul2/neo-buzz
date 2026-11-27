# Versioned state

A song stores each machine's global parameters as **raw numbers by position**, and the machine's own data (its `MachineState`) as a block of bytes. If a later build of the machine moved a parameter, changed a range or renamed a key, an old song would load as a different sound. The rule of the project is that saved songs, presets, MIDI maps and automation **load and sound the same** after every change. Versioned state is how that is kept.

## The version of a machine

- Every Neo machine has a **state version**: the number of migration steps in its chain. A machine with no steps is version 0, and version 0 is the layout every song had before versions existed.
- A song saved at version *v* is migrated by steps *v* to *current - 1* when it loads. Each step maps what one version saved onto the next.
- The machine base class does this for you. `NeoMachineBase` implements `IVersionedMachine` (`StateVersion`, `ParameterKeys`, `MigrateGlobals`, `MigrateKeyed`, `MigrateData`, `MapParameterIndex`). It uses the machine's `Migrations` chain, or an empty one.
- The machine's data is saved in a small envelope ([`StateEnvelope`](../../ReBuzzGUI/BuzzGUI.Interfaces/MachineInterface/IVersionedMachine.cs)): at version 0, the byte 2, the size and the content, exactly as before versions existed; above 0, the byte 3, the size, the content and the version.
- A song saved by a **newer** build than the one loading is refused for that machine, not guessed at.

Values travel by **key** (the parameter's `Id`), never by position, inside a migration. A reorder therefore needs no code: you declare the layout a step starts from, and the result is laid out by the current table.

## What needs a version, and what does not

| Change | Version? |
|---|---|
| Append a parameter at the end of `Params.def` | No. Old songs have no value there, so the default stands. Give it a default that sounds like the machine did. |
| Reorder, rename an `Id`, change a range, a mapping or a default of an existing parameter | **Yes** |
| Remove a parameter | **Yes** (the step removes what a song saved for it) |
| Change the machine's own data format | **Yes** (a `Data` step) |
| Rename the user-visible `"Name"` | No: add a `was "Old name"` line ([params.md](params.md#appending-and-versions)) |

When in doubt, the test below fails and says so.

## Writing a migration

Override `Migrations` in the machine class with a static chain. Each `Step` takes the **keys of the layout it starts from** (copy them before you change the table) and the operations that bring that layout to the next one:

```csharp
internal static class EnvelopeMigrations
{
    // The keys, in order, of what version 0 saved: copied before the table changed.
    public static readonly string[] Version0 = { "mode", "attack", "decay", "sustain", "release", "threshold", "curve", "gate", "glide" };

    public static readonly StateMigrations Chain = new StateMigrations(Params.Table)
        .Step(Version0, s => s.Add("velocity", 0));
}

// in the machine class
internal override StateMigrations Migrations => EnvelopeMigrations.Chain;
```

This is Neo Envelope's real chain ([`EnvelopeMigrations.cs`](../../Machines/NeoEnvelope/Engine/EnvelopeMigrations.cs)): it gained a Velocity parameter whose default is 0 % in a song saved before it, so every note opens the envelope fully, as before. The default in `Params.def` can be different for a new machine; the step says what an **old song** gets.

The operations on a step:

| Operation | What it does |
|---|---|
| `Rename(old, new)` | The same parameter under a new key. |
| `Rescale(key, oldMin, oldMax, newMin, newMax)` | The same range moved to another; the saved value keeps its place in the range. |
| `Map(key, f)` | Any change to one saved value. |
| `Remove(key)` | The parameter is gone; what a song saved is dropped. |
| `Add(key, value)` | A new parameter takes this value in an old song (not the table's default). |
| `Split(oldKey, newKeys, f)` | One parameter became several. |
| `Merge(oldKeys, newKey, f)` | Several parameters became one. |
| `Data(f)` | The machine's own data (the XML of its `MachineState`) from this version's to the next. |

Every step runs on the loader thread, never on the audio thread. A value that comes out of a chain is clamped to the current range, and one a song did not save keeps the default.

## The layout file

[`NeoBuzz.MachineTests/Layouts/state-layouts.txt`](../../NeoBuzz.MachineTests/Layouts/state-layouts.txt) has one line per machine and version, with tabs between four fields: the machine's namespace, the state version, the number of parameters and a fingerprint of the table (each parameter's index, key, range, mapping and default). The last line of a namespace is the table as it stands.

`EveryMachineLayoutIsRecordedAndOnlyChangesWithAVersion` compares every machine's table with its last line. It fails when:

- a new machine has no line: add its version 0 line;
- the **first *n* parameters** changed, where *n* is the count on the last line, and the last line is not for a newer version. Appending parameters is fine, as only the recorded prefix is guarded;
- the versions of a namespace do not run 0, 1, 2 and so on;
- a line names a namespace that no machine has.

To change a table on purpose: add the migration step, add a line `namespace<TAB>version<TAB>count<TAB>fingerprint` for the new version, and update the golden tests below. To get the fingerprints, run `NEO_WRITE_LAYOUTS=<file> dotnet test NeoBuzz.MachineTests --filter EveryMachineLayoutIsRecordedAndOnlyChangesWithAVersion`, which writes every machine's current line to the file. Copy yours.

## The tests of a chain

`StateVersionTests` proves the mechanism on a test machine with three versions: songs saved at each version load to the same sound, the keyed and positional APIs agree, data is migrated step by step, values not saved keep the defaults and are clamped, a newer version is refused, an index follows renames and drops, and `Validate()` finds mistakes in a chain. A machine with steps runs `chain.Validate()` in its own tests and expects it to be empty.

A machine that has a version also adds a test that **a song saved at each old version loads and sounds as it did**: it builds the saved values of version 0, migrates them, and compares with the current table's. `NoteGapsTests` does it for Neo Envelope and Neo Patch.

## The null and golden tests

Bit-exact tests keep the sound from changing when the code around it does.

- **Goldens.** A golden is a file of hashes made from renders of the machine before a change; the test renders the same material now and compares the hashes. No audio is stored, only the hashes.
  - [`FxGoldenTests`](../../NeoBuzz.MachineTests/FxGoldenTests.cs) renders every effect at its defaults and at each of its factory presets, from a drum loop and a chord, and compares with `Goldens/fx-defaults.txt`. Adding Mix and Amount changed no bit at their defaults.
  - [`LoFiGoldenTests`](../../NeoBuzz.MachineTests/LoFiGoldenTests.cs) and [`NeoPolyUnchangedTests`](../../NeoBuzz.MachineTests/NeoPolyUnchangedTests.cs) do the same for Neo Lo-Fi and Neo Poly.
  - Each has an environment variable that rewrites the file (for example `NEO_WRITE_FX_GOLDEN=1`). Use it only from a build of the commit **before** your change, never to make a failing test pass.
- **The null test.** [`tools/null-test.py`](../../tools/null-test.py) renders a fixed set of cases that cover every Neo machine, through the real engine, on Windows, Linux and macOS, and requires every sample to be identical. It is why machine code uses `PortableMath` and no platform maths ([audio-thread.md](audio-thread.md#portable-maths)). The package job renders the references and the other jobs compare.
- **Cross-platform bits.** `PortableMathTests` pins the bits of every function in `PortableMath` and scans the machine sources for platform maths (`Math.Sin` and the like) and width-dependent vector calls.

A machine that adds new cases to the null test lists them in `CASES` at the top of `tools/null-test.py`; a new effect needs no golden of its own, since there is no old sound to hold still.
