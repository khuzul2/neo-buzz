// Fork (docs/specs/studio-mvp.md S6): the pattern grid on Avalonia, a port of the WPF editor's
// PatternControl.xaml and its code-drawn ColumnRenderer (BeatVisual): column set headers, row
// numbers, the beats of each column set side by side, and on top of them the cursor row, the
// selection, the cursor and the play position. Drawn in layers, as WPF does: the beats redraw when
// values change or the view scrolls, the overlay when the cursor moves or the song plays. Only the
// beats and column sets in view are drawn. Input goes to the toolkit-free PatternControl.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using WDE.ModernPatternEditor.ColumnRenderer;
using Control = Avalonia.Controls.Control;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    // Avalonia's, not the portable editor's stand-in (PortableShims.cs).
    using Color = Avalonia.Media.Color;

    /// <summary>The grid: header, row numbers, and the scrolled beats with their overlay.</summary>
    public sealed class PatternGrid : Grid
    {
        internal const double SetLabelHeight = 34;
        internal const double ColumnLabelHeight = 80;

        internal readonly PatternEditor Editor;
        internal readonly PatternControl Controller;
        internal PatternFont Font { get; private set; } = new PatternFont();
        internal GridLayout Layout { get; private set; }

        internal readonly ScrollViewer Scroller;
        public PatternBody Body { get; }
        readonly OverlayLayer overlay;
        readonly RowNumberStrip rowNumbers;
        readonly HeaderStrip header;
        readonly Panel content;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        readonly HashSet<ParameterColumnSet> watched = new();

        /// <summary>The pattern position the play cursor shows (pattern time; int.MinValue: none).</summary>
        internal int PlayPosition { get; private set; } = int.MinValue;

        public PatternGrid(PatternEditor editor)
        {
            Editor = editor;
            Controller = editor.Control;
            ColumnDefinitions = new ColumnDefinitions("Auto,*");
            RowDefinitions = new RowDefinitions("Auto,*");
            ClipToBounds = true;

            Body = new PatternBody(this);
            overlay = new OverlayLayer(this) { IsHitTestVisible = false };
            content = new Panel { Children = { Body, overlay }, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            Scroller = new ScrollViewer
            {
                Content = content,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
                VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
                Background = PatternTheme.ElementBackground,
            };
            rowNumbers = new RowNumberStrip(this);
            header = new HeaderStrip(this);

            var headerBorder = Element(header, new Thickness(4, 4, 4, 0));
            SetRow(headerBorder, 0);
            SetColumn(headerBorder, 1);
            var rowBorder = Element(rowNumbers, new Thickness(4, 4, 0, 4));
            SetRow(rowBorder, 1);
            SetColumn(rowBorder, 0);
            var bodyBorder = Element(Scroller, new Thickness(4, 4, 4, 4));
            SetRow(bodyBorder, 1);
            SetColumn(bodyBorder, 1);
            Children.Add(headerBorder);
            Children.Add(rowBorder);
            Children.Add(bodyBorder);

            Scroller.ScrollChanged += (s, e) =>
            {
                if (Controller.Pattern != null) Controller.Pattern.ScrollPosition = Scroller.Offset.Y;
                Body.InvalidateVisual();
                header.InvalidateVisual();
                rowNumbers.InvalidateVisual();
            };

            Controller.PatternShown += Relayout;
            Controller.CursorMoved += OnCursorMoved;
            Controller.SelectionChanged += overlay.InvalidateVisual;
            Controller.Invalidated += InvalidateAll;
            PatternEditor.Settings.PropertyChanged += Settings_PropertyChanged;

            timer.Tick += (s, e) => TimerUpdate();
            AttachedToVisualTree += (s, e) => timer.Start();
            DetachedFromVisualTree += (s, e) => timer.Stop();

            Relayout();
        }

        static Border Element(Control child, Thickness margin) => new Border
        {
            Child = child,
            Margin = margin,
            BorderBrush = PatternTheme.ElementBorder,
            BorderThickness = new Thickness(1),
            Background = PatternTheme.ElementBackground,
        };

        void Settings_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "FontFamily":
                case "FontSize":
                case "FontStyle":
                case "FontWeight":
                    Font = new PatternFont();
                    Relayout();
                    break;
                default:
                    Relayout();
                    break;
            }
        }

        internal void Release()
        {
            timer.Stop();
            Controller.PatternShown -= Relayout;
            Controller.CursorMoved -= OnCursorMoved;
            Controller.SelectionChanged -= overlay.InvalidateVisual;
            Controller.Invalidated -= InvalidateAll;
            PatternEditor.Settings.PropertyChanged -= Settings_PropertyChanged;
            Unwatch();
        }

        void Unwatch()
        {
            foreach (var cs in watched) cs.BeatsInvalidated -= OnBeatsInvalidated;
            watched.Clear();
        }

        /// <summary>A pattern is shown, or its columns or length changed: lay out again (PatternControl.CreateAllElements).</summary>
        internal void Relayout()
        {
            Unwatch();
            var pattern = Controller.Pattern;
            Layout = pattern != null && pattern.ColumnSets != null && pattern.ColumnSets.Count > 0 ? new GridLayout(pattern, Font) : null;
            if (Layout != null)
            {
                foreach (var cs in pattern.ColumnSets)
                {
                    cs.BeatsInvalidated += OnBeatsInvalidated;
                    watched.Add(cs);
                }
                content.Width = Layout.ExtentWidth;
                content.Height = Layout.ExtentHeight;
                rowNumbers.Width = Math.Ceiling(Font.Width(RowDigits(pattern)));
                header.Height = HeaderHeight;
            }
            else
            {
                content.Width = content.Height = 0;
                rowNumbers.Width = Math.Ceiling(Font.Width(4));
                header.Height = HeaderHeight;
            }
            IsEnabled = Layout != null;
            PlayPosition = int.MinValue;
            InvalidateAll();
            if (pattern != null)
                Dispatcher.UIThread.Post(() => Scroller.Offset = new Vector(Scroller.Offset.X, pattern.ScrollPosition));
        }

        internal double HeaderHeight => SetLabelHeight + (PatternEditor.Settings.ColumnLabels ? ColumnLabelHeight : 0);

        static int RowDigits(PatternVM pattern) => pattern.RowNumberColumnSet.Columns[0].DigitCount;

        void OnBeatsInvalidated(HashSet<int> beats)
        {
            Body.InvalidateVisual();
            overlay.InvalidateVisual();
        }

        void InvalidateAll()
        {
            Body.InvalidateVisual();
            overlay.InvalidateVisual();
            header.InvalidateVisual();
            rowNumbers.InvalidateVisual();
        }

        void OnCursorMoved()
        {
            overlay.InvalidateVisual();
            rowNumbers.InvalidateVisual();
            Body.InvalidateVisual();        // the status of values under the cursor may change too
            if (Layout == null) return;
            var r = Layout.GetFieldRect(Controller.Pattern.CursorPosition);
            if (PatternEditor.Settings.CursorScrollMode == CursorScrollMode.Standard)
                MakeVisible(r);
            else
                Scroller.Offset = new Vector(Scroller.Offset.X, Math.Max(0, r.Top - Math.Floor(Scroller.Viewport.Height / 2 + r.Height / 2)));
            if (r.Left < Scroller.Offset.X || r.Right > Scroller.Offset.X + Scroller.Viewport.Width)
                MakeVisible(r);
        }

        /// <summary>Scrolls so a rectangle of the pattern is in view (WPF: BringIntoView).</summary>
        public void MakeVisible(Rect r)
        {
            var o = Scroller.Offset;
            var v = Scroller.Viewport;
            if (v.Width <= 0 || v.Height <= 0) return;
            double x = o.X, y = o.Y;
            if (r.Left < x) x = r.Left;
            else if (r.Right > x + v.Width) x = r.Right - v.Width;
            if (r.Top < y) y = r.Top;
            else if (r.Bottom > y + v.Height) y = r.Bottom - v.Height;
            if (x != o.X || y != o.Y)
                Scroller.Offset = new Vector(Math.Max(0, x), Math.Max(0, y));
        }

        /// <summary>The 20 ms timer (PatternEditor.SetTimer, PatternControl.TimerUpdate): the playing pattern and the play cursor.</summary>
        public void TimerUpdate()
        {
            Editor.TimerUpdate();
            var pattern = Controller.Pattern;
            int p = pattern?.Pattern != null ? pattern.Pattern.PlayPosition : int.MinValue;
            if (p < 0) p = int.MinValue;
            if (p == PlayPosition) return;
            PlayPosition = p;
            overlay.InvalidateVisual();
            rowNumbers.InvalidateVisual();
            if (p != int.MinValue && Layout != null && PatternEditor.Settings.FollowPlayPositioninPattern)
            {
                double y = PlayY(p);
                Scroller.Offset = new Vector(Scroller.Offset.X, Math.Max(0, y - Scroller.Viewport.Height / 2.0));
            }
        }

        /// <summary>Where a pattern time is drawn (y of the play cursor).</summary>
        internal double PlayY(int time) => time / (double)PatternEvent.TimeBase * Layout.RPB / PatternControl.BUZZ_TICKS_PER_BEAT * Font.LineHeight * PatternControl.Scale;

        internal Vector Offset => Scroller.Offset;
        internal Size Viewport => Scroller.Viewport;

        /// <summary>Gives the beats the keyboard (the editor's Activate).</summary>
        public void FocusGrid() => Body.Focus();

        /// <summary>The centre of a field (column set, column, beat, row in beat) in the beats' coordinates (for tests).</summary>
        public Point FieldCenter(int columnSet, int column, int beat, int row)
        {
            var r = Layout.GetFieldRect(new Digit(Controller.CurrentPattern, columnSet, column, beat, row, 0));
            return r.Center;
        }

        /// <summary>The cursor (for tests).</summary>
        public Digit Cursor => Controller.CurrentPattern.CursorPosition;

        /// <summary>How long the last drawing of the beats took (for tests: the grid redraws a lot).</summary>
        public TimeSpan LastRenderTime => Body.LastRenderTime;

        // ------------------------------------------------------------------ drawing a beat (BeatVisual)

        /// <summary>
        /// BeatVisual.RenderVisual: a beat of a column set at (x, y), its background and each column's rows.
        /// Fork (docs/specs/dye-essence.md §5.2): only the columns between <paramref name="visibleFrom"/>
        /// and <paramref name="visibleTo"/> (the view, in the same coordinates as <paramref name="x0"/>)
        /// are fetched and drawn. A machine's globals are one set: with hundreds of parameters every
        /// frame laid out text for all of them, 1.2 s a frame at 704.
        /// </summary>
        internal void DrawBeat(DrawingContext dc, IColumnSet set, int[] widths, int index, double x0, double y0, int h,
            double visibleFrom = double.NegativeInfinity, double visibleTo = double.PositiveInfinity)
        {
            int c0 = 0;
            double x = x0;
            while (c0 < widths.Length && x + widths[c0] < visibleFrom) x += widths[c0++];
            int c1 = c0;
            for (double end = x; c1 < widths.Length && end <= visibleTo; c1++) end += widths[c1];
            var beats = new IBeat[c1 - c0];
            for (int i = 0; i < beats.Length; i++) beats[i] = set.Columns[c0 + i].FetchBeat(index);
            double total = widths.Sum();
            if (PatternEditor.Settings.BeatHighlight == BeatHighlightMode.Block)
            {
                dc.FillRectangle((index & 1) == 0 ? PatternTheme.BeatDark : PatternTheme.Beat, new Rect(x0, y0, total, h));
            }
            else if (set.Columns.Count > 0)
            {
                var rows = (beats.Length > 0 ? beats[0] : set.Columns[0].FetchBeat(index)).Rows;
                double rowHeight = Math.Floor(h / (double)rows.Count);
                dc.FillRectangle(PatternTheme.BeatDark, new Rect(x0, y0, total, rowHeight));
                dc.FillRectangle(PatternTheme.Beat, new Rect(x0, y0 + rowHeight, total, h - rowHeight));
            }

            for (int i = 0; i < beats.Length; i++)
            {
                int c = c0 + i;
                double xoffset = set.Columns[c].TiedToNext ? Math.Ceiling(Font.Width(0)) / 2 : 0;
                if (PatternEditor.Settings.TextDropShadow)
                    DrawColumn(dc, beats[i], x + xoffset + 1, y0, 1, widths[c], h, true);
                DrawColumn(dc, beats[i], x + xoffset, y0, 0, widths[c], h, false);
                x += widths[c];
            }
        }

        /// <summary>BeatVisual.DrawColumn: a column's rows in a beat, squeezed when it has more rows than the grid.</summary>
        void DrawColumn(DrawingContext dc, IBeat beat, double x, double y0, double yoffs, int w, int h, bool shadow)
        {
            var rows = beat.Rows;
            double lineheight = Font.LineHeight;
            double scaling = h / (lineheight * rows.Count) * 0.999;
            lineheight *= Math.Max(scaling, 1.0);
            double sy = Math.Min(scaling, 1.0);

            using (dc.PushTransform(Matrix.CreateScale(1, sy) * Matrix.CreateTranslation(0, y0)))
            {
                for (int row = 0; row < rows.Count; row++)
                {
                    var r = rows[row];
                    IBrush brush;
                    if (r.Type == BeatValueType.RowNumber)
                        brush = shadow ? PatternTheme.RowNumberShadow : PatternTheme.RowNumber;
                    else if (r.Type == BeatValueType.NoValue)
                    {
                        if (shadow) continue;           // NoValueDropShadowBrush is transparent
                        brush = PatternTheme.NoValue;
                    }
                    else if (r.Type == BeatValueType.Note)
                    {
                        if (shadow)
                            brush = PatternTheme.TextShadow;
                        else if (PatternEditor.Settings.ColorNote != ColorNoteMode.None && r.Value != BuzzGUI.Common.BuzzNote.Off)
                            brush = PatternTheme.NoteBrushes[(((r.Value & 15) - 1) * 7 % 12 + 12) % 12];
                        else
                            brush = PatternTheme.Text;
                    }
                    else
                        brush = shadow ? PatternTheme.TextShadow : PatternTheme.Text;

                    var ft = Font.Text(r.ValueString ?? "", brush);
                    double tx = x + (w - ft.WidthIncludingTrailingWhitespace) / 2;
                    dc.DrawText(ft, new Point(tx, row * lineheight + yoffs));
                }
            }
        }
    }

    /// <summary>The beats of the column sets in view (the WPF ColumnSetElements), and the mouse.</summary>
    public sealed class PatternBody : Control
    {
        readonly PatternGrid grid;
        bool dragSelects, dragging;

        internal PatternBody(PatternGrid grid)
        {
            this.grid = grid;
            Focusable = true;
            ClipToBounds = true;
            Background = Brushes.Transparent;
            ContextMenu = PatternMenus.Create(grid.Controller);
        }

        public IBrush Background { get; set; }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            grid.Controller.IsKeyboardFocused = true;
            BuzzGUI.Common.Global.Buzz.EditContext = grid.Editor.EditContext;     // WPF: PreviewGotKeyboardFocus
            InvalidateOverlay();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            grid.Controller.IsKeyboardFocused = false;
            InvalidateOverlay();
        }

        void InvalidateOverlay() => (grid.Scroller.Content as Panel)?.Children.OfType<OverlayLayer>().FirstOrDefault()?.InvalidateVisual();

        internal TimeSpan LastRenderTime { get; private set; }

        public override void Render(DrawingContext dc)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            dc.FillRectangle(Background, new Rect(Bounds.Size));
            var layout = grid.Layout;
            if (layout == null) return;
            var o = grid.Offset;
            var v = grid.Viewport;
            if (v.Width <= 0 || v.Height <= 0) v = Bounds.Size;
            int h = layout.BeatHeight;
            int first = Math.Max(0, (int)Math.Floor(o.Y / h));
            int last = Math.Min(layout.BeatCount - 1, (int)Math.Floor((o.Y + v.Height) / h));
            var sets = layout.Pattern.ColumnSets;
            for (int s = 0; s < sets.Count; s++)
            {
                double x = layout.SetX(s);
                if (x > o.X + v.Width || x + layout.SetWidth(s) < o.X) continue;
                var widths = sets[s].Columns.Select(layout.ColumnWidth).ToArray();
                for (int b = first; b <= last; b++)
                    grid.DrawBeat(dc, sets[s], widths, b, x, b * h, h, o.X, o.X + v.Width);
            }
            LastRenderTime = clock.Elapsed;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed || grid.Layout == null) return;
            Focus();
            var d = grid.Layout.GetDigitAtPoint(point.Position);
            grid.Controller.PressAt(d, e.ClickCount, out dragSelects);
            dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!dragging || grid.Layout == null) return;
            var p = e.GetPosition(this);
            if (dragSelects)
                grid.Controller.DragTo(grid.Layout.GetDigitAtPoint(p));
            grid.MakeVisible(new Rect(p, new Size(1, 1)));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            // Outside the standard scroll mode the wheel moves the cursor (four rows a notch), as in WPF.
            if (PatternEditor.Settings.CursorScrollMode != CursorScrollMode.Standard && grid.Layout != null)
            {
                grid.Controller.MoveCursorDelta(0, e.Delta.Y > 0 ? -4 : 4);
                e.Handled = true;
                return;
            }
            base.OnPointerWheelChanged(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            var key = EditorKeys.Key(e.Key);
            if (key == EditorKey.Other) return;
            if (grid.Controller.KeyDown(key, EditorKeys.Modifiers(e.KeyModifiers), EditorKeys.ScanCode(e.PhysicalKey)))
                e.Handled = true;
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (!e.Handled && grid.Controller.TextInput(e.Text))
                e.Handled = true;
        }
    }

    /// <summary>The cursor row, the selection, the cursor and the play position, over the beats.</summary>
    internal sealed class OverlayLayer : Control
    {
        readonly PatternGrid grid;

        public OverlayLayer(PatternGrid grid)
        {
            this.grid = grid;
        }

        public override void Render(DrawingContext dc)
        {
            var layout = grid.Layout;
            var pattern = grid.Controller.Pattern;
            if (layout == null || pattern == null) return;
            double width = Math.Max(Bounds.Width, layout.ExtentWidth);

            var cursor = pattern.CursorPosition;
            var r = layout.GetCursorRect(cursor);
            if (PatternEditor.Settings.CursorRowHighlight)
                dc.FillRectangle(PatternTheme.CursorLine, new Rect(0, r.Top, width, r.Height));

            var selection = pattern.Selection;
            if (selection.Active)
            {
                var bounds = selection.Bounds;
                var sr = layout.GetFieldRect(bounds.Item1).Union(layout.GetFieldRect(bounds.Item2));
                dc.DrawRectangle(PatternTheme.SelectionFill, PatternTheme.SelectionStroke, sr.Deflate(0.5));
            }

            bool active = grid.Controller.IsKeyboardFocused;
            dc.FillRectangle(active ? PatternTheme.Cursor : PatternTheme.CursorInactive, r);
            if (active)
                dc.DrawRectangle(new ImmutablePen(PatternTheme.CursorBorder, 1), r.Deflate(0.5));

            if (grid.PlayPosition != int.MinValue)
                PlayRow(dc, grid, layout, width);
        }

        /// <summary>The play cursor (playPosRectangle): a row's height, cut at the pattern's end.</summary>
        internal static void PlayRow(DrawingContext dc, PatternGrid grid, GridLayout layout, double width)
        {
            double line = grid.Font.LineHeight;
            double y = grid.PlayY(grid.PlayPosition) / PatternControl.Scale;
            double patternHeight = layout.Pattern.Pattern.Length * layout.RPB / (double)PatternControl.BUZZ_TICKS_PER_BEAT * line;
            double diff = y + line - patternHeight;
            double height = diff > 0 && line - diff >= 0 ? line - diff : line;
            dc.FillRectangle(PatternTheme.PlayPosition, new Rect(0, y * PatternControl.Scale, width, height * PatternControl.Scale));
        }
    }

    /// <summary>The row numbers beside the beats (the WPF rowNumberGrid), scrolled with them.</summary>
    internal sealed class RowNumberStrip : Control
    {
        readonly PatternGrid grid;
        bool dragSelects, dragging;

        public RowNumberStrip(PatternGrid grid)
        {
            this.grid = grid;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(PatternTheme.ElementBackground, new Rect(Bounds.Size));
            var layout = grid.Layout;
            if (layout == null) return;
            var set = layout.Pattern.RowNumberColumnSet;
            int[] widths = { (int)Math.Ceiling(Bounds.Width) };
            double oy = grid.Offset.Y;
            int h = layout.BeatHeight;
            int first = Math.Max(0, (int)Math.Floor(oy / h));
            int last = Math.Min(layout.BeatCount - 1, (int)Math.Floor((oy + Bounds.Height) / h));
            using (dc.PushTransform(Matrix.CreateTranslation(0, -oy)))
            {
                for (int b = first; b <= last; b++)
                    grid.DrawBeat(dc, set, widths, b, 0, b * h, h);

                if (PatternEditor.Settings.CursorRowHighlight && grid.Controller.Pattern != null)
                {
                    var r = layout.GetCursorRect(grid.Controller.Pattern.CursorPosition);
                    dc.FillRectangle(PatternTheme.CursorLine, new Rect(0, r.Top, Bounds.Width, r.Height));
                }
                if (grid.PlayPosition != int.MinValue)
                    OverlayLayer.PlayRow(dc, grid, layout, Bounds.Width);
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed || grid.Layout == null) return;
            grid.FocusGrid();
            var d = grid.Layout.GetDigitAtPoint(new Point(0, point.Position.Y + grid.Offset.Y));
            grid.Controller.PressRowAt(d, e.ClickCount, out dragSelects);
            dragging = true;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!dragging || !dragSelects || grid.Layout == null) return;
            var p = e.GetPosition(this);
            var d = grid.Layout.GetDigitAtPoint(new Point(grid.Layout.ExtentWidth - 1, p.Y + grid.Offset.Y));
            grid.Controller.DragTo(d);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            dragging = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            e.Handled = true;       // as in WPF, the row numbers don't scroll on their own
        }
    }

    /// <summary>
    /// The column set headers (PatternControl.CreateHeaders): each set's label (a click mutes the set
    /// while playing), and each column's name, turned upright (clicks: go to the column, select it,
    /// select the set). Scrolled sideways with the beats. The WPF header's parameter knobs aren't here.
    /// </summary>
    internal sealed class HeaderStrip : Control
    {
        readonly PatternGrid grid;
        Point? hover;

        public HeaderStrip(PatternGrid grid)
        {
            this.grid = grid;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext dc)
        {
            dc.FillRectangle(PatternTheme.ElementBackground, new Rect(Bounds.Size));
            var layout = grid.Layout;
            if (layout == null) return;
            double ox = grid.Offset.X;
            var sets = layout.Pattern.ColumnSets;
            var labelFont = new Typeface(FontFamily.Default);
            using (dc.PushTransform(Matrix.CreateTranslation(-ox, 0)))
            {
                for (int s = 0; s < sets.Count; s++)
                {
                    double x = layout.SetX(s), w = layout.SetWidth(s);
                    if (x > ox + Bounds.Width || x + w < ox) continue;
                    var cs = sets[s];
                    var labelRect = new Rect(x, 0, w, PatternGrid.SetLabelHeight);
                    if (hover is Point h && labelRect.Contains(new Point(h.X + ox, h.Y)))
                        dc.DrawRectangle(PatternTheme.LabelHover, null, labelRect, 3, 3);
                    var label = new FormattedText(cs.Label ?? "", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, labelFont, 12,
                        cs.Muted ? PatternTheme.ColumnSetLabelMuted : PatternTheme.ColumnSetLabel);
                    double natural = Math.Max(1, label.WidthIncludingTrailingWhitespace);
                    label.MaxTextWidth = natural;
                    label.TextAlignment = TextAlignment.Center;
                    // Viewbox (DownOnly): a label wider than its set shrinks to fit.
                    double scale = Math.Min(1.0, Math.Min((w - 4) / natural, (PatternGrid.SetLabelHeight - 4) / Math.Max(1, label.Height)));
                    using (dc.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x + w / 2 - natural * scale / 2, PatternGrid.SetLabelHeight / 2 - label.Height * scale / 2)))
                        dc.DrawText(label, new Point(0, 0));

                    if (!PatternEditor.Settings.ColumnLabels) continue;
                    double cx = x;
                    for (int c = 0; c < cs.Columns.Count; c++)
                    {
                        int cw = layout.ColumnWidthAt(s, c);
                        // Fork (docs/specs/dye-essence.md §5.2): the labels in view only.
                        if (cx + cw < ox || cx > ox + Bounds.Width)
                        {
                            cx += cw;
                            continue;
                        }
                        var col = new Rect(cx, PatternGrid.SetLabelHeight, cw, PatternGrid.ColumnLabelHeight);
                        if (hover is Point hp && col.Contains(new Point(hp.X + ox, hp.Y)))
                            dc.DrawRectangle(PatternTheme.LabelHover, null, col, 3, 3);
                        var name = new FormattedText(cs.Columns[c].Label ?? "", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, labelFont, 10, PatternTheme.ColumnLabel)
                        { MaxTextWidth = PatternGrid.ColumnLabelHeight - 4, Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
                        // Turned -90°, its start at the bottom, centred in the column.
                        double tx = cx + cw / 2.0 - name.Height / 2;
                        double ty = PatternGrid.SetLabelHeight + PatternGrid.ColumnLabelHeight - 2;
                        using (dc.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(tx, ty)))
                            dc.DrawText(name, new Point(0, 0));
                        cx += cw;
                    }
                }
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            hover = e.GetPosition(this);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            hover = null;
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var layout = grid.Layout;
            var point = e.GetCurrentPoint(this);
            if (layout == null || !point.Properties.IsLeftButtonPressed) return;
            var p = point.Position;
            double x = p.X + grid.Offset.X;
            int s = layout.SetAt(x);
            if (x > layout.SetX(s) + layout.SetWidth(s)) return;
            var cs = layout.Pattern.ColumnSets[s];
            if (p.Y < PatternGrid.SetLabelHeight)
            {
                if (e.ClickCount == 1)
                {
                    cs.Muted = !cs.Muted;
                    grid.Editor.MPEPatternsDB.SetColumnsMuteFlag(cs);
                    InvalidateVisual();
                }
            }
            else if (PatternEditor.Settings.ColumnLabels)
            {
                double cx = layout.SetX(s);
                for (int c = 0; c < cs.Columns.Count; c++)
                {
                    cx += layout.ColumnWidthAt(s, c);
                    if (x < cx)
                    {
                        var column = cs.Columns[c];
                        if (e.ClickCount == 1) grid.Controller.GoToColumn(column);
                        else if (e.ClickCount == 2) grid.Controller.SelectColumn(column);
                        else if (e.ClickCount == 3) grid.Controller.SelectColumnSet(cs);
                        grid.FocusGrid();
                        break;
                    }
                }
            }
            e.Handled = true;
        }
    }
}
