#nullable disable
// Dye Essence's editor (docs/specs/dye-essence.md §8), its modulation parts: each LFO's shape, drawn
// with breakpoints on the kit's graph (Core/Breakpoints.cs), and the route list. One source for WPF
// and Avalonia (the kit's names; the views draw on INeoCanvas). A drawn shape lives in the machine
// state, so its edits are undo steps of their own (ShapeEditAction), a gesture's shape and the LFO's
// shape parameter together; the routes are parameters, undone as every parameter edit is.

using System;
using System.Collections.Generic;
using System.Linq;
#if AVALONIA
using Avalonia.Automation;
using Avalonia.Controls;
#else
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
#endif
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.DyeEssence
{
    /// <summary>The built-in LFO shapes as breakpoints: what a shape view shows, and where drawing starts from.</summary>
    internal static class BuiltInShapes
    {
        // S&H and Smooth play random values: shown (and started from) as eight fixed ones.
        const string Steps = "0,0.6 0.125,0.6 0.125,-0.3 0.25,-0.3 0.25,0.9 0.375,0.9 0.375,-0.8 0.5,-0.8 0.5,0.2 0.625,0.2 0.625,-0.5 0.75,-0.5 0.75,0.7 0.875,0.7 0.875,-0.1 1,-0.1";
        const string Glides = "0,0.6,0.3 0.125,-0.3,0.3 0.25,0.9,0.3 0.375,-0.8,0.3 0.5,0.2,0.3 0.625,-0.5,0.3 0.75,0.7,0.3 0.875,-0.1,0.3";

        static readonly DrawnShape[] shapes =
        {
            DrawnShape.Default,                                   // Sine
            DrawnShape.Parse("0,0 0.25,1 0.75,-1 1,0"),           // Triangle
            DrawnShape.Parse("0,-1 1,1"),                         // Saw up
            DrawnShape.Parse("0,1 1,-1"),                         // Saw down
            DrawnShape.Parse("0,1 0.5,1 0.5,-1 1,-1"),            // Square
            DrawnShape.Parse(Steps),                              // S&H
            DrawnShape.Parse(Glides),                             // Smooth
        };

        public static DrawnShape Of(WaveShape shape) => (int)shape >= 0 && (int)shape < shapes.Length ? shapes[(int)shape] : DrawnShape.Default;
    }

    /// <summary>
    /// An LFO's shape for the breakpoint view: its drawn shape while it plays one (shape = Drawn), else the built-in
    /// shape it plays, dimmed; a press on that makes the LFO Drawn, starting from it.
    /// </summary>
    internal sealed class LfoShapeEdit : IBreakpoints
    {
        readonly EditorContext ctx;
        readonly DyeEssenceMachine machine;
        readonly int lfo, shapeParam;
        DrawnShape before;
        int beforeChoice;

        public LfoShapeEdit(EditorContext ctx, DyeEssenceMachine machine, int lfo)
        {
            this.ctx = ctx;
            this.machine = machine;
            this.lfo = lfo;
            shapeParam = DyeEssenceMachine.LfoShapeParam[lfo];
        }

        int Choice => ctx.Raw(shapeParam);
        public bool Live => Choice == (int)WaveShape.Drawn;
        DrawnShape Shown => Live ? machine?.LfoShape(lfo) ?? DrawnShape.Default : BuiltInShapes.Of((WaveShape)Choice);

        public int Count => Shown.Count;
        public double X(int i) => Shown.X(i);
        public double Y(int i) => Shown.Y(i);
        public double Curve(int i) => Shown.Curve(i);
        public double Value(double x) => Shown.Value(x);
        public int MaxPoints => DrawnShape.MaxPoints;

        public void MakeLive()
        {
            if (machine == null || Live) return;
            machine.SetLfoShape(lfo, BuiltInShapes.Of((WaveShape)Choice));
            // Not through the editor's undo history: this gesture's step (ShapeEditAction) holds it with the shape.
            ctx.Parameter(shapeParam)?.SetValue(ctx.TrackOf(shapeParam), (int)WaveShape.Drawn);
        }

        public void Set(IReadOnlyList<(double X, double Y, double Curve)> points) => machine?.SetLfoShape(lfo, DrawnShape.From(points));

        public void BeginEdit()
        {
            before = machine?.LfoShape(lfo);
            beforeChoice = Choice;
        }

        public void EndEdit()
        {
            if (machine == null || before == null) return;
            var after = machine.LfoShape(lfo);
            int afterChoice = Choice;
            if (after.SameAs(before) && afterChoice == beforeChoice) return;
            var m = ctx.Machine;
            try
            {
                m?.Graph?.Do(new ShapeEditAction(m, lfo, before, after, beforeChoice, afterChoice));
            }
            catch (Exception)
            {
                // A host without an undo history: the edit stands.
            }
            before = null;
        }
    }

    /// <summary>
    /// One undo step for an LFO's drawn shape: the shape and the LFO's shape parameter before and after. It finds its
    /// machine when it runs (the same one while it is in the song, else one of its name), as parameter steps do.
    /// </summary>
    internal sealed class ShapeEditAction : IAction
    {
        readonly IMachine machine;
        readonly IMachineGraph graph;
        readonly string name;
        readonly int lfo;
        readonly DrawnShape before, after;
        readonly int beforeChoice, afterChoice;

        public ShapeEditAction(IMachine machine, int lfo, DrawnShape before, DrawnShape after, int beforeChoice, int afterChoice)
        {
            this.machine = machine;
            graph = machine.Graph;
            name = machine.Name;
            this.lfo = lfo;
            this.before = before;
            this.after = after;
            this.beforeChoice = beforeChoice;
            this.afterChoice = afterChoice;
        }

        public void Do() => Apply(after, afterChoice);

        public void Undo() => Apply(before, beforeChoice);

        void Apply(DrawnShape shape, int choice)
        {
            var machines = graph?.Machines;
            var m = machines == null || machines.Contains(machine) ? machine : machines.FirstOrDefault(x => x.Name == name);
            if (m?.ManagedMachine is not DyeEssenceMachine dye) return;
            dye.SetLfoShape(lfo, shape);
            var p = m.ParameterGroups.FirstOrDefault(g => g.Type == ParameterGroupType.Global)?.Parameters;
            int index = DyeEssenceMachine.LfoShapeParam[lfo];
            // Not recorded into patterns or session automation while the song records (as parameter steps).
            if (p != null && index < p.Count && p[index].GetValue(0) != choice) p[index].SetValue(0 | 1 << 16, choice);
        }
    }

    /// <summary>
    /// The routes in use as a list: each its source, its destination (click either for the list) and its amount (drag
    /// across, Shift finer, the wheel, a double-click for 0), and × to clear it; "+ Add route" takes the first route
    /// free. Sixteen routes, all parameters (R1 source … R16 amount): this is a view of them.
    /// </summary>
    internal sealed class RouteList : FrameworkElement
    {
        public const int Routes = 16;
        public const double RowHeight = 22, HeaderHeight = 18, NumberWidth = 22, BoxGap = 6, RemoveWidth = 18;
        const double TextSize = 11.5;
        static readonly (string Group, RouteDest[] Dests)[] DestGroups =
        {
            ("", new[] { RouteDest.Pitch }),
            ("Osc A", new[] { RouteDest.APitch, RouteDest.APosition, RouteDest.AWarp1, RouteDest.AWarp2, RouteDest.ALevel }),
            ("Osc B", new[] { RouteDest.BPitch, RouteDest.BPosition, RouteDest.BWarp1, RouteDest.BWarp2, RouteDest.BLevel }),
            ("Osc C", new[] { RouteDest.CPitch, RouteDest.CPosition, RouteDest.CWarp1, RouteDest.CWarp2, RouteDest.CLevel }),
            ("Sub, noise, amp", new[] { RouteDest.SubLevel, RouteDest.NoiseLevel, RouteDest.Amp }),
            ("Filters", new[] { RouteDest.F1Cutoff, RouteDest.F1Reso, RouteDest.F2Cutoff, RouteDest.F2Reso }),
            ("LFO rates", new[] { RouteDest.Lfo1Rate, RouteDest.Lfo2Rate, RouteDest.Lfo3Rate, RouteDest.Lfo4Rate }),
        };

        readonly EditorContext ctx;
        readonly WpfCanvas canvas;
        readonly double sourceWidth, destWidth, amountWidth;
        readonly List<ParamBinding> bindings = new List<ParamBinding>();
#if AVALONIA
        readonly EditGesture gesture;
#endif
        int dragRoute = -1, hoverRow = -1, hoverPart = -1;
        double dragStartX;
        int dragStartRaw;

        public RouteList(EditorContext ctx, double width)
        {
            this.ctx = ctx;
            canvas = new WpfCanvas(this);
            Width = width;
            Height = HeaderHeight + (Routes + 1) * RowHeight;
            double rest = width - NumberWidth - RemoveWidth - 3 * BoxGap - 14;
            sourceWidth = Math.Floor(rest * 0.3);
            destWidth = Math.Floor(rest * 0.36);
            amountWidth = rest - sourceWidth - destWidth;
            for (int i = 0; i < 3 * Routes; i++) bindings.Add(ctx.Bind(P.R1Src + i, _ => InvalidateVisual()));
            ToolTip = "Routes: what modulates what, and how far. Click a source or destination for the list; drag an amount across (Shift: finer; double-click: 0); × clears a route";
            Cursor = Cursors.Arrow;
#if AVALONIA
            gesture = new EditGesture(this);
            AutomationProperties.SetName(this, "Routes");
#endif
        }

        /// <summary>The list while it is open (for tests).</summary>
        public ContextMenu Menu { get; private set; }

        static int Src(int r) => P.R1Src + 3 * r;
        static int Dst(int r) => P.R1Src + 3 * r + 1;
        static int Amt(int r) => P.R1Src + 3 * r + 2;

        bool Used(int r) => ctx.Raw(Src(r)) != 0 || ctx.Raw(Dst(r)) != 0;

        /// <summary>The routes shown, in order: those in use.</summary>
        public List<int> Shown()
        {
            var list = new List<int>();
            for (int r = 0; r < Routes; r++) if (Used(r)) list.Add(r);
            return list;
        }

        /// <summary>The first route not in use, or −1.</summary>
        public int Free()
        {
            for (int r = 0; r < Routes; r++) if (!Used(r)) return r;
            return -1;
        }

        public NeoRect Row(int row) => new NeoRect(0, HeaderHeight + row * RowHeight, Width, RowHeight);

        // A row's parts: 0 source, 1 destination, 2 amount, 3 remove.
        public NeoRect Part(int row, int part)
        {
            var r = Row(row);
            double x = NumberWidth;
            double top = r.Y + 2, h = RowHeight - 4;
            if (part == 0) return new NeoRect(x, top, sourceWidth, h);
            x += sourceWidth + BoxGap + 14;
            if (part == 1) return new NeoRect(x, top, destWidth, h);
            x += destWidth + BoxGap;
            if (part == 2) return new NeoRect(x, top, amountWidth, h);
            x += amountWidth + BoxGap;
            return new NeoRect(x, top, RemoveWidth, h);
        }

        (int Row, int Part) HitTest(double x, double y)
        {
            int count = Shown().Count;
            int row = (int)Math.Floor((y - HeaderHeight) / RowHeight);
            if (y < HeaderHeight || row < 0 || row > count) return (-1, -1);
            if (row == count) return Free() >= 0 && x < Part(row, 1).Right ? (row, 4) : (-1, -1);   // "+ Add route"
            for (int part = 0; part < 4; part++)
            {
                var r = Part(row, part);
                if (x >= r.X - 2 && x <= r.Right + 2) return (row, part);
            }
            return (row, -1);
        }

        string Name(int index) => ctx.Table[index].Describe(ctx.Raw(index));

        protected override void OnRender(DrawingContext dc)
        {
            var c = canvas.On(dc);
            var accent = ctx.Accent;
            c.Text("SOURCE", NumberWidth + 1, 1, 10.5, Palette.DimText);
            c.Text("DESTINATION", Part(0, 1).X + 1, 1, 10.5, Palette.DimText);
            c.Text("AMOUNT", Part(0, 2).X + 1, 1, 10.5, Palette.DimText);
            var shown = Shown();
            for (int row = 0; row < shown.Count; row++)
            {
                int r = shown[row];
                var rr = Row(row);
                c.Text((r + 1).ToString(), 2, rr.Y + 4, 11, Palette.DimText);
                for (int part = 0; part < 2; part++)
                {
                    var b = Part(row, part);
                    int index = part == 0 ? Src(r) : Dst(r);
                    bool off = ctx.Raw(index) == 0;
                    bool hot = row == hoverRow && part == hoverPart;
                    c.Rect(b, hot ? Palette.Track : Palette.Background, new NeoStroke(hot ? accent : Palette.PanelEdge, 1), 3);
                    c.Text(off ? "choose…" : Name(index), b.X + 6, b.Y + 2, TextSize, off ? Palette.DimText : Palette.Text, maxWidth: Math.Max(10, b.Width - 20));
                    c.Text("▾", b.Right - 13, b.Y + 2, 11, accent);
                }
                var arrow = Part(row, 0);
                c.Text("→", arrow.Right + BoxGap + 1, arrow.Y + 1, 12, Palette.DimText);

                // The amount: a bar from the middle, its value written over it.
                var a = Part(row, 2);
                int raw = ctx.Raw(Amt(r));
                var def = ctx.Table[Amt(r)];
                double t = def.Normalized(raw) * 2 - 1;
                double mid = a.X + a.Width / 2;
                c.Rect(a, Palette.Background, new NeoStroke(row == hoverRow && hoverPart == 2 ? accent : Palette.PanelEdge, 1), 3);
                double w = Math.Abs(t) * (a.Width / 2 - 2);
                if (w > 0.5) c.Rect(new NeoRect(t >= 0 ? mid : mid - w, a.Y + 2, w, a.Height - 4), accent.WithAlpha(0x70), NeoStroke.None, 2);
                c.Line(new NeoPoint(mid, a.Y + 2), new NeoPoint(mid, a.Bottom - 2), new NeoStroke(Palette.KnobTrack, 1));
                c.Text(def.Describe(raw), a.X + a.Width / 2, a.Y + 2, TextSize, Palette.Text, align: NeoAlign.Center);

                var x = Part(row, 3);
                c.Text("×", x.X + x.Width / 2, x.Y + 1, 13, row == hoverRow && hoverPart == 3 ? accent : Palette.DimText, align: NeoAlign.Center);
            }
            if (Free() >= 0)
            {
                var add = Part(shown.Count, 0);
                bool hot = hoverRow == shown.Count && hoverPart == 4;
                c.Text("+ Add route", add.X + 1, add.Y + 2, TextSize, hot ? accent : Palette.DimText, hot);
            }
            if (shown.Count == 0)
                c.Text("No routes yet: add one, or pick an LFO, envelope or macro and a destination.", NumberWidth + 1, Row(1).Y + 6, 11, Palette.DimText, maxWidth: Width - NumberWidth - 4);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (dragRoute >= 0)
            {
                var def = ctx.Table[Amt(dragRoute)];
                double scale = (Fine(e) ? 0.1 : 1) * (def.Max - def.Min) / Math.Max(40, amountWidth);
                int raw = Math.Clamp((int)Math.Round(dragStartRaw + (p.X - dragStartX) * scale), def.Min, def.Max);
                if (Fine(e))
                {
                    // Finer: from here on, a tenth as far.
                    dragStartRaw = raw;
                    dragStartX = p.X;
                }
                ctx.Set(Amt(dragRoute), raw);
                InvalidateVisual();
                return;
            }
            var (row, part) = HitTest(p.X, p.Y);
            if (row == hoverRow && part == hoverPart) return;
            hoverRow = row;
            hoverPart = part;
            Cursor = part == 2 ? Cursors.SizeWE : part >= 0 ? Cursors.Hand : Cursors.Arrow;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (dragRoute >= 0) return;
            hoverRow = hoverPart = -1;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            var (row, part) = HitTest(p.X, p.Y);
            e.Handled = true;
            if (row < 0 || part < 0) return;
            var shown = Shown();
            if (part == 4)
            {
                int free = Free();
                if (free >= 0) OpenSources(free, Part(row, 0));
                return;
            }
            int r = shown[row];
            switch (part)
            {
                case 0: OpenSources(r, Part(row, 0)); break;
                case 1: OpenDestinations(r, Part(row, 1)); break;
                case 2:
                    if (e.ClickCount >= 2)
                    {
                        ctx.Set(Amt(r), ctx.Table[Amt(r)].Raw(0));
                        InvalidateVisual();
                        return;
                    }
                    dragRoute = r;
                    dragStartX = p.X;
                    dragStartRaw = ctx.Raw(Amt(r));
#if AVALONIA
                    gesture.Begin();
#endif
                    CaptureMouse();
                    break;
                case 3: Clear(r); break;
            }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (dragRoute < 0) return;
            dragRoute = -1;
            if (IsMouseCaptured) ReleaseMouseCapture();
#if AVALONIA
            gesture.End();
#endif
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var p = e.GetPosition(this);
            var (row, part) = HitTest(p.X, p.Y);
            if (row < 0 || part != 2) return;
            int r = Shown()[row];
            var def = ctx.Table[Amt(r)];
            int step = Math.Max(1, (def.Max - def.Min) / (Fine(e) ? 1000 : 200));
#if AVALONIA
            gesture.Wheel();
#endif
            ctx.Set(Amt(r), ctx.Raw(Amt(r)) + Math.Sign(e.Delta) * step);
            InvalidateVisual();
            e.Handled = true;
        }

#if AVALONIA
        static bool Fine(MouseEventArgs e) => (e.KeyModifiers & Avalonia.Input.KeyModifiers.Shift) != 0;
#else
        static bool Fine(MouseEventArgs e) => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
#endif

        /// <summary>Clears route <paramref name="r"/> (source and destination Off, amount 0).</summary>
        public void Clear(int r)
        {
            ctx.Set(Src(r), 0);
            ctx.Set(Dst(r), 0);
            ctx.Set(Amt(r), ctx.Table[Amt(r)].Raw(0));
            hoverRow = hoverPart = -1;
            InvalidateVisual();
        }

        /// <summary>Sets route <paramref name="r"/>'s source (a new route: its amount starts at +50 %).</summary>
        public void SetSource(int r, RouteSource source)
        {
            bool fresh = !Used(r);
            ctx.Set(Src(r), (int)source);
            if (fresh) ctx.Set(Amt(r), ctx.Table[Amt(r)].Raw(0.5));
            InvalidateVisual();
        }

        public void SetDestination(int r, RouteDest dest)
        {
            ctx.Set(Dst(r), (int)dest);
            InvalidateVisual();
        }

        static string Label(ParamDef def, int raw) => def.Names != null && raw - def.Min < def.Names.Length ? def.Names[raw - def.Min] : raw.ToString();

        void OpenSources(int r, NeoRect at)
        {
            var def = ctx.Table[Src(r)];
            int current = ctx.Raw(Src(r));
            var menu = new ContextMenu();
            for (int s = 1; s < (int)RouteSource.Count; s++)
            {
                var source = (RouteSource)s;
                menu.Items.Add(Item(Label(def, s), s == current, () => SetSource(r, source)));
            }
            Open(menu, at);
        }

        void OpenDestinations(int r, NeoRect at)
        {
            var def = ctx.Table[Dst(r)];
            int current = ctx.Raw(Dst(r));
            var menu = new ContextMenu();
            foreach (var (group, dests) in DestGroups)
            {
                if (group.Length == 0)
                {
                    foreach (var d in dests) menu.Items.Add(Item(Label(def, (int)d), (int)d == current, () => SetDestination(r, d)));
                    continue;
                }
                var sub = new MenuItem { Header = group };
                foreach (var d in dests) sub.Items.Add(Item(Label(def, (int)d), (int)d == current, () => SetDestination(r, d)));
                menu.Items.Add(sub);
            }
            Open(menu, at);
        }

        static MenuItem Item(string text, bool on, Action pick)
        {
#if AVALONIA
            var item = new MenuItem { Header = text, ToggleType = MenuItemToggleType.Radio, IsChecked = on };
#else
            var item = new MenuItem { Header = text, IsChecked = on };
#endif
            item.Click += (s, a) => pick();
            return item;
        }

        void Open(ContextMenu menu, NeoRect at)
        {
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
#if AVALONIA
            menu.PlacementRect = new Avalonia.Rect(at.X, at.Y, at.Width, at.Height);
            menu.Closed += (s, e) => { if (Menu == menu) Menu = null; };
            Menu = menu;
            menu.Open(this);
#else
            menu.PlacementRectangle = new Rect(at.X, at.Y, at.Width, at.Height);
            menu.Closed += (s, e) => { if (Menu == menu) Menu = null; };
            Menu = menu;
            menu.IsOpen = true;
#endif
        }
    }
}
