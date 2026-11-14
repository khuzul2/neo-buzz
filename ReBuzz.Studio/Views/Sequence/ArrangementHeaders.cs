// Fork (docs/specs/daw-modes.md AR1, §4.1): the arrangement's track headers: real controls, so each
// icon button has a name for assistive tools (AutomationProperties.Name) and the focus order runs
// down the tracks. A track's first lane has its fold, name, mute, solo and "add lane"; the other
// lanes say which lane they are. The Master's rows say Tempo and Markers. A lane the Session View
// has taken over is greyed.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Sequence
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ArrangementHeaders : Canvas
    {
        readonly ReBuzzCore core;
        readonly ArrangementCanvas timeline;
        readonly List<(ArrangementRow Row, Control Cell)> cells = new();
        readonly Dictionary<IMachine, (ToggleButton Mute, ToggleButton Solo)> switches = new();
        readonly List<(string Machine, ColumnKey Key, ToggleButton Record)> recorders = new();
        readonly Dictionary<IMachine, ToggleButton> arms = new();
        bool building;

        public ArrangementHeaders(ReBuzzCore core, ArrangementCanvas timeline)
        {
            this.core = core;
            this.timeline = timeline;
            ClipToBounds = true;
            Width = SequenceGeometry.HeaderWidth;
            timeline.ViewChanged += Reposition;
            timeline.SelectionChanged += Highlight;
        }

        void OnSessionChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Highlight);
        void OnTrackState(ISequence s) => OnSessionChanged();
        void OnSessionProperty(object sender, System.ComponentModel.PropertyChangedEventArgs e) => OnSessionChanged();
        void OnRecorderChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Highlight);
        void OnThemeChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);

        // The engine's events and the theme's are listened to while the window shows the view (a closed window lets go of them).
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            StudioTheme.Changed += OnThemeChanged;
            if (core.Arrangement?.Recorder is { } recorder) recorder.Changed += OnRecorderChanged;
            if (core.Arrangement?.Takes is { } takes) takes.Changed += OnRecorderChanged;
            if (core.SessionCore is { } session)
            {
                session.TrackStateChanged += OnTrackState;
                session.PropertyChanged += OnSessionProperty;
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            StudioTheme.Changed -= OnThemeChanged;
            if (core.Arrangement?.Recorder is { } recorder) recorder.Changed -= OnRecorderChanged;
            if (core.Arrangement?.Takes is { } takes) takes.Changed -= OnRecorderChanged;
            if (core.SessionCore is { } session)
            {
                session.TrackStateChanged -= OnTrackState;
                session.PropertyChanged -= OnSessionProperty;
            }
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>A track's lane was chosen with its header (the view publishes the machine as the studio's selection).</summary>
        public event Action<ISequence> LaneChosen;

        /// <summary>The "+" of a track: a lane for its machine.</summary>
        public event Action<IMachine> AddLaneRequested;

        public IReadOnlyList<Control> Cells => cells.Select(c => c.Cell).ToList();

        /// <summary>The control with a name (tests and the focus order find buttons by it).</summary>
        public T Find<T>(string name) where T : Control => this.GetVisualDescendants().OfType<T>().FirstOrDefault(c => AutomationProperties.GetName(c) == name);

        /// <summary>Builds the cells again for the timeline's rows (it calls this when they change).</summary>
        public void Rebuild()
        {
            building = true;
            try
            {
                Children.Clear();
                cells.Clear();
                switches.Clear();
                recorders.Clear();
                arms.Clear();
                var look = StudioTheme.Sequence;
                foreach (var row in timeline.Rows)
                {
                    var cell = Cell(row, look);
                    cells.Add((row, cell));
                    Children.Add(cell);
                }
            }
            finally
            {
                building = false;
            }
            Reposition();
        }

        void Reposition()
        {
            if (cells.Count != timeline.Rows.Count || cells.Where((c, i) => !ReferenceEquals(c.Row, timeline.Rows[i])).Any())
            {
                Rebuild();
                return;
            }
            foreach (var (row, cell) in cells)
            {
                SetTop(cell, timeline.YOf(row));
                SetLeft(cell, 0);
                cell.Width = SequenceGeometry.HeaderWidth;
                cell.Height = row.Height;
            }
            Highlight();
        }

        void Highlight()
        {
            var look = StudioTheme.Sequence;
            var session = core.SessionCore;
            foreach (var (row, cell) in cells)
            {
                if (cell is not Border b) continue;
                bool current = row.Lane != null && row.Lane == timeline.CursorLane;
                b.Background = current ? look.HeaderSelected : look.Header;
                b.Opacity = row.Lane != null && session != null && session.IsArrangementOverridden(row.Lane) ? 0.5 : 1.0;
            }
            foreach (var (machine, (mute, solo)) in switches)
            {
                mute.IsChecked = machine.IsMuted;
                solo.IsChecked = machine.IsSoloed;
            }
            foreach (var (machine, key, record) in recorders)
                record.IsChecked = core.Arrangement?.Recorder.IsArmed(machine, key) == true;
            foreach (var (machine, arm) in arms)
                arm.IsChecked = core.Arrangement?.Takes.IsArmed(machine) == true;
        }

        static ToggleButton Toggle(string content, string name, string tip)
        {
            var b = new ToggleButton { Content = content, Width = 24, Height = 24, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(b, name);
            ToolTip.SetTip(b, tip);
            b.Classes.Add("icon");
            return b;
        }

        static Button PlainButton(string content, string name, string tip)
        {
            var b = new Button { Content = content, Width = 24, Height = 24, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(b, name);
            ToolTip.SetTip(b, tip);
            b.Classes.Add("icon");
            return b;
        }

        static void Style(TemplatedControl c, SequenceLook look)
        {
            c.Background = look.ControlBack;
            c.Foreground = look.ControlText;
            c.BorderBrush = look.ControlBorder;
            c.BorderThickness = new Thickness(1);
        }

        /// <summary>The picker of the last "A" button pressed (the Add automation lane flyout's content; tests drive it).</summary>
        public ParameterPicker LastPicker { get; private set; }

        Flyout AutomateFlyout(IMachine machine, Button anchor)
        {
            var flyout = new Flyout { Placement = PlacementMode.RightEdgeAlignedTop };
            flyout.Opening += (s, e) =>
            {
                var picker = new ParameterPicker(machine);
                picker.Chosen += param =>
                {
                    core.Arrangement?.AddParameterLane(machine.Name, param.Key);
                    flyout.Hide();
                };
                LastPicker = picker;
                flyout.Content = picker;
            };
            return flyout;
        }

        /// <summary>A parameter lane's header: its name, the record switch (knob moves go into the lane while the song plays) and a button that takes the lane away.</summary>
        Control ParameterCell(ArrangementRow row, SequenceLook look, Func<string, bool, TextBlock> label)
        {
            var machine = row.Machine;
            var param = row.Param;
            var name = label(param.Name, false);
            name.MaxWidth = SequenceGeometry.HeaderWidth - 40;
            name.Margin = new Thickness(28, 0, 0, 0);
            var record = Toggle("●", $"Record {param.Name} of {machine.Name}", "Record: while the song plays, moves of this knob are written into the lane");
            var remove = PlainButton("×", $"Remove lane {param.Name} of {machine.Name}", "Take this lane away (the automation stays)");
            foreach (var b in new TemplatedControl[] { record, remove }) Style(b, look);
            record.IsChecked = core.Arrangement?.Recorder.IsArmed(machine.Name, param.Key) == true;
            record.Click += (s, e) => { if (!building) core.Arrangement?.Recorder.SetArmed(machine.Name, param.Key, record.IsChecked == true); };
            remove.Click += (s, e) => core.Arrangement?.RemoveParameterLane(machine.Name, param.Key);
            recorders.Add((machine.Name, param.Key, record));
            var grid = new Grid { Margin = new Thickness(4, 2), RowDefinitions = new RowDefinitions("*,*") };
            Grid.SetRow(name, 0);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 0, 0, 0), Children = { record, remove } };
            Grid.SetRow(buttons, 1);
            grid.Children.Add(name);
            grid.Children.Add(buttons);
            return grid;
        }

        Control Cell(ArrangementRow row, SequenceLook look)
        {
            var border = new Border { Background = look.Header, BorderBrush = look.TrackLine.ToImmutable(), BorderThickness = new Thickness(0, 0, 0, 1), Width = SequenceGeometry.HeaderWidth, Height = row.Height };
            var label = (string text, bool bold) => new TextBlock { Text = text, Foreground = look.HeaderText, FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(4, 0) };
            switch (row.Kind)
            {
                case ArrangementRowKind.Tempo:
                    border.Child = new StackPanel { Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center, Children = { label("Master", true), label("Tempo", false) } };
                    return border;
                case ArrangementRowKind.Markers:
                    border.Child = label("Markers", false);
                    return border;
                case ArrangementRowKind.Parameter:
                    border.Child = ParameterCell(row, look, label);
                    return border;
            }

            var machine = row.Machine;
            var grid = new Grid { Margin = new Thickness(4, 2), RowDefinitions = new RowDefinitions(row.Folded ? "*" : "*,*") };
            if (row.IsFirstLane)
            {
                var fold = Toggle(row.Folded ? "▸" : "▾", $"Fold {machine.Name}", row.Folded ? "Show this track's lanes" : "Fold this track to one row");
                fold.IsChecked = row.Folded;
                Style(fold, look);
                fold.Click += (s, e) => { if (!building) core.Arrangement?.SetFolded(machine.Name, fold.IsChecked == true); };
                var top = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Children = { fold } };
                var name = label(machine.Name, true);
                name.MaxWidth = SequenceGeometry.HeaderWidth - 76;
                name.Margin = new Thickness(4, 0, 0, 0);
                top.Children.Add(name);
                var arm = Toggle("R", $"Arm {machine.Name} for recording", "Arm for recording: the machine plays the keyboard, and Record writes what is played into this track (daw-modes.md AR5)");
                Style(arm, look);
                arm.IsChecked = core.Arrangement?.Takes.IsArmed(machine) == true;
                arm.Click += (s, e) => { if (!building) core.Arrangement?.Takes.SetArmed(machine, arm.IsChecked == true); };
                arms[machine] = arm;
                top.Children.Add(arm);
                Grid.SetRow(top, 0);
                grid.Children.Add(top);
                if (!row.Folded)
                {
                    var mute = Toggle("M", $"Mute {machine.Name}", "Mute this machine");
                    var solo = Toggle("S", $"Solo {machine.Name}", "Solo this machine");
                    var add = PlainButton("+", $"Add lane for {machine.Name}", "Add a lane: another sequence for this machine");
                    var auto = PlainButton("A", $"Add automation lane for {machine.Name}", "Add an automation lane: a parameter of this machine");
                    auto.Flyout = AutomateFlyout(machine, auto);
                    foreach (var b in new TemplatedControl[] { mute, solo, add, auto }) Style(b, look);
                    mute.Click += (s, e) => { if (!building) machine.IsMuted = mute.IsChecked == true; };
                    solo.Click += (s, e) => { if (!building) machine.IsSoloed = solo.IsChecked == true; };
                    add.Click += (s, e) => AddLaneRequested?.Invoke(machine);
                    switches[machine] = (mute, solo);
                    var bottom = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 0), Children = { mute, solo, add, auto } };
                    Grid.SetRow(bottom, 1);
                    grid.Children.Add(bottom);
                }
            }
            else
            {
                var role = core.Arrangement?.RoleOf(row.Lane) ?? LaneRole.Main;
                var text = label($"Lane {row.LaneNumber}" + (role == LaneRole.Main ? "" : $" ({role.ToString().ToLowerInvariant()})"), false);
                text.Margin = new Thickness(28, 0, 0, 0);
                Grid.SetRow(text, 0);
                grid.Children.Add(text);
            }
            Control content = grid;
            if (machine != null && !look.IsClassic && MachineIdentity.ColourOf(machine) is { } identity)
            {
                // P3h: the machine's identity colour, a strip at the header's left edge.
                var strip = new Border { Background = ReBuzz.Studio.Views.Session.SessionPalette.BrushOf(identity) };
                var both = new Grid { ColumnDefinitions = new ColumnDefinitions("4,*") };
                Grid.SetColumn(grid, 1);
                both.Children.Add(strip);
                both.Children.Add(grid);
                content = both;
            }
            border.Child = content;
            border.PointerPressed += (s, e) =>
            {
                var lane = row.Lane ?? row.Lanes.FirstOrDefault();
                if (lane != null) LaneChosen?.Invoke(lane);
            };
            return border;
        }
    }
}
