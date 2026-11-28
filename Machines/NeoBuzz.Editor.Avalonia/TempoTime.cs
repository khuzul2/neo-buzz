// Fork (docs/sdk/tempo-sync.md, SYN1): the one tempo-time control every Neo editor shows for a musical timing. It is the rate or time knob,
// a Free | Sync switch under it and one compact Feel picker (Straight, Dotted, Triplet, Quintuplet). In Free the knob reads Hz or ms as it
// always did; in Sync the same knob steps through the divisions from long to short (8 bars to 1/64) and reads the division with its feel
// ("1/8 dotted"). What it shows and writes is the toolkit-free TimingModel (Core/TimingModel.cs); this file draws it and routes the
// input, as the kit's other controls do.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>Where one timing's parameters are in the machine's table (-1: it has none).</summary>
    internal sealed class TempoTimeSpec
    {
        /// <summary>The Sync list (it holds Free as its first entry) or, with <see cref="Mode"/>, the Rate list of divisions.</summary>
        public int Division;
        public int Feel = -1;
        /// <summary>The Song tempo / Free time switch of Neo Arp's shape; -1 when Free is an entry of the Division list.</summary>
        public int Mode = -1;
        /// <summary>The knob that sets the free value (Hz, or ms).</summary>
        public int Rate = -1;
        /// <summary>The free value and the synced one are shown in Hz (an LFO), otherwise as a time.</summary>
        public bool Hz;
        /// <summary>The longest time the machine runs (s); divisions that do not fit at the song's tempo are greyed out and clamp. 0: none.</summary>
        public double MaxSeconds;
        /// <summary>The label over the knob when it differs from the parameter's name.</summary>
        public string RateLabel;
        public double KnobDiameter = 54;
        /// <summary>The Free | Sync switch and the Feel side by side under the knob (one line, 164 px wide) instead of one above the other (two lines, 88 px wide).</summary>
        public bool Wide;
    }

    /// <summary>One entry of a picker's menu.</summary>
    internal readonly struct TimingItem
    {
        public TimingItem(string text, bool enabled, bool selected, Action pick)
        {
            Text = text; Enabled = enabled; Selected = selected; Pick = pick;
        }

        public string Text { get; }
        public bool Enabled { get; }
        public bool Selected { get; }
        public Action Pick { get; }
    }

    /// <summary>The state the pieces of one control share: the bindings, the model, and the writes.</summary>
    internal sealed class TempoTimeController
    {
        readonly EditorContext ctx;
        readonly TempoTimeSpec spec;
        readonly ParamBinding division, feel, mode, rate;
        int lastDivision = int.MinValue;

        public TempoTimeController(EditorContext ctx, TempoTimeSpec spec, Action<int> changed)
        {
            this.ctx = ctx;
            this.spec = spec;
            Model = new TimingModel(ctx.Table[spec.Division], spec.Feel >= 0 ? ctx.Table[spec.Feel] : null, spec.Mode >= 0 ? ctx.Table[spec.Mode] : null, spec.Hz, spec.MaxSeconds);
            division = ctx.Bind(spec.Division, v => { Remember(); changed(v); });
            if (spec.Feel >= 0) feel = ctx.Bind(spec.Feel, changed);
            if (spec.Mode >= 0) mode = ctx.Bind(spec.Mode, v => { Remember(); changed(v); });
            rate = ctx.Bind(spec.Rate, changed);
            Remember();
        }

        public TimingModel Model { get; }
        public TempoTimeSpec Spec => spec;
        public EditorContext Context => ctx;
        /// <summary>The free rate or time parameter (what the knob is in Free).</summary>
        public ParamBinding Rate => rate;

        public int DivisionRaw => division.Value;
        public int FeelRaw => feel?.Value ?? 0;
        public int ModeRaw => mode?.Value ?? 0;
        public bool IsFree => Model.IsFree(DivisionRaw, ModeRaw);
        public double Tempo => ctx.SongTempo();

        void Remember()
        {
            if (!Model.IsFree(division.Value, mode?.Value ?? 0)) lastDivision = division.Value;
        }

        public void Apply(TimingWrite w)
        {
            if (w.Division is int d) division.Set(d);
            if (w.Feel is int f && feel != null) feel.Set(f);
            if (w.Mode is int m && mode != null) mode.Set(m);
        }

        public void PickFree() => Apply(Model.PickFree());
        public void PickSync() => Apply(Model.PickSync(DivisionRaw, lastDivision));

        /// <summary>The knob moved in Sync: to step <paramref name="step"/> of the divisions, long to short.</summary>
        public void PickStep(int step) => Apply(Model.PickStep(DivisionRaw, FeelRaw, step, Tempo));

        public int Step => Model.StepOf(DivisionRaw, FeelRaw);

        /// <summary>What the time comes to at the song's tempo ("1/8 dotted = 352 ms at 128 BPM"); the knob's tooltip in Sync.</summary>
        public string Readout() => Model.Readout(DivisionRaw, FeelRaw, ModeRaw, Tempo, "");

        public string KnobText => Model.KnobText(DivisionRaw, FeelRaw, ModeRaw, Tempo);

        public IEnumerable<TimingItem> FeelItems()
        {
            var names = Model.FeelNames;
            int shown = Model.ShownFeel(DivisionRaw, FeelRaw);
            for (int i = 0; i < names.Length; i++)
            {
                int pick = i;
                yield return new TimingItem(names[i], true, i == shown, () => Apply(Model.PickFeel(DivisionRaw, FeelRaw, ctx.Table[spec.Feel].Min + pick)));
            }
        }

        public string FeelText => Model.FeelNames[Math.Clamp(Model.ShownFeel(DivisionRaw, FeelRaw), 0, Model.FeelNames.Length - 1)];
    }

    /// <summary>
    /// The rate or time knob of a timing. In Free it is the machine's own parameter knob (Hz or ms); in Sync it steps through the divisions
    /// from long to short and reads the division with its feel.
    /// </summary>
    internal sealed class TimingKnob : Knob
    {
        readonly TempoTimeController c;

        public TimingKnob(EditorContext ctx, TempoTimeController controller, double diameter, string label)
            : base(ctx.Accent, diameter)
        {
            c = controller;
            var def = c.Rate.Def;
            Label = label ?? def.Name;
            Origin = ParamMapping.ArcOrigin(def);
            Default = c.Rate.Normalized(def.Default);
            UserChanged += t =>
            {
                if (c.IsFree) c.Rate.Set(c.Rate.RawFromNormalized(t));
                else c.PickStep((int)Math.Round(t * Math.Max(1, c.Model.Steps.Count - 1)));
            };
            BindModulation(ctx.Machine, c.Rate.Parameter, c.Rate.Track, c.Rate.Normalized);
            int index = c.Spec.Rate;
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
            Show();
        }

        /// <summary>Shows what the timing is now (the free value, or the division); called when any of its parameters or the tempo change.</summary>
        public void Show()
        {
            var def = c.Rate.Def;
            if (c.IsFree)
            {
                Steps = def.Max - def.Min;
                Value = c.Rate.Normalized(c.Rate.Value);
                ValueText = def.Describe(c.Rate.Value);
                ToolTip = def.Description;
            }
            else
            {
                int n = c.Model.Steps.Count;
                Steps = Math.Max(1, n - 1);
                Value = n > 1 ? c.Step / (double)(n - 1) : 0;
                ValueText = c.KnobText;
                ToolTip = c.Readout() + ". Turn to step through the divisions, long to short.";
            }
            Refresh();
        }
    }

    /// <summary>The Free | Sync switch, in the house style of the choice buttons (a row of two, the lit one in the accent).</summary>
    internal sealed class TimingSwitch : NeoElement
    {
        const double Row = 22;
        readonly TempoTimeController c;
        readonly AvaloniaCanvas canvas;
        readonly EditGesture gesture;
        readonly NeoColor accent;
        int hover = -1;

        public TimingSwitch(TempoTimeController controller, NeoColor accent, double width)
        {
            c = controller;
            this.accent = accent;
            Width = width;
            Height = Row;
            canvas = new AvaloniaCanvas(this);
            gesture = new EditGesture(this);
            Cursor = Cursors.Hand;
            ToolTip = "Free runs on the knob's own value; Sync locks to the song's tempo";
            AutomationProperties.SetName(this, "Timing");
        }

        /// <summary>0 when Free is lit, 1 when Sync is (for tests).</summary>
        public int Selected => c.IsFree ? 0 : 1;

        double CellWidth => (Bounds.Width > 0 ? Bounds.Width : Width) / 2;

        /// <summary>The middle of a button (for tests).</summary>
        public Point CellCentre(int i) => new Point(CellWidth * i + CellWidth / 2, Row / 2);

        int CellAt(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            return p.X < 0 || p.X >= CellWidth * 2 || p.Y < 0 || p.Y > Row ? -1 : (int)(p.X / CellWidth);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = CellAt(e);
            if (h != hover) { hover = h; InvalidateVisual(); }
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            hover = -1;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            int i = CellAt(e);
            if (i < 0) return;
            gesture.Begin();
            if (i == 0) c.PickFree(); else c.PickSync();
            gesture.End();
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var cv = (INeoCanvas)canvas.On(dc);
            double cw = CellWidth;
            string[] names = { "Free", "Sync" };
            int selected = Selected;
            var accentFill = accent.WithAlpha(0x38);
            for (int i = 0; i < 2; i++)
            {
                var r = new NeoRect(i * cw, 0, cw - 2, Row);
                bool on = i == selected;
                cv.Rect(r, on ? accentFill : i == hover ? Palette.Track : Palette.Panel, new NeoStroke(on ? accent : Palette.PanelEdge, 1), 3);
                cv.Text(names[i], r.X + r.Width / 2, r.Y + (r.Height - 15) / 2, 11, on ? accent : Palette.Text, on, NeoAlign.Center);
            }
        }
    }

    /// <summary>A compact drop-down over a list a callback makes (the picker's content follows the tempo and the other values).</summary>
    internal sealed class TimingDropdown : NeoElement
    {
        const double CompactHeight = 22;
        readonly string label;
        readonly Func<string> text;
        readonly Func<IEnumerable<TimingItem>> items;
        readonly Func<bool> active;
        readonly AvaloniaCanvas canvas;
        readonly EditGesture gesture;
        readonly NeoColor accent;

        public TimingDropdown(string label, double width, NeoColor accent, Func<string> text, Func<IEnumerable<TimingItem>> items, Func<bool> active, string tip)
        {
            this.label = label; this.text = text; this.items = items; this.active = active; this.accent = accent;
            Width = width;
            Height = CompactHeight;
            canvas = new AvaloniaCanvas(this);
            gesture = new EditGesture(this);
            ToolTip = tip;
            Cursor = Cursors.Hand;
            AutomationProperties.SetName(this, label);
        }

        /// <summary>The list, while it is open (for tests).</summary>
        public ContextMenu Menu { get; private set; }

        /// <summary>The text it shows now (for tests).</summary>
        public string Text => text();

        /// <summary>The entries it would list now (for tests).</summary>
        public IEnumerable<TimingItem> Items => items();

        public void Refresh()
        {
            Opacity = active() ? 1 : 0.45;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            foreach (var it in items())
            {
                var item = it;
                var mi = new MenuItem { Header = item.Text, ToggleType = MenuItemToggleType.Radio, IsChecked = item.Selected, IsEnabled = item.Enabled };
                mi.Click += (s, a) =>
                {
                    gesture.Begin();
                    item.Pick();
                    gesture.End();
                };
                menu.Items.Add(mi);
            }
            menu.Closed += (s, a) => { if (Menu == menu) Menu = null; };
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            Menu = menu;
            menu.Open(this);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var cv = (INeoCanvas)canvas.On(dc);
            double w = Bounds.Width > 0 ? Bounds.Width : Width;
            cv.Rect(new NeoRect(0, 0, w, CompactHeight), Palette.Background, new NeoStroke(Palette.PanelEdge, 1), 3);
            cv.Text(text(), 6, 3, 11.5, Palette.Text, maxWidth: Math.Max(10, w - 22));
            cv.Text("▾", w - 14, 3, 11, accent);
        }
    }

    /// <summary>The control itself: build it with <see cref="Create"/> where an editor shows a timing.</summary>
    internal sealed class ParamTempoTime : StackPanel
    {
        const double MinWidth = 88, WideWidth = 164, SwitchWidth = 72;
        readonly DispatcherTimer timer;
        double shownTempo = double.NaN;

        public ParamTempoTime(EditorContext ctx, TempoTimeSpec spec)
        {
            Orientation = Avalonia.Layout.Orientation.Vertical;
            Controller = new TempoTimeController(ctx, spec, _ => Refresh());
            var c = Controller;
            Knob = new TimingKnob(ctx, c, spec.KnobDiameter, spec.RateLabel);
            bool wide = spec.Wide && spec.Feel >= 0;
            double w = Math.Max(wide ? WideWidth : MinWidth, Knob.Width);
            Knob.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            Switch = new TimingSwitch(c, ctx.Accent, wide ? SwitchWidth : w);
            Children.Add(Knob);
            Children.Add(Layout.Gap(1, 2));
            if (spec.Feel >= 0)
            {
                Feel = new TimingDropdown("Feel", wide ? w - SwitchWidth - 6 : w - 2, ctx.Accent, () => c.FeelText, c.FeelItems, () => !c.IsFree, "Straight, dotted (x 3/2), triplet (x 2/3) or quintuplet (x 4/5); in Sync");
                if (wide)
                {
                    var line = Layout.Row(Switch, Layout.Gap(4), Feel);
                    line.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
                    Children.Add(line);
                }
                else
                {
                    Children.Add(Switch);
                    Children.Add(Layout.Gap(1, 3));
                    Children.Add(Feel);
                }
            }
            else Children.Add(Switch);
            Width = w;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) => { if (IsEffectivelyVisible && c.Tempo != shownTempo) Refresh(); };
            AttachedToVisualTree += (s, e) => timer.Start();
            DetachedFromVisualTree += (s, e) => timer.Stop();
            Refresh();
        }

        public TempoTimeController Controller { get; }

        /// <summary>The names of the parameters this control binds (the division, the Feel, the Timing switch and the rate knob): what a test asks to see that every parameter has a control.</summary>
        public IReadOnlyList<string> BoundNames
        {
            get
            {
                var t = Controller.Context.Table;
                var s = Controller.Spec;
                var r = new List<string> { t[s.Division].Name, t[s.Rate].Name };
                if (s.Feel >= 0) r.Add(t[s.Feel].Name);
                if (s.Mode >= 0) r.Add(t[s.Mode].Name);
                return r;
            }
        }

        public TimingKnob Knob { get; }
        public TimingSwitch Switch { get; }
        public TimingDropdown Feel { get; }

        void Refresh()
        {
            shownTempo = Controller.Tempo;
            Knob.Show();
            Switch.InvalidateVisual();
            Feel?.Refresh();
        }

        /// <summary>The control for a timing: the knob, the Free | Sync switch under it and the Feel.</summary>
        public static ParamTempoTime Create(EditorContext ctx, TempoTimeSpec spec) => new ParamTempoTime(ctx, spec);
    }
}
