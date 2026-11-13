// Fork (docs/specs/studio-mvp.md S1): the Neo editor kit's controls on Avalonia, ported from the
// WPF kit's Controls.cs with the same API: controls bound to ReBuzz parameters, and the frames,
// meters and graph surfaces editors are built from. Avalonia does the layout and routes input;
// layout maths, hit-testing and drawing are toolkit-free (Core/), drawn through AvaloniaCanvas.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using NeoBuzz.Dsp;
using KitTheme = NeoBuzz.Editor.Theme;   // inside controls, Theme is Avalonia's (StyledElement.Theme)

namespace NeoBuzz.Editor
{
    /// <summary>
    /// Brackets a control's edits into one undo step of the editor it is in (ParamEdits): what a
    /// drag changes between Begin and End, and wheel turns until they pause.
    /// </summary>
    internal sealed class EditGesture
    {
        static readonly TimeSpan WheelPause = TimeSpan.FromMilliseconds(600);
        readonly NeoElement owner;
        ParamEdits open, wheel;
        DispatcherTimer timer;

        public EditGesture(NeoElement owner)
        {
            this.owner = owner;
        }

        public void Begin()
        {
            EndWheel();
            open?.End();
            open = owner.Edits;
            open?.Begin();
        }

        public void End()
        {
            open?.End();
            open = null;
        }

        public void Wheel()
        {
            if (wheel == null)
            {
                wheel = owner.Edits;
                wheel?.Begin();
            }
            if (timer == null)
            {
                timer = new DispatcherTimer { Interval = WheelPause };
                timer.Tick += (s, e) => EndWheel();
            }
            timer.Stop();
            timer.Start();
        }

        void EndWheel()
        {
            timer?.Stop();
            wheel?.End();
            wheel = null;
        }
    }

    /// <summary>A knob for a parameter.</summary>
    internal sealed class ParamKnob : Knob
    {
        public ParamKnob(EditorContext ctx, int index, string label = null, double diameter = 46)
            : base(ctx.Accent, diameter)
        {
            var def = ctx.Table[index];
            Label = label ?? def.Name;
            Origin = ParamMapping.ArcOrigin(def);
            Steps = def.Max - def.Min;
            Binding = ctx.Bind(index, Show);
            Default = Binding.Normalized(def.Default);
            UserChanged += t => Binding.Set(Binding.RawFromNormalized(t));
            ToolTip = def.Description;
            // Explain (neo-learn.md §5.3): hovering and dragging the knob show it in the strip.
            if (ctx.Explain is { } explain)
                Phase += ph =>
                {
                    switch (ph)
                    {
                        case KnobPhase.Enter: explain.Hover(index); break;
                        case KnobPhase.Leave: explain.Unhover(index); break;
                        case KnobPhase.Touch: explain.BeginDrag(index); break;
                        case KnobPhase.Release: explain.EndDrag(index); break;
                    }
                };
            Show(Binding.Value);
        }

        public ParamBinding Binding { get; }

        void Show(int raw)
        {
            Value = Binding.Normalized(raw);
            ValueText = Binding.Def.Describe(raw);
            Refresh();
        }
    }

    /// <summary>
    /// A row of buttons, one per choice, for an enum parameter; an Off/On switch is one toggle named for its function,
    /// lit when on (ToggleFace).
    /// </summary>
    internal sealed class ParamChoice : NeoElement
    {
        readonly EditorContext ctx;
        readonly ChoiceStrip strip;
        readonly ToggleFace toggle;
        readonly AvaloniaCanvas canvas;
        readonly EditGesture gesture;
        int selected;
        int hover = -1;

        public ParamChoice(EditorContext ctx, int index, string label = null, double cellWidth = 0, bool vertical = false)
        {
            this.ctx = ctx;
            canvas = new AvaloniaCanvas(this);
            gesture = new EditGesture(this);
            var def = ctx.Table[index];
            strip = new ChoiceStrip(label ?? def.Name, ParamMapping.ChoiceNames(def), cellWidth, vertical, canvas);
            if (ParamMapping.IsToggle(def)) toggle = new ToggleFace(strip.Label, strip.Width, canvas);
            Width = toggle?.Width ?? strip.Width;
            Height = toggle?.Height ?? strip.Height;
            Binding = ctx.Bind(index, Show);
            ToolTip = def.Description;
            Cursor = Cursors.Hand;
            AutomationProperties.SetName(this, strip.Label);
            Show(Binding.Value);
        }

        public ParamBinding Binding { get; }
        public string Label => strip.Label;
        public bool Vertical => strip.Vertical;

        /// <summary>An Off/On switch, drawn as one toggle (a click switches it).</summary>
        public bool IsToggle => toggle != null;

        /// <summary>Whether a toggle is on (lit).</summary>
        public bool IsOn => toggle != null && selected == 1;

