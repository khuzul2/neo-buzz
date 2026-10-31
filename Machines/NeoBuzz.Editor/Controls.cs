#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): controls bound to ReBuzz parameters, and
// the frames, meters and graph surfaces editors are built from.

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
    /// <summary>
    /// Ties a control to one global parameter: user edits set the parameter, automation and other
    /// editors move the control.
    /// </summary>
    internal sealed class ParamBinding
    {
        readonly Action<int> onValue;
        readonly Action<IParameter, int> handler;

        public ParamBinding(IParameter parameter, ParamDef def, Action<int> onValue)
        {
            Parameter = parameter;
            Def = def;
            this.onValue = onValue;
            handler = (p, track) => onValue(Value);
            Parameter?.SubscribeEvents(0, handler, null);
        }

        public IParameter Parameter { get; }
        public ParamDef Def { get; }

        public int Value
        {
            get
            {
                try
                {
                    return Parameter != null ? Parameter.GetValue(0) : Def.Default;
                }
                catch (Exception)
                {
                    return Def.Default;
                }
            }
        }

        public void Set(int raw)
        {
            raw = Math.Clamp(raw, Def.Min, Def.Max);
            if (Parameter == null || raw == Value) return;
            Parameter.SetValue(0, raw);
            onValue(raw);
        }

        public void Detach() => Parameter?.UnsubscribeEvents(0, handler, null);

        public int RawFromNormalized(double t) => Def.Min + (int)Math.Round(Math.Clamp(t, 0, 1) * (Def.Max - Def.Min));

        public double Normalized(int raw) => Def.Max == Def.Min ? 0 : (raw - Def.Min) / (double)(Def.Max - Def.Min);
    }

    /// <summary>A knob for a parameter.</summary>
    internal sealed class ParamKnob : Knob
    {
        public ParamKnob(EditorContext ctx, int index, string label = null, double diameter = 46)
            : base(ctx.Accent, diameter)
        {
            var def = ctx.Table[index];
            Label = label ?? def.Name;
            Bipolar = def.Kind == MapKind.Bipolar || (def.Kind == MapKind.Int && def.Flag) ||
                      (def.Kind == MapKind.Db && def.A < 0 && def.B > 0 && !def.Flag);
            Steps = def.Max - def.Min;
            Binding = ctx.Bind(index, Show);
            Default = Binding.Normalized(def.Default);
            UserChanged += t => Binding.Set(Binding.RawFromNormalized(t));
            ToolTip = def.Description;
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

    /// <summary>A row of buttons, one per choice, for an enum (or switch) parameter.</summary>
    internal sealed class ParamChoice : FrameworkElement
    {
        readonly EditorContext ctx;
        readonly string[] names;
        readonly double cellWidth;
        int selected;
        int hover = -1;

        public ParamChoice(EditorContext ctx, int index, string label = null, double cellWidth = 0, bool vertical = false)
        {
            this.ctx = ctx;
            var def = ctx.Table[index];
            names = def.Names ?? (def.Kind == MapKind.Switch ? new[] { "Off", "On" } : BuildNames(def));
            Label = label ?? def.Name;
            Vertical = vertical;
            this.cellWidth = cellWidth > 0 ? cellWidth : Math.Max(34, MeasureLongest() + 14);
            if (vertical)
            {
                Width = this.cellWidth;
                Height = 18 + names.Length * 22;
            }
            else
            {
                Width = this.cellWidth * names.Length;
                Height = 18 + 24;
            }
            Binding = ctx.Bind(index, Show);
            ToolTip = def.Description;
            Cursor = Cursors.Hand;
            Show(Binding.Value);
        }

        public ParamBinding Binding { get; }
        public string Label { get; }
        public bool Vertical { get; }

        static string[] BuildNames(ParamDef def)
        {
            var n = new string[def.Max - def.Min + 1];
            for (int i = 0; i < n.Length; i++) n[i] = def.Describe(def.Min + i);
            return n;
        }

        double MeasureLongest()
        {
            double w = 0;
            foreach (var n in names) w = Math.Max(w, Theme.Format(this, n, 11, Theme.Text).Width);
            return w;
        }

        void Show(int raw)
        {
            selected = raw - Binding.Def.Min;
            InvalidateVisual();
        }

        int CellAt(Point p)
        {
            if (p.Y < 18) return -1;
            int i = Vertical ? (int)((p.Y - 18) / 22) : (int)(p.X / cellWidth);
            return i >= 0 && i < names.Length ? i : -1;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            int i = CellAt(e.GetPosition(this));
            if (i >= 0) Binding.Set(Binding.Def.Min + i);
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = CellAt(e.GetPosition(this));
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
            Theme.Text(dc, this, Label, 1, 0, 11, Theme.DimText);
            var accent = Theme.Solid(ctx.Accent);
            var accentFill = Theme.Solid(ctx.Accent, 0x38);
            for (int i = 0; i < names.Length; i++)
            {
                Rect r = Vertical ? new Rect(0, 18 + i * 22, cellWidth, 20) : new Rect(i * cellWidth, 18, cellWidth - 2, 22);
                bool on = i == selected;
                dc.DrawRoundedRectangle(on ? accentFill : i == hover ? Theme.Track : Theme.Panel,
                    Theme.Pen(on ? accent : Theme.PanelEdge, 1), r, 3, 3);
                Theme.TextCentered(dc, this, names[i], r.X + r.Width / 2, r.Y + (r.Height - 15) / 2, 11, on ? accent : Theme.Text, on);
            }
        }
    }

    /// <summary>A titled panel holding controls.</summary>
    internal sealed class Section : Border
    {
        public Section(string title, Color accent, UIElement content)
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
                Foreground = Theme.Solid(accent),
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
    /// A surface redrawn by a callback, on demand and (when <see cref="Animate"/>) about 30 times
    /// a second while visible: curves, envelopes, waves, meters.
    /// </summary>
    internal class GraphView : FrameworkElement
    {
        readonly Action<DrawingContext, Rect, GraphView> draw;
        DispatcherTimer timer;

        public GraphView(double width, double height, Action<DrawingContext, Rect, GraphView> draw, bool animate = false)
        {
            Width = width;
            Height = height;
            this.draw = draw;
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
            var r = new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
            dc.DrawRoundedRectangle(Theme.Background, Theme.Pen(Theme.PanelEdge, 1), r, 4, 4);
            dc.PushClip(new RectangleGeometry(r, 4, 4));
            try
            {
                draw(dc, r, this);
            }
            catch (Exception)
            {
                // A drawing error must never take the editor down.
            }
            dc.Pop();
        }
    }

    /// <summary>Vertical level meter in dB (−60 … +6), reading a value 30 times a second.</summary>
    internal sealed class Meter : GraphView
    {
        public Meter(string label, Func<float> level, Color color, double height = 120, bool reduction = false)
            : base(16, height, (dc, r, v) => Draw(dc, r, v, label, level, color, reduction), animate: true)
        {
            ToolTip = label;
        }

        static void Draw(DrawingContext dc, Rect r, Visual v, string label, Func<float> level, Color color, bool reduction)
        {
            const double lo = -60, hi = 6;
            float a = level();
            double db = DspMath.GainToDb(Math.Max(1e-9f, a));
            Rect inner = new Rect(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6);
            double Y(double d) => inner.Bottom - (Math.Clamp(d, lo, hi) - lo) / (hi - lo) * inner.Height;
            if (reduction)
            {
                // Gain reduction: a bar growing down from 0 dB (a is the gain, ≤ 1).
                double top = Y(0);
                double bottom = inner.Top + Math.Clamp(-db, 0, 30) / 30 * inner.Height;
                if (bottom > inner.Top + 0.5)
                    dc.DrawRectangle(Theme.Solid(color), null, new Rect(inner.X, inner.Top, inner.Width, bottom - inner.Top));
                return;
            }
            double y = Y(db);
            var brush = db > 0 ? Theme.Solid(Color.FromRgb(0xFF, 0x4D, 0x4D)) : Theme.Solid(color);
            if (y < inner.Bottom)
                dc.DrawRectangle(brush, null, new Rect(inner.X, y, inner.Width, inner.Bottom - y));
            // 0 dB and −12/−24 ticks.
            foreach (double t in new[] { 0.0, -12, -24, -48 })
                dc.DrawLine(Theme.Pen(Theme.Grid, 1, false), new Point(inner.X, Y(t)), new Point(inner.Right, Y(t)));
        }
    }
}
