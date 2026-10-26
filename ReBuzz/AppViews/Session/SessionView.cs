using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ReBuzz.AppViews.Session
{
    /// <summary>
    /// Session View grid (fork addition, docs/specs/session-view.md 0.1): tracks (sequences) as
    /// columns, scenes as rows, scene launch buttons on the right, track stop buttons at the
    /// bottom. Built in code because its rows and columns follow the song. Structure comes from
    /// <see cref="SessionLayout"/>; clip states are polled from <see cref="ISession"/>.
    /// </summary>
    internal sealed class SessionView : UserControl
    {
        const double ColumnWidth = 116;
        const double SceneColumnWidth = 140;
        const double RowHeight = 26;
        const double HeaderHeight = 30;
        const double Gap = 2;

        internal static class Palette
        {
            public static readonly Brush Window = Make("#1B1B1E");
            public static readonly Brush Bar = Make("#242428");
            public static readonly Brush Header = Make("#2E2E34");
            public static readonly Brush Empty = Make("#26262B");
            public static readonly Brush EmptyHover = Make("#33333A");
            public static readonly Brush Clip = Make("#3B5878");
            public static readonly Brush ClipHover = Make("#4A6A8E");
            public static readonly Brush Playing = Make("#2E9B49");
            public static readonly Brush Progress = Make("#6FD98A");
            public static readonly Brush Queued = Make("#3E7D50");
            public static readonly Brush Stopping = Make("#B5543A");
            public static readonly Brush Scene = Make("#37373F");
            public static readonly Brush SceneHover = Make("#45454F");
            public static readonly Brush Text = Make("#E8E8EC");
            public static readonly Brush DimText = Make("#8C8C96");
            public static readonly Brush Selection = Make("#F0C24B");
            public static readonly Brush Transparent = Brushes.Transparent;

            static Brush Make(string hex)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
        }

        readonly ReBuzzCore buzz;
        readonly ISession session;
        readonly SessionLayout layout;

        readonly Grid grid = new Grid { Background = Palette.Window, Margin = new Thickness(6) };
        readonly TextBlock clockText;
        readonly TextBlock hintText;
        readonly FlatButton backButton;
        readonly ComboBox quantizeBox;
        readonly ComboBox beatsBox;
        readonly DispatcherTimer timer;

        readonly List<SlotCell> slots = new List<SlotCell>();
        readonly List<SceneCell> scenes = new List<SceneCell>();
        int blinkPhase;
        bool syncingSettings;
        (int Track, int Scene)? selected;

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

            Background = Palette.Window;
            Foreground = Palette.Text;
            FontSize = 12;
            Focusable = true;
            FocusVisualStyle = null;

            // ---- top bar
            var bar = new DockPanel { Background = Palette.Bar, LastChildFill = false };
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
            right.Children.Add(new FlatButton("+ Scene", () => layout.InsertScene(layout.SceneCount), "Add a scene row at the bottom"));
            FlatButton addTrack = null;
            addTrack = new FlatButton("+ Track", () => ShowAddTrackMenu(addTrack), "Add a track for a machine that has none");
            right.Children.Add(addTrack);
            DockPanel.SetDock(right, Dock.Right);
            bar.Children.Add(right);

            // ---- grid
            hintText = new TextBlock
            {
                Foreground = Palette.DimText,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = "No tracks yet. Every sequence of the song is a track: add one with \"+ Track\", " +
                       "or create machines with patterns in the Machine View.",
                Visibility = Visibility.Collapsed
            };
            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { hintText, grid } },
                Background = Palette.Window
            };

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
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
            session.PropertyChanged += (s, e) => SyncSettings();

            PreviewKeyDown += OnKeyDown;
            MouseDown += (s, e) => Focus();
        }

        static TextBlock Label(string text) => new TextBlock
        {
            Text = text,
            Foreground = Palette.DimText,
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

            int trackCount = doc.Tracks.Count;
            int sceneCount = doc.Scenes.Count;
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
                var header = new Border
                {
                    Background = Palette.Header,
                    Margin = new Thickness(0, 0, Gap, Gap),
                    Padding = new Thickness(6, 0, 6, 0),
                    ToolTip = name,
                    Child = new TextBlock
                    {
                        Text = name,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                Place(header, 0, t);

                int track = t;
                var stop = new FlatButton("■", () => { if (layout.SequenceOf(track) is ISequence s) session.Stop(s); }, "Stop this track")
                {
                    Margin = new Thickness(0, 6, Gap, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                Place(stop, sceneCount + 1, t);
            }

            // Scene column header and Stop all
            Place(new Border
            {
                Background = Palette.Header,
                Margin = new Thickness(0, 0, 0, Gap),
                Padding = new Thickness(6, 0, 6, 0),
                Child = new TextBlock { Text = "Scenes", Foreground = Palette.DimText, VerticalAlignment = VerticalAlignment.Center }
            }, 0, trackCount);
            Place(new FlatButton("■  Stop all clips", () => session.StopAll(), "Stop every clip") { Margin = new Thickness(0, 6, 0, 0) },
                sceneCount + 1, trackCount);

            // Slots and scene buttons
            for (int s = 0; s < sceneCount; s++)
            {
                for (int t = 0; t < trackCount; t++)
                {
                    var cell = new SlotCell(this, t, s, layout.SequenceOf(t), layout.PatternAt(t, s));
                    slots.Add(cell);
                    Place(cell, s + 1, t);
                }
                var scene = new SceneCell(this, s, doc.Scenes[s].Name);
                scenes.Add(scene);
                Place(scene, s + 1, trackCount);
            }

            if (selected is { } sel && (sel.Track >= trackCount || sel.Scene >= sceneCount))
                selected = null;
            RefreshState();
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

        /// <summary>Updates clip states, progress, blinking and the clock from the session.</summary>
        internal void RefreshState()
        {
            blinkPhase = (blinkPhase + 1) % 8;
            bool blinkOn = blinkPhase < 4;   // ~200 ms on, ~200 ms off at 50 ms ticks

            foreach (var cell in slots)
                cell.Refresh(session, blinkOn, selected == (cell.Track, cell.Scene));

            long clock = session.Clock;
            if (clock < 0)
            {
                clockText.Text = "–.–.–";
                clockText.Foreground = Palette.DimText;
            }
            else
            {
                int tpb = Math.Max(1, buzz.TPB);
                int bpb = Math.Max(1, session.BeatsPerBar);
                long bar = clock / (tpb * bpb) + 1;
                long beat = clock / tpb % bpb + 1;
                long tick = clock % tpb + 1;
                clockText.Text = $"{bar}.{beat}.{tick}";
                clockText.Foreground = Palette.Text;
            }

            backButton.IsEnabled = session.IsAnyArrangementOverridden;
        }

        // ------------------------------------------------------------------ actions

        void Select(int track, int scene)
        {
            selected = (track, scene);
            Focus();
        }

        void LaunchSlot(SlotCell cell)
        {
            Select(cell.Track, cell.Scene);
            if (cell.Sequence == null) return;
            if (cell.Pattern != null)
                session.Launch(cell.Sequence, cell.Pattern);
            else
                session.Stop(cell.Sequence);     // an empty slot works as the track's stop button, as in Live
            RefreshState();
        }

        void LaunchScene(int scene)
        {
            layout.LaunchScene(scene);
            RefreshState();
        }

        void ShowSlotMenu(SlotCell cell)
        {
            Select(cell.Track, cell.Scene);
            var menu = new ContextMenu();
            var machine = cell.Sequence?.Machine;
            if (machine != null)
            {
                var patterns = machine.Patterns.ToList();
                var assign = new MenuItem { Header = $"Assign pattern of {machine.Name}" };
                foreach (var p in patterns)
                {
                    var pattern = p;
                    assign.Items.Add(new MenuItem
                    {
                        Header = pattern.Name,
                        IsChecked = pattern == cell.Pattern,
                        Command = new RelayCommand(() => layout.SetSlot(cell.Track, cell.Scene, pattern))
                    });
                }
                if (patterns.Count == 0)
                    assign.Items.Add(new MenuItem { Header = "(machine has no patterns)", IsEnabled = false });
                menu.Items.Add(assign);
            }
            menu.Items.Add(new MenuItem
            {
                Header = "Clear slot",
                InputGestureText = "Del",
                Command = new RelayCommand(() => layout.ClearSlot(cell.Track, cell.Scene), () => cell.Pattern != null)
            });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "Edit pattern",
                Command = new RelayCommand(() => EditPattern(cell.Pattern), () => cell.Pattern != null)
            });
            OpenMenu(menu, cell);
        }

        void ShowSceneMenu(SceneCell cell)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Launch scene", Command = new RelayCommand(() => LaunchScene(cell.Scene)) });
            menu.Items.Add(new MenuItem { Header = "Rename scene…", Command = new RelayCommand(() => RenameScene(cell.Scene)) });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Insert scene above", Command = new RelayCommand(() => layout.InsertScene(cell.Scene)) });
            menu.Items.Add(new MenuItem { Header = "Insert scene below", Command = new RelayCommand(() => layout.InsertScene(cell.Scene + 1)) });
            menu.Items.Add(new MenuItem { Header = "Delete scene", Command = new RelayCommand(() => layout.RemoveScene(cell.Scene)) });
            OpenMenu(menu, cell);
        }

        void ShowAddTrackMenu(FrameworkElement anchor)
        {
            var menu = new ContextMenu();
            var machines = layout.MachinesWithoutTrack().ToList();
            foreach (var m in machines)
            {
                var machine = m;
                menu.Items.Add(new MenuItem { Header = machine.Name, Command = new RelayCommand(() => layout.AddTrackFor(machine)) });
            }
            if (machines.Count == 0)
                menu.Items.Add(new MenuItem { Header = "Every machine already has a track", IsEnabled = false });
            OpenMenu(menu, anchor);
        }

        static void OpenMenu(ContextMenu menu, FrameworkElement anchor)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        void RenameScene(int scene)
        {
            var doc = layout.Ensure();
            if (scene < 0 || scene >= doc.Scenes.Count) return;
            string name = PromptWindow.Ask(Window.GetWindow(this), "Rename scene", "Scene name", doc.Scenes[scene].Name);
            if (name != null)
                layout.RenameScene(scene, name);
        }

        void EditPattern(IPattern pattern)
        {
            if (pattern == null) return;
            buzz.SetPatternEditorPattern(pattern);
            buzz.ActiveView = BuzzView.PatternView;
            Window.GetWindow(this)?.Owner?.Activate();
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (selected is not { } sel) return;
            var (track, scene) = sel;
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                layout.ClearSlot(track, scene);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                var cell = slots.FirstOrDefault(c => c.Track == track && c.Scene == scene);
                if (cell != null) LaunchSlot(cell);
                e.Handled = true;
            }
        }

        // ------------------------------------------------------------------ cells

        internal sealed class SlotCell : Border
        {
            readonly SessionView view;
            readonly Rectangle progress;
            readonly TextBlock label;
            bool hover;

            public int Track { get; }
            public int Scene { get; }
            public ISequence Sequence { get; }
            public IPattern Pattern { get; }

            public SlotCell(SessionView view, int track, int scene, ISequence sequence, IPattern clip)
            {
                this.view = view;
                Track = track;
                Scene = scene;
                Sequence = sequence;
                Pattern = clip;

                Margin = new Thickness(0, 0, Gap, Gap);
                BorderThickness = new Thickness(1);
                Cursor = Cursors.Hand;
                ToolTip = clip != null ? $"{clip.Name} ({clip.Length} ticks): click to launch, right-click for options" : "Empty: click to stop this track, right-click to assign a pattern";

                progress = new Rectangle { Fill = Palette.Progress, Height = 3, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Width = 0, IsHitTestVisible = false };
                label = new TextBlock
                {
                    Text = clip != null ? "▶  " + clip.Name : "",
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                var empty = new Rectangle { Width = 7, Height = 7, Fill = Palette.Header, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, Visibility = clip == null ? Visibility.Visible : Visibility.Collapsed };
                Child = new Grid { Children = { empty, label, progress } };

                MouseEnter += (s, e) => { hover = true; };
                MouseLeave += (s, e) => { hover = false; };
                MouseLeftButtonDown += (s, e) => { view.LaunchSlot(this); e.Handled = true; };
                MouseRightButtonUp += (s, e) => { view.ShowSlotMenu(this); e.Handled = true; };
            }

            public void Refresh(ISession session, bool blinkOn, bool isSelected)
            {
                BorderBrush = isSelected ? Palette.Selection : Palette.Transparent;
                if (Pattern == null || Sequence == null)
                {
                    Background = hover ? Palette.EmptyHover : Palette.Empty;
                    progress.Width = 0;
                    return;
                }

                var state = session.GetState(Sequence, Pattern);
                switch (state)
                {
                    case ClipState.Playing:
                        Background = Palette.Playing;
                        break;
                    case ClipState.Queued:
                        // Blinks, and keeps a green outline so it reads as "about to play"
                        // in either phase.
                        Background = blinkOn ? Palette.Queued : (hover ? Palette.ClipHover : Palette.Clip);
                        if (!isSelected) BorderBrush = Palette.Progress;
                        break;
                    case ClipState.Stopping:
                        Background = blinkOn ? Palette.Stopping : Palette.Playing;
                        break;
                    default:
                        Background = hover ? Palette.ClipHover : Palette.Clip;
                        break;
                }

                if (state == ClipState.Playing || state == ClipState.Stopping)
                {
                    int pos = session.GetClipPosition(Sequence);
                    int len = Math.Max(1, Pattern.Length);
                    progress.Width = pos < 0 ? 0 : Math.Max(0, ActualWidth - 2) * (pos + 1) / len;
                }
                else
                {
                    progress.Width = 0;
                }
            }
        }

        internal sealed class SceneCell : Border
        {
            public int Scene { get; }

            public SceneCell(SessionView view, int scene, string name)
            {
                Scene = scene;
                Margin = new Thickness(0, 0, 0, Gap);
                Background = Palette.Scene;
                Cursor = Cursors.Hand;
                ToolTip = "Launch this scene (right-click: rename, insert, delete)";
                Child = new TextBlock
                {
                    Text = "▶  " + (string.IsNullOrEmpty(name) ? (scene + 1).ToString() : name),
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = string.IsNullOrEmpty(name) ? Palette.DimText : Palette.Text
                };
                MouseEnter += (s, e) => Background = Palette.SceneHover;
                MouseLeave += (s, e) => Background = Palette.Scene;
                MouseLeftButtonDown += (s, e) => { view.LaunchScene(Scene); e.Handled = true; };
                MouseRightButtonUp += (s, e) => { view.ShowSceneMenu(this); e.Handled = true; };
            }
        }

        /// <summary>Flat dark button that fits the grid (WPF's default button looks out of place here).</summary>
        internal sealed class FlatButton : Border
        {
            readonly Action onClick;
            readonly TextBlock text;

            public HorizontalAlignment HorizontalContentAlignment
            {
                get => text.HorizontalAlignment;
                set => text.HorizontalAlignment = value;
            }

            public FlatButton(string label, Action onClick, string tooltip)
            {
                this.onClick = onClick;
                Background = Palette.Scene;
                Margin = new Thickness(0, 0, 6, 0);
                Padding = new Thickness(10, 3, 10, 3);
                Cursor = Cursors.Hand;
                ToolTip = tooltip;
                text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                Child = text;
                MouseEnter += (s, e) => { if (IsEnabled) Background = Palette.SceneHover; };
                MouseLeave += (s, e) => Background = Palette.Scene;
                MouseLeftButtonDown += (s, e) => { if (IsEnabled) this.onClick(); e.Handled = true; };
                IsEnabledChanged += (s, e) => Opacity = IsEnabled ? 1.0 : 0.4;
            }
        }

        /// <summary>Menu command. A MenuItem with a Command takes IsEnabled from CanExecute.</summary>
        sealed class RelayCommand : ICommand
        {
            readonly Action action;
            readonly Func<bool> canExecute;
            public RelayCommand(Action action, Func<bool> canExecute = null) { this.action = action; this.canExecute = canExecute; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => canExecute?.Invoke() ?? true;
            public void Execute(object parameter) => action();
        }
    }
}
