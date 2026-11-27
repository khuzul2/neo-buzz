# Plug-in export

A Neo machine can be exported as a **CLAP**, **VST3** or **Audio Unit** plug-in, made from the machine's **own engine and parameter table**, so the plug-in sounds like the machine. Nothing is written per machine except one small descriptor. The generator is [`tools/gen-plugin-export.py`](../../tools/gen-plugin-export.py), and the export lives in [`PluginExport/`](../../PluginExport).

Not every machine is exported. Machines in the export today include the filter, delay, compressor, EQ and reverb, the drums, the polyphonic synth and the FM synth, and the Neo Learn synths. Export is a step you take **when a task asks for it**, after the machine works.

## What the exported machine needs

The export compiles the machine's `Engine/` folder into a plug-in library (`neo_dsp`) and its editor into another (`neo_editor`), with no ReBuzz. So:

- **The engine must not depend on Buzz types.** Everything in `Engine/` is plain C#: `ParamValues`, `ParamDef`, the shared DSP. This is the same rule as the tutorial's engine, which is why a machine splits into an engine and a thin machine class. Keep the Buzz and host types in the machine class.
- **The engine has the members the export forwards to:**
  - `public const int MaxBlock` and a constructor that takes the `ParamValues`;
  - `SetSampleRate(float)`, `Reset()` and `Latency`;
  - an effect has `Process(left, right, n)` (or `Process(left, right, sidechainLeft, sidechainRight, n)` when the descriptor says `sidechain`);
  - an instrument has `Process(left, right, n)` returning false when silent, and `NoteOn(key, note, velocity)`, `NoteOff(key)`, `ModWheel`, `PitchBend`, `Sustain`, `Aftertouch` and `AllNotesOff(hard)` (the same `INoteSink` the machine uses, [notes.md](notes.md));
  - `Tempo` when the descriptor says `tempo`, and `Idle` when it says `idle`.
- **The machine class and the editor can be made with a null host.** The plug-in editor makes `new <Machine>(null)` and `new <Editor>()`, with no engine thread, only the machine's own `Values`. An editor that reads the machine in `BuildBody` must cope with `ctx.Machine` having no engine (the tutorial's `machine?.Engine.InMeter.Peak ?? 0` does).
- **Parameter ids, ranges, texts, units, groups, state, latency and ports** all come from the engine and its `Params` table at run time. A machine that follows [params.md](params.md) has them already. A plug-in's state is its parameters' values, so the append-only rule of [state.md](state.md) is what keeps a saved plug-in session loading.

## The descriptor

One JSON file in `PluginExport/descriptors/`, named for the machine (`neo-filter.json`):

```json
{
  "id": "org.neobuzz.neo-filter", "name": "Neo Filter", "folder": "NeoFilter", "namespace": "NeoBuzz.Filter",
  "engine": "FilterEngine", "machine": "NeoFilterMachine", "editor": "NeoFilterEditor",
  "kind": "effect", "features": ["filter"], "au": "NFlt",
  "description": "Resonant filter with envelope follower and LFO (ladder and state-variable, oversampled drive)",
  "process": "stereo", "tempo": true, "transport": "ticks", "idle": true, "tailSeconds": 2,
  "test": {"changes": [["Type", "raw:2"], ["Cutoff", "raw:5000"], ["Resonance", "raw:500"]], "settle": 0.5}
}
```

| Field | Meaning |
|---|---|
| `id`, `name` | The plug-in's unique id (reverse domain, `org.neobuzz.<machine>`) and its display name. |
| `folder`, `namespace`, `engine`, `machine`, `editor` | The machine's folder under `Machines/`, its C# namespace, and the classes: the engine (in `Engine/`), the machine class and the editor. |
| `gear` | The machine's name in ReBuzz, when it differs from the plug-in's. |
| `kind` | `effect` or `instrument`. |
| `features` | CLAP features after the kind's own (`filter`, `delay`, `compressor` and so on). Stereo is always added. |
| `au` | A four-character Audio Unit subtype, unique among the plug-ins. |
| `description` | One line for a plug-in browser. |
| `process` | `stereo`, `sidechain`, `synth` or `learn` (a Neo Learn synth, with `map`: its macro-map class). |
| `tempo`, `transport` | `tempo: true` gives the engine the host's tempo (`engine.Tempo`). `transport: "ticks"` gives it the song position as a `FilterTransport`. |
| `idle` | `true`: `engine.Idle` gates the render, so a silent plug-in costs nothing. `"quiet"`: the machine's own rule of 16 blocks with no input and no sound. |
| `latency` | `false` when the engine has no latency to report. |
| `tailSeconds` | How long the sound rings on after the input stops, so the host does not cut it. |
| `test` | `changes`: parameters to move for the null test, each as a name and a raw value (`["Cutoff", "raw:5000"]`); `settle`: seconds to wait. `tolerance` may widen the null test's limit. |

## Generate, and what is generated

```bash
python3 tools/gen-plugin-export.py            # write the generated files
python3 tools/gen-plugin-export.py --check    # CI: fail when a generated file is out of date
```

It reads every descriptor and writes: the adapter for each engine and the registry of plug-ins (`dsp/Generated/Registry.g.cs`), the list of sources each library compiles (`Sources.g.props`, for the DSP and the editor), the editor registry, the CMake list of plug-ins (`cmake/plugins.cmake`) and the manifest the tests read (`plugins.json`). **Commit them**: the `plugin-export` workflow runs `--check`.

The generated adapter only forwards: its `Table` is the machine's own `Params.Table`, its `Create` makes the engine, and its `Process` calls the engine's.

## Tests of an export

The export has its own CI ([`PluginExport/ci/`](../../PluginExport/ci)), run by the `plugin-export` workflow:

- **A null test** ([`null-test.py`](../../PluginExport/ci/null-test.py)): the native machine and the exported plug-in (as a CLAP and as a VST3) render the same song in the project's own plug-in host, and the outputs are subtracted. They must null to **-100 dBFS** (a tolerance of 1e-5). The plug-in's parameters are compared with the machine's (count, names, texts, units). Then the song is saved, reopened and rendered again.
- **Self-tests** of the runtime (`PluginExport/selftest`) and a smoke test of the editor (`editor-smoke`).
- **An Audio Unit list** (`ci/au-list.py`), so the system finds every component.

A machine that has been exported must keep nulling. A change to its engine is therefore checked twice: by its own tests and by this one, so keep an exported machine's `Engine/` free of anything that is different in a plug-in (a file path, a ReBuzz host call, a UI thread).
