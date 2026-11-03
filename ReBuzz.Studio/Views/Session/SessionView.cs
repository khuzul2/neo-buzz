// Fork (docs/specs/studio-mvp.md S5): the Session View on Avalonia, ported from the WPF view
// (ReBuzz/AppViews/Session/SessionView.cs): tracks (sequences) as columns, scenes as rows, scene
// launch buttons on the right, track stop buttons at the bottom, the mixer pinned under the grid
// and the clip panel beside it. Built in code, since its rows and columns follow the song.
// Structure comes from SessionLayout; clip states are polled from ISession. Interaction is in
// SessionView.Actions.cs, the cells in SessionCells.cs, groups, returns and the mixer in
// SessionMixerView.cs, the clip panel in SessionClipPanel.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The Session View's tab in the main window.</summary>
    internal sealed class SessionStudioView : IStudioView
    {
        public string Title => "Session";

        public Control Create(StudioHost host) => new SessionView(host.Core);
    }

    internal sealed partial class SessionView : UserControl, IStudioEditTarget
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

        readonly Grid grid = new Grid { Background = SessionPalette.Window, Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        readonly Grid mixerGrid = new Grid { Background = SessionPalette.Window, Margin = new Thickness(6, 0, 6, 4), HorizontalAlignment = HorizontalAlignment.Left };
        readonly ScrollViewer scroll;
        readonly ScrollViewer mixerPanel;
        readonly FlatButton mixerButton;
        readonly FlatButton clipButton;
        readonly ClipPanel clipPanel;
        internal ClipPanel ClipPanelView => clipPanel;
        readonly TextBlock clockText;
        readonly TextBlock hintText;
        readonly TextBlock statusText;
        readonly Border statusBar;
        readonly FlatButton stopAllButton;
        readonly FlatButton backButton;
        readonly FlatButton recordButton;
        readonly FlatButton overdubButton;       // 0.7: Session Record
        readonly FlatButton captureButton;
        readonly FlatButton clickButton;         // 0.7: metronome
        readonly FlatButton dropoutButton;
        long shownDropouts = -1;
        readonly FlatButton undoButton;
        readonly FlatButton redoButton;
        readonly FlatButton undoLaunchButton;
        readonly FlatButton followButton;
        readonly FlatButton tapButton;
        readonly FlatButton nudgeDownButton;
        readonly FlatButton nudgeUpButton;
        readonly FlatButton addSceneButton;
        readonly FlatButton addTrackButton;
        readonly FlatButton settingsButton;
        readonly TextBlock tempoText;
        readonly TextBlock syncText;          // 0.8: Link, MIDI clock
        string shownSync, shownSyncTip;
        readonly List<Border> ringBoxes = new List<Border>();     // 0.8: controllers' session rings
        string shownRings;
        string shownLastLaunch = "";
        int shownTempo = -1;
        readonly ComboBox quantizeBox;
        readonly ComboBox beatsBox;
        readonly DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };

        readonly List<SlotCell> slots = new List<SlotCell>();
        readonly List<SceneCell> scenes = new List<SceneCell>();
        readonly List<TrackHeader> headers = new List<TrackHeader>();
        readonly List<(int Track, FlatButton Button)> trackStops = new List<(int, FlatButton)>();
        FlatButton stopAllCell;
        int trackCount;
        int sceneCount;
        int blinkPhase;
        int structureCheck;
        bool syncingSettings;
        bool attached;
        (int Track, int Scene)? selected;
        (int Track, int Scene)? dropTarget;

        static readonly (LaunchQuantization Value, string Label)[] QuantizeChoices =
        {
            (LaunchQuantization.None, "None"),
            (LaunchQuantization.Sixteenth, "1/16"),
            (LaunchQuantization.Eighth, "1/8"),
            (LaunchQuantization.Beat, "1 beat"),
            (LaunchQuantization.HalfNote, "2 beats"),
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
            FocusAdorner = null;

            // ---- top bar
            // The left group wraps onto a second row when the view is narrow; the right group stays put.
            var bar = new Grid { Background = SessionPalette.Bar };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var left = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4) };
            stopAllButton = new FlatButton("■  Stop all", () => session.StopAll(), "Stop every clip at the next launch point");
            left.Children.Add(stopAllButton);
            recordButton = new FlatButton("●  Rec", () => buzz.Recording = !buzz.Recording,
                "Record into the arrangement (ReBuzz's Record, F7): every launch and stop is written into the sequences\n" +
                "at the song position where it happens. With looping off, the song grows while you record.\n" +
                "Undo removes a whole recording pass (↶ here).");
            left.Children.Add(recordButton);
            overdubButton = new FlatButton("◉  Overdub", ToggleSessionRecord,
                "Session record (Live's): tracks armed with ● in their header record what you play into their clips.\n" +
                "A track playing a clip overdubs it; a track playing none records a new take into its first empty slot\n" +
                "from the selected scene. Off ends every recording. Each pass is one undo step.\n" +
                "An armed track's empty slots also have their own ● to record a take there.");
            left.Children.Add(overdubButton);
            captureButton = new FlatButton("⤓  Capture", Capture,
                "Capture MIDI (Live's): what you played lately on armed tracks, recording or not, becomes a clip\n" +
                "in their first empty slot from the selected scene, from the bar line before the first note\n" +
                "(1, 2, 4 or 8 bars), and plays on at once in phase.");
            left.Children.Add(captureButton);
            clickButton = new FlatButton("♩", () => layout.SetMetronome(!(layout.Document?.Metronome ?? false)),
                "Metronome: a click on every beat while the session plays (heard, never recorded).\n" +
                "Record length and count-in: ⚙.");
            left.Children.Add(clickButton);
            backButton = new FlatButton("Back to arrangement", () => session.BackToArrangement(), "Lit while the session has taken over tracks: gives every track back to the arrangement");
            left.Children.Add(backButton);
            undoLaunchButton = new FlatButton("↶ Launch", UndoLaunch, "");
            OnRightClick(undoLaunchButton, () => ShowMidiOnlyMenu(undoLaunchButton, SessionTarget.UndoLaunch, 0, 0, "Undo the last launch"));
            left.Children.Add(undoLaunchButton);
            followButton = new FlatButton("Follow", () => layout.SetFollowActionsEnabled(!(layout.Document?.FollowActionsEnabled ?? true)), "");
            OnRightClick(followButton, () => ShowMidiOnlyMenu(followButton, SessionTarget.FollowActions, 0, 0, "Follow actions on/off"));
            left.Children.Add(followButton);
            quantizeBox = new ComboBox
            {
                Width = 96,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            ToolTip.SetTip(quantizeBox,
                "Launch quantization: launches and stops wait for the next beat or bar line you hear.\n" +
                "None starts on the next tick (a tick is 1/ticks-per-beat of a beat, 1/16 note at 4 TPB):\n" +
                "Buzz machines change on ticks, so raise ticks per beat for finer timing (1/16 and 1/8 need it).\n" +
                "A clip can have its own (clip panel).");
            foreach (var q in QuantizeChoices) quantizeBox.Items.Add(q.Label);
            quantizeBox.SelectionChanged += (s, e) =>
            {
                if (!syncingSettings && quantizeBox.SelectedIndex >= 0)
                    layout.SetLaunchQuantization(QuantizeChoices[quantizeBox.SelectedIndex].Value);
            };
            OnRightClick(quantizeBox, () => ShowMidiOnlyMenu(quantizeBox, SessionTarget.Quantization, 0, 0, "launch quantization"));
            left.Children.Add(Labelled("Launch", quantizeBox));      // a label wraps with its box
            beatsBox = new ComboBox { Width = 64, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            for (int i = 1; i <= 12; i++) beatsBox.Items.Add(i.ToString());
            beatsBox.SelectionChanged += (s, e) =>
            {
                if (!syncingSettings && beatsBox.SelectedIndex >= 0)
                    layout.SetBeatsPerBar(beatsBox.SelectedIndex + 1);
            };
            left.Children.Add(Labelled("Beats/bar", beatsBox));

            // Tempo (0.6): tap, and nudge while held (Live's), each MIDI-learnable.
            tempoText = new TextBlock
            {
                FontFamily = Mono,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                MinWidth = 56,
            };
            ToolTip.SetTip(tempoText, "Tempo (the song's BPM)");
            nudgeDownButton = HoldButton("◀", -1, SessionTarget.NudgeDown, "Nudge down");
            nudgeUpButton = HoldButton("▶", 1, SessionTarget.NudgeUp, "Nudge up");
            tapButton = new FlatButton("Tap", () => layout.TapTempo(), "") { Margin = new Thickness(4, 0, 14, 0) };
            OnRightClick(tapButton, () => ShowMidiOnlyMenu(tapButton, SessionTarget.TapTempo, 0, 0, "Tap tempo"));
            // Sync (0.8): Link or MIDI clock, when on; the tooltip says how far off the clock the session is.
            syncText = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0),
                Foreground = SessionPalette.DimText,
                IsVisible = false
            };
            // One group, so a narrow view wraps it whole.
            left.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { tempoText, nudgeDownButton, nudgeUpButton, tapButton, syncText }
            });
            UpdateBarTips();

            clockText = new TextBlock
            {
                FontFamily = Mono,
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                MinWidth = 90,
            };
            ToolTip.SetTip(clockText, "Session clock: bar.beat.tick");
            left.Children.Add(clockText);
            dropoutButton = new FlatButton("", () => { buzz.ResetAudioDropoutCounters(); shownDropouts = -1; RefreshState(); }, "")
            {
                Margin = new Thickness(10, 0, 0, 0),
                Padding = new Thickness(8, 3, 8, 3)
            };
            left.Children.Add(dropoutButton);
            bar.Children.Add(left);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 4, 6, 4), VerticalAlignment = VerticalAlignment.Top };
            undoButton = new FlatButton("↶", () => layout.Undo(), $"Undo grid edit ({ModName}+Z)") { Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 2, 0) };
            redoButton = new FlatButton("↷", () => layout.Redo(), $"Redo grid edit ({ModName}+Y)") { Padding = new Thickness(8, 3, 8, 3) };
            right.Children.Add(undoButton);
            right.Children.Add(redoButton);
            addSceneButton = new FlatButton("+ Scene", () => layout.InsertScene(layout.SceneCount), $"Add a scene row at the bottom ({ModName}+I inserts one below the selection)");
            right.Children.Add(addSceneButton);
            addTrackButton = new FlatButton("+ Track", () => ShowAddTrackMenu(addTrackButton), "Add a track for a machine that has none");
            right.Children.Add(addTrackButton);
            clipButton = new FlatButton("Clip", () => layout.SetShowClipPanel(!layout.Ensure().ShowClipPanel),
                "Show or hide the clip panel: how the selected clip launches (mode, quantization, legato, loop region, velocity)");
            right.Children.Add(clipButton);
            mixerButton = new FlatButton("Mixer", ToggleMixer, $"Show or hide the mixer under the grid ({ModName}+Alt+M)\nReturn tracks and the cue output: ⚙");
            right.Children.Add(mixerButton);
            settingsButton = new FlatButton("⚙", () => ShowSettingsMenu(settingsButton), "New clip length, recording, sync, controllers, mixer returns and cue output, MIDI mapping, keyboard shortcuts") { Margin = new Thickness(0) };
            right.Children.Add(settingsButton);
            Grid.SetColumn(right, 1);
            bar.Children.Add(right);

            // Status line under the top bar (MIDI learn), hidden when empty.
            statusText = new TextBlock
            {
                Foreground = SessionPalette.DarkText,
                FontWeight = FontWeight.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(10, 3, 10, 3)
            };
            statusBar = new Border { Background = SessionPalette.Learn, Child = statusText, IsVisible = false };

            // ---- grid
            hintText = new TextBlock
            {
                Foreground = SessionPalette.DimText,
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Text = "No tracks yet. Every sequence of the song is a track: add one with \"+ Track\", " +
                       "or create machines in the Machine View.",
                IsVisible = false
            };
            scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel { Children = { hintText, grid } },
                Background = SessionPalette.Window,
                Focusable = false
            };

            // Mixer: pinned under the grid, scrolled sideways with it.
            mixerPanel = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = mixerGrid,
                Background = SessionPalette.Window,
                Focusable = false,
                Padding = new Thickness(0, 4, 0, 0)
            };
            var mixerBorder = new Border { BorderBrush = SessionPalette.Header, BorderThickness = new Thickness(0, 1, 0, 0), Child = mixerPanel };
            scroll.ScrollChanged += (s, e) => mixerPanel.Offset = new Vector(scroll.Offset.X, 0);
            clipPanel = new ClipPanel(this);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            DockPanel.SetDock(statusBar, Dock.Top);
            root.Children.Add(statusBar);
            DockPanel.SetDock(mixerBorder, Dock.Bottom);
            root.Children.Add(mixerBorder);
            DockPanel.SetDock(clipPanel, Dock.Right);
            root.Children.Add(clipPanel);
            root.Children.Add(scroll);
            mixerSection = mixerBorder;
            Content = root;

            timer.Tick += (s, e) => RefreshState();
            meterTimer.Tick += (s, e) => OnMeterTick();

            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel);
            BindShortcuts();
            AddHandler(PointerPressedEvent, (s, e) =>
            {
                // A press anywhere but in a box takes the keys (and commits a box being typed in).
                if (e.Source is Visual v && (FindParent<TextBox>(v) != null || FindParent<ComboBox>(v) != null)) return;
                Focus();
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            LostFocus += (s, e) => { if (!IsKeyboardFocusWithin) ReleaseHeldKey(); };
        }

        static readonly FontFamily Mono = new FontFamily("Menlo, Consolas, DejaVu Sans Mono, monospace");

        /// <summary>The Control key's name here: Command on macOS (as the shell's shortcuts).</summary>
        internal static string ModName => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";

        /// <summary>Command on macOS does what Control does elsewhere: the view's keys see Control.</summary>
        internal static KeyModifiers Normalize(KeyModifiers mods)
        {
            if (OperatingSystem.IsMacOS() && mods.HasFlag(KeyModifiers.Meta))
                mods = (mods & ~KeyModifiers.Meta) | KeyModifiers.Control;
            return mods;
        }

        static void OnRightClick(Control control, Action action)
        {
            control.AddHandler(PointerReleasedEvent, (s, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Right) return;
                action();
                e.Handled = true;
            }, RoutingStrategies.Bubble, handledEventsToo: true);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            // Hidden views (another tab) don't follow the song: they rebuild when shown (0.6.3).
            layout.Changed += RebuildWhenSeen;
            layout.RecordingChanged += OnRecordingChanged;
            if (midi != null)
            {
                midi.Changed += RebuildWhenSeen;
                midi.Launched += OnMidiLaunched;
            }
            session.PropertyChanged += OnSessionChanged;
            Rebuild();
            timer.Start();
            // Shown (its tab selected): the view takes the keys, unless something in it has them.
            Dispatcher.UIThread.Post(() => { if (attached && !IsKeyboardFocusWithin) Focus(); });
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            attached = false;
            timer.Stop();
            layout.Changed -= RebuildWhenSeen;
            layout.RecordingChanged -= OnRecordingChanged;
            if (midi != null)
            {
                midi.Changed -= RebuildWhenSeen;
                midi.Launched -= OnMidiLaunched;
            }
            session.PropertyChanged -= OnSessionChanged;
            UpdateMetering();
            base.OnDetachedFromVisualTree(e);
        }

        void OnRecordingChanged()
        {
            if (attached) RefreshState();
        }

        void OnMidiLaunched(int track, int scene)
        {
            if (attached && SelectOnLaunch) SelectLaunched(track, scene);
        }

        void OnSessionChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) => SyncSettings();

        /// <summary>A tempo nudge button: slower or faster while the pointer holds it (0.6).</summary>
        FlatButton HoldButton(string label, int direction, SessionTarget target, string what)
        {
            FlatButton button = null;
            button = new FlatButton(label, () => (session as SessionCore)?.Nudge(direction), "")
            { Padding = new Thickness(7, 3, 7, 3), Margin = new Thickness(0, 0, 2, 0) };
            button.AddHandler(PointerPressedEvent, (s, e) =>
            {
                if (e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) e.Pointer.Capture(button);
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            button.PointerReleased += (s, e) => (session as SessionCore)?.Nudge(0);
            button.PointerCaptureLost += (s, e) => (session as SessionCore)?.Nudge(0);
            OnRightClick(button, () => ShowMidiOnlyMenu(button, target, 0, 0, what));
            return button;
        }

        /// <summary>Tooltips of the top bar's performance controls, with their MIDI mappings.</summary>
        void UpdateBarTips()
        {
            ToolTip.SetTip(followButton, Tip("Follow actions on or off for the whole song (Live's switch): clips and scenes\n" +
                                             "launch the next one at their follow time when lit. Set them in the clip panel.", SessionTarget.FollowActions, 0, 0));
            ToolTip.SetTip(tapButton, Tip("Tap tempo: tap the beat; from the third tap the tempo follows the last four.", SessionTarget.TapTempo, 0, 0));
            ToolTip.SetTip(nudgeDownButton, Tip("Nudge down: plays 4% slower while held, to bring the session back in step with\n" +
                                                "another source; the tempo stays as it is.", SessionTarget.NudgeDown, 0, 0));
            ToolTip.SetTip(nudgeUpButton, Tip("Nudge up: plays 4% faster while held; the tempo stays as it is.", SessionTarget.NudgeUp, 0, 0));
            shownLastLaunch = null;   // the undo button's tip, on the next refresh
        }

        void UndoLaunch()
        {
            if (session is SessionCore core)
                core.UndoLaunch();
            RefreshState();
        }

        static StackPanel Labelled(string text, Control control) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                new TextBlock
                {
                    Text = text,
                    Foreground = SessionPalette.DimText,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 6, 0)
                },
                control,
            },
        };

        // ------------------------------------------------------------------ structure

        void RebuildWhenSeen()
        {
            if (attached) Rebuild();
        }

        /// <summary>Rebuilds the grid from the layout (auto-filling one for songs without).</summary>
        internal void Rebuild()
        {
            var doc = layout.Ensure();
            SyncSettings();

            grid.Children.Clear();
            ringBoxes.Clear();                // 0.8: drawn again on the new grid
            shownRings = null;
            grid.RowDefinitions.Clear();
            grid.ColumnDefinitions.Clear();
            slots.Clear();
            scenes.Clear();
            headers.Clear();
            trackStops.Clear();
            groupSlots.Clear();
            groupHeaders.Clear();

            trackCount = doc.Tracks.Count;
            sceneCount = doc.Scenes.Count;
            hintText.IsVisible = trackCount == 0;
            PlanColumns(doc);
            int sceneColumn = columns.Count;

            for (int c = 0; c < columns.Count; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidth + Gap) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SceneColumnWidth + Gap) });

            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(HeaderHeight + Gap) });
            for (int s = 0; s < sceneCount; s++)
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight + Gap) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(RowHeight + 8) });

            // Columns: track headers, slots and stop buttons; groups; returns
            for (int c = 0; c < columns.Count; c++)
            {
                var col = columns[c];
                if (col.Kind == ColumnKind.Group)
                {
                    PlaceGroupColumn(doc, col, c);
                    continue;
                }
                if (col.Kind == ColumnKind.Return)
                {
                    PlaceReturnColumn(col, c);
                    continue;
                }

                int t = col.Track;
                var seq = layout.SequenceOf(t);
                string name = seq?.Machine?.Name ?? doc.Tracks[t].Machine;
                var header = new TrackHeader(this, t, seq?.Machine, name, doc.Tracks[t].ColorIndex);
                headers.Add(header);
                Place(header, 0, c);

                int track = t;
                var stop = new FlatButton("■", () => StopTrack(track), Tip("Stop this track (Backspace stops the selected track)", SessionTarget.TrackStop, t, 0))
                {
                    Margin = new Thickness(0, 6, Gap, 0),
                    HorizontalContentAlignment = HorizontalAlignment.Center
                };
                OnRightClick(stop, () => ShowMidiOnlyMenu(stop, SessionTarget.TrackStop, track, 0, "this track's stop button"));
                trackStops.Add((t, stop));
                Place(stop, sceneCount + 1, c);

                for (int s = 0; s < sceneCount; s++)
                {
                    var pattern = layout.PatternAt(t, s);
                    var cell = new SlotCell(this, t, s, seq, pattern, midi?.Describe(SessionTarget.Slot, t, s), pattern == null ? null : layout.ClipOf(pattern));
                    slots.Add(cell);
                    Place(cell, s + 1, c);
                }
            }

            // Scene column header and Stop all
            Place(new Border
            {
                Background = SessionPalette.Header,
                Margin = new Thickness(0, 0, 0, Gap),
                Padding = new Thickness(6, 0, 6, 0),
                Child = new TextBlock { Text = "Scenes", Foreground = SessionPalette.DimText, VerticalAlignment = VerticalAlignment.Center }
            }, 0, sceneColumn);
            stopAllCell = new FlatButton("■  Stop all clips", () => session.StopAll(), Tip("Stop every clip", SessionTarget.StopAll, 0, 0)) { Margin = new Thickness(0, 6, 0, 0) };
            OnRightClick(stopAllCell, () => ShowMidiOnlyMenu(stopAllCell, SessionTarget.StopAll, 0, 0, "Stop all clips"));
            Place(stopAllCell, sceneCount + 1, sceneColumn);

            // Scene buttons
            for (int s = 0; s < sceneCount; s++)
            {
                var scene = new SceneCell(this, s, doc.Scenes[s], midi?.Describe(SessionTarget.Scene, 0, s));
                scenes.Add(scene);
                Place(scene, s + 1, sceneColumn);
            }

            // Outline of the MIDI note grid (Live's "session ring"), when its tracks are shown.
            var ring = midi?.ActiveMap?.Grid;
            int firstColumn = -1, lastColumn = -1;
            if (ring != null && ring.OriginTrack >= 0 && ring.OriginTrack < trackCount && ring.Columns > 0)
            {
                firstColumn = trackColumn[ring.OriginTrack];
                lastColumn = trackColumn[Math.Min(trackCount, ring.OriginTrack + ring.Columns) - 1];
            }
            if (ring != null && ring.OriginScene < sceneCount && firstColumn >= 0 && lastColumn >= firstColumn)
            {
                var outline = new Border
                {
                    BorderBrush = SessionPalette.Ring,
                    BorderThickness = new Thickness(2),
                    Margin = new Thickness(-2, -2, 0, 0),
                    IsHitTestVisible = false,
                    ZIndex = 1
                };
                Grid.SetRowSpan(outline, Math.Min(ring.Rows, sceneCount - ring.OriginScene));
                Grid.SetColumnSpan(outline, lastColumn - firstColumn + 1);
                Place(outline, ring.OriginScene + 1, firstColumn);
            }

            if (selected is { } sel && (sel.Track >= trackCount || sel.Scene >= sceneCount || trackColumn[sel.Track] < 0))
                selected = null;
            BuildMixer(doc);
            UpdateBarTips();
            followButton.SetLit(doc.FollowActionsEnabled ? SessionPalette.Fader : null);
            clipPanel.IsVisible = doc.ShowClipPanel;
            clipButton.SetLit(doc.ShowClipPanel ? SessionPalette.Fader : null);
            clipPanel.Follow(selected, force: true);
            RefreshState();
        }

        string Tip(string text, SessionTarget target, int track, int scene)
        {
            string m = midi?.Describe(target, track, scene);
            return m == null ? text + "\nright-click: MIDI learn" : text + "\n" + m;
        }

        /// <summary>Each connected controller's session ring, as a red box round its slots (0.8, Live's).</summary>
        void UpdateRings()
        {
            var rings = buzz.SessionControllers?.Rings.ToList() ?? new List<(int, int, int, int)>();
            string key = string.Join(";", rings);
            if (key == shownRings) return;
            shownRings = key;
            foreach (var box in ringBoxes) grid.Children.Remove(box);
            ringBoxes.Clear();
            foreach (var (track, scene, width, height) in rings)
            {
                var cells = slots.Where(c => c.Track >= track && c.Track < track + width && c.Scene >= scene && c.Scene < scene + height).ToList();
                if (cells.Count == 0) continue;
                int r0 = cells.Min(Grid.GetRow), r1 = cells.Max(Grid.GetRow);
                int c0 = cells.Min(Grid.GetColumn), c1 = cells.Max(Grid.GetColumn);
                var box = new Border
                {
                    BorderBrush = SessionPalette.ControllerRing,
                    BorderThickness = new Thickness(2),
                    Margin = new Thickness(-1, -1, Gap - 1, Gap - 1),
                    IsHitTestVisible = false,
                    ZIndex = 50
                };
                Grid.SetRow(box, r0);
                Grid.SetRowSpan(box, r1 - r0 + 1);
                Grid.SetColumn(box, c0);
                Grid.SetColumnSpan(box, c1 - c0 + 1);
                grid.Children.Add(box);
                ringBoxes.Add(box);
            }
        }

        void Place(Control element, int row, int column)
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
                IBrush border = null;
                if (learn is { } l && l.Target == SessionTarget.Slot && l.Track == cell.Track && l.Scene == cell.Scene)
                    border = blinkOn ? SessionPalette.Learn : null;
                else if (dropTarget == (cell.Track, cell.Scene) || selected == (cell.Track, cell.Scene))
                    border = SessionPalette.Selection;
                cell.Refresh(session, blinkOn, border);
            }
            foreach (var cell in groupSlots)
                cell.Refresh(session, blinkOn);
            UpdateRings();
            foreach (var scene in scenes)
                scene.Refresh(learn is { } l && l.Target == SessionTarget.Scene && l.Scene == scene.Scene && blinkOn ? SessionPalette.Learn : null);
            foreach (var (t, stop) in trackStops)
                stop.BorderBrush = learn is { } l && l.Target == SessionTarget.TrackStop && l.Track == t && blinkOn ? SessionPalette.Learn : SessionPalette.Transparent;
            if (stopAllCell != null)
                stopAllCell.BorderBrush = learn is { } la && la.Target == SessionTarget.StopAll && blinkOn ? SessionPalette.Learn : SessionPalette.Transparent;
            foreach (var h in headers)
                h.Refresh();
            RefreshMixer();
            if (clipPanel.IsVisible)
                clipPanel.Follow(selected);

            // Machine groups change without an event the layout hears: look now and then.
            if (++structureCheck >= 20)
            {
                structureCheck = 0;
                if (layout.Document != null && StructureKey() != structureKey)
                {
                    Rebuild();
                    return;
                }
            }

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
                // Bars count on from where a scene last changed the time signature (0.5).
                long origin = 0, originBar = 0;
                if (session is SessionCore core)
                {
                    origin = core.GridOrigin;
                    originBar = core.GridOriginBar;
                }
                long rel = clock - origin;
                if (rel < 0) { rel = clock; originBar = 0; }
                long barNumber = originBar + rel / (tpb * bpb) + 1;
                long beat = rel / tpb % bpb + 1;
                long tick = rel % tpb + 1;
                clockText.Text = $"{barNumber}.{beat}.{tick}";
                clockText.Foreground = SessionPalette.Text;
            }

            bool overridden = session.IsAnyArrangementOverridden;
            backButton.IsEnabled = overridden;
            backButton.SetLit(overridden ? SessionPalette.Override : null);
            recordButton.SetLit(buzz.Recording ? SessionPalette.Record : null);
            layout.PollTakes();                                       // 0.7: takes whose first pass is over finish
            overdubButton.SetLit(layout.IsSessionRecording ? SessionPalette.Record : null);
            clickButton.SetLit(layout.Document?.Metronome == true ? SessionPalette.Fader : null);
            RefreshDropouts();
            undoButton.IsEnabled = layout.UndoStack.CanUndo;
            redoButton.IsEnabled = layout.UndoStack.CanRedo;
            RefreshPerformance(learn, blinkOn);
            statusText.Text = learn is { } target ? $"MIDI learn: press a pad, key or knob for {Describe(target.Target, target.Track, target.Scene)}  ·  Esc cancels" : "";
            statusBar.IsVisible = learn != null;
        }

        /// <summary>Undo-launch, tempo and learn highlights of the top bar (0.6).</summary>
        void RefreshPerformance((SessionTarget Target, int Track, int Scene)? learn, bool blinkOn)
        {
            string last = (session as SessionCore)?.LastLaunch;
            if (last != shownLastLaunch)
            {
                shownLastLaunch = last;
                undoLaunchButton.Opacity = last != null ? 1.0 : 0.4;   // stays enabled: right-click learns
                ToolTip.SetTip(undoLaunchButton, Tip(last == null
                    ? $"Undo the last launch ({ModName}+Alt+Z): nothing launched yet"
                    : $"Undo the last launch ({ModName}+Alt+Z): {last}.\nA launch still waiting is cancelled; one that has started gives the track(s)\n" +
                      "back what they played before, in step, as if it had never happened.", SessionTarget.UndoLaunch, 0, 0));
            }
            int bpm = buzz.BPM;
            if (bpm != shownTempo)
            {
                shownTempo = bpm;
                tempoText.Text = $"{bpm} BPM";
            }
            bool nudging = buzz.TempoNudge != 1f;
            tempoText.Foreground = nudging ? SessionPalette.Learn : SessionPalette.Text;
            string sync = buzz.SessionSync?.Status ?? "";
            if (sync != shownSync)
            {
                shownSync = sync;
                syncText.Text = sync;
                syncText.IsVisible = sync.Length > 0;
            }
            bool following = buzz.SyncClock.IsFollowing;
            syncText.Foreground = following ? SessionPalette.Text : SessionPalette.DimText;
            double error = buzz.SyncClock.ErrorMicros;
            string tip = following && !double.IsNaN(error)
                ? $"Following: the session is {Math.Abs(error) / 1000:0.0} ms {(error >= 0 ? "ahead of" : "behind")} the clock."
                : "Sync: Ableton Link, MIDI clock in and out.";
            if (tip != shownSyncTip)
            {
                shownSyncTip = tip;
                ToolTip.SetTip(syncText, tip);
            }
            foreach (var (button, target) in new[] {
                (undoLaunchButton, SessionTarget.UndoLaunch), (followButton, SessionTarget.FollowActions), (tapButton, SessionTarget.TapTempo),
                (nudgeDownButton, SessionTarget.NudgeDown), (nudgeUpButton, SessionTarget.NudgeUp) })
                button.BorderBrush = learn is { } l && l.Target == target && blinkOn ? SessionPalette.Learn : SessionPalette.Transparent;
        }

        /// <summary>
        /// Dropouts since ReBuzz started or the last reset (click): audio callbacks that took
        /// longer than the buffer they filled, plus driver resets. Red while there are any.
        /// </summary>
        void RefreshDropouts()
        {
            long count = buzz.DeadlineMissCount + buzz.DriverResetCount;
            if (count == shownDropouts) return;
            shownDropouts = count;
            dropoutButton.Text = count == 0 ? "No dropouts" : count == 1 ? "1 dropout" : $"{count} dropouts";
            dropoutButton.SetLit(count > 0 ? SessionPalette.Record : null);
            double latency = session is SessionCore core ? core.CompensatedLatencyMs : 0;
            ToolTip.SetTip(dropoutButton,
                "Audio dropouts since ReBuzz started or since you last clicked here:\n" +
                $"  late buffers (the engine took longer than the buffer lasts): {buzz.DeadlineMissCount}\n" +
                $"  driver resets: {buzz.DriverResetCount}\n" +
                $"  worst overrun: {buzz.DeadlineWorstOverrunMicros / 1000.0:F1} ms\n" +
                (latency > 0 ? $"Launches are timed to what you hear, {latency:F0} ms behind the engine (⚙ to turn off).\n" : "") +
                "Click to reset.");
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
            SessionTarget.UndoLaunch => "undo the last launch",
            SessionTarget.TapTempo => "tap tempo",
            SessionTarget.NudgeDown => "nudge the tempo down",
            SessionTarget.NudgeUp => "nudge the tempo up",
            SessionTarget.Crossfader => "the crossfader",
            SessionTarget.FollowActions => "follow actions on/off",
            _ => target.ToString()
        };

        // ------------------------------------------------------------------ for tests

        internal IReadOnlyList<SlotCell> Slots => slots;
        internal IReadOnlyList<SceneCell> Scenes => scenes;
        internal IReadOnlyList<TrackHeader> Headers => headers;
        internal (int Track, int Scene)? Selected => selected;
        internal SlotCell SlotAt(int track, int scene) => CellAt(track, scene);
        internal FlatButton StopAllButton => stopAllButton;
        internal FlatButton RecordButton => recordButton;
        internal FlatButton OverdubButton => overdubButton;
        internal FlatButton BackButton => backButton;
        internal FlatButton UndoButton => undoButton;
        internal FlatButton RedoButton => redoButton;
        internal FlatButton MixerButton => mixerButton;
        internal FlatButton ClipButton => clipButton;
        internal FlatButton FollowButton => followButton;
        internal FlatButton TapButton => tapButton;
        internal FlatButton UndoLaunchButton => undoLaunchButton;
        internal FlatButton AddSceneButton => addSceneButton;
        internal FlatButton SettingsButton => settingsButton;
        internal FlatButton NudgeUpButton => nudgeUpButton;
        internal ComboBox QuantizeBox => quantizeBox;
        internal ComboBox BeatsBox => beatsBox;
        internal string ClockText => clockText.Text;
        internal string TempoText => tempoText.Text;
        internal ScrollViewer GridScroll => scroll;
        internal string StatusLine => statusBar.IsVisible ? statusText.Text : "";
    }
}
