// Fork (docs/sdk/tempo-sync.md, SYN1): the one tempo-time control every Neo editor shows for a musical timing. A rate or time knob (Free:
// ms or Hz), a Free / Sync switch, a division picker (8 bars to 1/64, longest first, whatever the stored order) and a Feel picker
// (Straight, Dotted, Triplet, Quintuplet), with a readout ("1/8 dotted = 375 ms at 120 BPM"). What it shows and writes is the toolkit-free
// TimingModel (Core/TimingModel.cs); this file draws it and routes the input, as the kit's other controls do.

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
        /// <summary>The knob is part of the control (false: the editor keeps its own knob where it was; the control only reads its value).</summary>
        public bool KnobInside = true;
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
            if (spec.Rate >= 0) rate = ctx.Bind(spec.Rate, changed);
            Remember();
        }

        public TimingModel Model { get; }
        public TempoTimeSpec Spec => spec;
        public EditorContext Context => ctx;

        public int DivisionRaw => division.Value;
        public int FeelRaw => feel?.Value ?? 0;
        public int ModeRaw => mode?.Value ?? 0;
        public bool IsFree => Model.IsFree(DivisionRaw, ModeRaw);
        public double Tempo => ctx.SongTempo();

        void Remember()
        {
            if (!Model.IsFree(division.Value, mode?.Value ?? 0)) lastDivision = division.Value;
        }

        // Free: the rate knob under it already says its value, so the line stays empty (no second copy of the same number).
        public string FreeText => "";

        public string Readout() => Model.Readout(DivisionRaw, FeelRaw, ModeRaw, Tempo, FreeText);

        public void Apply(TimingWrite w)
        {
            if (w.Division is int d) division.Set(d);
            if (w.Feel is int f && feel != null) feel.Set(f);
            if (w.Mode is int m && mode != null) mode.Set(m);
        }

        /// <summary>The Free / Sync switch's two entries.</summary>
        public IEnumerable<TimingItem> ModeItems()
        {
            bool free = IsFree;
            yield return new TimingItem("Free", true, free, () => Apply(Model.PickFree()));
            yield return new TimingItem("Sync", true, !free, () => Apply(Model.PickSync(DivisionRaw, lastDivision)));
        }

        /// <summary>The divisions, longest first; one that cannot fit the machine's longest time at this tempo is greyed out.</summary>
        public IEnumerable<TimingItem> DivisionItems()
        {
            var shown = Model.Shown(DivisionRaw, FeelRaw, out _);
            bool free = IsFree;
            double bpm = Tempo;
            foreach (var e in Model.Divisions)
            {
                var entry = e;
                bool fits = Model.Fits(entry, Model.HasFeel ? FeelRaw : 0, bpm);
                yield return new TimingItem(TempoSync.Label(entry.DivisionBeats, 0), fits, !free && shown.Index == entry.Index, () => Apply(Model.PickDivision(DivisionRaw, FeelRaw, entry)));
            }
        }

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

        public string DivisionText => Model.DivisionText(DivisionRaw, FeelRaw, ModeRaw);
        public string FeelText => Model.FeelNames[Math.Clamp(Model.ShownFeel(DivisionRaw, FeelRaw), 0, Model.FeelNames.Length - 1)];
        public string ModeText => IsFree ? "Free" : "Sync";
    }

    /// <summary>A compact drop-down over a list a callback makes (the picker's content follows the tempo and the other values).</summary>
    internal sealed class TimingDropdown : NeoElement
    {
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
            Height = DropdownFace.Height;
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

        protected override void OnRender(DrawingContext dc) =>
            DropdownFace.Draw(canvas.On(dc), Bounds.Width > 0 ? Bounds.Width : Width, label, text(), accent);
    }

    /// <summary>The line under the control: what the timing comes to at the song's tempo (it follows the tempo while shown).</summary>
    internal sealed class TimingReadout : NeoElement
    {
        readonly Func<string> text;
        readonly AvaloniaCanvas canvas;
        readonly DispatcherTimer timer;

        public TimingReadout(double width, Func<string> text)
        {
            this.text = text;
            Width = width;
            Height = 16;
            canvas = new AvaloniaCanvas(this);
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += (s, e) => { if (IsEffectivelyVisible) InvalidateVisual(); };
            Loaded += (s, e) => timer.Start();
            Unloaded += (s, e) => timer.Stop();
        }

        /// <summary>What it says now (for tests).</summary>
        public string Text => text();

        protected override void OnRender(DrawingContext dc) =>
            ((INeoCanvas)canvas.On(dc)).Text(text(), 1, 1, 10, Palette.DimText, maxWidth: Math.Max(10, (Bounds.Width > 0 ? Bounds.Width : Width) - 2));
    }

    /// <summary>The control itself: build it with <see cref="Create"/> where an editor shows a timing.</summary>
    internal sealed class ParamTempoTime : StackPanel
    {
        public ParamTempoTime(EditorContext ctx, TempoTimeSpec spec)
        {
            Orientation = Avalonia.Layout.Orientation.Vertical;
            var parts = new List<TimingDropdown>();
            ParamKnob knob = null;
            Action<int> changed = _ => Refresh();
            Controller = new TempoTimeController(ctx, spec, changed);
            var c = Controller;
            var accent = ctx.Accent;
            Mode = new TimingDropdown("Timing", 66, accent, () => c.ModeText, c.ModeItems, () => true, "Free runs on the knob's own time; Sync locks to the song's tempo");
            Division = new TimingDropdown("Division", 106, accent, () => c.DivisionText, c.DivisionItems, () => !c.IsFree, "The length of one cycle or step at the song's tempo, from 8 bars to 1/64");
            parts.Add(Mode); parts.Add(Division);
            if (spec.Feel >= 0)
            {
                Feel = new TimingDropdown("Feel", 176, accent, () => c.FeelText, c.FeelItems, () => !c.IsFree, "Straight, dotted (x 3/2), triplet (x 2/3) or quintuplet (x 4/5)");
                parts.Add(Feel);
            }
            if (spec.Rate >= 0 && spec.KnobInside) knob = new ParamKnob(ctx, spec.Rate, spec.RateLabel, spec.KnobDiameter);
            Knob = knob;
            Readout = new TimingReadout((knob != null ? spec.KnobDiameter + 6 : 0) + 176, c.Readout);
            var right = Feel != null
                ? Layout.Column(Layout.Row(Mode, Layout.Gap(4), Division), Layout.Gap(1, 2), Feel)
                : Layout.Row(Mode, Layout.Gap(4), Division);
            Children.Add(Layout.Row(knob, Layout.Gap(knob != null ? 6 : 0), right));
            Children.Add(Readout);
            all = parts;
            Refresh();
        }

        readonly List<TimingDropdown> all;

        public TempoTimeController Controller { get; }

        /// <summary>The names of the parameters this control binds (the division, the Feel and the Timing switch; the rate knob when it is inside): what a test asks to see that every parameter has a control.</summary>
        public IReadOnlyList<string> BoundNames
        {
            get
            {
                var t = Controller.Context.Table;
                var s = Controller.Spec;
                var r = new List<string> { t[s.Division].Name };
                if (s.Feel >= 0) r.Add(t[s.Feel].Name);
                if (s.Mode >= 0) r.Add(t[s.Mode].Name);
                if (s.Rate >= 0 && s.KnobInside) r.Add(t[s.Rate].Name);
                return r;
            }
        }

        public TimingDropdown Mode { get; }
        public TimingDropdown Division { get; }
        public TimingDropdown Feel { get; }
        public ParamKnob Knob { get; }
        public TimingReadout Readout { get; }

        void Refresh()
        {
            foreach (var d in all) d.Refresh();
            if (Knob != null) Knob.Opacity = Controller.IsFree ? 1 : 0.45;
            Readout?.InvalidateVisual();
        }

        /// <summary>The control for a timing: knob, Free / Sync, division, Feel and readout.</summary>
        public static ParamTempoTime Create(EditorContext ctx, TempoTimeSpec spec) => new ParamTempoTime(ctx, spec);
    }
}
