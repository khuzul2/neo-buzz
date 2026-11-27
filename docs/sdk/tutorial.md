# Tutorial: a tremolo effect

You will build **Tutorial Tremolo**: a low-frequency oscillator moves the level of the sound, with a sine or triangle shape, a phase offset between the two channels, and the Mix and Amount controls every Neo effect has. It is small on purpose. Every kind of file a real machine has appears once.

Every code block on this page is a region of real source in [`NeoBuzz.MachineTests/Sdk/TutorialTremolo/`](../../NeoBuzz.MachineTests/Sdk/TutorialTremolo). The test project compiles and tests it, and [`SdkDocTests.cs`](../../NeoBuzz.MachineTests/Sdk/SdkDocTests.cs) fails if a block here differs from its source by one byte. The machine class and the editor need the Buzz and editor kits, which the test project does not have, so a project of their own, `TutorialTremolo.Studio.csproj`, builds them (see [the end of this page](#step-11-what-a-real-machine-adds)).

The fixture is a test, not a shipped machine. In a real machine the same files live in `Machines/NeoTremolo/` with two project files; the commands below use that path. To regenerate the fixture's own generated files, run `python3 NeoBuzz.MachineTests/Sdk/TutorialTremolo/regenerate.py`: it runs the same two generators and supplies the gear name and catalogue entry that a real machine's projects and catalogue would.

## Step 1: The plan

| Control | Unit | What it does |
|---|---|---|
| Rate | 0.1 to 20 Hz | how fast the level moves |
| Depth | 0 to 100 % | how far the level falls |
| Shape | Sine or Triangle | the shape of the movement |
| Phase | 0 to 180 degrees | the right channel's offset against the left |
| Mix | 0 to 100 % | dry against wet |
| Amount | 0 to 200 % | how strong the effect is: 100 % is the settings as they are, 0 % is bit-exact dry |

The gain at a position in the cycle is `1 - depth * dip`, where `dip` runs from 0 at the top to 1 at the bottom. The sound is not delayed, so the effect has no latency.

## Step 2: Params.def

`Params.def` is the one source of truth for the parameters. A header names the C# namespace and the machine class, and gives the manifest's summary:

<!-- snippet: ParamsHeader -->
```def
namespace NeoBuzz.TutorialTremolo
class TutorialTremoloMachine
summary A tremolo for the SDK tutorial: a low-frequency oscillator moves the level, with a sine or triangle shape, a phase offset between the channels and Mix and Amount. Use it for swells, pulses and a slow stereo pan.
```

Each parameter is one line: an id, a name, the raw `min max default`, a *map kind* with its arguments, and after `|` a description. The indented `how` and `sound` lines are the sentences the editor's Explain strip and the agent read. A `group` line groups the parameters that follow.

<!-- snippet: ParamsBody -->
```def
group Tremolo
Rate   "Rate"   0 1000 740  exp 0.1 20 Hz 2   | Speed of the level movement
    how Sets how many times a second the level moves down and up, from 0.1 to 20 Hz.
    sound Slow rates swell like breathing; fast rates flutter and become a buzz.
Depth  "Depth"  0 1000 500  pct               | How far the level moves down
    how Sets how far the level falls at the bottom of each cycle: 0 % none, 100 % to silence.
    sound More depth makes the pulse deeper and more obvious.
Shape  "Shape"  0 1 0       enum Sine Triangle | Shape of the level movement
    how Chooses the shape of the movement: Sine or Triangle.
    sound Sine is smooth and round; Triangle is sharper and more mechanical.
Phase  "Phase"  0 1000 0    lin 0 180 ° 0     | Phase offset of the right channel
    how Delays the right channel's movement against the left, from 0 to 180 degrees.
    sound At 180 degrees one side is loud while the other is quiet, so the sound pans from side to side.
group Output
Mix    "Mix"    0 1000 1000 pct               | Dry/wet (the effect has no latency, so the dry signal is not delayed)
    how Blends the tremolo with the unprocessed sound.
    sound Lower values keep the original level steady and add the pulse as a layer.
Amount "Amount" 0 2000 1000 lin 0 200 % 1     | Effect strength: 100 % = the settings as shown, 0 % = bypass (bit for bit the input), 200 % = twice as far from neutral
    how Scales Depth. 0 % is bypass, 100 % your settings, 200 % twice as far from neutral.
    sound Turn it down to ease the tremolo in, up to push it harder.
```

Things to notice:

- **Raw values are what songs store.** Rate's raw value 0 to 1000 is mapped to 0.1 to 20 Hz on an exponential scale (`exp 0.1 20 Hz`), so equal knob steps are equal musical steps. The default 740 is about 5 Hz. The engine never sees the raw number: it asks the table for the real value in Hz.
- **Real units everywhere.** Phase is in degrees (`lin 0 180 °`), Amount in per cent. The units show in the editor, the pattern editor and the manifest.
- **Mix and Amount are the last two lines.** Every effect has both, and they are appended after the machine's own parameters ([params.md](params.md#appending-and-versions) says why order matters).
- **Every enum choice is a name.** `enum Sine Triangle` has two names for the range 0 to 1; the generator checks the count.

[params.md](params.md) lists every map kind and every line the file understands.

## Step 3: Generate the code

Run the two generators on the machine's folder:

<!-- snippet: GenerateCommands -->
```bash
python3 tools/gen-machine-params.py Machines/NeoTremolo
python3 tools/gen-machine-presets.py Machines/NeoTremolo
```

The first writes three files. Commit them: CI fails if one is out of date.

`Engine/Params.g.cs` is the table the engine, the editor and the tests read. It has the index constants, and for each parameter a `ParamDef` that holds its range, its mapping and its texts:

<!-- snippet: GeneratedIndices -->
```csharp
internal static class P
{
    public const int Rate = 0;
    public const int Depth = 1;
    public const int Shape = 2;
    public const int Phase = 3;
    public const int Mix = 4;
    public const int Amount = 5;
    public const int Count = 6;
}
```

<!-- snippet: GeneratedEntry -->
```csharp
new ParamDef(P.Shape, "Shape", "Shape", 0, 1, 0, MapKind.Enum, names: new[] { "Sine", "Triangle" }, group: "Tremolo", description: "Shape of the level movement", how: "Chooses the shape of the movement: Sine or Triangle.", sound: "Sine is smooth and round; Triangle is sharper and more mechanical."),
```

`Machine.Params.g.cs` gives the machine class one property per parameter, in the shape ReBuzz expects. The setter writes the value into the machine's `ParamValues`:

<!-- snippet: GeneratedProperty -->
```csharp
[ParameterDecl(Name = "Depth", Description = "How far the level moves down", MinValue = 0, MaxValue = 1000, DefValue = 500)]
public int Depth { get => Values[P.Depth]; set => Values.Set(P.Depth, value); }
```

`Neo Tremolo.machine.json` is the manifest: every parameter with its unit, mapping and texts, for the agent server, which sets values in real units. Here is Phase:

<!-- snippet: ManifestExcerpt -->
```json
"index": 3,
"id": "Phase",
"name": "Phase",
"group": "Tremolo",
"min": 0,
"max": 1000,
"default": 0,
"kind": "linear",
"a": 0.0,
"b": 180.0,
"unit": "°",
"description": "Phase offset of the right channel",
```

## Step 4: The engine

The engine is a plain class: no Buzz types, no editor, so the test project compiles it as it is. It keeps its state in a few fields:

<!-- snippet: EngineFields -->
```csharp
public const int MaxBlock = 256;
const double SmoothSeconds = 0.02;

readonly ParamValues values;     // what the engine reads: the machine's values with Amount applied
readonly FxStage fx;
SmoothRamp depth, offset, triangle;
double sampleRate = 48000;
double phase;                    // 0 … 1, the left channel's position in the cycle
double increment;                // phase per sample
```

`values` is not the machine's own `ParamValues`. It is the *effective* one that the Mix and Amount stage makes for each block ([step 5](#step-5-mix-and-amount)). The engine reads parameters with `values.Value(P.Rate)` (the real value: Hz, a fraction, an index) and never touches a raw number.

Setting the sample rate sets up the smoothers and reads every parameter once. `Reset` clears what the sound has built up:

<!-- snippet: EngineRate -->
```csharp
public void SetSampleRate(float sr)
{
    fx.SetSampleRate(sr);
    sampleRate = sr;
    depth.Setup(SmoothSeconds, sr);
    offset.Setup(SmoothSeconds, sr);
    triangle.Setup(SmoothSeconds, sr);
    InMeter.SetSampleRate(sr);
    OutMeter.SetSampleRate(sr);
    Update(true);
}

public void Reset()
{
    fx.Reset();
    phase = 0;
}
```

`Update` runs once per block, not once per sample. It reads only what changed (`values.AnyDirty`) and gives each smoothed value its new target:

<!-- snippet: EngineUpdate -->
```csharp
/// <summary>Reads the parameters that changed. Runs once per block, never per sample.</summary>
void Update(bool force)
{
    if (!force && !values.AnyDirty) return;
    increment = values.Value(P.Rate) / sampleRate;
    float d = values.F(P.Depth);
    float o = (float)(values.Value(P.Phase) / 360.0);
    float t = values.Choice(P.Shape) == 1 ? 1f : 0f;
    if (force)
    {
        depth.Reset(d);
        offset.Reset(o);
        triangle.Reset(t);
    }
    else
    {
        depth.SetTarget(d);
        offset.SetTarget(o);
        triangle.SetTarget(t);
    }
    values.Clean();
}
```

**Smoothing.** Automation moves a parameter in steps, one per block. If the engine used each step as it came, the sound would click. So every parameter that reaches the signal goes through a `SmoothRamp`, which glides to a new target over 20 ms and lands exactly on it:

- Depth, the right channel's phase offset and the shape (as a crossfade between 0 and 1) each have one.
- Rate needs none: it sets the *speed* of a phase that only ever moves forward, so the level never jumps when the rate changes.

`Process` is the audio path:

<!-- snippet: EngineProcess -->
```csharp
/// <summary>Processes a stereo block in place.</summary>
public void Process(float[] left, float[] right, int n)
{
    if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
    fx.Begin(left, right, n);     // the effective values for this block, and the dry input kept for Mix
    Update(false);
    InMeter.Feed(left, right, n);
    for (int i = 0; i < n; i++)
    {
        float d = depth.Next();
        float shape = triangle.Next();
        float o = offset.Next();
        left[i] *= Gain(phase, d, shape);
        right[i] *= Gain(phase + o, d, shape);
        phase += increment;
        if (phase >= 1) phase -= 1;
    }
    OutMeter.Feed(left, right, n);
    fx.End(left, right, n, Latency);   // Mix and Amount's bypass: the dry input blended in
}

/// <summary>The gain at a position in the cycle: 1 at the top, 1 − depth at the bottom. Sine and triangle crossfade by <paramref name="shape"/> (0 sine, 1 triangle).</summary>
static float Gain(double position, float depth, float shape)
{
    double p = position - Math.Floor(position);
    float sine = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * p));
    float tri = (float)(p < 0.5 ? 2 * p : 2 - 2 * p);
    float dip = sine + shape * (tri - sine);
    return 1f - depth * dip;
}
```

The rules this code follows (all in [audio-thread.md](audio-thread.md)):

- It allocates nothing. Every buffer is made before the audio runs.
- It takes no lock.
- It uses `PortableMath.Cos`, never `Math.Cos`, so the bits are the same on every platform.
- It has no feedback, so there is nothing to flush to zero. An engine with a feedback loop passes its state through `DspMath.Flush`.
- `LevelMeter` is how the editor reads the level: one `volatile float` the audio thread writes and the UI reads.

## Step 5: Mix and Amount

Every effect has **Mix** (dry against wet, with the dry signal delayed by the latency the effect reports) and **Amount** (0 to 200 %: the strength of the effect, where 100 % is the settings as they are and 0 % is bit-exact dry). You do not write either. The shared helper [`FxStage`](../../Machines/NeoBuzz.Dsp/FxStage.cs) does both. You give it two things.

First, **what Amount scales**. An `AmountRule` names a parameter and the value it moves away from. Tremolo's neutral is a Depth of 0 %, so Amount scales Depth's distance from 0:

<!-- snippet: AmountRules -->
```csharp
/// <summary>What Amount scales: the distance of Depth from 0 %.</summary>
public static readonly AmountRule[] AmountRules =
{
    AmountRule.Lin(P.Depth, 0),
};
```

At Amount 50 % a Depth of 70 % acts as 35 %. At 200 % it acts as 140 %, held to the parameter's own range (100 %). The other rule kinds are `Log` (a frequency or a time, scaled in octaves), `Ratio` (a compressor's ratio) and `Fun` (your own function).

Second, **the stage itself**, made in the constructor with the indices of Amount and Mix:

<!-- snippet: EngineCtor -->
```csharp
public TremoloEngine(ParamValues raw)
{
    fx = new FxStage(raw, P.Amount, P.Mix, AmountRules, maxLatency: Latency, maxBlock: MaxBlock);
    values = fx.Effective;
    SetSampleRate(48000);
}
```

Then each block, as `Process` showed: `fx.Begin(...)` before the effect makes the effective values (the raw ones with Amount applied) and keeps a copy of the dry input; `fx.End(...)` after it blends the dry input back in by Mix. What the stage guarantees:

- **Amount 0 % is the input, bit for bit**, delayed by the latency. The effect keeps running on neutral settings, so raising Amount again does not start cold. Between 0 and 5 % the wet path fades in, so there is no click.
- **Mix 0 % is the input, bit for bit.**
- **Both glide over 20 ms**, so automating either does not zipper.
- At the defaults (100 % and 100 %) the stage costs only the copy of the dry input.

If your effect already mixes itself with its own latency-aligned dry (Neo Drive does), pass `-1` as the Mix index: the stage then only does Amount. The effect's Delta and Listen switches, if it has them, are passed to the stage too (see `FxStage`'s constructor).