        /// <summary>Where choice <paramref name="i"/>'s button is (for tests); a toggle's one button for either.</summary>
        public Rect CellBounds(int i)
        {
            var r = toggle?.Button ?? strip.Cell(i);
            return new Rect(r.X, r.Y, r.Width, r.Height);
        }

        void Show(int raw)
        {
            selected = raw - Binding.Def.Min;
            InvalidateVisual();
        }

        int CellAt(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (toggle != null) return toggle.Hit(p.X, p.Y) ? 0 : -1;
            return strip.CellAt(p.X, p.Y);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            int i = CellAt(e);
            if (i >= 0)
            {
                gesture.Begin();
                // A toggle switches; a row picks the button.
                Binding.Set(toggle != null ? (selected == 1 ? Binding.Def.Min : Binding.Def.Max) : Binding.Def.Min + i);
                gesture.End();
            }
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = CellAt(e);
            if (i != hover)
            {
                hover = i;
                InvalidateVisual();
            }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            hover = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (toggle != null) toggle.Draw(canvas.On(dc), selected == 1, hover >= 0, ctx.Accent);
            else strip.Draw(canvas.On(dc), selected, hover, ctx.Accent);
        }
    }

    /// <summary>A titled panel holding controls.</summary>
    internal sealed class Section : Border
    {
        public Section(string title, NeoColor accent, Control content)
        {
            Background = KitTheme.Panel;
            BorderBrush = KitTheme.PanelEdge;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(10, 6, 10, 8);
            Margin = new Thickness(4);
            AutomationProperties.SetName(this, title);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(),
                Foreground = KitTheme.Brush(accent),
                FontFamily = KitTheme.FontFamily,
                FontSize = 10.5,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            stack.Children.Add(content);
            Child = stack;
        }
    }

    /// <summary>Lays controls out in a row.</summary>
    internal static class Layout
    {
        public static StackPanel Row(params Control[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) if (i != null) p.Children.Add(i);
            return p;
        }

        public static StackPanel Column(params Control[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var i in items) if (i != null) p.Children.Add(i);
            return p;
        }

        public static Control Gap(double w, double h = 1) => new Control { Width = w, Height = h };

        /// <summary>Controls in a grid of <paramref name="columns"/> columns.</summary>
        public static UniformGrid Grid(int columns, params Control[] items)
        {
            var g = new UniformGrid { Columns = columns };
            foreach (var i in items) if (i != null) g.Children.Add(i);
            return g;
        }
    }

    /// <summary>
    /// A surface redrawn by a callback, on demand and (when <c>animate</c>) about 30 times a
    /// second while shown: curves, envelopes, waves, meters. The callback draws on an INeoCanvas,
    /// inside the frame (Core/Graphs.cs: GraphFrame).
    /// </summary>
    internal class GraphView : NeoElement
    {
        readonly Action<INeoCanvas, NeoRect> draw;
        readonly AvaloniaCanvas canvas;
        readonly DispatcherTimer timer;

        public GraphView(double width, double height, Action<INeoCanvas, NeoRect> draw, bool animate = false)
        {
            Width = width;
            Height = height;
            this.draw = draw;
            canvas = new AvaloniaCanvas(this);
            if (animate)
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
                timer.Tick += (s, e) => { if (IsEffectivelyVisible) InvalidateVisual(); };
                Loaded += (s, e) => timer.Start();
                Unloaded += (s, e) => timer.Stop();
            }
        }

