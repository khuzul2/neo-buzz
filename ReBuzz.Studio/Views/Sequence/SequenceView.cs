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

namespace ReBuzz.Studio.Views.Sequence
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The "Sequence" tab.</summary>
    internal sealed class SequenceTab : IStudioView
    {
        public string Title => "Sequence";

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
        readonly ScrollBar horizontal = new ScrollBar { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, AllowAutoHide = true, Margin = new Thickness(SequenceGeometry.HeaderWidth, 0, 0, 0) };
        readonly ScrollBar vertical = new ScrollBar { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Right, AllowAutoHide = true, Margin = new Thickness(0, SequenceGeometry.RulerHeight, 0, 0) };
        readonly Slider zoom = new Slider
        {
            Minimum = SequenceGeometry.MinTickWidth, Maximum = SequenceGeometry.MaxTickWidth, TickFrequency = SequenceGeometry.TickWidthStep,
            IsSnapToTickEnabled = true, Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        readonly ComboBox steps = new ComboBox { Width = 58, Height = 24, MinHeight = 0, FontSize = 12, Padding = new Thickness(6, 0, 0, 0), Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly Button addTrack = new Button { Content = "+", Width = 24, Height = 24, MinHeight = 0, Padding = new Thickness(0), FontSize = 14, Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
        readonly ToggleButton follow = new ToggleButton { Content = "Follow", Height = 24, MinHeight = 0, FontSize = 11, Padding = new Thickness(5, 0), Margin = new Thickness(2, 0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, IsChecked = true };
        readonly ListBox patternList = new ListBox
        {
            Background = new SolidColorBrush(Color.Parse("#F7F7F3")), Foreground = Brushes.Black, FontSize = 12,
            BorderBrush = new SolidColorBrush(Color.Parse("#8000")), BorderThickness = new Thickness(1),
        };
        readonly List<(char Char, IPattern Pattern)> patterns = new List<(char, IPattern)>();
        bool syncing;
        bool attached;

        public SequenceView(ReBuzzCore core)
        {
            this.core = core;
            state = SequenceSongState.For(core);
            Canvas = new SequenceCanvas(core, state);
            Focusable = true;

            // The corner's controls in the editor's light colours (the studio's theme is dark).
            var light = new SolidColorBrush(Color.Parse("#E8E8E2"));
            var edge = new SolidColorBrush(Color.Parse("#8000"));
            foreach (var c in new TemplatedControl[] { addTrack, steps, follow })
            {
                c.Background = light;
                c.Foreground = Brushes.Black;
                c.BorderBrush = edge;
                c.BorderThickness = new Thickness(1);
            }
            patternList.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
            {
                Setters =
                {
                    new Setter(TemplatedControl.PaddingProperty, new Thickness(6, 2)),
                    new Setter(MinHeightProperty, 0.0),
                },
            });
            // The list is light in a dark theme: its items' states get light-list colours. The dark
            // theme's pointer-over drew white text on a near-white tint ("1. Arp" at 1.06:1, gallery
            // mac-070), and its selection white on light blue.
            ListStates(patternList);

            foreach (var s in Steps) steps.Items.Add(s);
            steps.SelectedItem = state.TimeSignatures.GetBarLengthAt(0);
            ToolTip.SetTip(steps, "Bar length (ticks): the cursor's and the selection's step");
            ToolTip.SetTip(addTrack, "Add a track");
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
            var timeline = new Panel { Children = { Canvas, Canvas.Overlay, horizontal, vertical, corner } };

            var right = new DockPanel { Width = 120, Background = SequenceCanvas.BackgroundBrush };
            var zoomRow = new Border { Height = SequenceGeometry.RulerHeight, Child = zoom };
            DockPanel.SetDock(zoomRow, Dock.Top);
            right.Children.Add(zoomRow);
            right.Children.Add(patternList);

            var root = new DockPanel { Background = SequenceCanvas.BackgroundBrush };
            DockPanel.SetDock(right, Dock.Right);
            root.Children.Add(right);
            root.Children.Add(timeline);
            Content = root;

            zoom.Value = state.Zoom;
            Canvas.ViewChanged += Sync;
            Canvas.CursorChanged += RefreshPatternList;
            Canvas.TracksChanged += RefreshPatternList;
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
        internal static Color PatternListBackground => Color.Parse("#F7F7F3");
        public Button AddTrackButton => addTrack;

        /// <summary>The menu a right click (or the add-track button) opened last (tests click through it).</summary>
        public ContextMenu OpenMenu { get; private set; }

        /// <summary>A pattern was chosen for editing here (as <see cref="SequenceTab.PatternChosen"/>).</summary>
        public event Action<IPattern> PatternChosen;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            attached = true;
            state.Changed += OnStateChanged;
            OnStateChanged();
            RefreshPatternList();
            // Shown (its tab selected): the timeline takes the keys, unless something in the view has them.
            Dispatcher.UIThread.Post(() => { if (attached && !IsKeyboardFocusWithin) Canvas.Focus(); });
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            attached = false;
            state.Changed -= OnStateChanged;
            base.OnDetachedFromVisualTree(e);
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

        static readonly IBrush ItemHover = new SolidColorBrush(Color.Parse("#E0E0D8"));
        static readonly IBrush ItemPressed = new SolidColorBrush(Color.Parse("#D0D0C6"));
        static readonly IBrush ItemSelected = new SolidColorBrush(Color.Parse("#2F6DAE"));
        static readonly IBrush ItemSelectedHover = new SolidColorBrush(Color.Parse("#285F9C"));

        /// <summary>Every state of a light list's items readable: dark text on light, white on the selection's blue (4.5:1 and more).</summary>
        internal static void ListStates(ListBox list)
        {
            void State(Func<Selector, Selector> state, IBrush background, IBrush foreground) =>
                list.Styles.Add(new Style(x => state(x.OfType<ListBoxItem>()).Template().OfType<ContentPresenter>().Name("PART_ContentPresenter"))
                {
                    Setters =
                    {
                        new Setter(ContentPresenter.BackgroundProperty, background),
                        new Setter(ContentPresenter.ForegroundProperty, foreground),
                    },
                });
            State(x => x, Brushes.Transparent, Brushes.Black);
            State(x => x.Class(":pointerover"), ItemHover, Brushes.Black);
            State(x => x.Class(":pressed"), ItemPressed, Brushes.Black);
            State(x => x.Class(":selected"), ItemSelected, Brushes.White);
            State(x => x.Class(":selected").Class(":focus"), ItemSelected, Brushes.White);
            State(x => x.Class(":selected").Class(":pointerover"), ItemSelectedHover, Brushes.White);
            State(x => x.Class(":selected").Class(":pressed"), ItemSelectedHover, Brushes.White);
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

        /// <summary>Set Time Signature... (Ctrl+T): the bar length from the cursor on, asked for (StepEditWindow).</summary>
        public async Task AskTimeSignature()
        {
            int time = Canvas.CursorTime;
            string answer = await Dialogs.AskText("Bar length (ticks)", Canvas.CursorSpan.ToString());
            if (int.TryParse(answer, out int step) && step > 0 && step <= 1024) Canvas.Edits.SetTimeSignature(time, step);
            Canvas.Focus();
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
                    case Key.D: DeleteAll(); return true;
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
            for (int i = 0; i < palette.Count; i++)
            {
                int index = i;
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
                    Item("Delete (All Tracks)", DeleteAll, Gesture(Key.D), hasTrack),
                    new Separator(),
                    colour,
                    new Separator(),
                    Item("Rename Machine...", RenameMachine, null, hasTrack),
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
