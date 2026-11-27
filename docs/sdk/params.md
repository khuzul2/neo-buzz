# Params.def and Presets.txt

A Neo machine's parameters are written once, in `Params.def`, in the machine's folder. [`tools/gen-machine-params.py`](../../tools/gen-machine-params.py) turns it into the table the engine, the editor and the tests read, the machine's parameter properties and the manifest. Presets are written in real units in `Presets.txt` and turned into the file ReBuzz loads by [`tools/gen-machine-presets.py`](../../tools/gen-machine-presets.py). This page lists what both files accept. [The tutorial](tutorial.md#step-2-paramsdef) shows them in use.

Run the generators on one folder, or on all with `--all`. CI runs both with `--all --check`, which writes nothing and fails if a generated file is out of date, so commit the generated files with your change.

## The lines of Params.def

A line starting with `#` is a comment. Lines that start with a space or tab, and begin with `id`, `how`, `sound` or `was`, belong to the parameter above them.

| Line | Meaning |
|---|---|
| `namespace NeoBuzz.Drive` | The C# namespace of the generated code. Required. |
| `class NeoDriveMachine` | The machine class, which is a `partial` class. Required. |
| `summary What it is. Use it for...` | One or two sentences for the manifest: what the machine is and what to use it for. |
| `group Shape` | The group the following parameters show in, in the editor and the manifest. |
| `Id "Name" min max default kind args \| description` | A parameter (see below). |
| `tracks 16` | The machine has 16 tracks. It gets the track parameters Note and Velocity. |
| `track Id "Name" min max \| what` | Another track parameter after Velocity. |
| `track Id "Name" switch \| what` | The same, as an off or on switch. |
| `reading key unit \| what` | A value the machine measures, which the agent can read. |
| `import kind .ext,.ext \| what` | A data file the machine loads (a sample, an impulse response). |
| `notekey 36 "Kick" [Group]` | A key that makes a sound, with its name and an optional group ([notes.md](notes.md#the-note-map)). |
| `notekey 48 60 [Group]` | A range of unnamed keys. Keys run 0 to 127. |

Indented lines after a parameter:

| Line | Meaning |
|---|---|
| `id snake_case_id` | The id the agent and the manifest use. The default is the C# `Id`. |
| `how One sentence.` | How the control changes the sound. It is the editor's Explain text. The description defaults to it. |
| `sound One sentence.` | What you hear. |
| `was "Old name"` | A name the parameter had before a rename. One line for each former name. |

## One parameter

```def
Cutoff "Cutoff" 0 1000 600 exp 20 20000 Hz 0 | Where the filter starts to cut
    how Sets the frequency above which the sound is cut.
    sound Lower values make the sound darker and rounder.
```

The fields, in order:

- **`Id`** is the C# name. It becomes the index constant `P.Cutoff`, the property on the machine and, unless an `id` line says otherwise, the key the agent and saved songs use.
- **`"Name"`** is what the user sees.
- **`min max default`** is the **raw** range the host stores: whole numbers with `0 <= min <= default <= max <= 65534`. A maximum above 254 makes a word parameter, which ReBuzz stores in two bytes. The raw range is how finely the control moves, so use 1000 for a smooth knob and a tight range for a choice.
- **`kind args`** says how a raw value maps to the value you read in the engine, in real units.
- **`| description`** is shown in the editor and sent to the agent. When a `how` line is present and there is no description, the `how` text is used.

An `Id` and a name must each be used once in a machine.

## Map kinds

`t` is the position in the raw range, 0 at `min` and 1 at `max`.

| Kind | Arguments | The value you read |
|---|---|---|
| `lin` | `A B [unit] [digits]` | `A + t (B - A)` |
| `exp` | `A B [unit] [digits] [detent=N]` | `A (B / A)^t`. Equal steps are equal ratios, so it suits frequencies and times. `detent=N` makes the value rest on multiples of N (within 1.5 %). |
| `pow` | `A B EXP [unit] [digits]` | `A + t^EXP (B - A)`. For a control that needs more room at one end. |
| `db` | `A B [inf]` | Decibels, linear in dB. With `inf` the bottom of the range is minus infinity. |
| `time` | `A B` | Seconds, exponential. |
| `freq` | `A B` | Hertz, exponential. |
| `pct` | `[digits]` | A fraction from 0 to 1, shown as per cent. |
| `bip` | `[digits]` | A fraction from -1 to 1, shown as signed per cent. |
| `int` | `OFFSET [unit] [signed]` | `raw + OFFSET`: a count, a semitone or an octave. `signed` shows a plus sign. |
| `enum` | `Name1 Name2 ...` | A choice. There must be exactly one name for each value from `min` to `max`. An underscore in a name shows as a space. |
| `switch` | `[OffName OnName]` | Off or on. The range must be two values wide. The names default to Off and On. |
| `ratio` | `A B [inf]` | A ratio `n:1`, exponential. With `inf` the top is infinity. |

In the engine you do not compute any of this. Read the value through `ParamValues`:

- `values.Value(P.Cutoff)` is the real value (Hz, dB, seconds, a fraction, a choice index);
- `values.F(P.Depth)` is the same as a `float`;
- `values.Choice(P.Shape)` is an enum's index;
- `values.On(P.Bypass)` is a switch.

The parameter table works the other way too: `def.Raw(physical)` gives the raw number for a real value, and the two are inverse over the whole range (the tutorial's [state test](tutorial.md#step-10-tests) proves it for the fixture).

## Mix and Amount

Every effect ends its `Params.def` with the two lines below. They come last, and a machine with a delay, a limiter or another latency says so in the Mix description. Neo's own effects all use these names, ranges and defaults:

```def
Mix    "Mix"    0 1000 1000 pct               | Dry/wet
Amount "Amount" 0 2000 1000 lin 0 200 % 1     | Effect strength: 100 % = the settings as shown, 0 % = bypass
```

[`FxStage`](../../Machines/NeoBuzz.Dsp/FxStage.cs) does the rest ([tutorial, step 5](tutorial.md#step-5-mix-and-amount)). Only meters and analysers, which pass audio unchanged, have neither.

## Track parameters

A machine with tracks (a synth that plays tracks of notes in a clip) says `tracks 16`. The generator then writes two stateless track parameters, **Note** and **Velocity** (1 to 127, 100 when empty), in `Machine.Params.g.cs`. Notes go to `Notes.SetNote` and `Notes.SetVelocity` ([notes.md](notes.md)). More track parameters:

```def
tracks 16
track Glide  "Glide"  0 254 | Slide time into this note
track Accent "Accent" switch | An accented note
```

A track parameter is stateless: a value plays, and an empty cell does nothing. The machine receives it in `OnTrackParameter(TP.Glide, track, value)`. A track parameter's maximum over 254 makes a word parameter, as for a global one. Track parameters are not part of the global table, so they have no map kind and no Amount.

## Appending and versions

A song stores each global parameter's **raw value by its position**. So the table has these rules:

- **Appending a parameter at the end is free.** Songs saved before have nothing at that position, and the default stands. Give the new parameter a default that sounds like the old machine, so that an old song still sounds the same. This is how Mix and Amount were added to the effects.
- **Changing anything else is a new version:** reordering, renaming an `Id`, a different range, a different mapping, a different default for a parameter that existed. It needs a migration step, a new line in `state-layouts.txt` and the golden tests that prove old songs sound the same. [state.md](state.md) has all of it, and the test that fails until you do it.
- **Renaming what the user sees** (the `"Name"`) is not a state change, but a user preset saved by name needs to find it again: write `was "Old name"` under the parameter. The id, the index and saved state do not change with a rename.
- **Never reuse an index** for another meaning.

## Presets.txt

`Presets.txt` is read together with `Params.def`, in the same folder. It writes each preset in real units, so a person can read it and review it.

```text
machine "Neo Drive"

preset "Tape — Warm Bus"
comment "Soft saturation that thickens a mix."
Mode = Tape
Drive = 9
Mix = 60
Cutoff = raw:512
```

| Line | Meaning |
|---|---|
| `machine "Display Name"` | The machine's display name, the name of its gear. Required. It names the output file `<Display Name>.prs.xml`. |
| `preset "Group — Name"` | A new preset. `"Group — Name"` (a long dash) files it under a group in the editor's preset bar. A name must be used once. |
| `comment "text"` | The preset's comment, which the preset list shows and searches. It follows its preset line. |
| `Param = value` | A value, in the unit of the parameter's kind. The name is the parameter's `"Name"` or its `Id`, and the case does not matter. |
| `state Class` | Before the first preset: the class of the machine's `MachineState`, for presets that carry data. |
| `state Property = text` | After a preset line: the machine's own data in this preset, as text. |

How a value is written:

- an enum or a switch by its **name** (`Mode = Tape`, a switch by `On` or `Off`);
- a number in the **real unit** for `lin`, `exp`, `pow`, `db`, `time`, `freq` and `int` (dB, Hz, seconds, degrees, semitones), `-inf` for the bottom of a `db` with `inf`, and `inf` for the top of a `ratio` with `inf`;
- a **percentage** from 0 to 100 for `pct`, and from -100 to 100 for `bip`;
- `raw:512` for a raw value, when no real number is the point.

A parameter a preset does not name takes its default. The generator rounds a real value to the nearest raw one, and stops with the line number for a name it does not know, a choice that is not in the list or a raw value out of range.

Presets must be clearly audible on full-scale material and clearly different from each other ([rules.md](rules.md#presets)). Name each by the sound it makes, never by a brand or a person ([rules.md](rules.md#no-trademarked-names)).
