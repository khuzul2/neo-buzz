// Fork (docs/specs/ux-overhaul.md P3b): the piano roll of the detail panel. The Tracker and the piano roll are two views of one
// pattern: this one shows the notes of the pattern's Note columns in the Tracker (Core/Notes/EditorTrackerNotes) and edits them
// through the editor, one undo step a gesture, on the history the Tracker has. The pattern is the Tracker's: the page that holds
// both (PatternPage) hands it over, and the studio's selection follows it. A machine without a Note column gets a message.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Detail
{
    using Control = Avalonia.Controls.Control;

    internal sealed class TrackerRoll : DockPanel
    {
        readonly ReBuzzCore core;
        readonly ScrollViewer scroll;
        readonly TextBlock summary, message;
        readonly ComboBox gridBox;
        readonly Panel body;
        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
        PianoRoll roll;
        INoteSource source;
        TrackerNoteChoice choice;
        TrackerLane wanted = TrackerLane.Auto;
        readonly ComboBox laneBox;
        bool settingLane;
        IPattern pattern;
        (int Count, long Sum) seen;
        int rowTime = ClipNotes.TimeBase;

        public TrackerRoll(ReBuzzCore core)
        {
            this.core = core;
            Background = StudioTokens.SurfaceViewBrush;
            AutomationProperties.SetName(this, "Piano roll");

            gridBox = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 8, 0), FontSize = StudioTokens.FontBody };
            AutomationProperties.SetName(gridBox, "Grid");
            foreach (var label in GridLabels) gridBox.Items.Add(label);
            gridBox.SelectedIndex = 0;
            gridBox.SelectionChanged += (s, e) => { ApplyGrid(); roll?.Focus(); };

            laneBox = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 8, 0), FontSize = StudioTokens.FontBody, IsVisible = false };
            AutomationProperties.SetName(laneBox, "Notes of");
            laneBox.Items.Add("Notes: Note column");
            laneBox.Items.Add("Notes: MIDI lanes");
            laneBox.SelectionChanged += (s, e) =>
            {
                if (settingLane || laneBox.SelectedIndex < 0) return;
                wanted = laneBox.SelectedIndex == 0 ? TrackerLane.NoteColumn : TrackerLane.MidiLanes;
                Reopen();
                roll?.Focus();
            };
            summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(8, 0, 0, 0), FontSize = StudioTokens.FontBody };
            AutomationProperties.SetName(summary, "Piano roll summary");
            string mod = Views.Session.SessionView.ModName;
            var tools = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };
            tools.Children.Add(laneBox);
            tools.Children.Add(gridBox);
            tools.Children.Add(Tool("Quantize", "Quantize the selected notes (or all) to the grid (Q)", () => roll?.Quantize()));
            tools.Children.Add(Tool("Vel −", $"Softer ({mod}+Down)", () => roll?.ChangeVelocity(-8)));
            tools.Children.Add(Tool("Vel +", $"Louder ({mod}+Up)", () => roll?.ChangeVelocity(8)));
            tools.Children.Add(Tool("Delete", "Delete the selected notes (Delete)", () => roll?.DeleteSelected()));
            tools.Children.Add(Tool("−", $"Zoom out ({mod}+wheel)", () => Zoom(0.8)));
            tools.Children.Add(Tool("+", $"Zoom in ({mod}+wheel)", () => Zoom(1.25)));
            tools.Children.Add(summary);
            SetDock(tools, Dock.Top);
            Children.Add(tools);

            scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Background = StudioTokens.SurfaceViewBrush,
                AllowAutoHide = false,
            };
            scroll.ScrollChanged += (s, e) => UpdateViewport();
            scroll.PropertyChanged += (s, e) => { if (e.Property == BoundsProperty) UpdateViewport(); };
            scroll.AddHandler(PointerWheelChangedEvent, (s, e) =>
            {
                if (!Views.Session.SessionView.Normalize(e.KeyModifiers).HasFlag(KeyModifiers.Control)) return;
                Zoom(e.Delta.Y > 0 ? 1.25 : 0.8);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);
            message = new TextBlock
            {
                Foreground = StudioTokens.TextSecondaryBrush, FontSize = StudioTokens.FontBody, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16), IsVisible = false,
            };
            body = new Panel { Children = { scroll, message } };
            Children.Add(body);

            timer.Tick += (s, e) => Poll();
            // A change of note names redraws the keys and the summary (ux-overhaul.md §3.5).
            Action names = () => Dispatcher.UIThread.Post(() => { roll?.InvalidateVisual(); UpdateSummary(); });
            AttachedToVisualTree += (s, e) => { timer.Start(); BuzzGUI.Common.NoteNames.Changed += names; };
            DetachedFromVisualTree += (s, e) => { timer.Stop(); BuzzGUI.Common.NoteNames.Changed -= names; };
        }

        static readonly string[] GridLabels = { "Grid: row", "Grid: beat", "Grid: bar", "Grid: off" };

        Button Tool(string label, string tip, Action action)
        {
            var b = new Button { Content = label, Padding = new Thickness(8, 2), MinHeight = 24, Margin = new Thickness(0, 0, 6, 0), FontSize = StudioTokens.FontBody, Focusable = false };
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, label);
            b.Click += (s, e) => { action(); roll?.Focus(); };
            return b;
        }

        /// <summary>The roll showing the pattern now, or null (no pattern, or its machine has no Note column).</summary>
        public PianoRoll Roll => roll;

        /// <summary>The notes the roll shows (tests); null when it shows a message.</summary>
        internal System.Collections.Generic.IReadOnlyList<ClipNote> Notes() => roll?.Notes;

        public IPattern Pattern => pattern;

        /// <summary>The Tracker's notes the roll shows: the Note column or the MIDI lanes (Auto before a pattern is shown).</summary>
        public TrackerLane Lane => choice?.Lane ?? wanted;

        public ComboBox LaneBox => laneBox;

        /// <summary>Shows the Note column's notes or the MIDI lanes' (for a machine that has both).</summary>
        public void UseLane(TrackerLane lane)
        {
            wanted = lane;
            Reopen();
        }

        void Reopen()
        {
            var p = pattern;
            pattern = null;
            Show(p);
        }

        public ScrollViewer Scroll => scroll;

        public ComboBox GridBox => gridBox;

        /// <summary>What the roll says instead of notes (no pattern, no Note column), or null.</summary>
        public string Message => message.IsVisible ? message.Text : null;

        public string SummaryText => summary.Text;

        /// <summary>Shows a pattern's notes (the Tracker's pattern), or says why there are none.</summary>
        public void Show(IPattern p)
        {
            if (roll != null && ReferenceEquals(pattern, p) && source?.IsLive == true)
            {
                Poll();
                return;
            }
            pattern = p;
            DropRoll();
            if (p == null)
            {
                Say("No pattern. Choose one above, or make one with New pattern.");
                return;
            }
            choice = TrackerNoteSources.Open(core, p, wanted);
            source = choice?.Source;
            if (choice == null)
            {
                laneBox.IsVisible = false;
                Say(p.Machine is MachineCore { DLL: { } } m && !core.MachineManager.HasTrackerEditor(m) && !core.MachineManager.HasClipNoteEditor(m)
                    ? $"{p.Machine.Name}'s pattern editor can't share its notes with the piano roll."
                    : $"{p.Machine?.Name} has no Note column in the Tracker, so there are no notes to draw here. Its pattern still plays what the Tracker holds.");
                return;
            }
            settingLane = true;
            laneBox.IsVisible = choice.BothAvailable;
            laneBox.SelectedIndex = choice.Lane == TrackerLane.NoteColumn ? 0 : 1;
            settingLane = false;
            message.IsVisible = false;
            scroll.IsVisible = true;
            rowTime = choice.RowTime ?? ClipNotes.TimeBase;
            roll = new PianoRoll(core, source, null) { Grid = rowTime, AltBypassesGrid = true, RowHeight = 13 };
            AutomationProperties.SetName(roll, "Piano roll");
            roll.Changed += UpdateSummary;
            scroll.Content = roll;
            ApplyGrid();
            UpdateSummary();
            seen = Signature();
            scroll.Offset = new Vector(0, Math.Max(0, roll.FocusOffset()));
            UpdateViewport();
        }

        void DropRoll()
        {
            if (roll != null) roll.Changed -= UpdateSummary;
            roll = null;
            source = null;
            scroll.Content = null;
        }

        void Say(string text)
        {
            scroll.IsVisible = false;
            message.Text = text;
            message.IsVisible = true;
            summary.Text = "";
        }

        void ApplyGrid()
        {
            if (roll == null) return;
            int beat = PatternEvent.TimeBase * 4;
            roll.Grid = gridBox.SelectedIndex switch
            {
                1 => beat,
                2 => beat * Math.Max(1, core.Session?.BeatsPerBar ?? 4),
                3 => 1,
                _ => rowTime,
            };
        }

        void Zoom(double factor)
        {
            if (roll == null) return;
            roll.TickWidth = Math.Clamp(roll.TickWidth * factor, 3, 160);
            roll.InvalidateMeasure();
            roll.InvalidateVisual();
            UpdateViewport();
        }

        void UpdateViewport()
        {
            if (roll == null) return;
            roll.Viewport = new Rect(scroll.Offset.X, scroll.Offset.Y, scroll.Viewport.Width, scroll.Viewport.Height);
            roll.InvalidateVisual();
        }

        /// <summary>Follows what the Tracker (or an undo, or the agent) changed.</summary>
        internal void Poll()
        {
            if (roll == null || !IsEffectivelyVisible) return;       // a roll that isn't shown doesn't read the pattern
            if (!source.IsLive)
            {
                Show(pattern);
                return;
            }
            var now = Signature();
            if (now != seen)
            {
                seen = now;
                roll.Reload();
            }
        }

        (int, long) Signature()
        {
            var notes = source.Notes();
            long sum = 0;
            foreach (var n in notes) sum = sum * 31 + n.Time * 7 + n.Duration * 3 + n.Note * 131 + n.Velocity;
            return (notes.Count, sum);
        }

        void UpdateSummary()
        {
            if (roll == null) return;
            int count = roll.Notes.Count, sel = roll.Selection.Count;
            summary.Text = roll.Refusal is string why ? why
                : roll.Source.WhyNotEditable is string waiting ? waiting
                : count == 0 ? "No notes yet: double-click to draw one."
                : sel == 0 ? $"{count} note{(count == 1 ? "" : "s")}"
                : $"{sel} of {count} selected" + (sel == 1 ? $" · {ClipNotes.Name(roll.Notes[roll.Selection.First()].Note)}, velocity {roll.Notes[roll.Selection.First()].Velocity}" : "");
            seen = Signature();
        }

        // The Edit commands, when the keyboard is in the roll.
        internal bool Delete() { if (roll == null) return false; roll.DeleteSelected(); return true; }
        internal bool SelectAll() { if (roll == null) return false; roll.SelectAll(); return true; }
        internal bool DeselectAll() { if (roll == null) return false; roll.DeselectAll(); return true; }
        internal bool Duplicate() { if (roll == null) return false; roll.DuplicateSelected(); return true; }

        /// <summary>Gives the keyboard to the roll.</summary>
        internal void FocusRoll() => roll?.Focus();
    }
}
