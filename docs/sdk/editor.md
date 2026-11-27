# The editor kit

A Neo machine's editor is one source file, `Neo<Name>Editor.cs`, in the machine's folder. It is built on the kit in [`Machines/NeoBuzz.Editor`](../../Machines/NeoBuzz.Editor) (toolkit-free: parameters, tokens, drawing) and [`Machines/NeoBuzz.Editor.Avalonia`](../../Machines/NeoBuzz.Editor.Avalonia) (the controls). The studio build links both as source. [The tutorial](tutorial.md#step-8-the-editor) shows a whole editor; this page lists the parts.

The editor file is written with WPF's names (`UIElement`, `Thickness`, `StackPanel`), which [`WpfNames.cs`](../../Machines/NeoBuzz.Editor.Avalonia/WpfNames.cs) maps onto Avalonia. The editor is only in the studio build: the headless build (no `AVALONIA` constant) has none, so wrap the factory in `#if AVALONIA`.

## The shell

Derive from `NeoEditor`. It draws the header (the machine's name and its preset bar, over ReBuzz's preset files), keeps the parameters in sync with the machine and gives every edit an undo step. You give it three things and a body:

```csharp
protected override string Title => "Tutorial Tremolo";
protected override NeoColor Accent => AccentColor;
internal override ParamDef[] Table => Params.Table;
internal override UIElement BuildBody(EditorContext ctx) { /* ... */ }
```

`EditorContext` is what the body is built with: the machine, its parameters and the accent. It reads the **raw** value (`ctx.Raw(i)`), binds a control to a parameter (`ctx.Bind`) and raises `ctx.Changed` whenever a bound parameter changes, whoever changed it. The editor looks at the parameters about 30 times a second, so a change from automation, MIDI, a preset or the agent shows without any code of yours.

## Controls

| Control | Use |
|---|---|
| `ParamKnob(ctx, P.Depth, label?, diameter)` | A rotary knob for a parameter. Drag up or down (Shift for fine), mouse wheel, double-click for the default. It shows the value as text while hovered or dragged. A modulator can be dropped on it. |
| `ParamChoice(ctx, P.Shape, label?, cellWidth, vertical)` | A row of buttons for an enum. For a switch it is one toggle, lit when on. |
| `ParamDropdown(ctx, P.Mode, label?, width)` | A drop-down for an enum with many choices. |
| `Section(title, accent, content)` | A titled panel. The title is in the accent colour. |
| `Layout.Row(...)`, `Layout.Column(...)`, `Layout.Gap(w, h)`, `Layout.Grid(columns, ...)` | Arrange controls. |
| `GraphView(width, height, draw)` | A picture you draw with `INeoCanvas` (below). |
| `Meter(label, () => level, color, height)` | A level meter that reads a function every frame, such as `engine.OutMeter.Peak`. |
| `ExplainStrip(ctx, width)` | The strip at the bottom that shows the **how** and **sound** lines of the control under the pointer. |
| `Pages(accent, width, height)` | Named tabs over pages of the same size, for a machine with many controls. |
| `ActionButton(text, accent, width)` | A button for an action that is not a parameter (a load, a randomise). |
| `Build.Knobs(ctx, columns, diameter, indices...)` | A grid of knobs from a list of indices. |

Controls bind **by index** (`P.*`, from the generated `Params.g.cs`). There is no value conversion in the editor: the control asks the table for the text of a raw value (`def.Describe(raw)`), so the unit, the digits and the choice names are exactly what `Params.def` says.

## Binding and the undo history

A control writes a value through its binding, and that write is a **hand edit**. The binding knows its own writes from a value that arrives from outside, so:

- a drag, a reset and a run of wheel turns are **one undo step** each;
- loading a preset from the bar is one undo step;
- a value the automation moves does not make an undo step.

A parameter that is not a global one (a track parameter) is bound by the machine's slot table. A Neo effect has only global parameters, so it does not need this.

## Drawing: INeoCanvas

Anything that is not a standard control is drawn with `INeoCanvas`, a small canvas that each of its calls maps to one call of a toolkit:

| Call | Draws |
|---|---|
| `Line`, `Rect(rect, fill, stroke, radius)`, `Ellipse`, `Arc` | Lines and shapes. Arcs run from 0 degrees at the top, clockwise. |
| `Path(NeoPath, fill, stroke)` | Polylines and polygons. Build the path with `MoveTo` and `LineTo`. |
| `Text(text, x, y, size, color, bold, align, maxWidth)` | One line of text, cut with an ellipsis at `maxWidth`. |
| `PushClip(rect)`, `PopClip()` | Clip what follows. |
| `MeasureText(text, size)` | The width of a line, for layout. |

The drawing code uses the canvas only, so the same function draws in the studio, in an exported plug-in and, with the `RecordingCanvas`, in a test that records the calls and checks them (`EditorCanvasTests`). A graph should **draw what the engine does**: call the engine's own function (as the tutorial's `DrawWave` calls `CurveAt` and `FxStage.Fill`), never a second copy of the maths.

