# Audio into a generator

A generator is an instrument: it plays notes and has no audio input. One that wants audio, such as a resonator that rings when a sound is fed in, declares it. Then the engine lets wires into it, sums them as it does for an effect and hands them to its `Work`.

```csharp
[MachineDecl(Name = "Neo Example", ShortName = "Example", InputCount = 1, OutputCount = 1, Generator = true)]
public class NeoExampleMachine : NeoMachineBase
{
    public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
    {
        var inL = input != null && input.Count > 0 ? input[0] : null;   // null: nothing is wired
        var outL = output[0];
        for (int i = 0; i < n; i++)
            outL[i] = voice.Next(inL != null ? inL[i] : default);
        return true;
    }
}
```

- `Generator = true` and `InputCount` above 0 are what make it take audio. Without `Generator = true` a machine with this `Work` is an effect. With `Generator = true` and `InputCount = 0` the machine does not load.
- `Work` is the effect's multi-input form: `Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)`. `input[k]` is the sum of the wires into input *k* (each at its connection's amp and pan, the sources' latency compensated), or `null` when no wire is on it. Handle `null`: an instrument works with nothing wired.
- Name the inputs by answering `GetChannelName(bool input, int index)`. Input 1 is the one a plain connect uses.
- Notes and parameters are those of any generator. The machine is a generator for the Session View, the sequence, solo and the keyboard.
- Mute, solo and bypass act as for any generator: muted or bypassed, the instrument is silent and its input is not passed on.
- Take no allocation in `Work`: the engine makes an input's buffer when the wire is made.
- A generator that does not declare an input refuses every wire, as it always did. Do not declare one you do not use.

Details and the tests: [`docs/specs/instrument-input.md`](../specs/instrument-input.md).
