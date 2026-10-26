using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// Session View grid (fork addition, docs/specs/session-view.md 0.1–0.2): tracks (sequences)
    /// as columns, scenes as rows, scene launch buttons on the right, track stop buttons at the
    /// bottom. Built in code because its rows and columns follow the song. Structure comes from
    /// <see cref="SessionLayout"/>; clip states are polled from <see cref="ISession"/>.
    /// Interaction (menus, keys, drag, MIDI learn) is in SessionView.Actions.cs, the cells in
    /// SessionCells.cs.
    /// </summary>
    internal sealed partial class SessionView : UserControl
    {
        const double ColumnWidth = 116;
        const double SceneColumnWidth = 140;
        const double RowHeight = 26;
        const double HeaderHeight = 32;
        const double Gap = 2;

        readonly ReBuzzCore buzz;
        readonly ISession session;
        readonly SessionLayout layout;
        readonly SessionMidi midi;

        readonly Grid grid = new Grid { Background = SessionPalette.Window, Margin = new Thickness(6) };
        readonly TextBlock clockText;
        readonly TextBlock hintText;
        readonly TextBlock statusText;
        readonly Border statusBar;
        readonly FlatButton backButton;
        readonly FlatButton undoButton;
        readonly FlatButton redoButton;
        readonly ComboBox quantizeBox;
        readonly ComboBox beatsBox;
        readonly DispatcherTimer timer;

        readonly List<SlotCell> slots = new List<SlotCell>();
        readonly List<SceneCell> scenes = new List<SceneCell>();
        readonly List<TrackHeader> headers = new List<TrackHeader>();
        readonly List<FlatButton> trackStops = new List<FlatButton>();
        FlatButton stopAllCell;
        int trackCount;
        int sceneCount;
        int blinkPhase;
        bool syncingSettings;
        (int Track, int Scene)? selected;
        (int Track, int Scene)? dropTarget;

        static readonly (LaunchQuantization Value, string Label)[] QuantizeChoices =
        {
            (LaunchQuantization.None, "None"),
            (LaunchQuantization.Beat, "1 beat"),
            (LaunchQuantization.Bar, "1 bar"),
            (LaunchQuantization.TwoBars, "2 bars"),
            (LaunchQuantization.FourBars, "4 bars"),
            (LaunchQuantization.EightBars, "8 bars"),
        };

        internal SessionView(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            session = buzz.Session;
            layout = buzz.SessionLayout;
            midi = buzz.SessionMidi;

            Background = SessionPalette.Window;
            Foreground = SessionPalette.Text;
            FontSize = 12;
            Focusable = true;
            FocusVisualStyle = null;

            // ---- top bar
            var bar = new DockPanel { Background = SessionPalette.Bar, LastChildFill = false };
            var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
            left.Children.Add(new FlatButton("■  Stop all", () => session.StopAll(), "Stop every clip at the next launch point"));
            backButton = new FlatButton("Back to arrangement", () => session.BackToArrangement(), "Give every track back to the arrangement");
            left.Children.Add(backButton);
            left.Children.Add(Label("Launch"));
            quantizeBox = new ComboBox { Width = 82, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            foreach (var q in QuantizeChoices) quantizeBox.Items.Add(q.Label);
            quantizeBox.SelectionChanged += (s, e) =>
            {
                if (!syncingSettings && quantizeBox.SelectedIndex >= 0)
                    layout.SetLaunchQuantization(QuantizeChoices[quantizeBox.SelectedIndex].Value);
            };
            quantizeBox.MouseRightButtonUp += (s, e) => { ShowMidiOnlyMenu(quantizeBox, SessionTarget.Quantization, 0, 0, "launch quantization"); e.Handled = true; };
            left.Children.Add(quantizeBox);
            left.Children.Add(Label("Beats/bar"));
            beatsBox = new ComboBox { Width = 50, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
            for (int i = 1; i <= 12; i++) beatsBox.Items.Add(i.ToString());
            beatsBox.SelectionChanged += (s, e) =>
            {
                if (!syncingSettings && beatsBox.SelectedIndex >= 0)
                    layout.SetBeatsPerBar(beatsBox.SelectedIndex + 1);
            };
            left.Children.Add(beatsBox);
            clockText = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                MinWidth = 90,
                ToolTip = "Session clock: bar.beat.tick"
            };
            left.Children.Add(clockText);
            DockPanel.SetDock(left, Dock.Left);
            bar.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
            undoButton = new FlatButton("↶", () => layout.Undo(), "Undo grid edit (Ctrl+Z)") { Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 2, 0) };
            redoButton = new FlatButton("↷", () => layout.Redo(), "Redo grid edit (Ctrl+Y)") { Padding = new Thickness(8, 3, 8, 3) };
            right.Children.Add(undoButton);
            right.Children.Add(redoButton);
            right.Children.Add(new FlatButton("+ Scene", () => layout.InsertScene(layout.SceneCount), "Add a scene row at the bottom (Ctrl+I inserts one below the selection)"));
            FlatButton addTrack = null;
            addTrack = new FlatButton("+ Track", () => ShowAddTrackMenu(addTrack), "Add a track for a machine that has none");
            right.Children.Add(addTrack);
            FlatButton settings = null;
            settings = new FlatButton("⚙", () => ShowSettingsMenu(settings), "New clip length, MIDI mapping, keyboard shortcuts") { Margin = new Thickness(0) };
            right.Children.Add(settings);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);

            // Status line under the top bar (MIDI learn), hidden when empty.
            statusText = new TextBlock
            {
                Foreground = SessionPalette.DarkText,
                FontWeight = FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 3, 10, 3)
            };
            statusBar = new Border { Background = SessionPalette.Learn, Child = statusText, Visibility = Visibility.Collapsed };

            // ---- grid
            hintText = new TextBlock
            {
                Foreground = SessionPalette.DimText,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = "No tracks yet. Every sequence of the song is a track: add one with \"+ Track\", " +
                       "or create machines in the Machine View.",
                Visibility = Visibility.Collapsed
            };
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { hintText, grid } },
                Background = SessionPalette.Window,
                Focusable = false
            };

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            DockPanel.SetDock(statusBar, Dock.Top);
            root.Children.Add(statusBar);
            root.Children.Add(scroll);
            Content = root;

            timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
            timer.Tick += (s, e) => RefreshState();
            Loaded += (s, e) => { Rebuild(); timer.Start(); };
            Unloaded += (s, e) => timer.Stop();
            // The window is hidden rather than closed: don't poll while nobody can see it.
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible && IsLoaded) { RefreshState(); timer.Start(); }
                else timer.Stop();
            };

            layout.Changed += () => { if (IsLoaded) Rebuild(); };
            if (midi != null)
                midi.Changed += () => { if (IsLoaded) Rebuild(); };
            session.PropertyChanged += (s, e) => SyncSettings();

            PreviewKeyDown += OnKeyDown;
            MouseDown += (s, e) => Focus();
        }

        static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            Foreground = SessionPalette.DimText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0)
        };

        // ------------------------------------------------------------------ structure

        /// <summary>Rebuilds the grid from the layout (auto-filling one for songs without).</summary>
        internal void Rebuild()
        {
            var doc = layout.Ensure();
            SyncSettings();

            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();
            slots.Clear();
            scenes.Clear();
            headers.Clear();
            trackStops.Clear();

            trackCount = doc.Tracks.Count;
            sceneCount = doc.Scenes.Count;
            hintText.Visibility = trackCount == 0 ? Visibility.Visible : Visibility.Collapsed;

            for (int t = 0; t < trackCount; t++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth + Gap) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SceneColumnWidth + Gap) });

            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight + Gap) });
            for (int s = 0; s < sceneCount; s++)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight + Gap) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight + 8) });

            // Track headers and stop buttons
            for (int t = 0; t < trackCount; t++)
            {
                var seq = layout.SequenceOf(t);
                string name = seq?.Machine?.Name ?? doc.Tracks[t].Machine;
                var header = new TrackHeader(this, t, seq?.Machine, name, doc.Tracks[t].ColorIndex);
                headers.Add(header);
                Place(header, 0, t);

                int track = t;
                var stop = new FlatButton("■", () => StopTrack(track), Tip("Stop this track (Backspace stops the selected track)", SessionTarget.TrackStop, t, 0))
                {
                    Margin = new Thickness(0, 6, Gap, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                stop.MouseRightButtonUp += (s, e) => { ShowMidiOnlyMenu(stop, SessionTarget.TrackStop, track, 0, "this track's stop button"); e.Handled = true; };
                trackStops.Add(stop);
                Place(stop, sceneCount + 1, t);
            }

            // Scene column header and Stop all
            Place(new Border
            {
                Background = SessionPalette.Header,
                Margin = new Thickness(0, 0, 0, Gap),
                Padding = new Thickness(6, 0, 6, 0),
                Child = new TextBlock { Text = "Scenes", Foreground = SessionPalette.DimText, VerticalAlignment = VerticalAlignment.Center }
            }, 0, trackCount);
            stopAllCell = new FlatButton("■  Stop all clips", () => session.StopAll(), Tip("Stop every clip", SessionTarget.StopAll, 0, 0)) { Margin = new Thickness(0, 6, 0, 0) };
            stopAllCell.MouseRightButtonUp += (s, e) => { ShowMidiOnlyMenu(stopAllCell, SessionTarget.StopAll, 0, 0, "Stop all clips"); e.Handled = true; };
            Place(stopAllCell, sceneCount + 1, trackCount);

            // Slots and scene buttons
            for (int s = 0; s < sceneCount; s++)
            {
                for (int t = 0; t < trackCount; t++)
                {
                    var cell = new SlotCell(this, t, s, layout.SequenceOf(t), layout.PatternAt(t, s), midi?.Describe(SessionTarget.Slot, t, s));
                    slots.Add(cell);
                    Place(cell, s + 1, t);
                }
                var scene = new SceneCell(this, s, doc.Scenes[s], midi?.Describe(SessionTarget.Scene, 0, s));
                scenes.Add(scene);
                Place(scene, s + 1, trackCount);
            }

            // Outline of the MIDI note grid (Live's "session ring").
            if (midi?.ActiveMap?.Grid is NoteGrid ring && ring.OriginTrack < trackCount && ring.OriginScene < sceneCount)
            {
                var outline = new Border
                {
                    BorderBrush = SessionPalette.Ring,
                    BorderThickness = new Thickness(2),
                    Margin = new Thickness(-2, -2, 0, 0),
                    IsHitTestVisible = false,
                    ToolTip = "MIDI note grid"
                };
                Grid.SetRowSpan(outline, Math.Min(ring.Rows, sceneCount - ring.OriginScene));
                Grid.SetColumnSpan(outline, Math.Min(ring.Columns, trackCount - ring.OriginTrack));
                Panel.SetZIndex(outline, 1);
                Place(outline, ring.OriginScene + 1, ring.OriginTrack);
            }

            if (selected is { } sel && (sel.Track >= trackCount || sel.Scene >= sceneCount))
                selected = null;
            RefreshState();
        }

        string Tip(string text, SessionTarget target, int track, int scene)
        {
            string m = midi?.Describe(target, track, scene);
            return m == null ? text + "\nright-click: MIDI learn" : text + "\n" + m;
        }

        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        void SyncSettings()
        {
            syncingSettings = true;
            try
            {
                int q = Array.FindIndex(QuantizeChoices, c => c.Value == session.LaunchQuantization);
                quantizeBox.SelectedIndex = q;
                beatsBox.SelectedIndex = Math.Clamp(session.BeatsPerBar, 1, 12) - 1;
            }
            finally
            {
                syncingSettings = false;
            }
        }

        // ------------------------------------------------------------------ state (polled)

        /// <summary>Updates clip states, progress, blinking, the clock and the learn status.</summary>
        internal void RefreshState()
        {
            blinkPhase = (blinkPhase + 1) % 8;
            bool blinkOn = blinkPhase < 4;   // ~200 ms on, ~200 ms off at 50 ms ticks
            var learn = midi?.LearnTarget;

            foreach (var cell in slots)
            {
                Brush border = null;
                if (learn is { } l && l.Target == SessionTarget.Slot && l.Track == cell.Track && l.Scene == cell.Scene)
                    border = blinkOn ? SessionPalette.Learn : null;
                else if (dropTarget == (cell.Track, cell.Scene) || selected == (cell.Track, cell.Scene))
                    border = SessionPalette.Selection;
                cell.Refresh(session, blinkOn, border);
            }
            foreach (var scene in scenes)
                scene.Refresh(learn is { } l && l.Target == SessionTarget.Scene && l.Scene == scene.Scene && blinkOn ? SessionPalette.Learn : null);
            for (int t = 0; t < trackStops.Count; t++)
                trackStops[t].BorderBrush = learn is { } l && l.Target == SessionTarget.TrackStop && l.Track == t && blinkOn ? SessionPalette.Learn : SessionPalette.Transparent;
            if (stopAllCell != null)
                stopAllCell.BorderBrush = learn is { } la && la.Target == SessionTarget.StopAll && blinkOn ? SessionPalette.Learn : SessionPalette.Transparent;
            foreach (var h in headers)
                h.Refresh();

            long clock = session.Clock;
            if (clock < 0)
            {
                clockText.Text = "–.–.–";
                clockText.Foreground = SessionPalette.DimText;
            }
            else
            {
                int tpb = Math.Max(1, buzz.TPB);
                int bpb = Math.Max(1, session.BeatsPerBar);
                long barNumber = clock / (tpb * bpb) + 1;
                long beat = clock / tpb % bpb + 1;
                long tick = clock % tpb + 1;
                clockText.Text = $"{barNumber}.{beat}.{tick}";
                clockText.Foreground = SessionPalette.Text;
            }

            backButton.IsEnabled = session.IsAnyArrangementOverridden;
            undoButton.IsEnabled = layout.UndoStack.CanUndo;
            redoButton.IsEnabled = layout.UndoStack.CanRedo;
            statusText.Text = learn is { } target ? $"MIDI learn: press a pad, key or knob for {Describe(target.Target, target.Track, target.Scene)}  ·  Esc cancels" : "";
            statusBar.Visibility = learn != null ? Visibility.Visible : Visibility.Collapsed;
        }

        static string Describe(SessionTarget target, int track, int scene) => target switch
        {
            SessionTarget.Slot => $"track {track + 1}, scene {scene + 1}",
            SessionTarget.Scene => $"scene {scene + 1}",
            SessionTarget.TrackStop => $"track {track + 1} stop",
            SessionTarget.StopAll => "Stop all clips",
            SessionTarget.Quantization => "launch quantization",
            SessionTarget.Transport => "play/stop",
            SessionTarget.GridUp => "move the note grid up",
            SessionTarget.GridDown => "move the note grid down",
            SessionTarget.GridLeft => "move the note grid left",
            SessionTarget.GridRight => "move the note grid right",
            _ => target.ToString()
        };
    }
}
