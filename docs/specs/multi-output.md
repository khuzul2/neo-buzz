# Machines with several outputs (MO1, issue #228)

A machine can have more than one audio output, each wired to its own chain. Neo Split (#201) is the first that will: one input, several outputs. This page is what the code does today.

## Rules

- **Output 1 is the main one.** A plain connect (the menu, a drop, `connect` without `from_output`) uses it, as before. A machine with one output looks and works exactly as it did.
- **A Neo machine names its outputs** with `[NeoOutputs("Low", "Mid", "High")]` on its class (`Machines/NeoBuzz.Machine/NeoOutputs.cs`), next to `[MachineDecl(OutputCount = 3)]`. Without names an output is called "Output 2" and so on. See [`docs/sdk/outputs.md`](../sdk/outputs.md).
- **A wire carries its source output** (`IMachineConnection.SourceChannel`). `.nbl` already saved it in the connection's channel lists; a song without it loads on output 1, bit for bit as before.
- **The engine allocates an output's buffer when the wire is made**, on the UI or loading thread (`MachineCore.PrepareMultiIo`, `MachineWorkInstance.PrepareOutputs`), never on the audio thread. The audio thread skips an output that has no buffer.

## The Studio

- **Machines view.** A machine with several outputs draws one port per output down its right edge; hovering one names it ("High, output 3 of 3 of Multi"). A wire is dragged from a port. Dragging from the box (or Shift-drag) uses output 1.
- **Inspector.** The selected machine has a **Wires** section: each outgoing wire (and each incoming wire from a machine with several outputs) has a **From output** picker. A change is one undo step.
- **Insert on a wire** keeps the wire's output: the wire into the inserted machine leaves the same output, and the machine's own wire leaves its output 1.
- **Mixer.** Each wired extra output (not output 1, which is the machine's own strip) has a strip "Multi · High", grouped under its machine's track, with the wire's own fader (the connection's amp), pan, mute and solo. Solo on one wire silences the machine's other wires. Strips come and go with the wires. Wire mute and solo are not saved in `.nbl`.

## The agent

- `connect` takes `from_output`: a number from 1 or a name ("High", any case). An error lists the machine's outputs. `connection_set` takes it too, to move a wire.
- `machine_list` and `machine_describe` report each machine's `output_ports` (index and name), and each wire's `from_output`.
- `machine_swap` keeps each wire on the output of the same number, or on output 1 if the new machine has fewer outputs, and the reply's `moved_wires` says which moved.
- Every edit is one undo step.

## Gates (tests)

`ReBuzz.Studio.Tests/MultiOutputTests.cs` and `MultiOutputUiTests.cs`, against `TestMachines/MultiOut` ("Multi Test": three outputs, Low, Mid and High, a sine each; test-only, not shipped): routing (each chain from its own output, the mix is the sum to the last bit), 0 bytes (the first block after a wire from output 3, and 2000 blocks), save and load, undo of connect and the picker, the agent, the mixer's strips and faders, and the gallery shots (`multi-output-*`).

## Not done

- The Session View's own mixer has no sub-strips (its columns follow the session grid); it shows a machine's output 1 as before.
