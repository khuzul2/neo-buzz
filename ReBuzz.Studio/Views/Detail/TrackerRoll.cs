// Fork (docs/specs/ux-overhaul.md P3b, clips.md §8 CU4): the piano roll of the detail panel. The Tracker and the piano roll are two views
// of one clip: this one shows the clip's notes through SongNotes, the one reader and writer of a clip's notes, and edits them on the
// song's history, one undo step a gesture. The clip is the Tracker's: the page that holds both (ClipPage) hands it over, and the
// studio's selection follows it. A machine that takes no notes gets a message.
//
// Fork (daw-modes.md AR2): the same control is the arrangement's piano roll panel (songOnly: the notes the song keeps for the
// machine, as the Session View's clip plays them). Both get a velocity lane or a controller lane (CC, pitch bend) under the
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
using Avalonia.VisualTree;
using Avalonia.Platform.Storage;
using Buzz.MachineInterface;
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
        readonly EnvelopeStack envelopes;          // EV1: the clip's envelope lanes under the roll (the Detail panel's page and the Arrangement's roll panel alike)
        readonly RollAxis axis;
        int shownPlay = int.MinValue;
        bool loopUndoable, loopRedoable;
        INoteSource source;
        readonly bool songOnly;
        readonly Button slideButton;               // FU2b: the per-note Slide flag of the selection
        readonly ComboBox keyBox, scaleBox, foldBox, ghostBox, lowerBox;
        readonly ToggleButton stepBox, snapBox;
        ControllerLane ctrl;
        MidiControllers controllers;
        int lowerController = -1;                 // -1: the velocity lane; else the controller the lane under the roll shows
        // TM1: a machine that plays a sample per note: the picker over the roll, the Sample lane (lowerController SampleLaneId), colour by sample.
        const int SampleLaneId = -2;
        readonly SamplePicker picker = new SamplePicker();
        readonly ToggleButton colourBox;
        SampleLane sampleLane;
        NoteSampleContext sampleContext;
        bool settingLower;
        (int, int, long) ghostSeen;
        (int Count, long Sum) ctrlSeen;
        IClip clip;
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

            summary = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(8, 0, 0, 0), FontSize = StudioTokens.FontBody };
            AutomationProperties.SetName(summary, "Piano roll summary");
            string mod = Views.Session.SessionView.ModName;
            var tools = new WrapPanel { Margin = new Thickness(8, 4, 8, 4) };
            tools.Children.Add(gridBox);
            tools.Children.Add(Tool("Quantize", "Quantize the selected notes (or all) to the grid (Q)", () => roll?.Quantize()));
            slideButton = Tool("Slide", "Slide: the selected notes glide into their pitch from the note before (L). Neo Poly glides at its Slide time, whatever its Slide mode but Never; other machines play them as plain notes", () => roll?.ToggleSlide());
            tools.Children.Add(slideButton);
            tools.Children.Add(Tool("Vel −", $"Softer ({mod}+Down)", () => roll?.ChangeVelocity(-8)));
            tools.Children.Add(Tool("Vel +", $"Louder ({mod}+Up)", () => roll?.ChangeVelocity(8)));
            tools.Children.Add(Tool("Delete", "Delete the selected notes (Delete)", () => roll?.DeleteSelected()));
            followBox = StudioButtons.KeepFocus(new ToggleButton { Content = "Follow", IsChecked = true, Margin = new Thickness(0, 0, 6, 0) });
            ToolTip.SetTip(followBox, "Scroll with the playhead while the clip plays");
            Avalonia.Automation.AutomationProperties.SetName(followBox, "Follow playhead");
            tools.Children.Add(followBox);
            tools.Children.Add(Tool("−", $"Zoom out ({mod}+wheel)", () => Zoom(0.8)));
            tools.Children.Add(Tool("+", $"Zoom in ({mod}+wheel)", () => Zoom(1.25)));
            tools.Children.Add(summary);

            // Fork (AR2): the second row, the roll's help for the notes: key and scale, fold, step input, ghosts, the lane under the roll.
            keyBox = Combo("Key", 96, "Key: off", "Key: C", "Key: C#", "Key: D", "Key: D#", "Key: E", "Key: F", "Key: F#", "Key: G", "Key: G#", "Key: A", "Key: A#", "Key: B");
            scaleBox = Combo("Scale", 150, Scale.All.Select(sc => sc.Name).ToArray());
            scaleBox.SelectedIndex = 0;
            foldBox = Combo("Fold", 130, "Fold: off", "Fold: used", "Fold: scale");
            // Fork (daw-modes.md §8.9, NM1): Fold: playable (the keys that make a sound on this machine); disabled while the machine reports none.
            playableItem = new ComboBoxItem { Content = "Fold: playable", IsEnabled = false };
            foldBox.Items.Add(playableItem);
            UpdatePlayable();
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
            colourBox = StudioButtons.KeepFocus(new ToggleButton { Content = "Colour: by sample", IsChecked = true, IsVisible = false, Margin = new Thickness(0, 0, 6, 0) });
            ToolTip.SetTip(colourBox, "Colour every note by its sample, so a re-sequenced break reads at a glance");
            AutomationProperties.SetName(colourBox, "Colour by sample");
            colourBox.IsCheckedChanged += (s, e) => { if (sampleContext != null) sampleContext.ColourBySample = colourBox.IsChecked == true; roll?.InvalidateVisual(); roll?.Focus(); };
            tools.Children.Add(colourBox);
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
            body = new Panel { Children = { scroll, playhead, message, picker } };
            picker.Closed += () => roll?.Focus();
            picker.LoadSample = () => _ = LoadSampleAsync();
            picker.OpenSampler = OpenSampler;
            // A press anywhere but in the picker closes it (it is a menu).
            body.AddHandler(PointerPressedEvent, (s, e) =>
            {
                if (picker.IsVisible && !IsInside(e.Source as Visual, picker)) picker.Close();
            }, RoutingStrategies.Tunnel);
            rollGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            rollGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            rollGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetRow(body, 1);
            rollGrid.Children.Add(body);
            {
                // Fork (daw-modes.md EV1; FU2b: the Arrangement's panel too): under the roll (and its velocity or controller lane), the clip's envelopes on the roll's time axis.
                axis = new RollAxis(this);
                envelopes = new EnvelopeStack(core, axis);
                envelopes.LaneEdited += OnEnvelopeEdited;
                rollGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Grid.SetRow(envelopes, 3);
                rollGrid.Children.Add(envelopes);
                SizeChanged += (s, e) => envelopes.MaxLanesHeight = e.NewSize.Height * 0.45;
            }
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

        /// <summary>The roll showing the clip now, or null (no clip, or its machine has no Note column).</summary>
        public PianoRoll Roll => roll;

        /// <summary>The notes the roll shows (tests); null when it shows a message.</summary>
        internal System.Collections.Generic.IReadOnlyList<ClipNote> Notes() => roll?.Notes;

        public IClip Clip => clip;

        public ScrollViewer Scroll => scroll;

        public ComboBox GridBox => gridBox;

        /// <summary>What the roll says instead of notes (no clip, no Note column), or null.</summary>
        public string Message => message.IsVisible ? message.Text : null;

        public string SummaryText => summary.Text;

        /// <summary>Shows a clip's notes (the Tracker's clip), or says why there are none.</summary>
        public void Show(IClip p)
        {
            if (roll != null && ReferenceEquals(clip, p) && source?.IsLive == true)
            {
                Poll();
                return;
            }
            clip = p;
            DropRoll();
            if (p == null)
            {
                Say("No clip. Choose one above, or make one with New clip.");
                ShowEnvelopes();
                return;
            }
            source = SongNotes.For(core, p);
            if (source == null)
            {
                ShowEnvelopes();
                if (songOnly)
                {
                    Say($"{p.Machine?.Name} takes no notes, so there is no piano roll for its clips.");
                    return;
                }
                Say($"{p.Machine?.Name} has no Note column and takes no MIDI notes, so there are no notes to draw here. Its clip still plays what the Tracker holds.");
                return;
            }
            message.IsVisible = false;
            scroll.IsVisible = true;
            rowTime = source.RowTime;
            roll = new PianoRoll(core, source, null) { Grid = rowTime, AltBypassesGrid = true, RowHeight = 13 };
            SetUpSamples(p);
            AutomationProperties.SetName(roll, songOnly ? "Arrangement piano roll notes" : "Piano roll");
            roll.Changed += UpdateSummary;
            roll.MapChanged += OnMapChanged;
            UpdatePlayable();
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
            ShowEnvelopes();
        }

        // ---- Fork (daw-modes.md EV1): the envelope lanes

        /// <summary>The envelopes of the clip the roll shows: under the roll's own time axis, or under a ruler of their own when the clip's machine takes no notes.</summary>
        public EnvelopeStack Envelopes => envelopes;

        void ShowEnvelopes(ColumnKey? focus = null)
        {
            if (envelopes == null) return;
            envelopes.ShowRuler = roll == null;
            envelopes.Show(clip, focus);
            axis.Raise();
        }

        /// <summary>Shows the clip with the lane of a parameter (a double click on a curve in the Arrangement or the Session).</summary>
        internal void ShowEnvelope(IClip p, ColumnKey key)
        {
            if (envelopes == null) return;
            if (!ReferenceEquals(clip, p)) Show(p);
            ShowEnvelopes(key);
            envelopes.LaneFor(key)?.Focus();
        }

        void OnEnvelopeEdited()
        {
            // The song's history holds the step: the roll's Undo goes there first (as for a loop drag).
            loopUndoable = true;
            loopRedoable = false;
        }

        /// <summary>Whether the keyboard is in an envelope lane (Undo and Redo are the song's then).</summary>
        internal bool EnvelopesFocused => envelopes?.OwnsFocus == true;

        readonly ComboBoxItem playableItem;

        const string NoPlayableTip = "This machine reports no playable keys";
        const string PlayableTip = "Show only the keys that make a sound on this machine (and any key that holds notes)";

        /// <summary>Whether Fold: playable can be chosen: the machine reports its keys (a Neo machine, a hosted plug-in's note names, a manifest's map).</summary>
        public bool CanFoldPlayable => roll?.Map is { IsEmpty: false };

        /// <summary>The playable item follows the machine's note map: enabled with a map, with the reason in a tooltip when there is none.</summary>
        void UpdatePlayable()
        {
            bool can = CanFoldPlayable;
            playableItem.IsEnabled = can;
            ToolTip.SetTip(playableItem, can ? PlayableTip : NoPlayableTip);
            ToolTip.SetTip(foldBox, can ? null : NoPlayableTip);
            if (!can && foldBox.SelectedIndex == (int)FoldMode.Playable) foldBox.SelectedIndex = 0;   // the machine changed: all the keys again
        }

        /// <summary>The machine's key names changed (or arrived): the fold choice, the key column and the lanes that follow it.</summary>
        void OnMapChanged()
        {
            UpdatePlayable();
            ApplyScale();
            if (ruler != null) ruler.InvalidateVisual();
            if (ctrl != null) ctrl.InvalidateVisual();
            UpdateSummary();
        }

        /// <summary>The key and scale the boxes name, and the fold, applied to the roll.</summary>
        void ApplyScale()
        {
            if (roll == null) return;
            roll.Scale = keyBox.SelectedIndex <= 0 || scaleBox.SelectedIndex < 0 ? null : new NoteScale(keyBox.SelectedIndex - 1, Scale.All[scaleBox.SelectedIndex]);
            roll.Fold = (FoldMode)Math.Max(0, foldBox.SelectedIndex);
            FitRows();
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
        public Button SlideButton => slideButton;
        public ToggleButton StepBox => stepBox;
        public ToggleButton SnapBox => snapBox;

        /// <summary>Whether the roll shows the notes the song keeps (what the Session View's clip plays) and edits them on the song's history.</summary>
        public bool IsSongLane => source != null;

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
            roll.Ghosts = anchor == null ? null : SongNotes.Ghosts(core, clip, anchor.Value, otherTracks: ghostBox.SelectedIndex == 2);
        }

        // The lane under the roll: the velocity of the notes, or one controller of the MIDI column (a machine whose notes are in its tracks has none).
        void FillLower()
        {
            settingLower = true;
            lowerBox.Items.Clear();
            lowerBox.Items.Add("Velocity");
            if (sampleContext != null) lowerBox.Items.Add("Sample");
            if (controllers != null)
            {
                var ids = controllers.Present().Union(ControllerPoint.Usual).OrderBy(c => c == ControllerPoint.PitchBend ? 999 : c).ToList();
                foreach (int c in ids) lowerBox.Items.Add(ControllerPoint.NameOf(c));
                lowerIds = ids;
            }
            else
                lowerIds = new System.Collections.Generic.List<int>();
            int first = sampleContext != null ? 2 : 1;
            int at = lowerController == SampleLaneId ? (sampleContext != null ? 1 : 0) : lowerController < 0 ? 0 : lowerIds.IndexOf(lowerController) + first;
            if (lowerController >= 0 && !lowerIds.Contains(lowerController)) at = 0;
            lowerBox.SelectedIndex = at > 0 ? at : 0;
            if (at <= 0) lowerController = -1;
            settingLower = false;
        }

        System.Collections.Generic.List<int> lowerIds = new System.Collections.Generic.List<int>();
        bool lowerChosen;

        void ChooseLower()
        {
            int i = lowerBox.SelectedIndex;
            int first = sampleContext != null ? 2 : 1;
            lowerChosen = true;
            lowerController = i == 1 && sampleContext != null ? SampleLaneId : i < first || i - first >= lowerIds.Count ? -1 : lowerIds[i - first];
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
            if (lane != null) lane.IsVisible = ctrl == null && lowerController != SampleLaneId;
            if (sampleLane != null) sampleLane.IsVisible = lowerController == SampleLaneId && sampleContext != null;
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
            picker.Close();
            if (roll != null) { roll.Changed -= UpdateSummary; roll.MapChanged -= OnMapChanged; roll.PickerRequested -= OnPickerRequested; }
            if (source != null) source.Changed -= OnSourceEdited;
            ClearFurniture();
            slideButton.IsEnabled = false;
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
            int beat = ClipEvent.TimeBase * 4;
            roll.Grid = gridBox.SelectedIndex switch
            {
                1 => beat,
                2 => roll.BarTime,
                3 => 1,
                _ => rowTime,
            };
            axis?.Raise();
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
            sampleLane?.InvalidateVisual();
            ctrl?.InvalidateVisual();
            PlaceHead();
            axis?.Raise();
        }

        void UpdateViewport()
        {
            if (roll == null) return;
            FitRows();
            roll.Viewport = new Rect(scroll.Offset.X, scroll.Offset.Y, scroll.Viewport.Width, scroll.Viewport.Height);
            roll.InvalidateVisual();
            if (ruler != null) { ruler.OffsetX = scroll.Offset.X; ruler.InvalidateVisual(); }
            if (lane != null) lane.OffsetX = scroll.Offset.X;
            if (sampleLane != null) sampleLane.OffsetX = scroll.Offset.X;
            if (ctrl != null) ctrl.OffsetX = scroll.Offset.X;
            PlaceHead();
            axis?.Raise();
        }

        /// <summary>Follows what the Tracker (or an undo, or the agent) changed.</summary>
        internal void Poll()
        {
            if (envelopes != null && IsEffectivelyVisible) envelopes.Refresh();
            if (roll == null || !IsEffectivelyVisible) return;       // a roll that isn't shown doesn't read the clip
            if (!source.IsLive)
            {
                Show(clip);
                return;
            }
            // TM1: the machine's Mode changed (a sample per note, or not): the roll is made again for the notes' other store.
            if (MachinePlaysPerNote() != (sampleContext != null))
            {
                var again = clip;
                clip = null;
                Show(again);
                return;
            }
            if (sampleContext != null && sampleContext.Refresh())
            {
                roll.InvalidateVisual();
                sampleLane?.InvalidateVisual();
                picker.Reload();
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
            foreach (var n in notes) sum = sum * 31 + n.Time * 7 + n.Duration * 3 + n.Note * 131 + n.Velocity + n.Sample * 17 + n.Offset * 29 + n.Tune * 37;
            return (notes.Count, sum);
        }

        void UpdateSummary()
        {
            if (roll == null) return;
            int count = roll.Notes.Count, sel = roll.Selection.Count;
            slideButton.IsEnabled = roll.CanSlide && sel > 0;
            summary.Text = roll.Refusal is string why ? why
                : roll.Source.WhyNotEditable is string waiting ? waiting
                : count == 0 ? "No notes yet: double-click to draw one."
                : sel == 0 ? $"{count} note{(count == 1 ? "" : "s")}"
                : $"{sel} of {count} selected" + (sel == 1 ? $" · {ClipNotes.Name(roll.Notes[roll.Selection.First()].Note)}, velocity {roll.Notes[roll.Selection.First()].Velocity}" + SampleSummary(roll.Notes[roll.Selection.First()]) : "");
            seen = Signature();
            lane?.InvalidateVisual();
            sampleLane?.InvalidateVisual();
            if (stepBox.IsChecked == true) roll.InvalidateVisual();
        }

        // ---- TM1: a sample per note

        bool MachinePlaysPerNote() => clip?.Machine is MachineCore { ManagedMachine: INoteSampleProvider { PlaysSamplePerNote: true } };

        bool PerNoteNow() => clip?.Machine is MachineCore { ManagedMachine: INoteSampleProvider { PlaysSamplePerNote: true } } && source is TrackerColumnNotes { PerNoteSamples: true };

        /// <summary>When the machine plays a sample per note and the notes carry one: the roll gets the samples (chips, picker, colour), the Sample lane is offered.</summary>
        void SetUpSamples(IClip p)
        {
            picker.Close();
            sampleContext = null;
            colourBox.IsVisible = false;
            if (PerNoteNow() && p.Machine is MachineCore { ManagedMachine: INoteSampleProvider provider })
            {
                sampleContext = new NoteSampleContext(provider) { ColourBySample = colourBox.IsChecked == true };
                roll.Samples = sampleContext;
                roll.RowHeight = 30;
                roll.PickerRequested += OnPickerRequested;
                colourBox.IsVisible = true;
                if (!lowerChosen) lowerController = SampleLaneId;      // the lane to look at when notes name their samples
            }
            else if (lowerController == SampleLaneId) lowerController = -1;
        }

        string SampleSummary(ClipNote n) =>
            sampleContext == null ? "" : n.Sample > 0 ? $", sample {SampleColours.Label(n.Sample)} {sampleContext.NameOf(n.Sample)}" : ", sample: key map";

        /// <summary>The picker, over the roll at the chip (or the lane's chip) it was asked from.</summary>
        void OnPickerRequested(Control from, Rect anchor)
        {
            if (sampleContext == null) return;
            sampleContext.Refresh();
            var at = from.TranslatePoint(anchor.BottomLeft, body) ?? new Point(40, 40);
            double w = SamplePicker.PickerWidth;
            double maxList = Math.Max(120, Math.Min(280, body.Bounds.Height - 190));
            picker.MaxListHeight = maxList;
            picker.Open(roll);
            picker.Measure(new Size(w, double.PositiveInfinity));
            double h = picker.DesiredSize.Height;
            double x = Math.Clamp(at.X, 4, Math.Max(4, body.Bounds.Width - w - 4));
            double y = at.Y + 4;
            if (y + h > body.Bounds.Height - 4)
            {
                // No room below the chip: above it, else as low as it fits.
                var above = from.TranslatePoint(anchor.TopLeft, body) ?? at;
                y = above.Y - h - 4 >= 4 ? above.Y - h - 4 : Math.Max(4, body.Bounds.Height - h - 4);
            }
            picker.Margin = new Thickness(x, Math.Max(4, y), 0, 0);
        }

        /// <summary>The picker for the selected notes, as S asks (tests).</summary>
        public SamplePicker Picker => picker;

        /// <summary>The Sample lane under the roll, when it is the one shown (tests).</summary>
        internal SampleLane Samples => sampleLane;

        /// <summary>The samples the notes can name, when the machine plays one per note (tests); null otherwise.</summary>
        internal NoteSampleContext SampleContext => sampleContext;

        public ToggleButton ColourBox => colourBox;

        /// <summary>Row height to the room there is: more of the picture when a fold leaves few rows (as the canvas draws it), never under what a chip needs.</summary>
        void FitRows()
        {
            if (roll == null || !roll.PerNoteSamples) return;
            double h = scroll.Viewport.Height;
            int n = roll.Rows?.Count ?? 0;
            double want = n > 0 && h > 0 ? Math.Clamp(Math.Floor(h / n), 30, 56) : 30;
            if (Math.Abs(want - roll.RowHeight) < 0.5) return;
            roll.RowHeight = want;
            roll.InvalidateMeasure();
        }

        static bool IsInside(Visual v, Visual ancestor)
        {
            for (; v != null; v = v.GetVisualParent())
                if (ReferenceEquals(v, ancestor)) return true;
            return false;
        }

        void OpenSampler()
        {
            if (clip?.Machine is not IMachine machine) return;
            ReBuzz.Studio.Views.StudioSelection.For(core).SelectMachine(machine, this);
            ReBuzz.Studio.Views.Mixer.MixerView.DeviceOpener?.Invoke(machine);
        }

        /// <summary>Loads a sample file into the song's first empty wave slot (one undo step) for the picker: its row is there when it is done.</summary>
        async System.Threading.Tasks.Task LoadSampleAsync()
        {
            System.Collections.Generic.IReadOnlyList<string> files = LoadChooser?.Invoke();
            if (files == null && LoadChooser == null && TopLevel.GetTopLevel(this)?.StorageProvider is { } storage)
            {
                var picked = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
                {
                    Title = "Load a sample",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new Avalonia.Platform.Storage.FilePickerFileType("Audio files") { Patterns = ReBuzz.Agent.WaveEdits.Extensions.Select(e => "*" + e).ToList() } },
                });
                files = picked.Select(f => f.TryGetLocalPath()).Where(f => f != null).ToList();
            }
            if (files == null || files.Count == 0) return;
            string refusal = ReBuzz.Agent.WaveEdits.Load(core, files[0], -1, null, false, out _);
            summary.Text = refusal ?? $"Loaded {System.IO.Path.GetFileName(files[0])} as a new sample.";
            sampleContext?.Refresh();
            picker.Reload();
        }

        /// <summary>Tests choose the file the picker's Load sample… loads (null: the system's file dialog).</summary>
        internal Func<System.Collections.Generic.IReadOnlyList<string>> LoadChooser { get; set; }

        // The Edit commands, when the keyboard is in the roll.
        internal bool Delete() { if (envelopes?.OwnsFocus == true) return envelopes.Delete(); if (roll == null) return false; roll.DeleteSelected(); return true; }
        internal bool SelectAll() { if (envelopes?.OwnsFocus == true) return envelopes.SelectAll(); if (roll == null) return false; roll.SelectAll(); return true; }
        internal bool DeselectAll() { if (envelopes?.OwnsFocus == true) return envelopes.DeselectAll(); if (roll == null) return false; roll.DeselectAll(); return true; }
        internal bool Duplicate() { if (roll == null) return false; roll.DuplicateSelected(); return true; }
        internal bool Cut() { if (envelopes?.OwnsFocus == true) return envelopes.Cut(); if (roll == null) return false; roll.CutSelected(); return true; }
        internal bool Copy() { if (envelopes?.OwnsFocus == true) return envelopes.Copy(); if (roll == null) return false; roll.CopySelected(); return true; }
        internal bool Paste() { if (envelopes?.OwnsFocus == true) return envelopes.Paste(); if (roll == null) return false; roll.Paste(); return true; }

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
            if (sampleContext != null)
            {
                sampleLane = new SampleLane(roll) { IsVisible = false, OffsetX = scroll.Offset.X };
                Grid.SetRow(sampleLane, 2);
                rollGrid.Children.Add(sampleLane);
            }
            source.Changed += OnSourceEdited;
            shownPlay = int.MinValue;
            FillLower();
            ShowLower();
        }

        void ClearFurniture()
        {
            if (ruler != null) { ruler.LoopSet -= SetLoop; rollGrid.Children.Remove(ruler); }
            if (lane != null) rollGrid.Children.Remove(lane);
            if (sampleLane != null) { rollGrid.Children.Remove(sampleLane); sampleLane = null; }
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

        /// <summary>The song's tick where this clip's first tick lies: its event that plays now, else its first in the song; null if it isn't in the song.</summary>
        internal int? Anchor() => AnchorOf(core, clip);

        /// <summary>Anchor for any clip (the Tracker's bar rules use it too).</summary>
        internal static int? AnchorOf(ReBuzzCore core, IClip clip)
        {
            var song = core.Song;
            if (song == null || clip == null) return null;
            int? first = null;
            int pos = core.SongCore?.PlayPosition ?? 0;
            foreach (var seq in song.Sequences)
            {
                if (seq.Machine != clip.Machine) continue;
                foreach (var kv in seq.Events)
                {
                    var e = kv.Value;
                    if (e.Type != SequenceEventType.PlayClip || !ReferenceEquals(e.Clip, clip)) continue;
                    int a = kv.Key - e.PlayedOffset;
                    if (pos >= kv.Key && pos < kv.Key + Math.Max(1, e.Span)) return a;
                    if (first == null || a < first) first = a;
                }
            }
            return first;
        }

        /// <summary>The bar length in ticks the song's list has where the clip lies (the first bar's when it isn't in the song).</summary>
        internal int BarLength() => BarLengthOf(core, clip);

        internal static int BarLengthOf(ReBuzzCore core, IClip clip) => Math.Max(1, core.SongMeter.GetBarLengthAt(Math.Max(0, AnchorOf(core, clip) ?? 0)));

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
            axis?.Raise();
        }

        /// <summary>The loop brace was dragged: the song's loop is these ticks of the clip, one step on the song's history.</summary>
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

        /// <summary>Moves the playhead with the clip's own play position (Tracker's row marker), and the viewer with it when Follow is on.</summary>
        internal void UpdatePlayhead()
        {
            if (clip == null) return;
            if (roll == null)
            {
                // No roll (the machine takes no notes): the envelope lanes draw the play position.
                int at = clip.PlayPosition;
                if (at != shownPlay) { shownPlay = at; envelopes?.Invalidate(); }
                return;
            }
            int pos = clip.PlayPosition;
            if (pos == shownPlay) return;
            shownPlay = pos;
            int time = pos == int.MinValue ? -1 : pos;
            roll.PlayheadTime = time;
            envelopes?.Invalidate();
            if (ruler != null) { ruler.Playhead = time; ruler.InvalidateVisual(); }
            if (time >= 0 && followBox.IsChecked == true) Follow(time);
            PlaceHead();
        }

        void Follow(int time)
        {
            double x = roll.X(time);
            double left = scroll.Offset.X + roll.KeyColumn, right = scroll.Offset.X + scroll.Viewport.Width - 24;
            if (scroll.Viewport.Width <= 0 || (x >= left && x <= right)) return;
            scroll.Offset = new Vector(Math.Max(0, x - roll.KeyColumn - 24), scroll.Offset.Y);
        }

        void PlaceHead()
        {
            if (roll == null) { playhead.LineX = -1; return; }
            playhead.Left = roll.KeyColumn;
            playhead.LineX = roll.PlayheadTime < 0 ? -1 : roll.X(roll.PlayheadTime) - scroll.Offset.X;
            playhead.InvalidateVisual();
        }

        /// <summary>Gives the keyboard to the roll.</summary>
        internal void FocusRoll() => roll?.Focus();

        /// <summary>
        /// The roll's time axis for the envelope lanes (EV1): its zoom, horizontal scroll, grid, bars and loop. Without a roll (a machine that takes no
        /// notes) the axis has a zoom and a scroll of its own, and the ruler of the lanes.
        /// </summary>
        sealed class RollAxis : IEnvelopeAxis
        {
            readonly TrackerRoll host;
            double ownWidth = 24, ownOffset;

            public RollAxis(TrackerRoll host) => this.host = host;

            public event Action Changed;

            public void Raise() => Changed?.Invoke();

            PianoRoll Roll => host.roll;

            public double Gutter => Roll?.KeyColumn ?? PianoRoll.KeysWidth;

            public double X(int time) => Roll != null ? Roll.X(time) - host.scroll.Offset.X : Gutter + time / (double)ClipNotes.TimeBase * ownWidth - ownOffset;

            public int TimeAt(double x) => Roll != null
                ? Roll.TimeAt(x + host.scroll.Offset.X)
                : (int)Math.Round(Math.Max(0, x + ownOffset - Gutter) / ownWidth * ClipNotes.TimeBase);

            public int GridUnits => Roll?.Grid ?? ClipNotes.TimeBase;

            public int BeatUnits => Math.Max(1, host.core.TPB) * ClipNotes.TimeBase;

            public int BarUnits => Roll != null ? Roll.BarTime : Math.Max(1, host.clip == null ? 1 : BarLengthOf(host.core, host.clip)) * ClipNotes.TimeBase;

            public (int From, int To)? Loop =>
                host.ruler is { LoopStartTick: >= 0 } r && r.LoopEndTick > r.LoopStartTick ? (r.LoopStartTick * ClipNotes.TimeBase, r.LoopEndTick * ClipNotes.TimeBase) : null;

            public int Playhead => Roll != null ? Roll.PlayheadTime : host.clip is { } p && p.PlayPosition != int.MinValue ? p.PlayPosition : -1;

            public void Zoom(double factor)
            {
                if (Roll != null) host.Zoom(factor);
                else
                {
                    ownWidth = Math.Clamp(ownWidth * factor, 3, 160);
                    Raise();
                }
            }

            public void ScrollBy(double pixels)
            {
                if (Roll != null) host.scroll.Offset = new Vector(Math.Max(0, host.scroll.Offset.X + pixels), host.scroll.Offset.Y);
                else
                {
                    ownOffset = Math.Max(0, ownOffset + pixels);
                    Raise();
                }
            }
        }
    }
}
