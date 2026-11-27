# The audio thread

The audio thread calls `Work` for every block of every machine, and must finish in time every time. A late block is a click or a dropout. Neo machines follow these rules ([AGENTS.md](../../AGENTS.md) section 3 lists them as product rules), and each rule has a test.

| Rule | How it is kept | How it is tested |
|---|---|---|
| 0 bytes allocated per block | Make every buffer before the audio runs. | `AllocationCheck.Run` |
| No lock the UI can hold | Hand data to the UI with a `TripleBuffer` or a `volatile` field. | review, and the throughput tests |
| No denormals | Pass feedback state through `DspMath.Flush`. | the machine's tail tests |
| Every parameter smoothed | `SmoothRamp` on whatever reaches the signal. | a click test, and a zipper test |
| The same bits on every platform | `PortableMath`, no platform maths. | `PortableMathTests`, the null test |

## No allocation

Anything that allocates can start the garbage collector, and the collector can stop the audio thread for milliseconds. In `Process`:

- no `new` of an array, a list, a string or a closure (a lambda that captures a variable allocates);
- no LINQ, no `foreach` over an interface, no boxing, no string formatting;
- no `params` calls that make an array;
- no resizing of a buffer: make it at its **largest** size in the constructor or in `SetSampleRate`, and use part of it. The tutorial engine has `MaxBlock = 256` and takes a block larger than that as a mistake.

Do the work that allocates on another thread, and hand over the finished result. A new preset, a loaded sample or a changed sample rate is prepared on the UI thread and picked up by the engine in a single assignment at the start of a block.

**How it is tested.** [`AllocationCheck.Run(blocks, block)`](../../NeoBuzz.MachineTests/AllocationCheck.cs) runs a path block by block and measures the bytes this thread allocated in each. The test moves **every parameter every block**, because a path that is quiet when nothing changes can allocate when something does. The path is warmed up twice first. A measurement that finds bytes is measured again, up to three times, and the test fails only if every attempt allocates (`GuardPolicy`): a runtime's one-off does not come back, and a real allocation per block does. The failure text names the blocks and what the runtime did in them.

The tutorial's test is [step 10](tutorial.md#step-10-tests). `FxMixAmountTests` does the same for every effect, with Mix and Amount automated.

## No locks

A lock the UI thread can hold may be held when the audio thread asks for it, and then the audio waits for a person's mouse. So the audio thread **never takes a lock the UI also takes**.

- **Parameters** are read from `ParamValues`, which the host writes as plain values. Read them at the top of the block (the tutorial's `Update`), not inside the sample loop.
- **Level meters** use `LevelMeter`: one `volatile float` that the engine writes and the editor reads.
- **Snapshots** (a spectrum, a waveform, a set of voices for the editor to draw) use [`TripleBuffer<T>`](../../Machines/NeoBuzz.Dsp/TripleBuffer.cs). The audio thread fills its own buffer and swaps it in with one atomic exchange. Readers take a lock **among themselves**; the writer never does.
- **Events from the UI** (a note played on the on-screen keyboard, a button) go in a queue that the audio thread empties at the start of a block, such as the note queue of `NeoSynthBase` ([notes.md](notes.md)).

## Denormals

A very small float (below about 1e-38) is a *denormal*, and arithmetic on it can be a hundred times slower. It happens in the tail of a reverb, a filter or a feedback delay, where a value decays towards zero for ever. The machines run with IEEE subnormals on, so the same bits come out everywhere (`PortableMathTests.SubnormalsAreKept`), which means **you** remove them:

- pass the state of any feedback path through `DspMath.Flush(x)`, which sets a value smaller than 1e-18 (float) or 1e-30 (double) to zero;
- never add a "noise" constant to hide it, which changes the sound.

The tutorial tremolo has no feedback, so it has nothing to flush. A machine with a tail tests it in its own tests: ring the effect, feed silence for a long time and check the tail decays to exactly zero and the cost does not rise. Several machines' tests name subnormals; search the test project for `subnormal`.

## Smoothing

Automation, MIDI and a hand on a knob change a parameter in **steps**, one per block. A step in a gain, a cutoff or a delay time is a click. So every parameter that reaches the signal goes through a [`SmoothRamp`](../../Machines/NeoBuzz.Dsp/SmoothRamp.cs): a linear ramp over a time you choose (the tutorial uses 20 ms), followed by a 4 ms one-pole that rounds the corners. It lands exactly on the target, so a gain of exactly 1 ends at exactly 1 and a machine at its neutral setting passes the bits through.

- Call `Setup(seconds, sampleRate)` when the sample rate is known, `Reset(value)` to jump (when the machine is made or reset), `SetTarget(value)` once per block, and `Next()` once per sample.
- A parameter that sets a **rate** (an LFO's speed, a delay's feedback time) of something that only moves forward does not need a ramp on the rate itself, but its effect on the signal must not jump.
- Switches and choices that change the *path* (a filter type) need a crossfade between the two paths, as the tutorial's Shape does.

**How it is tested.** The rule is no zipper noise above -60 dB under automation. Two kinds of test:

- a **click test**: pass a steady tone through the machine while its parameters move and run the click detector on the result;
- a **zipper test**: with a steady tone, measure the energy in a band above the tone and compare it with the tone, as `ZipperDb` does in the Dye Essence tests, or the local second-difference ratio in `FxMixAmountTests.MixAndAmountHaveNoZipperOnASteadyTone`.

## Portable maths

Neo machines must give the same bits on Windows, Linux and macOS (the null test in [state.md](state.md#the-null-and-golden-tests) checks it). Platform maths libraries differ in the last bit. So audio code uses `PortableMath` and `PortableMathF` (sine, cosine, exponent, logarithm, power and the like, made from basic IEEE operations) and never `Math.Sin`, `Math.Exp`, `Math.Pow` and their relatives. `PortableMathTests` **scans the machine sources** for them and fails with the file and line. It also rejects calls whose result depends on the width of the processor's vector unit.

## Listening packs and the click detector

Tests prove the numbers. The project owner approves the **sound**, and a new instrument or effect, or any change to a sound, comes with a **listening pack** ([AGENTS.md](../../AGENTS.md) section 5):

- a render of **every preset** on the sources the task names, as 48 kHz WAV files, with a README that says what each file is;
- kept **local**, in `listening-packs/<issue>/`, which Git ignores. **Never commit audio** ([rules.md](rules.md#licences)).
- a **click detector** run on every render. The expected count is 0 clicks unless the sound is meant to click.
- for a blind A/B pack, each version is in slot A in exactly half the items (plus or minus one), chosen by a seeded shuffle, so the order is fair and can be reproduced.

The click detector is `ClickDetector.Find(samples, floor)` in the test project. It smooths the signal by [1 2 1] / 4, takes the second difference, and reports a sample as a click when its second difference is at least a floor (so a faint tick in a quiet tail does not count), at least 12 times the local mean around it, and impulsive and isolated: a click happens once and is two or three samples wide, while a filter ringing or a saw's edges repeat. The tutorial's [click test](tutorial.md#step-10-tests) uses it.

A preset must also be **clearly audible** at first listen on full-scale material and **clearly different** from the others: a subtle preset is a bug. The tutorial's preset test measures both.
