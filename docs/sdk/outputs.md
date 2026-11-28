# Several outputs

A generator or effect can have more than one audio output. The engine runs it as a multi-output machine: `[MachineDecl(OutputCount = 3)]`, and a `Work(IList<Sample[]> output, int n, WorkModes mode)` that fills each list entry. An output that is not wired has a `null` entry: skip its work, but keep any phase that must go on running.

Name the outputs with an attribute on the class:

```csharp
[MachineDecl(Name = "Neo Split", ShortName = "Split", InputCount = 1, OutputCount = 3)]
[NeoOutputs("Low", "Mid", "High")]
public class NeoSplitMachine : NeoMachineBase { /* ... */ }
```

and answer `GetChannelName` through the helper, `NeoOutputs.ChannelName(this, input, index)`. The first name is output 1, the main one: a plain connect uses it. The studio shows one port per output in the Machines view, a "From output" picker in the Inspector and a mixer strip for each wired extra output. Buffers are allocated when a wire is made, so `Work` never allocates for an output. Details: [`docs/specs/multi-output.md`](../specs/multi-output.md).