## Step 6: The machine class

The machine class is thin. It gives the engine its samples and its sample rate, and reports its latency. It is a partial class: the other half, with the parameter properties, is `Machine.Params.g.cs`.

<!-- snippet: MachineClass -->
```csharp
[MachineDecl(Name = "Tutorial Tremolo", ShortName = "Tremolo", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
public partial class TutorialTremoloMachine : NeoMachineBase, IBuzzMachine
{
    readonly float[] left = new float[TremoloEngine.MaxBlock], right = new float[TremoloEngine.MaxBlock];
    int rate;
    int quietBlocks;

    public TutorialTremoloMachine(IBuzzMachineHost host) : base(host, Params.Table)
    {
        Engine = new TremoloEngine(Values);
    }

    internal TremoloEngine Engine { get; }

    public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
    {
        if (SampleRate != rate)
        {
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }
        bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
        if (!hasInput)
        {
            // A few quiet blocks of silence through the engine (Mix and the smoothers settle), then rest.
            if (quietBlocks > 4) return false;
            quietBlocks++;
            Array.Clear(left, 0, n);
            Array.Clear(right, 0, n);
        }
        else
        {
            quietBlocks = 0;
            Split(input, left, right, n);
        }
        if (n > TremoloEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
        Engine.Process(left, right, n);
        return Join(left, right, output, n) || hasInput;
    }

    /// <summary>Samples of delay ReBuzz compensates: none.</summary>
    public int GetLatency() => Engine.Latency;
```

