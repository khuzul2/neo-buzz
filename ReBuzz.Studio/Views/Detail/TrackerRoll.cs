// Fork (docs/specs/ux-overhaul.md P3b): the piano roll of the detail panel. The Tracker and the piano roll are two views of one
// pattern: this one shows the notes of the pattern's Note columns in the Tracker (Core/Notes/EditorTrackerNotes) and edits them
// through the editor, one undo step a gesture, on the history the Tracker has. The pattern is the Tracker's: the page that holds
// both (PatternPage) hands it over, and the studio's selection follows it. A machine without a Note column gets a message.
//
// Fork (daw-modes.md AR2): the same control is the arrangement's piano roll panel (songOnly: the notes the song keeps for the
// machine, SongNotes, on the song's history, as the Session View's clip plays them) and the Pattern tab's roll can show them too
// (the Tracker's own stores are not converted before AR3). Both get a velocity lane or a controller lane (CC, pitch bend) under the
// roll, key and scale (highlight, snap, fold), step input and the ghosts of the other clips.

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
using BuzzGUI.Common.Actions.SongActions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Model;
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
        RollRuler ruler;
        VelocityLane lane;
        readonly PlayheadLine playhead = new PlayheadLine();
        readonly DispatcherTimer playTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        readonly ToggleButton followBox;
        readonly Grid rollGrid = new Grid();
        int shownPlay = int.MinValue;
        bool loopUndoable, loopRedoable;
        INoteSource source;
        TrackerNoteChoice choice;
        TrackerLane wanted = TrackerLane.Auto;
        readonly ComboBox laneBox;
        bool settingLane;
        readonly bool songOnly;
        readonly ComboBox keyBox, scaleBox, foldBox, ghostBox, lowerBox;
        readonly ToggleButton stepBox, snapBox;
        ControllerLane ctrl;
        MidiControllers controllers;
        int lowerController = -1;                 // -1: the velocity lane; else the controller the lane under the roll shows
        bool settingLower;
        (int, int, long) ghostSeen;
        (int Count, long Sum) ctrlSeen;
        IPattern pattern;
        (int Count, long Sum) seen;
        int rowTime = ClipNotes.TimeBase;

        public TrackerRoll(ReBuzzCore core, bool songOnly = false)
        {
            this.core = core;
            this.songOnly = songOnly;
            Background = StudioTokens.SurfaceViewBrush;
            AutomationProperties.SetName(this, songOnly ? "Arrangement piano roll" : "Piano roll");

            gridBox = new ComboBox { Width = 120, Margin = new Thickness(0, 0, 8, 0), FontSize = StudioTokens.FontBody };
            AutomationProperties.SetName(gridBox, "Grid");
            foreach (var label in GridLabels) gridBox.Items.Add(label);
            gridBox.SelectedIndex = 0;
            gridBox.SelectionChanged += (s, e) => { ApplyGrid(); roll?.Focus(); };

            laneBox = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 8, 0), FontSize = StudioTokens.FontBody, IsVisible = false };
            AutomationProperties.SetName(laneBox, "Notes of");
            laneBox.SelectionChanged += (s, e) =>
            {
                if (settingLane || laneBox.SelectedIndex < 0 || choice?.Available == null || laneBox.SelectedIndex >= choice.Available.Count) return;
                wanted = choice.Available[laneBox.SelectedIndex];
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
            followBox = StudioButtons.KeepFocus(new ToggleButton { Content = "Follow", IsChecked = true, Margin = new Thickness(0, 0, 6, 0) });
            ToolTip.SetTip(followBox, "Scroll with the playhead while the pattern plays");
            Avalonia.Automation.AutomationProperties.SetName(followBox, "Follow playhead");
            tools.Children.Add(followBox);
            tools.Children.Add(Tool("−", $"Zoom out ({mod}+wheel)", () => Zoom(0.8)));
            tools.Children.Add(Tool("+", $"Zoom in ({mod}+wheel)", () => Zoom(1.25)));
            tools.Children.Add(summary);

            // Fork (AR2): the second row, the roll's help for the notes: key and scale, fold, step input, ghosts, the lane under the roll.
            keyBox = Combo("Key", 96, "Key: off", "Key: C", "Key: C#", "Key: D", "Key: D#", "Key: E", "Key: F", "Key: F#", "Key: G", "Key: G#", "Key: A", "Key: A#", "Key: B");
            scaleBox = Combo("Scale", 150, Scale.All.Select(sc => sc.Name).ToArray());
            scaleBox.SelectedIndex = 0;
            foldBox = Combo("Fold", 110, "Fold: off", "Fold: used", "Fold: scale");
            snapBox = StudioButtons.KeepFocus(new ToggleButton { Content = "Snap to scale", Margin = new Thickness(0, 0, 6, 0) });
            ToolTip.SetTip(snapBox, "New notes and moved ones land on the scale's pitches (needs a key)");
            AutomationProperties.SetName(snapBox, "Snap to scale");
            stepBox = StudioButtons.KeepFocus(new ToggleButton { Content = "Step", Margin = new Thickness(0, 0, 6, 0) });
            ToolTip.SetTip(stepBox, "Step input: the piano keys (Z S X D C…) write notes at the cursor and step on by the grid; hold keys for a chord, Right (or Space) rests, Left (or Backspace) steps back, * and / change the octave");
            AutomationProperties.SetName(stepBox, "Step input");
            ghostBox = Combo("Ghosts", 130, "Ghosts: off", "Ghosts: this track", "Ghosts: all tracks");
            ghostBox.SelectedIndex = songOnly ? 1 : 0;
            lowerBox = Combo("Lane under the roll", 150, "Velocity");
            keyBox.SelectionChanged += (s, e) => ApplyScale();
            scaleBox.SelectionChanged += (s, e) => ApplyScale();
            foldBox.SelectionChanged += (s, e) => ApplyScale();
            snapBox.IsCheckedChanged += (s, e) => { if (roll != null) roll.SnapToScale = snapBox.IsChecked == true; };
            stepBox.IsCheckedChanged += (s, e) => { if (roll != null) roll.StepInput = stepBox.IsChecked == true; roll?.Focus(); };
            ghostBox.SelectionChanged += (s, e) => { RefreshGhosts(force: true); roll?.Focus(); };
            lowerBox.SelectionChanged += (s, e) => { if (!settingLower) ChooseLower(); roll?.Focus(); };
            tools.Children.Add(keyBox);
            tools.Children.Add(scaleBox);
            tools.Children.Add(foldBox);
            tools.Children.Add(snapBox);
            tools.Children.Add(stepBox);
            tools.Children.Add(ghostBox);
            tools.Children.Add(lowerBox);
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
            // The grid under the ruler, the velocity lane under it; the playhead's line over the viewer.
            body = new Panel { Children = { scroll, playhead, message } };
            rollGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            rollGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            rollGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(body, 1);
            rollGrid.Children.Add(body);
            Children.Add(rollGrid);

            timer.Tick += (s, e) => Poll();
            playTimer.Tick += (s, e) => UpdatePlayhead();
            // A change of note names redraws the keys and the summary (ux-overhaul.md §3.5).
            Action names = () => Dispatcher.UIThread.Post(() => { roll?.InvalidateVisual(); UpdateSummary(); });
            AttachedToVisualTree += (s, e) => { timer.Start(); playTimer.Start(); BuzzGUI.Common.NoteNames.Changed += names; };
            DetachedFromVisualTree += (s, e) => { timer.Stop(); playTimer.Stop(); BuzzGUI.Common.NoteNames.Changed -= names; };
        }

        static readonly string[] GridLabels = { "Grid: row", "Grid: beat", "Grid: bar", "Grid: off" };

        ComboBox Combo(string name, double width, params string[] items)
        {
            var box = new ComboBox { Width = width, Margin = new Thickness(0, 0, 8, 0), FontSize = StudioTokens.FontBody };
            AutomationProperties.SetName(box, name);
            foreach (var item in items) box.Items.Add(item);
            box.SelectedIndex = 0;
            return box;
        }

        Button Tool(string label, string tip, Action action)
        {
            var b = StudioButtons.KeepFocus(new Button { Content = label, Margin = new Thickness(0, 0, 6, 0) });
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, label == "−" ? "Zoom out" : label == "+" ? "Zoom in" : label);
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
            choice = TrackerNoteSources.Open(core, p, wanted, songOnly);
            source = choice?.Source;
            if (choice == null)
            {
                laneBox.IsVisible = false;
                if (songOnly)
                {
                    Say($"{p.Machine?.Name} takes no notes, so there is no piano roll for its patterns.");
                    return;
                }
                Say(p.Machine is MachineCore { DLL: { } } m && !core.MachineManager.HasTrackerEditor(m) && !core.MachineManager.HasClipNoteEditor(m)
                    ? $"{p.Machine.Name}'s pattern editor can't share its notes with the piano roll."
                    : $"{p.Machine?.Name} has no Note column in the Tracker, so there are no notes to draw here. Its pattern still plays what the Tracker holds.");
                return;
            }
            settingLane = true;
            var labels = (choice.Available ?? new[] { choice.Lane })
                .Select(l => l switch { TrackerLane.NoteColumn => "Notes: Note column", TrackerLane.MidiLanes => "Notes: MIDI lanes", _ => "Notes: Song" }).ToList();
            if (!laneBox.Items.Cast<object>().SequenceEqual(labels))      // (not while the box itself is choosing)
            {
                laneBox.Items.Clear();
                foreach (var label in labels) laneBox.Items.Add(label);
            }
            laneBox.IsVisible = choice.BothAvailable && !songOnly;
            laneBox.SelectedIndex = Math.Max(0, (choice.Available ?? new[] { choice.Lane }).ToList().IndexOf(choice.Lane));
            settingLane = false;
            message.IsVisible = false;
            scroll.IsVisible = true;
            rowTime = choice.RowTime ?? ClipNotes.TimeBase;
            roll = new PianoRoll(core, source, null) { Grid = rowTime, AltBypassesGrid = true, RowHeight = 13 };
            AutomationProperties.SetName(roll, songOnly ? "Arrangement piano roll notes" : "Piano roll");
            roll.Changed += UpdateSummary;
            roll.StepOctave = Math.Clamp(p.Machine?.BaseOctave ?? 4, 0, 9);
            roll.SnapToScale = snapBox.IsChecked == true;
            roll.StepInput = stepBox.IsChecked == true;
            controllers = SongNotes.IsMidi(source) ? new MidiControllers(core, p) : null;
            scroll.Content = roll;
            ApplyScale();
            BuildFurniture();
            RefreshBars();
            ApplyGrid();
            UpdateSummary();
            seen = Signature();
            scroll.Offset = new Vector(0, Math.Max(0, roll.FocusOffset()));
            UpdateViewport();
            RefreshGhosts(force: true);
        }

        /// <summary>The key and scale the boxes name, and the fold, applied to the roll.</summary>
        void ApplyScale()
        {
            if (roll == null) return;
            roll.Scale = keyBox.SelectedIndex <= 0 || scaleBox.SelectedIndex < 0 ? null : new NoteScale(keyBox.SelectedIndex - 1, Scale.All[scaleBox.SelectedIndex]);
            roll.Fold = (FoldMode)Math.Max(0, foldBox.SelectedIndex);
            roll.InvalidateVisual();
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, Math.Min(scroll.Offset.Y, Math.Max(0, roll.Rows == null ? 128 * roll.RowHeight : roll.Rows.Count * roll.RowHeight))));
            UpdateViewport();
        }

        /// <summary>The key and scale, the fold, step input and snap (tests set them as the boxes do).</summary>
        public ComboBox KeyBox => keyBox;
        public ComboBox ScaleBox => scaleBox;
        public ComboBox FoldBox => foldBox;
        public ComboBox GhostBox => ghostBox;
        public ComboBox LowerBox => lowerBox;
        public ToggleButton StepBox => stepBox;
        public ToggleButton SnapBox => snapBox;

        /// <summary>Whether the roll shows the notes the song keeps (what the Session View's clip plays) and edits them on the song's history.</summary>
        public bool IsSongLane => choice?.Lane == TrackerLane.SongNotes;

        public ControllerLane Controller => ctrl;

        /// <summary>The other clips' notes the roll shows faintly now.</summary>
        internal System.Collections.Generic.IReadOnlyList<ClipNote> Ghosts => roll?.Ghosts;

        void RefreshGhosts(bool force = false)
        {
            if (roll == null || (!force && ghostBox.SelectedIndex == 0)) return;
            var stack = core.SongCore?.ActionStack;
            int? anchor = ghostBox.SelectedIndex > 0 ? Anchor() : null;
            var key = (stack?.Actions.Count() ?? 0, (stack?.CanRedo == true ? 1 : 0) + 2 * (ghostBox.SelectedIndex), (long)(anchor ?? int.MinValue));
            if (!force && key == ghostSeen) return;
            ghostSeen = key;
            roll.Ghosts = anchor == null ? null : SongNotes.Ghosts(core, pattern, anchor.Value, otherTracks: ghostBox.SelectedIndex == 2);
        }

        // The lane under the roll: the velocity of the notes, or one controller of the MIDI column (a machine whose notes are in its tracks has none).
        void FillLower()
        {
            settingLower = true;
            lowerBox.Items.Clear();
            lowerBox.Items.Add("Velocity");
            if (controllers != null)
            {
                var ids = controllers.Present().Union(ControllerPoint.Usual).OrderBy(c => c == ControllerPoint.PitchBend ? 999 : c).ToList();
                foreach (int c in ids) lowerBox.Items.Add(ControllerPoint.NameOf(c));
                lowerIds = ids;
            }
            else
                lowerIds = new System.Collections.Generic.List<int>();
            int at = lowerController < 0 ? 0 : lowerIds.IndexOf(lowerController) + 1;
            lowerBox.SelectedIndex = at > 0 ? at : 0;
            if (at <= 0) lowerController = -1;
            settingLower = false;
        }

        System.Collections.Generic.List<int> lowerIds = new System.Collections.Generic.List<int>();

        void ChooseLower()
        {
            int i = lowerBox.SelectedIndex;
            lowerController = i <= 0 || i - 1 >= lowerIds.Count ? -1 : lowerIds[i - 1];
            ShowLower();
        }

        void ShowLower()
        {
            if (ctrl != null) { ctrl.Edited -= OnControllerEdited; rollGrid.Children.Remove(ctrl); ctrl = null; }
            if (roll == null) return;
            if (lowerController >= 0 && controllers != null)
            {
                ctrl = new ControllerLane(roll, controllers, lowerController) { OffsetX = scroll.Offset.X };
                Grid.SetRow(ctrl, 2);
                rollGrid.Children.Add(ctrl);
                ctrl.Edited += OnControllerEdited;
            }
            if (lane != null) lane.IsVisible = ctrl == null;
            ctrlSeen = ControllerSignature();
        }

        void OnControllerEdited()
        {
            loopUndoable = loopRedoable = false;
            ctrlSeen = ControllerSignature();
            UpdateSummary();
        }

        (int Count, long Sum) ControllerSignature()
        {
            if (controllers == null) return (0, 0);
            var points = controllers.Points();
            long sum = 0;
            foreach (var q in points) sum = sum * 31 + q.Time * 7 + q.Controller * 131 + q.Value;
            return (points.Count, sum);
        }

        void DropRoll()
        {
            if (roll != null) roll.Changed -= UpdateSummary;
            if (source != null) source.Changed -= OnSourceEdited;
            ClearFurniture();
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
                2 => roll.BarTime,
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
            ruler?.InvalidateVisual();
            lane?.InvalidateVisual();
            ctrl?.InvalidateVisual();
            PlaceHead();
        }

        void UpdateViewport()
        {
            if (roll == null) return;
            roll.Viewport = new Rect(scroll.Offset.X, scroll.Offset.Y, scroll.Viewport.Width, scroll.Viewport.Height);
            roll.InvalidateVisual();
            if (ruler != null) { ruler.OffsetX = scroll.Offset.X; ruler.InvalidateVisual(); }
            if (lane != null) lane.OffsetX = scroll.Offset.X;
            if (ctrl != null) ctrl.OffsetX = scroll.Offset.X;
            PlaceHead();
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
                loopUndoable = loopRedoable = false;
            }
            if (controllers != null)
            {
                var c = ControllerSignature();
                if (c != ctrlSeen)
                {
                    ctrlSeen = c;
                    ctrl?.Reload();
                    loopUndoable = loopRedoable = false;
                }
            }
            RefreshGhosts();
            RefreshBars();
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
            lane?.InvalidateVisual();
            if (stepBox.IsChecked == true) roll.InvalidateVisual();
        }

        // The Edit commands, when the keyboard is in the roll.
        internal bool Delete() { if (roll == null) return false; roll.DeleteSelected(); return true; }
        internal bool SelectAll() { if (roll == null) return false; roll.SelectAll(); return true; }
        internal bool DeselectAll() { if (roll == null) return false; roll.DeselectAll(); return true; }
        internal bool Duplicate() { if (roll == null) return false; roll.DuplicateSelected(); return true; }
        internal bool Cut() { if (roll == null) return false; roll.CutSelected(); return true; }
        internal bool Copy() { if (roll == null) return false; roll.CopySelected(); return true; }
        internal bool Paste() { if (roll == null) return false; roll.Paste(); return true; }

        // ---- Fork (P3b2): the ruler, the loop brace, the playhead and the velocity lane

        public RollRuler Ruler => ruler;
        public VelocityLane Velocities => lane;
        public PlayheadLine Playhead => playhead;
        public ToggleButton FollowBox => followBox;

        void BuildFurniture()
        {
            ruler = new RollRuler(roll);
            ruler.LoopSet += SetLoop;
            lane = new VelocityLane(roll);
            Grid.SetRow(ruler, 0);
            Grid.SetRow(lane, 2);
            rollGrid.Children.Add(ruler);
            rollGrid.Children.Add(lane);
            source.Changed += OnSourceEdited;
            shownPlay = int.MinValue;
            FillLower();
            ShowLower();
        }

        void ClearFurniture()
        {
            if (ruler != null) { ruler.LoopSet -= SetLoop; rollGrid.Children.Remove(ruler); }
            if (lane != null) rollGrid.Children.Remove(lane);
            if (ctrl != null) { ctrl.Edited -= OnControllerEdited; rollGrid.Children.Remove(ctrl); }
            ruler = null;
            lane = null;
            ctrl = null;
            controllers = null;
            playhead.LineX = -1;
            loopUndoable = loopRedoable = false;
        }

        // An edit of the notes ends the song-loop undo the roll was holding (the notes' undo comes first again).
        void OnSourceEdited() { loopUndoable = false; loopRedoable = false; RefreshGhosts(force: true); }

        /// <summary>The song's tick where this pattern's first tick lies: its event that plays now, else its first in the song; null if it isn't in the song.</summary>
        internal int? Anchor() => AnchorOf(core, pattern);

        /// <summary>Anchor for any pattern (the Tracker's bar rules use it too).</summary>
        internal static int? AnchorOf(ReBuzzCore core, IPattern pattern)
        {
            var song = core.Song;
            if (song == null || pattern == null) return null;
            int? first = null;
            int pos = core.SongCore?.PlayPosition ?? 0;
            foreach (var seq in song.Sequences)
            {
                if (seq.Machine != pattern.Machine) continue;
                foreach (var kv in seq.Events)
                {
                    var e = kv.Value;
                    if (e.Type != SequenceEventType.PlayPattern || !ReferenceEquals(e.Pattern, pattern)) continue;
                    int a = kv.Key - e.PlayedOffset;
                    if (pos >= kv.Key && pos < kv.Key + Math.Max(1, e.Span)) return a;
                    if (first == null || a < first) first = a;
                }
            }
            return first;
        }

        /// <summary>The bar length in ticks the song's list has where the pattern lies (the first bar's when it isn't in the song).</summary>
        internal int BarLength() => BarLengthOf(core, pattern);

        internal static int BarLengthOf(ReBuzzCore core, IPattern pattern) => Math.Max(1, core.SongMeter.GetBarLengthAt(Math.Max(0, AnchorOf(core, pattern) ?? 0)));

        /// <summary>The bars, the loop and the length the ruler shows, read from the song again.</summary>
        internal void RefreshBars()
        {
            if (roll == null || ruler == null) return;
            int tpb = Math.Max(1, core.TPB);
            int bar = BarLength();
            roll.BarTicks = bar;
            ruler.BarTicks = bar;
            ruler.Tpb = tpb;
            ruler.LengthTicks = roll.Source.Length / ClipNotes.TimeBase;
            int? a = Anchor();
            ruler.CanSetLoop = a != null;
            int s = -1, e = -1;
            if (a != null)
            {
                int ls = core.SongCore.LoopStart - a.Value, le = core.SongCore.LoopEnd - a.Value;
                if (le > 0 && ls < ruler.LengthTicks && le > ls)
                {
                    s = Math.Max(0, ls);
                    e = Math.Min(ruler.LengthTicks, le);
                }
            }
            bool looping = core.Looping;
            if (s != ruler.LoopStartTick || e != ruler.LoopEndTick || looping != ruler.LoopOn)
            {
                ruler.LoopStartTick = s;
                ruler.LoopEndTick = e;
                ruler.LoopOn = looping;
                ruler.InvalidateVisual();
            }
            if (gridBox.SelectedIndex == 2) roll.Grid = roll.BarTime;
        }

        /// <summary>The loop brace was dragged: the song's loop is these ticks of the pattern, one step on the song's history.</summary>
        void SetLoop(int startTick, int endTick)
        {
            int? a = Anchor();
            var song = core.SongCore;
            if (a == null || song == null) return;
            int start = a.Value + startTick, end = a.Value + endTick;
            if (start < 0 || (start == song.LoopStart && end == song.LoopEnd)) return;
            var stack = song.ActionStack;
            stack.BeginActionGroup();
            try
            {
                stack.Do(new SetMarkerAction(song, SongMarkers.LoopStart, start));
                stack.Do(new SetMarkerAction(song, SongMarkers.LoopEnd, end));
            }
            finally
            {
                stack.EndActionGroup();
            }
            loopUndoable = true;
            loopRedoable = false;
            RefreshBars();
        }

        /// <summary>Undoes the loop drag when it is the last thing done in the roll (the song's history holds it); false otherwise.</summary>
        internal bool UndoLoop()
        {
            var stack = core.SongCore?.ActionStack;
            if (!loopUndoable || stack?.CanUndo != true) return false;
            stack.Undo();
            loopUndoable = false;
            loopRedoable = true;
            RefreshBars();
            return true;
        }

        internal bool RedoLoop()
        {
            var stack = core.SongCore?.ActionStack;
            if (!loopRedoable || stack?.CanRedo != true) return false;
            stack.Redo();
            loopRedoable = false;
            loopUndoable = true;
            RefreshBars();
            return true;
        }

        internal bool CanUndoLoop => loopUndoable && core.SongCore?.ActionStack?.CanUndo == true;
        internal bool CanRedoLoop => loopRedoable && core.SongCore?.ActionStack?.CanRedo == true;

        /// <summary>Moves the playhead with the pattern's own play position (Tracker's row marker), and the viewer with it when Follow is on.</summary>
        internal void UpdatePlayhead()
        {
            if (roll == null || pattern == null) return;
            int pos = pattern.PlayPosition;
            if (pos == shownPlay) return;
            shownPlay = pos;
            int time = pos == int.MinValue ? -1 : pos;
            roll.PlayheadTime = time;
            if (ruler != null) { ruler.Playhead = time; ruler.InvalidateVisual(); }
            if (time >= 0 && followBox.IsChecked == true) Follow(time);
            PlaceHead();
        }

        void Follow(int time)
        {
            double x = roll.X(time);
            double left = scroll.Offset.X + PianoRoll.KeysWidth, right = scroll.Offset.X + scroll.Viewport.Width - 24;
            if (scroll.Viewport.Width <= 0 || (x >= left && x <= right)) return;
            scroll.Offset = new Vector(Math.Max(0, x - PianoRoll.KeysWidth - 24), scroll.Offset.Y);
        }

        void PlaceHead()
        {
            if (roll == null) { playhead.LineX = -1; return; }
            playhead.LineX = roll.PlayheadTime < 0 ? -1 : roll.X(roll.PlayheadTime) - scroll.Offset.X;
            playhead.InvalidateVisual();
        }

        /// <summary>Gives the keyboard to the roll.</summary>
        internal void FocusRoll() => roll?.Focus();
    }
}