        /// <summary>True while it redraws on its own (shown, and animated).</summary>
        public bool Animating => timer?.IsEnabled == true;

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (timer == null || change.Property != IsVisibleProperty) return;
            if (IsVisible && IsLoaded) timer.Start();
            else timer.Stop();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var r = new NeoRect(0, 0, Bounds.Width > 0 ? Bounds.Width : Width, Bounds.Height > 0 ? Bounds.Height : Height);
            GraphFrame.Draw(canvas.On(dc), r, draw);
        }
    }

    /// <summary>
    /// The Explain strip (neo-learn.md §5.3): the control you touch, its value and who set it (Manual, Agent or Automation),
    /// what it does and what you will hear. State and drawing are the kit's core (Core/Explain.cs).
    /// </summary>
    internal sealed class ExplainStrip : GraphView
    {
        readonly EditorContext ctx;

        public ExplainStrip(EditorContext ctx, double width)
            : base(width, ExplainPainter.Height, (c, r) => ExplainPainter.Draw(c, r, ctx.Explain?.Current, ctx.Accent), animate: true)
        {
            this.ctx = ctx;
        }

        /// <summary>What it shows now (null: nothing touched yet).</summary>
        public ExplainLine Line => ctx.Explain?.Current;
    }

    /// <summary>Vertical level meter in dB (−60 … +6), reading a value 30 times a second.</summary>
    internal sealed class Meter : GraphView
    {
        /// <param name="level">The level as a gain; with <paramref name="reduction"/>, the gain applied (≤ 1), drawn as a bar growing down from 0 dB.</param>
        public Meter(string label, Func<float> level, NeoColor color, double height = 120, bool reduction = false)
            : base(16, height, (c, r) => MeterPainter.Draw(c, r, level(), color, reduction), animate: true)
        {
            ToolTip = label;
            AutomationProperties.SetName(this, label);
        }
    }

    /// <summary>A compact drop-down for an enum parameter with many choices (click for the list).</summary>
    internal sealed class ParamDropdown : NeoElement
    {
        readonly EditorContext ctx;
        readonly string[] names;
        readonly AvaloniaCanvas canvas;
        readonly EditGesture gesture;
        int selected;

        public ParamDropdown(EditorContext ctx, int index, string label = null, double width = 110)
        {
            this.ctx = ctx;
            var def = ctx.Table[index];
            names = def.Names ?? new[] { "Off", "On" };
            Label = label ?? def.Name;
            Width = width;
            Height = DropdownFace.Height;
            canvas = new AvaloniaCanvas(this);
            gesture = new EditGesture(this);
            Binding = ctx.Bind(index, raw => { selected = raw - def.Min; InvalidateVisual(); });
            selected = Binding.Value - def.Min;
            ToolTip = def.Description;
            Cursor = Cursors.Hand;
            AutomationProperties.SetName(this, Label);
        }

        public ParamBinding Binding { get; }
        public string Label { get; }

        /// <summary>The list, while it is open (for tests).</summary>
        public ContextMenu Menu { get; private set; }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            for (int i = 0; i < names.Length; i++)
            {
                int choice = i;
                var item = new MenuItem { Header = names[i], ToggleType = MenuItemToggleType.Radio, IsChecked = i == selected };
                item.Click += (s, a) =>
                {
                    gesture.Begin();
                    Binding.Set(Binding.Def.Min + choice);
                    gesture.End();
                };
                menu.Items.Add(item);
            }
            menu.Closed += (s, a) => { if (Menu == menu) Menu = null; };
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            Menu = menu;
            menu.Open(this);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            DropdownFace.Draw(canvas.On(dc), Bounds.Width > 0 ? Bounds.Width : Width, Label, DropdownFace.TextOf(names, selected), ctx.Accent);
    }

    /// <summary>Shortcuts editors use to lay out many parameters.</summary>
    internal static class Build
    {
        /// <summary>Knobs for parameters in a grid of <paramref name="columns"/> columns.</summary>
        public static UniformGrid Knobs(EditorContext ctx, int columns, double diameter, params int[] indices)
        {
            var g = new UniformGrid { Columns = columns };
            foreach (int i in indices) g.Children.Add(new ParamKnob(ctx, i, diameter: diameter));
            return g;
        }

        /// <summary>A picture of an ADSR envelope from four parameters (times in seconds, sustain 0 … 1).</summary>
        public static GraphView Envelope(EditorContext ctx, int attack, int decay, int sustain, int release, double width = 150, double height = 56)
        {
            var view = new GraphView(width, height, (c, r) =>
            {
                double a = ctx.Value(attack), d = ctx.Value(decay), s = ctx.Value(sustain), rl = ctx.Value(release);
                EnvelopePainter.Adsr(c, r, a, d, s, rl, ctx.Accent);
            });
            ctx.Changed += view.InvalidateVisual;
            return view;
        }
    }

    /// <summary>A small button drawn in the theme.</summary>
    internal sealed class ActionButton : NeoElement
    {
        readonly NeoColor accent;
        readonly AvaloniaCanvas canvas;
        string text;
        bool hover;

        public ActionButton(string text, NeoColor accent, double width = 90)
        {
            Text = text;
            this.accent = accent;
            canvas = new AvaloniaCanvas(this);
            Width = width;
            Height = ButtonFace.Height;
            Cursor = Cursors.Hand;
        }

        public string Text
        {
            get => text;
            set
            {
                text = value;
                AutomationProperties.SetName(this, value);
            }
        }

        public event Action Click;

        protected override void OnMouseEnter(MouseEventArgs e) { hover = true; InvalidateVisual(); }
        protected override void OnMouseLeave(MouseEventArgs e) { hover = false; InvalidateVisual(); }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            Click?.Invoke();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            ButtonFace.Draw(canvas.On(dc), Bounds.Width > 0 ? Bounds.Width : Width, Bounds.Height > 0 ? Bounds.Height : Height, Text, hover, accent);
    }

    /// <summary>
    /// Runs an action now and then while an element is shown: for state that changes without a
    /// parameter changing (a table or bank loaded with the song or a preset).
    /// </summary>
    internal static class Poll
    {
        public static void Every(Control owner, double seconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) => action();
            owner.Loaded += (s, e) => timer.Start();
            owner.Unloaded += (s, e) => timer.Stop();
        }
    }
}