What it does:

- `[MachineDecl]` names the machine. `InputCount = 1, OutputCount = 1` makes it an effect; `MaxTracks = 0` says it has no tracks.
- `NeoMachineBase` holds the parameter values (`Values`), reads the sample rate and tempo from the host, and does the versioned-state work ([state.md](state.md)).
- `Split` turns Buzz samples (±32768) into two float channels (±1) and `Join` turns them back; both are in the base class.
- `Work` returns `false` when it has nothing to say, so ReBuzz can skip the machines downstream. The effect runs a few silent blocks first so its tail and its smoothers settle.
- `GetLatency` is the delay in samples that ReBuzz compensates. A machine with a lookahead or a linear-phase filter reports it here.

The editor is opened by a small factory. It exists only in the studio build (`AVALONIA` is defined there), not in the headless one:

<!-- snippet: MachineFactory -->
```csharp
#if AVALONIA   // the editor: Avalonia in the studio; none headless
[MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
public class MachineGUIFactory : IMachineGUIFactory
{
    public IMachineGUI CreateGUI(IMachineGUIHost host) => new TutorialTremoloEditor();
}
#endif
```

An instrument derives from `NeoSynthBase` instead and calls `BeginBlock(Engine)` in `Work` to apply the queued notes; see [notes.md](notes.md). The file also holds an "About" menu item, which is not shown here.

