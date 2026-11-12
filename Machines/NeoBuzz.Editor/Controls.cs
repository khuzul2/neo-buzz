#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): controls bound to ReBuzz parameters, and
// the frames, meters and graph surfaces editors are built from. WPF does the layout and routes
// input; layout maths, hit-testing and drawing are toolkit-free (Core/), drawn through WpfCanvas.
// ParamBinding is in Params/EditorContext.cs (shared with the studio's Avalonia kit).

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
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
    internal sealed class ParamChoice : FrameworkElement
    {
        readonly EditorContext ctx;
        readonly ChoiceStrip strip;
        readonly ToggleFace toggle;
        readonly WpfCanvas canvas;
        int selected;
        int hover = -1;

        public ParamChoice(EditorContext ctx, int index, string label = null, double cellWidth = 0, bool vertical = false)
        {
            this.ctx = ctx;
            canvas = new WpfCanvas(this);
            var def = ctx.Table[index];
            strip = new ChoiceStrip(label ?? def.Name, ParamMapping.ChoiceNames(def), cellWidth, vertical, canvas);
            if (ParamMapping.IsToggle(def)) toggle = new ToggleFace(strip.Label, strip.Width, canvas);
            Width = toggle?.Width ?? strip.Width;
            Height = toggle?.Height ?? strip.Height;
            Binding = ctx.Bind(index, Show);
            ToolTip = def.Description;
            Cursor = Cursors.Hand;
            Show(Binding.Value);
        }

        public ParamBinding Binding { get; }
        public string Label => strip.Label;
        public bool Vertical => strip.Vertical;

        /// <summary>An Off/On switch, drawn as one toggle (a click switches it).</summary>
        public bool IsToggle => toggle != null;

        /// <summary>Whether a toggle is on (lit).</summary>
        public bool IsOn => toggle != null && selected == 1;

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
            // A toggle switches; a row picks the button.
            if (i >= 0) Binding.Set(toggle != null ? (selected == 1 ? Binding.Def.Min : Binding.Def.Max) : Binding.Def.Min + i);
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
        public Section(string title, NeoColor accent, UIElement content)
        {
            Background = Theme.Panel;
            BorderBrush = Theme.PanelEdge;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(6);
            Padding = new Thickness(10, 6, 10, 8);
            Margin = new Thickness(4);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = title.ToUpperInvariant(),
                Foreground = Theme.Brush(accent),
                FontFamily = Theme.FontFamily,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            stack.Children.Add(content);
            Child = stack;
        }
    }

    /// <summary>Lays controls out in a row.</summary>
    internal static class Layout
    {
        public static StackPanel Row(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var i in items) if (i != null) p.Children.Add(i);
            return p;
        }

        public static StackPanel Column(params UIElement[] items)
        {
            var p = new StackPanel { Orientation = Orientation.Vertical };
            foreach (var i in items) if (i != null) p.Children.Add(i);
            return p;
        }

        public static FrameworkElement Gap(double w, double h = 1) => new FrameworkElement { Width = w, Height = h };

        /// <summary>Controls in a grid of <paramref name="columns"/> columns.</summary>
        public static UniformGrid Grid(int columns, params UIElement[] items)
        {
            var g = new UniformGrid { Columns = columns };
            foreach (var i in items) if (i != null) g.Children.Add(i);
            return g;
        }
    }

    /// <summary>
    /// A surface redrawn by a callback, on demand and (when <c>animate</c>) about 30 times a
    /// second while visible: curves, envelopes, waves, meters. The callback draws on an
    /// INeoCanvas, inside the frame (Core/Graphs.cs: GraphFrame).
    /// </summary>
    internal class GraphView : FrameworkElement
    {
        readonly Action<INeoCanvas, NeoRect> draw;
        readonly WpfCanvas canvas;
        DispatcherTimer timer;

        public GraphView(double width, double height, Action<INeoCanvas, NeoRect> draw, bool animate = false)
        {
            Width = width;
            Height = height;
            this.draw = draw;
            canvas = new WpfCanvas(this);
            if (animate)
            {
                timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
                timer.Tick += (s, e) => { if (IsVisible) InvalidateVisual(); };
                Loaded += (s, e) => timer.Start();
                Unloaded += (s, e) => timer.Stop();
                IsVisibleChanged += (s, e) => { if (IsVisible) timer.Start(); else timer.Stop(); };
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var r = new NeoRect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
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
        }
    }
}

namespace NeoBuzz.Editor
{
    /// <summary>A compact drop-down for an enum parameter with many choices (click for the list).</summary>
    internal sealed class ParamDropdown : FrameworkElement
    {
        readonly EditorContext ctx;
        readonly string[] names;
        readonly WpfCanvas canvas;
        int selected;

        public ParamDropdown(EditorContext ctx, int index, string label = null, double width = 110)
        {
            this.ctx = ctx;
            var def = ctx.Table[index];
            names = def.Names ?? new[] { "Off", "On" };
            Label = label ?? def.Name;
            Width = width;
            Height = DropdownFace.Height;
            canvas = new WpfCanvas(this);
            Binding = ctx.Bind(index, raw => { selected = raw - def.Min; InvalidateVisual(); });
            selected = Binding.Value - def.Min;
            ToolTip = def.Description;
            Cursor = System.Windows.Input.Cursors.Hand;
        }

        public ParamBinding Binding { get; }
        public string Label { get; }

        protected override void OnMouseLeftButtonDown(System.Windows.Input.MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            for (int i = 0; i < names.Length; i++)
            {
                int choice = i;
                var item = new MenuItem { Header = names[i], IsChecked = i == selected };
                item.Click += (s, a) => Binding.Set(Binding.Def.Min + choice);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            DropdownFace.Draw(canvas.On(dc), Width, Label, DropdownFace.TextOf(names, selected), ctx.Accent);
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
}

namespace NeoBuzz.Editor
{
    /// <summary>A small button drawn in the theme.</summary>
    internal sealed class ActionButton : FrameworkElement
    {
        readonly NeoColor accent;
        readonly WpfCanvas canvas;
        bool hover;

        public ActionButton(string text, NeoColor accent, double width = 90)
        {
            Text = text;
            this.accent = accent;
            canvas = new WpfCanvas(this);
            Width = width;
            Height = ButtonFace.Height;
            Cursor = System.Windows.Input.Cursors.Hand;
        }

        public string Text { get; set; }
        public event Action Click;

        protected override void OnMouseEnter(System.Windows.Input.MouseEventArgs e) { hover = true; InvalidateVisual(); }
        protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e) { hover = false; InvalidateVisual(); }

        protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
        {
            Click?.Invoke();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) => ButtonFace.Draw(canvas.On(dc), Width, Height, Text, hover, accent);
    }
}

namespace NeoBuzz.Editor
{
    /// <summary>
    /// Runs an action now and then while an element is shown: for state that changes without a
    /// parameter changing (a table or bank loaded with the song or a preset).
    /// </summary>
    internal static class Poll
    {
        public static void Every(FrameworkElement owner, double seconds, Action action)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (s, e) => action();
            owner.Loaded += (s, e) => timer.Start();
            owner.Unloaded += (s, e) => timer.Stop();
        }
    }
}
