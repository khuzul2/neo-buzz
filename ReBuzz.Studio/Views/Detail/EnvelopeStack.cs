// Fork (docs/specs/daw-modes.md EV1, §8.8): the Envelopes control of the Detail panel: a header (the label, the parameter chooser, + Envelope, Ramp
// and Step for the selection, the selection count) over a stack of lanes, one for each parameter of the clip's machine that holds data in the clip
// or that was added. It is one control that knows only a time axis (IEnvelopeAxis) and the clip (a clip): the piano roll page and the Audio
// page host it under their own time axis, and the Tracker can host it later with its rows as the axis.
//
// The parameters that hold data in the clip show by default; a lane's × takes it away from the view (its points stay in the clip) and its ▾
// collapses it to its title. What is shown is view state kept for the clip while the song is open.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    /// <summary>A small bar and beat ruler for a stack that has no piano roll above it.</summary>
    internal sealed class EnvelopeRuler : Control
    {
        readonly IEnvelopeAxis axis;
        readonly ReBuzzCore core;

        public EnvelopeRuler(ReBuzzCore core, IEnvelopeAxis axis)
        {
            this.core = core;
            this.axis = axis;
            Height = 22;
            ClipToBounds = true;
            IsHitTestVisible = false;
            axis.Changed += () => InvalidateVisual();
        }

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height, g = axis.Gutter;
            dc.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(0, 0, w, h));
            dc.DrawLine(new Pen(StudioTokens.SurfaceBorderBrush, 1), new Point(0, h - 0.5), new Point(w, h - 0.5));
            int unit = ClipEvent.TimeBase;
            double tw = Math.Abs(axis.X(unit) - axis.X(0));
            if (tw <= 0) return;
            int tpb = Math.Max(1, axis.BeatUnits / unit), bar = Math.Max(1, axis.BarUnits / unit);
            int first = Math.Max(0, axis.TimeAt(g) / unit - 1), last = axis.TimeAt(w) / unit + 1;
            foreach (var line in RollRuler.Lines(first, last, tpb, bar, tw))
            {
                double x = Math.Round(axis.X(line.Tick * unit)) + 0.5;
                if (x < g) continue;
                bool isBar = line.Kind == RulerMark.Bar;
                dc.DrawLine(isBar ? PianoRoll.BarLine : PianoRoll.BeatLine, new Point(x, isBar ? 2 : 9), new Point(x, h - 1));
                if (line.Label.Length > 0)
                    dc.DrawText(new FormattedText(line.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default,
                        isBar ? StudioTokens.FontBody : StudioTokens.FontCaption, isBar ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush), new Point(x + 3, isBar ? 0 : 3));
            }
        }
    }

    internal sealed class EnvelopeStack : DockPanel
    {
        /// <summary>What the stack shows for a clip while the song is open.</summary>
        sealed class ViewState
        {
            public readonly List<ColumnKey> Added = new();
            public readonly HashSet<ColumnKey> Removed = new();
            public readonly HashSet<ColumnKey> Collapsed = new();
        }

        sealed class Row
        {
            public ColumnKey Key;
            public Border Frame;
            public EnvelopeLane Lane;
            public Button Fold, Remove;
            public TextBlock Title;
        }

        static readonly ConditionalWeakTable<IClip, ViewState> states = new();

        public const string Hint = "Click: add a point · drag: move · drag on empty space: select · Shift: no snap · R: ramp or step · Delete · each edit is one undo step";

        readonly ReBuzzCore core;
        readonly IEnvelopeAxis axis;
        readonly TextBlock count, hintText, title;
        readonly Button chooser, add;
        readonly ToggleButton ramp, step;
        readonly StackPanel lanePanel = new() { Orientation = Orientation.Vertical };
        readonly ScrollViewer scroll;
        readonly EnvelopeRuler ruler;
        readonly Dictionary<ColumnKey, Row> rows = new();
        readonly List<Row> order = new();
        readonly Flyout flyout;
        IClip clip;
        AutoParam chosen;
        bool updating;

        public EnvelopeStack(ReBuzzCore core, IEnvelopeAxis axis, bool showRuler = false)
        {
            this.core = core;
            this.axis = axis;
            Background = StudioTokens.SurfaceViewBrush;
            AutomationProperties.SetName(this, "Envelopes");
            IsVisible = false;

            title = new TextBlock { Text = "Envelopes", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), Foreground = StudioTokens.TextPrimaryBrush, FontSize = StudioTokens.FontBody };
            chooser = StudioButtons.KeepFocus(new Button { Content = "Choose a parameter ▾", Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            AutomationProperties.SetName(chooser, "Envelope parameter");
            ToolTip.SetTip(chooser, "The parameter of this clip's machine the next envelope is for (its state parameters; a track parameter once for each track)");
            flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
            flyout.Opening += (s, e) => OpenPicker();
            chooser.Flyout = flyout;
            add = StudioButtons.KeepFocus(new Button { Content = "+ Envelope", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = false });
            AutomationProperties.SetName(add, "Add envelope");
            ToolTip.SetTip(add, "Show an envelope lane for the chosen parameter, to draw its points in this clip");
            add.Click += (s, e) => { if (chosen != null) AddLane(chosen.Key); };
            ramp = StudioButtons.KeepFocus(new ToggleButton { Content = "Ramp", Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = false });
            step = StudioButtons.KeepFocus(new ToggleButton { Content = "Step", Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsEnabled = false });
            AutomationProperties.SetName(ramp, "Ramp");
            AutomationProperties.SetName(step, "Step");
            ToolTip.SetTip(ramp, "The line after the selected points goes straight to the next point (R toggles)");
            ToolTip.SetTip(step, "The line after the selected points holds its value until the next point (R toggles)");
            ramp.Click += (s, e) => SetKind(true);
            step.Click += (s, e) => SetKind(false);
            count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, FontSize = StudioTokens.FontBody, Margin = new Thickness(4, 0, 0, 0) };
            AutomationProperties.SetName(count, "Envelope selection");

            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4), Children = { title, chooser, add, ramp, step, count } };
            var barFrame = new Border { Background = StudioTokens.SurfacePanelBrush, BorderBrush = StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(0, 1, 0, 1), Child = bar };
            SetDock(barFrame, Dock.Top);
            Children.Add(barFrame);

            hintText = new TextBlock { Text = Hint, Foreground = StudioTokens.TextSecondaryBrush, FontSize = StudioTokens.FontCaption, Margin = new Thickness(8, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            AutomationProperties.SetName(hintText, "Envelope hint");
            SetDock(hintText, Dock.Bottom);
            Children.Add(hintText);

            ruler = new EnvelopeRuler(core, axis) { IsVisible = showRuler };
            SetDock(ruler, Dock.Top);
            Children.Add(ruler);

            scroll = new ScrollViewer
            {
                Content = lanePanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Focusable = false,
            };
            Children.Add(scroll);
        }

        // ------------------------------------------------------------------ for the host, the window and the tests

        public IClip Clip => clip;

        public IReadOnlyList<EnvelopeLane> Lanes => order.Select(r => r.Lane).ToList();

        public EnvelopeLane LaneFor(ColumnKey key) => rows.TryGetValue(key, out var r) ? r.Lane : null;

        public Button Chooser => chooser;
        public Button AddButton => add;
        public ToggleButton RampButton => ramp;
        public ToggleButton StepButton => step;
        public string CountText => count.Text;
        public string HintText => hintText.Text;

        /// <summary>The parameter the chooser names now, or null.</summary>
        public AutoParam Chosen => chosen;

        /// <summary>The list of the chooser's flyout (tests drive it).</summary>
        public ParameterPicker LastPicker { get; private set; }

        public bool ShowRuler { get => ruler.IsVisible; set => ruler.IsVisible = value; }

        /// <summary>The most height the lanes take before they scroll.</summary>
        public double MaxLanesHeight { get => scroll.MaxHeight; set => scroll.MaxHeight = Math.Max(EnvelopeLane.LaneHeight + 26, value); }

        /// <summary>Whether the keyboard is in a lane (the Edit commands are the lane's then).</summary>
        public bool OwnsFocus => IsVisible && IsKeyboardFocusWithin;

        /// <summary>The lane with the keyboard, else the one with a selection.</summary>
        public EnvelopeLane ActiveLane => Lanes.FirstOrDefault(l => l.IsKeyboardFocusWithin) ?? Lanes.FirstOrDefault(l => l.Selection.Count > 0);

        // ------------------------------------------------------------------ the clip

        /// <summary>Shows a clip's envelopes: the parameters that hold data in it, those added, and <paramref name="focus"/>. Null hides the stack.</summary>
        public void Show(IClip clip, ColumnKey? focus = null)
        {
            if (!ReferenceEquals(clip, clip))
            {
                clip = clip;
                if (chosen != null && !ReferenceEquals(chosen.Machine, clip?.Machine)) SetChosen(null);
            }
            if (clip?.Machine == null || AutomationParams.List(clip.Machine).Count == 0)
            {
                clip = null;
                IsVisible = false;
                Rebuild(new List<ColumnKey>());
                return;
            }
            IsVisible = true;
            if (focus is { } key && AutomationParams.Find(clip.Machine, key) != null)
            {
                var st = StateOf(clip);
                st.Removed.Remove(key);
                st.Collapsed.Remove(key);
                if (!st.Added.Contains(key) && !ClipEnvelopes.KeysWithData(clip).Contains(key)) st.Added.Add(key);
            }
            Rebuild(Keys());
            UpdateSelection();
        }

        static ViewState StateOf(IClip clip) => states.GetValue(clip, _ => new ViewState());

        List<ColumnKey> Keys()
        {
            var st = StateOf(clip);
            var keys = ClipEnvelopes.KeysWithData(clip).Where(k => !st.Removed.Contains(k)).ToList();
            foreach (var k in st.Added)
                if (!keys.Contains(k) && AutomationParams.Find(clip.Machine, k) != null) keys.Add(k);
            return keys;
        }

        /// <summary>Reads the clip again (an undo, the agent, the recorder or another view changed it); the host calls it from its timer.</summary>
        public void Refresh()
        {
            if (clip == null || !IsVisible) return;
            if (rows.Values.Any(r => r.Lane.IsDragging)) return;
            var keys = Keys();
            if (!keys.SequenceEqual(order.Select(r => r.Key))) Rebuild(keys);
            foreach (var r in rows.Values) r.Lane.Reload();
            UpdateSelection();
        }

        /// <summary>Draws the lanes again (the playhead moved).</summary>
        public void Invalidate()
        {
            foreach (var r in rows.Values) r.Lane.InvalidateVisual();
        }

        void Rebuild(List<ColumnKey> keys)
        {
            updating = true;
            try
            {
                var keep = new Dictionary<ColumnKey, Row>();
                foreach (var key in keys)
                {
                    if (!rows.TryGetValue(key, out var row)) row = MakeRow(key);
                    keep[key] = row;
                }
                foreach (var gone in rows.Where(kv => !keep.ContainsKey(kv.Key)).ToList())
                {
                    gone.Value.Lane.SelectionChanged -= UpdateSelection;
                    gone.Value.Lane.Edited -= OnEdited;
                    gone.Value.Lane.Release();
                }
                rows.Clear();
                order.Clear();
                lanePanel.Children.Clear();
                foreach (var key in keys)
                {
                    rows[key] = keep[key];
                    order.Add(keep[key]);
                    lanePanel.Children.Add(keep[key].Frame);
                }
                foreach (var row in rows.Values) ApplyFold(row);
            }
            finally
            {
                updating = false;
            }
        }

        Row MakeRow(ColumnKey key)
        {
            var lane = new EnvelopeLane(core, axis, clip, key);
            var row = new Row { Key = key, Lane = lane };
            string name = lane.Param == null ? "Envelope" : $"{clip.Machine.Name} ▸ {lane.Param.Name}";
            row.Title = new TextBlock { Text = name, FontSize = StudioTokens.FontBody, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0) };
            row.Fold = StudioButtons.KeepFocus(StudioButtons.Icon(new Button { Content = "▾", Width = 22, Height = 20, VerticalAlignment = VerticalAlignment.Center }, $"Collapse {name}", "Collapse or expand this lane"));
            row.Remove = StudioButtons.KeepFocus(StudioButtons.Icon(new Button { Content = "×", Width = 22, Height = 20, VerticalAlignment = VerticalAlignment.Center }, $"Remove envelope {name}", "Take this lane away (the points stay in the clip)"));
            row.Fold.Click += (s, e) => ToggleFold(row);
            row.Remove.Click += (s, e) => RemoveLane(key);
            var head = new DockPanel { Height = 22, Background = StudioTokens.SurfacePanelBrush, LastChildFill = true };
            SetDock(row.Remove, Dock.Right);
            head.Children.Add(row.Remove);
            SetDock(row.Fold, Dock.Left);
            head.Children.Add(row.Fold);
            head.Children.Add(row.Title);
            var body = new StackPanel { Orientation = Orientation.Vertical, Children = { head, lane } };
            row.Frame = new Border { BorderBrush = StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = body };
            AutomationProperties.SetName(lane, $"Envelope {name}");
            lane.SelectionChanged += UpdateSelection;
            lane.Edited += OnEdited;
            return row;
        }

        void ApplyFold(Row row)
        {
            bool folded = clip != null && StateOf(clip).Collapsed.Contains(row.Key);
            row.Lane.IsVisible = !folded;
            row.Fold.Content = folded ? "▸" : "▾";
        }

        void ToggleFold(Row row)
        {
            var st = StateOf(clip);
            if (!st.Collapsed.Remove(row.Key)) st.Collapsed.Add(row.Key);
            ApplyFold(row);
        }

        /// <summary>Whether a lane is collapsed to its title.</summary>
        public bool IsCollapsed(ColumnKey key) => clip != null && StateOf(clip).Collapsed.Contains(key);

        public void ToggleCollapsed(ColumnKey key)
        {
            if (rows.TryGetValue(key, out var row)) ToggleFold(row);
        }

        // ------------------------------------------------------------------ lanes

        /// <summary>Shows the lane of a parameter (a lane that was taken away comes back).</summary>
        public EnvelopeLane AddLane(ColumnKey key)
        {
            if (clip == null || AutomationParams.Find(clip.Machine, key) == null) return null;
            Show(clip, key);
            var lane = LaneFor(key);
            lane?.Focus();
            return lane;
        }

        /// <summary>Takes a lane away from the view; its points stay in the clip (and show again if it is added).</summary>
        public void RemoveLane(ColumnKey key)
        {
            if (clip == null) return;
            var st = StateOf(clip);
            st.Added.Remove(key);
            st.Removed.Add(key);
            Rebuild(Keys());
            UpdateSelection();
        }

        void SetChosen(AutoParam param)
        {
            chosen = param;
            chooser.Content = param == null ? "Choose a parameter ▾" : $"{param.Machine.Name} ▸ {param.Name} ▾";
            add.IsEnabled = param != null;
        }

        /// <summary>Names the parameter the next envelope is for (what the chooser's list does for a pick).</summary>
        public void Choose(AutoParam param) => SetChosen(param);

        void OpenPicker()
        {
            if (clip?.Machine == null) return;
            var picker = new ParameterPicker(clip.Machine, "Choose");
            picker.Chosen += param =>
            {
                SetChosen(param);
                flyout.Hide();
            };
            LastPicker = picker;
            flyout.Content = picker;
        }

        // ------------------------------------------------------------------ selection and the buttons

        /// <summary>A lane was edited (one step of the song's history).</summary>
        public event Action LaneEdited;

        void OnEdited()
        {
            // An edit may have made a lane's data appear or vanish: the parameters that hold data are read again.
            Refresh();
            LaneEdited?.Invoke();
        }

        void UpdateSelection()
        {
            if (updating) return;
            var active = Lanes.FirstOrDefault(l => l.Selection.Count > 0);
            foreach (var l in Lanes.Where(l => !ReferenceEquals(l, active) && l.Selection.Count > 0).ToList()) l.ClearSelection();
            int n = active?.Selection.Count ?? 0;
            count.Text = n == 0 ? "" : $"{n} point{(n == 1 ? "" : "s")} selected";
            var (ramps, steps) = active?.SelectedKinds() ?? (0, 0);
            ramp.IsEnabled = step.IsEnabled = n > 0;
            ramp.IsChecked = n > 0 && steps == 0;
            step.IsChecked = n > 0 && ramps == 0;
        }

        void SetKind(bool asRamp)
        {
            var lane = ActiveLane;
            if (lane == null || lane.Selection.Count == 0) { UpdateSelection(); return; }
            lane.SetRamp(asRamp);
            UpdateSelection();
        }

        // ------------------------------------------------------------------ the Edit commands, when the keyboard is in a lane

        public bool Delete() => ActiveLane?.DeleteSelection() == true;

        public bool SelectAll() => ActiveLane is { } l ? l.SelectAll() : Lanes.FirstOrDefault(x => x.IsVisible)?.SelectAll() == true;

        public bool DeselectAll() { var l = ActiveLane; if (l == null) return false; l.ClearSelection(); return true; }

        public bool Copy() => ActiveLane?.CopySelection() == true;

        public bool Cut() => Copy() && Delete();

        public bool Paste() => ActiveLane?.Paste() == true;
    }
}
