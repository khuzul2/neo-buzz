// Fork (docs/specs/studio-mvp.md S4): the Sequence tab, the horizontal sequence editor
// (ReBuzzGUI/ModernSequenceEditorHorizontal, studio-mvp.md §3.5) on Avalonia. The layout of
// SequenceEditor.xaml: the code-drawn timeline (SequenceCanvas) with the add-track button and the
// bar length in its corner, scroll bars, the zoom slider and the pattern list on the right. The
// keys, the context menu and the edit commands of SequenceEditor.xaml.cs are here; the edits go
// through SequenceEditing onto the song's undo history.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Sequence
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The "Arrangement" tab (named "Sequence" before P3a; ShowView still takes that name).</summary>
    internal sealed class SequenceTab : IStudioView
    {
        public string Title => "Arrangement";

        public Control Create(StudioHost host) => new SequenceView(host.Core);

        /// <summary>
        /// A pattern was chosen for editing in the sequence editor (a double click on its block, or
        /// Return): the pattern editor (S6) shows it. The engine's pattern editor pattern is set too.
        /// </summary>
        public static event Action<IPattern> PatternChosen;

        internal static void RaisePatternChosen(IPattern p) => PatternChosen?.Invoke(p);
    }

    internal sealed class SequenceView : UserControl, IStudioEditTarget
    {
        /// <summary>The bar lengths the corner's box offers (cbSteps).</summary>
        static readonly int[] Steps = new[] { 1, 2, 4 }.Concat(Enumerable.Range(0, 22).Select(i => 8 + 4 * i)).Concat(new[] { 128, 192, 256 }).ToArray();

        readonly ReBuzzCore core;
        readonly SequenceSongState state;
        readonly Shell.EmptyState emptyState;
        readonly DispatcherTimer emptyTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        readonly ScrollBar horizontal = new ScrollBar { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, AllowAutoHide = true, Margin = new Thickness(SequenceGeometry.HeaderWidth, 0, 0, 0) };
        readonly ScrollBar vertical = new ScrollBar { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, AllowAutoHide = true, Margin = new Thickness(0, SequenceGeometry.RulerHeight, 0, 0) };
        readonly Slider zoom = new Slider
        {
            Minimum = SequenceGeometry.MinTickWidth, Maximum = SequenceGeometry.MaxTickWidth, TickFrequency = SequenceGeometry.TickWidthStep,
            IsSnapToTickEnabled = true, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        readonly ComboBox steps = new ComboBox { Width = 58, Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Button addTrack = new Button { Content = "+", Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly ToggleButton follow = new ToggleButton { Content = "Follow", Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center, IsChecked = true };
        readonly ListBox patternList = new ListBox
        {
            FontSize = 12, BorderThickness = new Thickness(1),
        };
        readonly List<(char Char, IPattern Pattern)> patterns = new List<(char, IPattern)>();
        bool syncing;
        bool attached;
        DockPanel right, root;
        readonly List<Style> listStates = new List<Style>();

        /// <summary>
        /// Fork (ux-overhaul.md §3.3): the theme's colours for the view's own controls (the canvas draws itself): the pattern
        /// list and the corner's buttons. The dark theme's are the tokens'; Classic Buzz's are the editor's light ones.
        /// </summary>
        void ApplyLook()
        {
            var look = StudioTheme.Sequence;
            root.Background = right.Background = look.Background;
            patternList.Background = Shell.Design.StudioTheme.Solid(look.PatternList);
            patternList.Foreground = look.ListText;
            patternList.BorderBrush = look.ListBorder;
            foreach (var c in new TemplatedControl[] { addTrack, steps, follow })
            {
                c.Background = look.ControlBack;
                c.Foreground = look.ControlText;
                c.BorderBrush = look.ControlBorder;
                c.BorderThickness = new Thickness(1);
            }
            foreach (var style in listStates) patternList.Styles.Remove(style);
            listStates.Clear();
            ListStates(patternList, look, listStates);
        }

        /// <summary>Fork (ux-overhaul.md P2c): the cursor's track (and pattern) is the studio's selection; a track another view selected is the cursor's.</summary>
        void OnCursorChanged()
        {
            RefreshPatternList();
            if (Canvas.FollowingSelection || Canvas.CursorSequence?.Machine is not { } machine) return;
            StudioSelection.For(core).Set(machine, Canvas.CursorPattern, this);
        }

        void FollowSelection(object source)
        {
            if (source == this) return;
            if (StudioSelection.For(core).Machine is { } machine) Canvas.FollowTrackOf(machine);
        }

        void OnThemeChanged() => Dispatcher.UIThread.Post(() => { if (root != null) ApplyLook(); });

        public SequenceView(ReBuzzCore core)
        {
            this.core = core;
            state = SequenceSongState.For(core);
            Canvas = new SequenceCanvas(core, state);
            Focusable = true;

            patternList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 2)),
                    new Setter(MinHeightProperty, 0.0),
                },
            });
            foreach (var s in Steps) steps.Items.Add(s);
            steps.SelectedItem = state.TimeSignatures.GetBarLengthAt(0);
            ToolTip.SetTip(steps, "Bar length (ticks): the cursor's and the selection's step");
            ToolTip.SetTip(addTrack, "Add a track");
            addTrack.Classes.Add("icon");
            Avalonia.Automation.AutomationProperties.SetName(addTrack, "Add track");
            ToolTip.SetTip(follow, "Follow the play position");
            ToolTip.SetTip(zoom, "Zoom (Ctrl/Command + wheel)");

            // The corner above the track headers: add track, the bar length, follow.
            var corner = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Width = SequenceGeometry.HeaderWidth,
                Height = SequenceGeometry.RulerHeight,
                Children = { addTrack, steps, follow },
            };
            emptyState = new Shell.EmptyState(core) { Margin = new Thickness(SequenceGeometry.HeaderWidth, SequenceGeometry.RulerHeight, 0, 0) };
            var timeline = new Panel { Children = { Canvas, Canvas.Overlay, emptyState, horizontal, vertical, corner } };

            right = new DockPanel { Width = 120 };
            var zoomRow = new Border { Height = SequenceGeometry.RulerHeight, Child = zoom };
            DockPanel.SetDock(zoomRow, Dock.Top);
            right.Children.Add(zoomRow);
            right.Children.Add(patternList);

            root = new DockPanel();
            DockPanel.SetDock(right, Dock.Right);
            root.Children.Add(right);
            root.Children.Add(timeline);
            Content = root;
            ApplyLook();

            zoom.Value = state.Zoom;
            Canvas.ViewChanged += Sync;
            Canvas.CursorChanged += OnCursorChanged;
            Canvas.TracksChanged += RefreshPatternList;
            Canvas.TracksChanged += UpdateEmpty;
            emptyTimer.Tick += (s, e) => UpdateEmpty();
            Canvas.PatternDoubleClicked += p => ChoosePattern(p, true);
            Canvas.MenuRequested += p => OpenContextMenu(Canvas);
            horizontal.ValueChanged += (s, e) => { if (!syncing) Canvas.ScrollTo(e.NewValue, Canvas.Geometry.ScrollY); };
            vertical.ValueChanged += (s, e) => { if (!syncing) Canvas.ScrollTo(Canvas.Geometry.ScrollX, e.NewValue); };
            zoom.ValueChanged += (s, e) => { if (!syncing) Canvas.SetZoom(e.NewValue, null); };
            zoom.AddHandler(PointerPressedEvent, (s, e) =>
            {
                // A right click puts the zoom back (WPF: zoomSlider.Value = 2).
                if (e.GetCurrentPoint(zoom).Properties.IsRightButtonPressed)
                {
                    Canvas.SetZoom(SequenceGeometry.DefaultTickWidth, null);
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
            steps.SelectionChanged += (s, e) =>
            {
                if (syncing || steps.SelectedItem is not int step || step == state.TimeSignatures.GetBarLengthAt(0)) return;
                Canvas.Edits.SetTimeSignature(0, step);
                Canvas.Focus();
            };
            follow.IsCheckedChanged += (s, e) => Canvas.Follow = follow.IsChecked == true;
            addTrack.Click += (s, e) => OpenAddTrackMenu();
            patternList.DoubleTapped += (s, e) => PlaceListedPattern();

            AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AddHandler(TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        }

        public SequenceCanvas Canvas { get; }
        public Slider ZoomSlider => zoom;
        public ComboBox StepsBox => steps;
        public ListBox PatternList => patternList;
        /// <summary>The list's own background (its items' states draw on it).</summary>
        internal static Color PatternListBackground => StudioTheme.Sequence.PatternList;
        public Button AddTrackButton => addTrack;

        /// <summary>The menu a right click (or the add-track button) opened last (tests click through it).</summary>
        public ContextMenu OpenMenu { get; private set; }

        /// <summary>A pattern was chosen for editing here (as <see cref="SequenceTab.PatternChosen"/>).</summary>
        public event Action<IPattern> PatternChosen;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            StudioTheme.Changed += OnThemeChanged;
            StudioSelection.For(core).Changed += FollowSelection;
            FollowSelection(null);                  // shown again: on the selection the other views made meanwhile
            ApplyLook();
            state.Changed += OnStateChanged;
            OnStateChanged();
            RefreshPatternList();
            UpdateEmpty();
            emptyTimer.Start();
            // Shown (its tab selected): the timeline takes the keys, unless something in the view has them.
            Dispatcher.UIThread.Post(() => { if (attached && !IsKeyboardFocusWithin) Canvas.Focus(); });
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            attached = false;
            StudioTheme.Changed -= OnThemeChanged;
            StudioSelection.For(core).Changed -= FollowSelection;
            state.Changed -= OnStateChanged;
            emptyTimer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>The empty state (P3i): no tracks yet, or tracks with nothing arranged.</summary>
        public Shell.EmptyState EmptyState => emptyState;

        internal void RefreshEmpty() => UpdateEmpty();

        void UpdateEmpty()
        {
            var sequences = core.Song.Sequences;
            if (sequences.Count == 0)
            {
                bool bare = Shell.EmptyState.SongIsEmpty(core);
                emptyState.Update(true, bare ? "tracks:bare" : "tracks:gear", "No tracks yet",
                    bare ? "A track plays one machine. Start with a sound, or search every machine."
                         : "A track plays a generator, and the song has none. Add one to get a track.",
                    Shell.EmptyState.AddMachineStep(), bare);
                return;
            }
            bool arranged = sequences.Any(s => s.Events.Count > 0);
            emptyState.Update(!arranged, "arrange", "Nothing is arranged yet",
                "Put a pattern on a track: pick a row and press the pattern's key, or double-click it in the list on the right. Or launch clips in the Session View.",
                new (string, Action)[] { ("New pattern here", () => { Canvas.Edits.CreatePatterns(Math.Max(0, Canvas.CursorRow), Math.Max(0, Canvas.CursorTime), 1, SequenceCanvas.NewPatternLength, false); Canvas.Focus(); }) }, false);
        }

        void OnStateChanged()
        {
            syncing = true;
            try
            {
                steps.SelectedItem = state.TimeSignatures.GetBarLengthAt(0);
                zoom.Value = state.Zoom;
            }
            finally
            {
                syncing = false;
            }
        }

        /// <summary>The scroll bars and the slider follow the timeline.</summary>
        void Sync()
        {
            syncing = true;
            try
            {
                var geo = Canvas.Geometry;
                var extent = Canvas.Extent;
                double vw = Math.Max(1, Canvas.Bounds.Width - SequenceGeometry.HeaderWidth), vh = Math.Max(1, Canvas.Bounds.Height - SequenceGeometry.RulerHeight);
                horizontal.Maximum = Math.Max(0, extent.Width - vw);
                horizontal.ViewportSize = vw;
                horizontal.Value = geo.ScrollX;
                vertical.Maximum = Math.Max(0, extent.Height - vh);
                vertical.ViewportSize = vh;
                vertical.Value = geo.ScrollY;
                zoom.Value = geo.TickWidth;
            }
            finally
            {
                syncing = false;
            }
        }

        // ------------------------------------------------------------------ the pattern list

        /// <summary>Every state of the list's items readable in the theme (4.5:1 and more): text on the list, on the pointer's tint, on the selection.</summary>
        internal static void ListStates(ListBox list, SequenceLook look, List<Style> added = null)
        {
            void State(Func<Selector, Selector> state, IBrush background, IBrush foreground)
            {
                var style = new Style(x => state(x.OfType<ListBoxItem>()).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.BackgroundProperty, background),
                        new Setter(ContentPresenter.ForegroundProperty, foreground),
                    },
                };
                list.Styles.Add(style);
                added?.Add(style);
            }
            var white = ReBuzz.Studio.Shell.Design.StudioTokens.White;
            State(x => x, Brushes.Transparent, look.ListText);
            State(x => x.Class(":pointerover"), look.ListHover, look.ListHoverText);
            State(x => x.Class(":pressed"), look.ListPressed, look.ListHoverText);
            State(x => x.Class(":selected"), look.ListSelected, white);
            State(x => x.Class(":selected").Class(":focus"), look.ListSelected, white);
            State(x => x.Class(":selected").Class(":pointerover"), look.ListSelectedHover, white);
            State(x => x.Class(":selected").Class(":pressed"), look.ListSelectedHover, white);
        }

        /// <summary>The n-th pattern's key: 0–9, then a–z, then A–Z (TrackHeaderControl.UpdatePatternList).</summary>
        static char KeyOf(int i) => i < 10 ? (char)('0' + i) : i < 36 ? (char)('a' + i - 10) : (char)('A' + i - 36);

        /// <summary>The cursor track's patterns by name, each with the key that places it.</summary>
        public IReadOnlyList<(char Char, IPattern Pattern)> Patterns => patterns;

        void RefreshPatternList()
        {
            var machine = Canvas.CursorSequence?.Machine;
            var now = machine?.Patterns.OrderBy(p => p.Name, StringComparer.Ordinal).Select((p, i) => (KeyOf(i), p)).ToList() ?? new List<(char, IPattern)>();
            if (!now.SequenceEqual(patterns))
            {
                patterns.Clear();
                patterns.AddRange(now);
                patternList.ItemsSource = patterns.Select(x => $"{x.Char}. {x.Pattern.Name}").ToList();
            }
            int index = patterns.FindIndex(x => x.Pattern == Canvas.CursorPattern);
            if (patternList.SelectedIndex != index) patternList.SelectedIndex = index;
        }

        /// <summary>A double click in the list places that pattern at the cursor (patternListBox).</summary>
        void PlaceListedPattern()
        {
            int i = patternList.SelectedIndex;
            if (i < 0 || i >= patterns.Count) return;
            Place(patterns[i].Pattern);
            Canvas.Focus();
        }

        /// <summary>Places a pattern at the cursor and moves past it (typing its key).</summary>
        public void Place(IPattern p)
        {
            if (p == null || Canvas.CursorSequence == null) return;
            Canvas.Edits.PlacePattern(Canvas.CursorRow, Canvas.CursorTime, p);
            Canvas.MoveCursor(Canvas.CursorTime + Math.Max(1, p.Length), Canvas.CursorRow);
        }

        // ------------------------------------------------------------------ edit commands

        // The shell's Edit menu (IStudioEditTarget, studio-mvp.md §3.10): sequence edits are on the
        // song's history, so Undo and Redo fall through to it; Cut, Copy, Paste and Delete are the
        // editor's, on the selection (or the block under the cursor) and at the cursor.

        public bool Undo() => false;

        public bool Redo() => false;

        /// <summary>The selection, else the block under the cursor (WPF needs a selection).</summary>
        SequenceRect? Target()
        {
            if (Canvas.Selection.NotEmpty) return Canvas.Selection.Rect(state.TimeSignatures);
            var (time, e) = Canvas.EventAt(Canvas.CursorTime, Canvas.CursorRow);
            if (e == null) return null;
            int end = state.TimeSignatures.Snap(time + SequenceGeometry.SpanOf(e) - 1, int.MaxValue);
            return new SequenceRect(time, Canvas.CursorRow, end + state.TimeSignatures.GetBarLengthAt(end) - time, 1);
        }

        public bool Cut()
        {
            if (Target() is not { } r) return false;
            Canvas.Edits.Cut(r);
            Canvas.Selection.Kill();
            Canvas.Changed();
            return true;
        }

        public bool Copy()
        {
            if (Target() is not { } r) return false;
            Canvas.Edits.Copy(r);
            return true;
        }

        public bool Paste()
        {
            var edits = Canvas.Edits;
            if (!edits.CanPaste || Canvas.CursorSequence == null) return false;
            edits.Paste(Canvas.CursorTime, Canvas.CursorRow);
            Canvas.Selection.Kill();
            Canvas.Changed();
            return true;
        }

        /// <summary>Clears the selection (or the block under the cursor).</summary>
        public bool Delete()
        {
            if (Target() is not { } r) return false;
            Canvas.Edits.Clear(r);
            Canvas.Selection.Kill();
            Canvas.Changed();
            return true;
        }

        /// <summary>Duplicate (⌘D): the selection (or the block under the cursor) again right after itself; the selection follows it.</summary>
        public bool Duplicate()
        {
            if (Target() is not { } r) return false;
            Canvas.Edits.Duplicate(r);
            if (Canvas.Selection.NotEmpty) Canvas.Selection.Kill();
            Canvas.MoveCursor(r.End, r.Row);
            Canvas.Changed();
            return true;
        }

        /// <summary>Deselect All (⇧⌘A, Esc).</summary>
        public bool DeselectAll()
        {
            if (!Canvas.Selection.NotEmpty) return false;
            Canvas.Selection.Kill();
            Canvas.Changed();
            return true;
        }

        /// <summary>Rename (⌘R): the track's machine.</summary>
        public bool Rename()
        {
            if (Canvas.CursorSequence?.Machine == null) return false;
            RenameMachine();
            return true;
        }

        /// <summary>
        /// A pattern for editing (SelectPatternCommand): the engine's pattern editor gets it, and
        /// <see cref="SequenceTab.PatternChosen"/> tells the pattern editor's tab.
        /// </summary>
        public void ChoosePattern(IPattern p, bool activate)
        {
            if (p == null) return;
            try
            {
                core.SetPatternEditorPattern(p);
            }
            catch (Exception e)
            {
                core.DCWriteLine("Sequence editor: " + e.Message);
            }
            if (!activate) return;
            PatternChosen?.Invoke(p);
            SequenceTab.RaisePatternChosen(p);
        }

        public void SetLoopStart() => Canvas.Edits.SetLoopStart(Canvas.CursorTime);

        public void SetEnd() => Canvas.Edits.SetEnd(Canvas.CursorBottomTime);

        public void InsertAll() => Canvas.Edits.InsertOrDeleteAll(Canvas.CursorTime, Canvas.CursorSpan, true);

        public void DeleteAll() => Canvas.Edits.InsertOrDeleteAll(Canvas.CursorTime, Canvas.CursorSpan, false);

        public void DeleteTrack()
        {
            Canvas.Edits.DeleteTrack(Canvas.CursorRow);
            Canvas.MoveCursor(Canvas.CursorTime, 0);
        }

        public void MoveTrack(int delta)
        {
            if (Canvas.Edits.MoveTrack(Canvas.CursorRow, delta)) Canvas.MoveCursorDelta(0, delta, int.MaxValue, false);
        }

        public void SetColour(int index)
        {
            var p = Canvas.CursorPattern;
            if (p == null) return;
            if (index < 0) state.Remove(p);
            else state.Set(p, index);
        }

        /// <summary>
        /// Set Time Signature... (Ctrl+T): the bar length from the cursor on, asked for (StepEditWindow)
        /// as a time signature ("3/4", "7/8") or in ticks ("12"), checked as it is typed: "4/4" was
        /// silently ignored (gallery mac-336).
        /// </summary>
        public async Task AskTimeSignature()
        {
            int time = Canvas.CursorTime;
            int tpb = Math.Max(1, core.TPB);
            string answer = await Dialogs.AskText("Set Time Signature", TimeSignatureText(Canvas.CursorSpan, tpb),
                $"The bar's length from the cursor on: beats/note (4/4, 3/4, 7/8) or in ticks ({tpb} ticks a beat).",
                text => ParseBarLength(text, tpb, out _));
            if (answer != null && ParseBarLength(answer, tpb, out int step) == null) Canvas.Edits.SetTimeSignature(time, step);
            Canvas.Focus();
        }

        /// <summary>A bar length as a time signature where it is one (16 ticks at 4 a beat: "4/4"), else in ticks.</summary>
        internal static string TimeSignatureText(int ticks, int tpb) =>
            ticks % tpb == 0 ? $"{ticks / tpb}/4" : ticks * 2 % tpb == 0 ? $"{ticks * 2 / tpb}/8" : ticks.ToString();

        /// <summary>
        /// A bar length typed as beats/note ("3/4": three quarter notes; "7/8") or in ticks ("12"), at
        /// <paramref name="tpb"/> ticks per beat (a quarter note). Null when it is one (in
        /// <paramref name="ticks"/>), else what's wrong with it, to show under the box.
        /// </summary>
        internal static string ParseBarLength(string text, int tpb, out int ticks)
        {
            ticks = 0;
            text = text?.Trim() ?? "";
            var number = System.Globalization.NumberStyles.None;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (text.Length == 0) return "Type a time signature (4/4) or the bar's length in ticks.";
            int slash = text.IndexOf('/');
            if (slash < 0)
            {
                if (!int.TryParse(text, number, invariant, out ticks)) return $"\"{text}\" isn't a time signature (4/4) or a number of ticks.";
            }
            else
            {
                if (!int.TryParse(text[..slash].Trim(), number, invariant, out int beats) || !int.TryParse(text[(slash + 1)..].Trim(), number, invariant, out int note))
                    return $"\"{text}\" isn't a time signature: beats/note, as 4/4 or 7/8.";
                if (beats < 1) return "A bar has 1 beat or more (the number before the /).";
                if (note is not (1 or 2 or 4 or 8 or 16 or 32)) return "The note (after the /) is 1, 2, 4, 8, 16 or 32.";
                long length = (long)beats * tpb * 4;
                if (length % note != 0) return $"{beats}/{note} isn't a whole number of ticks at {tpb} ticks a beat: give the length in ticks, or raise ticks per beat.";
                ticks = (int)Math.Min(int.MaxValue, length / note);
            }
            if (ticks < 1 || ticks > 1024) return "A bar is 1 to 1024 ticks long.";
            return null;
        }

        void RenameMachine()
        {
            var m = Canvas.CursorSequence?.Machine;
            m?.ShowDialog(MachineDialog.Rename, 0, 0);
        }

        // ------------------------------------------------------------------ keys

        static bool IsMac => OperatingSystem.IsMacOS();

        /// <summary>Command on macOS does what Control does elsewhere.</summary>
        static KeyModifiers Normalize(KeyModifiers mods)
        {
            if (IsMac && mods.HasFlag(KeyModifiers.Meta)) mods = (mods & ~KeyModifiers.Meta) | KeyModifiers.Control;
            return mods;
        }

        bool TypingInBox(object source) => source is Visual v && (Find<TextBox>(v) != null || Find<ComboBox>(v) is ComboBox { IsDropDownOpen: true });

        static T Find<T>(Visual v) where T : class
        {
            for (var x = v; x != null; x = x.GetVisualParent()) if (x is T t) return t;
            return null;
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || TypingInBox(e.Source)) return;
            if (HandleKey(e.Key, Normalize(e.KeyModifiers))) e.Handled = true;
        }

        /// <summary>The editor's keys (SequenceEditor.xaml.cs PreviewKeyDown and InputBindings); Control is Command on a Mac.</summary>
        public bool HandleKey(Key key, KeyModifiers mods)
        {
            var c = Canvas;
            bool none = mods == KeyModifiers.None, shift = mods == KeyModifiers.Shift, ctrl = mods == KeyModifiers.Control;
            if (c.CursorSequence == null) return ctrl && key == Key.Return && OpenContextMenu(c);

            if (none || shift)
            {
                switch (key)
                {
                    case Key.Right: c.MoveCursorDelta(1, 0, int.MaxValue, shift); return true;
                    case Key.Left: c.MoveCursorDelta(-1, 0, int.MaxValue, shift); return true;
                    case Key.Down: c.MoveCursorDelta(0, 1, int.MaxValue, shift); return true;
                    case Key.Up: c.MoveCursorDelta(0, -1, int.MaxValue, shift); return true;
                    case Key.PageDown: c.MoveCursorDelta(0, 16, int.MaxValue, shift); return true;
                    case Key.PageUp: c.MoveCursorDelta(0, -16, int.MaxValue, shift); return true;
                    case Key.Home:
                        if (c.CursorRow > 0) c.MoveCursorDelta(0, -int.MaxValue, int.MaxValue, shift);
                        else c.MoveCursorDelta(-int.MaxValue, 0, int.MaxValue, shift);
                        return true;
                    case Key.End:
                        int last = state.TimeSignatures.Snap(int.MaxValue, Song.SongEnd);
                        if (c.CursorTime != last) c.MoveCursorDelta(int.MaxValue, 0, Song.SongEnd, shift);
                        else c.MoveCursorDelta(0, int.MaxValue, Song.SongEnd, shift);
                        return true;
                }
            }

            if (none)
            {
                switch (key)
                {
                    case Key.Insert:
                        c.Edits.InsertOrDelete(c.CursorRow, c.CursorTime, c.CursorSpan, true);
                        return true;
                    case Key.Delete:
                        // A selection is cleared; otherwise the bar at the cursor goes and what follows moves up (Buzz).
                        if (c.Selection.NotEmpty) return Delete();
                        c.Edits.InsertOrDelete(c.CursorRow, c.CursorTime, c.CursorSpan, false);
                        return true;
                    case Key.Back:
                        if (c.Selection.NotEmpty) return Delete();
                        int before = c.CursorTime;
                        c.MoveCursorDelta(-1, 0, int.MaxValue, false);
                        if (c.CursorTime < before) c.Edits.Clear(c.CursorRow, c.CursorTime, c.CursorSpan);
                        return true;
                    case Key.Return:
                        ChoosePattern(c.CursorPattern, true);
                        return true;
                    case Key.Escape:
                        c.Selection.Kill();
                        c.Changed();
                        return true;
                }
            }
            else if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && key is Key.Delete or Key.Back)
            {
                DeleteAll();
                return true;
            }
            else if (shift && key == Key.OemComma)
            {
                c.Edits.SetEvent(c.CursorRow, c.CursorTime, new SequenceEvent(SequenceEventType.Off));
                c.MoveCursorDelta(1, 0, int.MaxValue, false);
                return true;
            }
            else if (ctrl)
            {
                switch (key)
                {
                    case Key.M: if (c.CursorSequence.Machine != null) c.CursorSequence.Machine.IsMuted ^= true; c.Changed(); return true;
                    case Key.L: if (c.CursorSequence.Machine != null) c.CursorSequence.Machine.IsSoloed ^= true; c.Changed(); return true;
                    case Key.Return: return OpenContextMenu(c);
                    case Key.Up: MoveTrack(-1); return true;
                    case Key.Down: MoveTrack(1); return true;
                    case Key.Delete: DeleteTrack(); return true;
                    case Key.B: SetLoopStart(); return true;
                    case Key.E: SetEnd(); return true;
                    case Key.T: _ = AskTimeSignature(); return true;
                    case Key.I: InsertAll(); return true;
                    // Fork (ux-overhaul.md §4): ⌘D duplicates in every view; Delete (All Tracks) is ⇧⌘⌫ (⇧⌘⌦).
                    case Key.D: Duplicate(); return true;
                    case Key.R: Rename(); return true;
                    // The shell's Edit menu routes Cut, Copy and Paste here (IStudioEditTarget) but shows their
                    // keys without binding them (MenuItem.InputGesture), so the view takes them itself for now.
                    case Key.X: Cut(); return true;
                    case Key.C: Copy(); return true;
                    case Key.V: Paste(); return true;
                }
            }
            return false;
        }

        ISong Song => core.Song;

        void OnTextInput(object sender, TextInputEventArgs e)
        {
            if (e.Handled || TypingInBox(e.Source) || string.IsNullOrEmpty(e.Text) || e.Text.Length != 1) return;
            if (HandleChar(e.Text[0])) e.Handled = true;
        }

        /// <summary>Typing (PreviewTextInput): a pattern's key places it; , break; - mute; _ thru; . clears.</summary>
        public bool HandleChar(char ch)
        {
            var c = Canvas;
            if (c.CursorSequence == null) return false;
            var listed = patterns.FirstOrDefault(x => x.Char == ch);
            if (listed.Pattern != null)
            {
                Place(listed.Pattern);
                return true;
            }
            SequenceEventType? type = ch switch
            {
                ',' => SequenceEventType.Break,
                '-' => SequenceEventType.Mute,
                '_' => SequenceEventType.Thru,
                _ => null,
            };
            if (type is { } t)
            {
                c.Edits.SetEvent(c.CursorRow, c.CursorTime, new SequenceEvent(t));
                c.MoveCursorDelta(1, 0, int.MaxValue, false);
                return true;
            }
            if (ch == '.')
            {
                c.Edits.Clear(c.CursorRow, c.CursorTime, c.CursorSpan);
                c.MoveCursorDelta(1, 0, int.MaxValue, false);
                return true;
            }
            return false;
        }

        // ------------------------------------------------------------------ menus

        /// <summary>Control (Command on a Mac) and a key, shown beside a menu item (InputGestureText).</summary>
        static KeyGesture Gesture(Key key) => new KeyGesture(key, IsMac ? KeyModifiers.Meta : KeyModifiers.Control);

        static MenuItem Item(string header, Action run, KeyGesture gesture = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture };
            item.Click += (s, e) => run();
            return item;
        }

        IEnumerable<MenuItem> MachineItems() => core.Song.Machines
            .Where(m => !(m is MachineCore mc && mc.Hidden))
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m => Item(m.Name, () => Canvas.Edits.AddTrack(m)));

        /// <summary>The context menu (SequenceEditor.xaml's Grid.ContextMenu, less VU meter target, MIDI export and settings).</summary>
        bool OpenContextMenu(Control at)
        {
            var c = Canvas;
            bool hasTrack = c.CursorSequence != null;
            var add = new MenuItem { Header = "Add Track" };
            foreach (var i in MachineItems()) add.Items.Add(i);

            var colour = new MenuItem { Header = "Color", IsEnabled = c.CursorPattern != null };
            var palette = SequenceSongState.Palette;
            int current = c.CursorPattern != null ? ReBuzz.Core.Session.PatternColors.Get(c.CursorPattern) : -1;
            for (int i = 0; i < palette.Count; i++)
            {
                int index = i;
                // Fork (ux-overhaul.md §3.1): the state colours (play, record, arrangement override) aren't offered; one in use stays.
                if (ReBuzz.Studio.Shell.Design.StudioTokens.IsReservedForState(Color.FromUInt32(palette[i].Argb)) && current % palette.Count != i) continue;
                var item = Item(palette[i].Name, () => SetColour(index));
                item.Icon = new Border { Width = 16, Height = 16, Background = new SolidColorBrush(Color.FromUInt32(palette[i].Argb)) };
                colour.Items.Add(item);
            }
            colour.Items.Add(new Separator());
            colour.Items.Add(Item("Default", () => SetColour(-1)));

            var quick = new MenuItem { Header = "Quick Create Patterns", IsEnabled = hasTrack };
            for (int n = 1; n <= 8; n++)
            {
                int count = n;
                quick.Items.Add(Item(n.ToString(), () => c.Edits.CreatePatterns(c.CursorRow, c.CursorTime, count, SequenceCanvas.NewPatternLength, StudioKeyboardHeld(KeyModifiers.Control))));
            }

            bool hasTarget = Target() != null;
            var menu = new ContextMenu
            {
                Items =
                {
                    add,
                    Item("Delete Track", DeleteTrack, Gesture(Key.Delete), hasTrack),
                    Item("Move Track Up", () => MoveTrack(-1), Gesture(Key.Up), hasTrack && c.CursorRow > 0),
                    Item("Move Track Down", () => MoveTrack(1), Gesture(Key.Down), hasTrack && c.CursorRow < c.Rows - 1),
                    new Separator(),
                    Item("Set Start", SetLoopStart, Gesture(Key.B)),
                    Item("Set End", SetEnd, Gesture(Key.E)),
                    Item("Set Time Signature...", () => _ = AskTimeSignature(), Gesture(Key.T)),
                    new Separator(),
                    Item("Insert (All Tracks)", InsertAll, Gesture(Key.I), hasTrack),
                    Item("Delete (All Tracks)", DeleteAll, new KeyGesture(IsMac ? Key.Back : Key.Delete, (IsMac ? KeyModifiers.Meta : KeyModifiers.Control) | KeyModifiers.Shift), hasTrack),
                    Item("Duplicate", () => Duplicate(), Gesture(Key.D), hasTarget),
                    new Separator(),
                    colour,
                    new Separator(),
                    Item("Rename Machine...", RenameMachine, Gesture(Key.R), hasTrack),
                    quick,
                    new Separator(),
                    Item("Cut", () => Cut(), Gesture(Key.X), hasTarget),
                    Item("Copy", () => Copy(), Gesture(Key.C), hasTarget),
                    Item("Paste", () => Paste(), Gesture(Key.V), c.Edits.CanPaste && hasTrack),
                },
            };
            OpenMenu = menu;
            menu.Open(at);
            return true;
        }

        static bool StudioKeyboardHeld(KeyModifiers m) => ReBuzz.Studio.Platform.StudioKeyboard.Held.HasFlag(m) || (IsMac && m == KeyModifiers.Control && ReBuzz.Studio.Platform.StudioKeyboard.Held.HasFlag(KeyModifiers.Meta));

        /// <summary>The "+" button: a track for one of the song's machines.</summary>
        void OpenAddTrackMenu()
        {
            var menu = new ContextMenu();
            foreach (var i in MachineItems()) menu.Items.Add(i);
            OpenMenu = menu;
            menu.Open(addTrack);
        }
    }
}
