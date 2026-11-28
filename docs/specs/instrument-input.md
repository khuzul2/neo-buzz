# Audio into an instrument (AI1a, issue #229)

A generator can take audio when it declares an input. This is the host side: the engine, the Machines view, the Inspector, the agent and the song file. The first machines to use it, Neo Mallet's resonator and Neo Grain's live input, come in AI1b. This page is what the code does today.

## Rules

- **Declared, not general.** A generator takes audio only if its `[MachineDecl]` says `Generator = true` and `InputCount` above 0, and its `Work` has the effect's shape with an input list. Every other generator, and every song saved before, behaves as it did: a wire into one is refused with the same words. See [`docs/sdk/inputs.md`](../sdk/inputs.md).
- **The same engine path as an effect.** The wires into the generator are summed per input (each connection's amp and pan, and the latency compensation of the sources), and the generator's `Work` reads them as an effect's multi-input `Work` does. Its notes and parameters work as any generator's. The buffers are made when the wire is made (`MachineCore.PrepareMultiIo`), so the audio thread allocates nothing.
- **Mute, solo and bypass act as for any generator.** A muted instrument drops its input (it is silent); solo silences the other generators, and an instrument that takes audio is one of them; a bypassed one is silent, as a bypassed generator is, and does not pass its input on.
- **The loop check stays.** A generator cannot be fed from its own output chain: Input Test → Neo Utility → Input Test is refused.
- **Session View.** It stays a track's instrument. Its input wires are drawn in the Machines view and listed in the Inspector like an effect's.
- **The song file.** The wire is an ordinary connection in `.nbl` (source, destination, channels, amp, pan). A song saved before this loads and plays bit for bit as it did. A `machine_swap` to a generator without an input leaves its wires out; undo brings them back.

## The Studio

- **Machines view.** A generator that declares an input has an in-port on the left edge, the same dot as an effect's, shown when the machine is hovered, selected or a wire is being dragged. Hovering it says "Input of NAME: drop a wire from another machine's output here". Drag, drop and Shift-drag connect to it, and insert on a wire into it works. A refused wire says why, in the words it always had: "NAME is an instrument: it has no input" for a generator without an input.
- **Inspector.** The selected instrument's Wires row counts its input wires with its output wires.

## The agent

- `connect` into such a generator works, with `gain_db` and `pan`. For any other generator it says "NAME is a generator and takes no audio input. Connect into an effect or Master."
- `machine_list` and `machine_describe` give `audio_inputs` (the number of inputs) for a generator that takes audio and nothing for one that does not; `gear_list` gives it for the gear. The `machine_list` text says "(takes audio: 1 input)".
- Every edit is one undo step.

## Gates (tests)

`ReBuzz.Studio.Tests/InstrumentInputTests.cs` and `InstrumentInputUiTests.cs`, against `TestMachines/InstrumentInput` ("Input Test": its input times a gate open while a note is held, plus a quiet sine at the note's pitch; "Noise Test": a seeded noise; both test-only, not shipped):

- **Routing.** A noise source wired into Input Test, a note held for 188 blocks (1 s): the output is the input during the note and silence outside it, sample for sample against a reference built from the source rendered alone and the voice rendered alone; two sources sum with their connection amps (0 dB and -6 dB).
- **Refusal.** A wire into Neo Poly is refused in the core, the view and the agent, with the reason.
- **Loop.** Input Test → Neo Utility → Input Test is refused, in the core, the view and the agent.
- **0 bytes.** 2000 blocks with the input wired, and the block after a new connection while playing, allocate 0 bytes on the audio thread.
- **Save, load, undo.** The wire round-trips in `.nbl` and the song renders sample for sample as before; connect, undo and redo, insert on the wire, and a swap, leave the graph as expected.
- **Agent.** `connect` into Input Test and the refusal for Neo Poly, through the tool.
- **UI.** Gallery shots, dark and light: the Machines view with a source wired into Input Test (its in-port drawn), and the Inspector on it (`instrument-input-*`).

## Not done

- No shipped machine takes audio yet (AI1b).
- The Session View's mixer has no strip for an instrument's input wires.