## Step 7: Presets

Factory presets are written in real units in `Presets.txt`:

<!-- snippet: PresetsFile -->
```text
machine "Tutorial Tremolo"

preset "Swell — Slow Breath"
comment "A slow, deep swell: the level falls to a quarter and comes back every two seconds."
Rate = 0.5
Depth = 75
Shape = Sine

preset "Amp — Classic Wobble"
comment "A steady 5.5 Hz sine pulse, the sound of an old guitar amplifier."
Rate = 5.5
Depth = 70
Shape = Sine

preset "Pulse — Triangle Chop"
comment "A fast, sharp triangle pulse at 9 Hz that goes all the way to silence."
Rate = 9
Depth = 100
Shape = Triangle

preset "Wide — Slow Pan"
comment "The two channels move in opposite directions, so the sound drifts from side to side."
Rate = 0.8
Depth = 100
Shape = Sine
Phase = 180
```

A name `"Group — Name"` files the preset under a group in the editor's preset bar. A parameter a preset does not name takes its default, so these presets all have Mix and Amount at 100 %. The generator (the second command of [step 3](#step-3-generate-the-code)) turns each value into the raw number ReBuzz stores. 0.5 Hz becomes 304 on Rate's exponential scale:

<!-- snippet: PresetXml -->
```xml
<Item Key="Swell — Slow Breath">
  <Preset Machine="Tutorial Tremolo">
    <Parameters>
      <Parameter Name="Rate" Group="1" Index="0" Track="0" Value="304" />
      <Parameter Name="Depth" Group="1" Index="1" Track="0" Value="750" />
```