Ready-made graphs live in `Core/`: frequency-response graphs (`ResponseGraph`), scope and spectrum graphs (`ScopeGraphs`, `AnalysisGraphs`), breakpoint editors (`Breakpoints`) and the operator diagram of the FM machines.

## Theming and the house style

**No literal colours.** Colours come from [`Tokens`](../../Machines/NeoBuzz.Editor/Core/Tokens.cs), one table for the studio and every editor, with a dark and a light set (`Tokens.SetLight`). A view reads the colour each time it draws, so a theme change shows at once.

- **Surfaces and text:** `Palette.Panel`, `Palette.Text`, `Palette.DimText`, `Palette.Grid` and the `Tokens.Surface*` and `Tokens.Text*` colours. Text is 11 pt or larger, at `TextSecondary` or brighter on a panel.
- **The accent:** `Tokens.Identity(MachineCategory.X, classicColour)`. The machine's category gives the colour, so every machine of a category is related. The second argument is the colour the machine had before tokens.
- **A second trace:** `Tokens.Partner(accent, fallback)`.
- **Graph lines:** `Tokens.Series`.
- **Reserved colours:** play green, record red and override orange mean what they mean in the studio, so do not use them as an accent.
- **Sizes and gaps** are on the 8 pt grid.

The categories are Synth, Drums, Sampler, Dynamics, EqFilter, DelayReverb, Modulation, Distortion, Utility and Control.

The layout of an effect: a section of the controls for the sound, a section with a picture of what it does, and **the last section with Amount, then Mix, as two 40 px knobs, beside the input and output meters**. The Explain strip, where the machine has one, goes at the bottom.

## The gallery

Every Neo machine's editor is photographed by the UI gallery, in the dark and the light theme, so a change that breaks an editor shows in a picture.

- The gallery test (`ReBuzz.Studio.Tests`, `GalleryTests`) opens each Neo machine's editor as a double click does and takes a shot, with the machine's name in the file name (`NNN-editor-neo-<name>`). A new machine appears in it **by itself**, once its project exists and its DLL has a GUI factory.
- The Mac run draws it with macOS fonts: dispatch `fork-mac.yml` with `gallery=true` on your branch. It pushes the pictures to the branch `ci-artifacts-mac`, which holds **the latest gallery run only**. The dark shots are in `headless/`, the light in `headless-light/`.
- **Read your own shots right after your run** (`git show origin/ci-artifacts-mac:headless/<file>`, and the same in `headless-light/`), and check them: nothing is cut off, the text is readable at 11 pt or more, the accent is the category's, Amount and Mix are where the house style puts them. Say in your report which you looked at ([AGENTS.md](../../AGENTS.md) section 5).

A change that touches no UI needs no Mac run.

## Tests of an editor

The toolkit-free drawing is tested on the recording canvas: a graph draws inside its rectangle, a knob maps a drag to the right value, a hit test finds the right control. A machine's own editor tests build its graphs with a recording canvas and check what they draw (for example, that a curve follows Amount). The studio tests (`ReBuzz.Studio.Tests`) open the real editors on a headless Avalonia.