Presets must be **clearly audible** on full-scale material and **clearly different from each other**: a subtle preset is a bug ([rules.md](rules.md)). The test in [step 10](#step-10-tests) checks both for these four.

## Step 8: The editor

The editor is one source file, written with WPF's type names (`UIElement`, `Thickness`), which the kit maps onto Avalonia for the studio. The full kit is in [editor.md](editor.md). This editor has three parts.

The head names the machine and its accent. The accent is not a literal colour: `Tokens.Identity` gives the colour of the machine's category (Modulation here), so every modulation machine looks related. The second argument is the colour the old classic app keeps.

<!-- snippet: EditorHead -->
```csharp
// The machine's category gives its accent (Tokens.Identity); the second argument is the colour the classic app keeps.
static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Modulation, NeoColor.FromRgb(0x5B, 0xC0, 0xEB));
static readonly NeoColor PartnerColor = Tokens.Partner(AccentColor, NeoColor.FromRgb(0xF2, 0xA9, 0x3B));

protected override string Title => "Tutorial Tremolo";
protected override NeoColor Accent => AccentColor;
internal override ParamDef[] Table => Params.Table;
```

The body lays out sections of controls. A `ParamKnob` or `ParamChoice` is bound to a parameter by its index `P.*`: it shows the table's text for the value, writes the value when you drag, makes undo steps, and follows automation, MIDI and the agent.

<!-- snippet: EditorBody -->
```csharp
internal override UIElement BuildBody(EditorContext ctx)
{
    var machine = ctx.Machine?.ManagedMachine as TutorialTremoloMachine;

    var tremolo = Layout.Row(
        Layout.Column(
            new ParamKnob(ctx, P.Rate, diameter: 58),
            new ParamKnob(ctx, P.Phase)),
        Layout.Gap(8),
        Layout.Column(
            new ParamKnob(ctx, P.Depth, diameter: 58),
            new ParamChoice(ctx, P.Shape, cellWidth: 70)));

    var wave = new GraphView(220, 140, (g, r) => DrawWave(g, r, ctx));
    ctx.Changed += wave.InvalidateVisual;   // redraw when a value changes (a knob, automation, a preset)

    var meters = Layout.Row(
        new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 120),
        Layout.Gap(4),
        new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 120));

    // House style: Amount, then Mix, two 40 px knobs, in the last section of every Neo effect.
    var output = Layout.Row(
        Layout.Column(Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40))),
        Layout.Gap(6),
        meters);

    return Layout.Row(
        new Section("Tremolo", AccentColor, tremolo),
        new Section("Level", AccentColor, wave),
        new Section("Output", AccentColor, output));
}
```

The house style: knobs in sections, the machine's accent on every section title, **Amount and then Mix as two 40 px knobs in the last section**, and meters on the right. The curve draws what the engine does. It runs the same `FxStage.Fill` the engine uses, so it follows Amount, and it calls the engine's own `Gain`:

<!-- snippet: EditorDraw -->
```csharp
/// <summary>Two cycles of the gain of the left (accent) and the right channel (partner colour), drawn with the engine's own Gain.</summary>
static void DrawWave(INeoCanvas g, NeoRect r, EditorContext ctx)
{
    // What the engine would run: the raw values with Amount applied (the same FxStage.Fill the engine uses).
    var rawValues = new ParamValues(Params.Table);
    var values = new ParamValues(Params.Table);
    values.AllowPhysical();
    for (int i = 0; i < Params.Table.Length; i++) rawValues.Set(i, ctx.Raw(i));
    FxStage.Fill(rawValues, values, P.Amount, TremoloEngine.AmountRules);

    var grid = new NeoStroke(Palette.Grid, 1, false);
    double top = r.Y + 8, bottom = r.Bottom - 8;
    g.Line(r.Left, top, r.Right, top, grid);          // gain 1
    g.Line(r.Left, bottom, r.Right, bottom, grid);    // gain 0

    const int steps = 200;
    foreach (bool right in new[] { true, false })     // the left channel last, so it is on top
    {
        var curve = new NeoPath();
        for (int i = 0; i <= steps; i++)
        {
            double cycle = 2.0 * i / steps;
            double gain = TremoloEngine.CurveAt(values, cycle, right);
            curve.LineTo(r.X + r.Width * i / steps, bottom - gain * (bottom - top));
        }
        g.Path(curve, NeoFill.None, new NeoStroke(right ? PartnerColor : AccentColor, 2));
    }
    g.Text("1", r.X + 4, top - 8, 11, Palette.DimText);
    g.Text("0", r.X + 4, bottom - 14, 11, Palette.DimText);
}
```

The drawing code uses the kit's canvas (`INeoCanvas`), not Avalonia or WPF types, so the same picture is drawn in the studio, in a plug-in and in the tests.

A real editor also gets a gallery shot (light and dark) that you read yourself: see [editor.md](editor.md#the-gallery).

## Step 9: The shared entries

A real machine also appears in a few shared files. This is what tools such as the agent server and the machine browser read.

**The two projects.** One for each flavour; each sets the folder, the gear name and the kind (`Effects` or `Generators`) and imports the shared build file. A new machine copies a neighbour's:

<!-- snippet: StudioProject -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Neo Tremolo, studio flavour (docs/specs/studio-mvp.md S1): see ../NeoStudio.targets. -->
  <PropertyGroup>
    <NeoFolder>NeoTremolo</NeoFolder>
    <NeoName>Neo Tremolo</NeoName>
    <NeoKind>Effects</NeoKind>
  </PropertyGroup>
  <Import Project="../NeoStudio.targets" />
</Project>
```

<!-- snippet: PortableProject -->
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Neo Tremolo, portable (docs/specs/mac-engine.md M2): see ../NeoPortable.targets. -->
  <PropertyGroup>
    <NeoFolder>NeoTremolo</NeoFolder>
    <NeoName>Neo Tremolo</NeoName>
    <NeoKind>Effects</NeoKind>
  </PropertyGroup>
  <Import Project="../NeoPortable.targets" />
</Project>
```

Add the Studio project to `ReBuzz.sln` by copying a neighbour's entry (the project line, its build configurations and its place in the solution folder).

**The catalogue.** `Machines/Manifests/machine-catalog.json` puts every machine in one of the browser's categories, with tags from a fixed list and one line of what it is for. The generator writes these three fields into the manifest and stops if the machine is not in the catalogue. Under `machines`, add the machine's name with:

<!-- snippet: Catalog -->
```json
{
 "category": "Modulation",
 "tags": [
  "rhythmic",
  "stereo-width"
 ],
 "use_for": "Swells, pulses and slow stereo pans: moves the level up and down in time."
}
```

<!-- snippet: CatalogCommand -->
```bash
python3 tools/machine_catalog.py
```

That command must report 0 problems. It also checks that every machine in the catalogue is in the profile.

**The profile.** `Machines/Manifests/machine-profile.json` is generated, not written. `tools/machine-profiler.py` loads every machine in a running engine and measures it: its latency, its tail, its response, its level at the extremes and its cost per second. It fills in the entry for your machine on a package run. Do not edit the profile by hand.

**The state layout.** One line per machine in `NeoBuzz.MachineTests/Layouts/state-layouts.txt` records the table as it is. It is how a later change to the order or the ranges is caught ([state.md](state.md)). The line, with tabs between its four fields (namespace, version, parameter count, fingerprint):

<!-- snippet: LayoutLine -->
```text
NeoBuzz.TutorialTremolo	0	6	ae4ecf4a09593708
```

The fixture's own line is in that file. For a new machine, make it with `NEO_WRITE_LAYOUTS=<file> dotnet test NeoBuzz.MachineTests --filter EveryMachineLayoutIsRecorded` and copy your line.

## Step 10: Tests

The engine needs no ReBuzz to be tested, so its tests run in seconds on any machine. First some helpers: set a parameter in its real unit, and run the engine over some material in blocks of 256, as the machine's `Work` does:

<!-- snippet: TestHelpers -->
```csharp
static ParamValues Values(Action<ParamValues> set = null)
{
    var v = new ParamValues(Params.Table);
    set?.Invoke(v);
    return v;
}

/// <summary>Sets a parameter in its real unit (Hz, %, degrees): the table maps it to the raw value ReBuzz stores.</summary>
static void Set(ParamValues v, int index, double physical) => v.Set(index, v.Table[index].Raw(physical));

/// <summary>The engine over the material in blocks of 256, as the machine's Work calls it. Values are set before the engine is made.</summary>
static (float[] L, float[] R) Render(ParamValues v, float[] inL, float[] inR, Action<int> beforeBlock = null)
{
    var engine = new TremoloEngine(v);
    engine.SetSampleRate(Measure.Rate);
    var l = (float[])inL.Clone();
    var r = (float[])inR.Clone();
    var bl = new float[TremoloEngine.MaxBlock];
    var br = new float[TremoloEngine.MaxBlock];
    for (int pos = 0, b = 0; pos < l.Length; pos += TremoloEngine.MaxBlock, b++)
    {
        beforeBlock?.Invoke(b);
        int n = Math.Min(TremoloEngine.MaxBlock, l.Length - pos);
        Array.Copy(l, pos, bl, 0, n);
        Array.Copy(r, pos, br, 0, n);
        engine.Process(bl, br, n);
        Array.Copy(bl, 0, l, pos, n);
        Array.Copy(br, 0, r, pos, n);
    }
    return (l, r);
}

static void AssertSameBits(float[] want, float[] got, string what)
{
    for (int i = 0; i < want.Length; i++)
        if (BitConverter.SingleToInt32Bits(want[i]) != BitConverter.SingleToInt32Bits(got[i]))
            Assert.Fail($"{what}: sample {i} is {got[i]:R}, the input was {want[i]:R}");
}

static (float[] L, float[] R) Material() => (FxGoldenTests.DrumLoop(), FxGoldenTests.Chord());
```

**Mix 0 % and Amount 0 % are the input, bit for bit.** Values are set before the engine is made, so the stage starts at its target:

<!-- snippet: TestBitExact -->
```csharp
[Test]
public void AmountZeroIsTheInputBitForBit()
{
    var v = Values(x => Set(x, P.Amount, 0));        // 0 %: the effect is out of the path
    var (inL, inR) = Material();
    var (l, r) = Render(v, inL, inR);
    AssertSameBits(inL, l, "left");
    AssertSameBits(inR, r, "right");
}

[Test]
public void MixZeroIsTheInputBitForBit()
{
    var v = Values(x => Set(x, P.Mix, 0));           // 0 %: all dry
    var (inL, inR) = Material();
    var (l, r) = Render(v, inL, inR);
    AssertSameBits(inL, l, "left");
    AssertSameBits(inR, r, "right");
}
```

**Nothing is allocated per block**, while automation moves every parameter every block. `AllocationCheck.Run` measures each block on its own and tries again before it fails, so one stray runtime allocation on a busy runner does not fail the test, but a real allocation per block does ([audio-thread.md](audio-thread.md#no-allocation)):

<!-- snippet: TestAllocation -->
```csharp
[Test]
public void ProcessAllocatesNothingWhileEveryParameterMoves()
{
    var v = Values();
    var engine = new TremoloEngine(v);
    engine.SetSampleRate(Measure.Rate);
    var (inL, inR) = Material();
    var l = new float[TremoloEngine.MaxBlock];
    var r = new float[TremoloEngine.MaxBlock];
    int blocks = inL.Length / TremoloEngine.MaxBlock - 1;
    void Block(int b)
    {
        // Automation moves every parameter every block.
        v.Set(P.Rate, (b * 37) % 1001);
        v.Set(P.Depth, (b * 53) % 1001);
        v.Set(P.Shape, b / 7 % 2);
        v.Set(P.Phase, (b * 71) % 1001);
        v.Set(P.Mix, (b * 29) % 1001);
        v.Set(P.Amount, (b * 83) % 2001);
        Array.Copy(inL, b * TremoloEngine.MaxBlock, l, 0, TremoloEngine.MaxBlock);
        Array.Copy(inR, b * TremoloEngine.MaxBlock, r, 0, TremoloEngine.MaxBlock);
        engine.Process(l, r, TremoloEngine.MaxBlock);
    }
    var (bytes, why) = AllocationCheck.Run(blocks, Block);
    Assert.That(bytes, Is.EqualTo(0), why);
}
```

**Moving the controls makes no click.** A steady tone goes through while the parameters move; the click detector finds the discontinuities a listener would hear:

<!-- snippet: TestNoClick -->
```csharp
[Test]
public void MovingTheControlsMakesNoClick()
{
    // A steady tone through the tremolo while Depth, Phase, Shape, Mix and Amount are all moved every few blocks:
    // the click detector (second difference against its local mean) finds nothing.
    var v = Values();
    var tone = Measure.Sine(1000, Measure.Rate * 4, 0.4);
    var (l, _) = Render(v, tone, tone, b =>
    {
        v.Set(P.Depth, (b * 211) % 1001);
        v.Set(P.Shape, b / 9 % 2);
        v.Set(P.Phase, (b * 97) % 1001);
        v.Set(P.Mix, 500 + (b * 131) % 501);
        v.Set(P.Amount, 800 + (b * 17) % 800);
    });
    Assert.That(ClickDetector.Find(l, floor: 0.02), Is.Empty);
}
```

**State round-trips.** A song stores each parameter's raw number. The value and raw mappings are inverses over the whole range, so what a song stores loads as the same sound. With no migration steps the saved numbers load unchanged ([state.md](state.md)):

<!-- snippet: TestState -->
```csharp
[Test]
public void StateRoundTrips()
{
    // A song stores each parameter's raw number. Value and Raw are inverses over the whole range, so what a song stores
    // loads as the same sound; and a machine with no migration steps (version 0) loads the numbers it saved unchanged.
    foreach (var def in Params.Table)
        for (int raw = def.Min; raw <= def.Max; raw++)
            Assert.That(def.Raw(def.Value(raw)), Is.EqualTo(raw), $"{def.Id} at {raw}");

    var saved = Params.Table.Select(d => d.Default).ToArray();
    saved[P.Rate] = 321; saved[P.Depth] = 777; saved[P.Shape] = 1; saved[P.Phase] = 250; saved[P.Mix] = 640; saved[P.Amount] = 1500;
    var chain = new StateMigrations(Params.Table);
    Assert.That(chain.Validate(), Is.Empty);
    Assert.That(chain.Current, Is.EqualTo(0));
    Assert.That(chain.MigrateGlobals(0, saved), Is.EqualTo(saved));
    Assert.That(chain.Keys, Is.EqualTo(new[] { "Rate", "Depth", "Shape", "Phase", "Mix", "Amount" }));
}
```

**Presets are clearly audible and clearly different.** The test reads the `.prs.xml` the generator wrote, renders a tone through each preset, and checks that the level really moves (at least 3 dB) and that no two presets sound alike:

<!-- snippet: TestPresets -->
```csharp
/// <summary>The factory presets as the generator wrote them: name and raw values by index.</summary>
static List<(string Name, int[] Raw)> FactoryPresets()
{
    var doc = XDocument.Load(Path.Combine(Fixture, "Tutorial Tremolo.prs.xml"));
    return doc.Root!.Elements("Item").Select(item => (
        (string)item.Attribute("Key")!,
        item.Element("Preset")!.Element("Parameters")!.Elements("Parameter").OrderBy(p => (int)p.Attribute("Index")!)
            .Select(p => (int)p.Attribute("Value")!).ToArray())).ToList();
}

[Test]
public void PresetsAreClearlyAudibleAndClearlyDifferent()
{
    var presets = FactoryPresets();
    Assert.That(presets.Count, Is.GreaterThanOrEqualTo(4));
    var tone = Measure.Sine(220, Measure.Rate * 4, 0.4);
    var outputs = new List<float[]>();
    foreach (var (name, raw) in presets)
    {
        var v = Values(x => { for (int i = 0; i < raw.Length; i++) x.Set(i, raw[i]); });
        var (l, _) = Render(v, tone, tone);
        outputs.Add(l);
        // Audible: the output is not the input (at least 3 dB of level moving), at full-scale-ish material.
        double lo = double.MaxValue, hi = 0;
        for (int pos = 9600; pos + 480 <= l.Length; pos += 480)
        {
            double rms = Measure.Rms(l, pos, 480);
            lo = Math.Min(lo, rms);
            hi = Math.Max(hi, rms);
        }
        Assert.That(Measure.Db(hi / Math.Max(lo, 1e-9)), Is.GreaterThan(3), name + " is subtle");
    }
    for (int a = 0; a < outputs.Count; a++)
        for (int b = a + 1; b < outputs.Count; b++)
        {
            double diff = 0, energy = 0;
            for (int i = 0; i < tone.Length; i++)
            {
                diff += Math.Pow(outputs[a][i] - outputs[b][i], 2);
                energy += Math.Pow(outputs[a][i], 2);
            }
            Assert.That(Measure.Db(Math.Sqrt(diff / energy)), Is.GreaterThan(-12), $"{presets[a].Name} and {presets[b].Name} sound alike");
        }
}
```

Run them:

<!-- snippet: TestCommands -->
```bash
dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~TutorialTremolo"
dotnet test NeoBuzz.MachineTests
```

The first line is the fast loop; the second is the full project. A real machine also adds itself to the shared tests that cover every machine. `FxMixAmountTests.Cases` proves Mix 0 %, Amount 0 %, bounded growth at 200 %, no allocation and no zipper for every effect, and `PresetTests.Machines` renders every factory preset of every machine. An instrument also adds a row to `NoteContractTests` ([notes.md](notes.md#the-note-contract-tests)).

## Step 11: What a real machine adds

The fixture stops here. A shipped machine also has:

- **The studio build and the solution entry:** build with the two commands below. `Studio.proj` and `Portable.proj` pick up every `*/*.Studio.csproj` and `*/*.Portable.csproj` on their own.

<!-- snippet: BuildCommands -->
```bash
dotnet build Machines/Studio/Studio.proj -p:GearDir=/tmp/gear
dotnet build Machines/Portable/Portable.proj -p:GearDir=/tmp/gear-portable
```

- **The editor in the gallery,** light and dark, checked by you ([editor.md](editor.md#the-gallery)).
- **An agent guide** page in `ReBuzz.Agent/Guides/machines/`, within its token budget, and the guide test lists. [AGENTS.md](../../AGENTS.md) section 4 has the whole list.
- **A listening pack:** a render of every preset, run through the click detector, which the project owner listens to before the machine merges ([audio-thread.md](audio-thread.md#listening-packs-and-the-click-detector)).
- **Rows** in `docs/specs/machines.md` and in `FORK.md`.
- **A plug-in descriptor,** if the machine should export as a CLAP, VST3 or AU plug-in ([export.md](export.md)).

To check that the fixture's machine class and editor still build, run `dotnet build NeoBuzz.MachineTests/Sdk/TutorialTremolo/TutorialTremolo.Studio.csproj -p:GearDir=/tmp/tremolo-gear` by hand (CI does not).
