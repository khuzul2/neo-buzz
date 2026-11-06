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

        // For tests: the header's labels where they're drawn, in the header's own coordinates (the
        // beats' x minus the sideways scroll), and the part of a set or column in view.
        public Rect SetLabelBounds(int columnSet) => header.SetLabel(columnSet).Bounds.Translate(new Vector(-Offset.X, 0));
        public Rect ColumnLabelBounds(int columnSet, int column) => header.ColumnLabel(columnSet, column).Bounds.Translate(new Vector(-Offset.X, 0));
        public double HeaderWidth => header.Bounds.Width;
        public (double Left, double Right) SetSpan(int columnSet) => (Layout.SetX(columnSet) - Offset.X, Layout.SetX(columnSet) + Layout.SetWidth(columnSet) - Offset.X);
        public void ScrollSideways(double x) => Scroller.Offset = new Vector(x, Scroller.Offset.Y);
        public double SidewaysOffset => Offset.X;
        public int ColumnSetCount => Layout.Pattern.ColumnSets.Count;
        public int ColumnCount(int columnSet) => Layout.Pattern.ColumnSets[columnSet].Columns.Count;
        public double ColumnX(int columnSet, int column)
        {
            double x = Layout.SetX(columnSet);
            for (int c = 0; c < column; c++) x += Layout.ColumnWidthAt(columnSet, c);
            return x - Offset.X;
        }
        public double ColumnWidth(int columnSet, int column) => Layout.ColumnWidthAt(columnSet, column);

        /// <summary>How long the last drawing of the beats took (for tests: the grid redraws a lot).</summary>
        public TimeSpan LastRenderTime => Body.LastRenderTime;

        // ------------------------------------------------------------------ drawing a beat (BeatVisual)

        /// <summary>BeatVisual.RenderVisual: a beat of a column set at (x, y), its background and each column's rows.</summary>
        internal void DrawBeat(DrawingContext dc, IColumnSet set, int[] widths, int index, double x0, double y0, int h)
        {
            var beats = set.Columns.Select(c => c.FetchBeat(index)).ToArray();
            double total = widths.Sum();
            if (PatternEditor.Settings.BeatHighlight == BeatHighlightMode.Block)
            {
                dc.FillRectangle((index & 1) == 0 ? PatternTheme.BeatDark : PatternTheme.Beat, new Rect(x0, y0, total, h));
            }
            else if (beats.Length > 0)
            {
                double rowHeight = Math.Floor(h / (double)beats[0].Rows.Count);
                dc.FillRectangle(PatternTheme.BeatDark, new Rect(x0, y0, total, rowHeight));
                dc.FillRectangle(PatternTheme.Beat, new Rect(x0, y0 + rowHeight, total, h - rowHeight));
            }

            double x = x0;
            for (int i = 0; i < beats.Length; i++)
            {
                double xoffset = set.Columns[i].TiedToNext ? Math.Ceiling(Font.Width(0)) / 2 : 0;
                if (PatternEditor.Settings.TextDropShadow)
                    DrawColumn(dc, beats[i], x + xoffset + 1, y0, 1, widths[i], h, true);
                DrawColumn(dc, beats[i], x + xoffset, y0, 0, widths[i], h, false);
                x += widths[i];
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
                    grid.DrawBeat(dc, sets[s], widths, b, x, b * h, h);
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
                    var set = SetLabel(s);
                    using (dc.PushClip(labelRect))
                    using (dc.PushTransform(Matrix.CreateScale(set.Scale, set.Scale) * Matrix.CreateTranslation(set.Bounds.X, set.Bounds.Y)))
                        dc.DrawText(set.Text, new Point(0, 0));

                    if (!PatternEditor.Settings.ColumnLabels) continue;
                    double cx = x;
                    for (int c = 0; c < cs.Columns.Count; c++)
                    {
                        int cw = layout.ColumnWidthAt(s, c);
                        var col = new Rect(cx, PatternGrid.SetLabelHeight, cw, PatternGrid.ColumnLabelHeight);
                        if (cx <= ox + Bounds.Width && cx + cw >= ox)
                        {
                            if (hover is Point hp && col.Contains(new Point(hp.X + ox, hp.Y)))
                                dc.DrawRectangle(PatternTheme.LabelHover, null, col, 3, 3);
                            var name = ColumnLabel(s, c);
                            // Turned -90°, its start at the bottom.
                            using (dc.PushClip(col))
                            using (dc.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(name.Bounds.X, name.Bounds.Bottom)))
                                dc.DrawText(name.Text, new Point(0, 0));
                        }
                        cx += cw;
                    }
                }
            }
        }

        static readonly Typeface LabelFont = new Typeface(FontFamily.Default);

        /// <summary>
        /// A column set's label and where it goes (the beats' coordinates): shrunk to fit its set
        /// (a Viewbox, DownOnly), centred over the part of the set in view, so a set wider than the
        /// view keeps its label on screen while it scrolls (gallery mac-052 cut "Pad / Track" at
        /// the right); never outside its set.
        /// </summary>
        internal (FormattedText Text, Rect Bounds, double Scale) SetLabel(int s)
        {
            var layout = grid.Layout;
            var cs = layout.Pattern.ColumnSets[s];
            double x = layout.SetX(s), w = layout.SetWidth(s);
            var label = new FormattedText(cs.Label ?? "", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont, 12,
                cs.Muted ? PatternTheme.ColumnSetLabelMuted : PatternTheme.ColumnSetLabel);
            double natural = Math.Max(1, label.WidthIncludingTrailingWhitespace);
            label.MaxTextWidth = natural;
            label.TextAlignment = TextAlignment.Center;
            double scale = Math.Min(1.0, Math.Min((w - 4) / natural, (PatternGrid.SetLabelHeight - 4) / Math.Max(1, label.Height)));
            double width = natural * scale, height = label.Height * scale;
            double left = Stick(x, w, width, 2);
            return (label, new Rect(left, PatternGrid.SetLabelHeight / 2 - height / 2, width, height), scale);
        }

        /// <summary>
        /// A column's name, turned upright, and where it goes (the beats' coordinates, the box it
        /// fills once turned): centred in the part of its column in view, so a column half
        /// scrolled out keeps its name readable (gallery mac-051 cut "Closed hat tune" at the left).
        /// </summary>
        internal (FormattedText Text, Rect Bounds) ColumnLabel(int s, int c)
        {
            var layout = grid.Layout;
            var cs = layout.Pattern.ColumnSets[s];
            double cx = layout.SetX(s);
            for (int i = 0; i < c; i++) cx += layout.ColumnWidthAt(s, i);
            int cw = layout.ColumnWidthAt(s, c);
            var name = new FormattedText(cs.Columns[c].Label ?? "", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, LabelFont, 10, PatternTheme.ColumnLabel)
            { MaxTextWidth = PatternGrid.ColumnLabelHeight - 4, Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
            double thickness = name.Height, length = Math.Min(name.WidthIncludingTrailingWhitespace, PatternGrid.ColumnLabelHeight - 4);
            double left = Stick(cx, cw, thickness, 0);
            double bottom = PatternGrid.SetLabelHeight + PatternGrid.ColumnLabelHeight - 2;
            return (name, new Rect(left, bottom - length, thickness, length));
        }

        /// <summary>
        /// Where something <paramref name="width"/> wide goes across a span (start, width): centred in
        /// the span's part in view when it fits there, else as far into view as the span allows.
        /// </summary>
        double Stick(double start, double span, double width, double pad)
        {
            double ox = grid.Offset.X, view = Bounds.Width > 0 ? Bounds.Width : double.PositiveInfinity;
            double lo = Math.Max(start, ox), hi = Math.Min(start + span, ox + view);
            double first = start + pad, last = start + span - pad - width;      // inside the span
            if (last < first) return start + span / 2 - width / 2;              // wider than the span: centred, clipped by it
            double left = hi > lo ? (lo + hi) / 2 - width / 2 : start + span / 2 - width / 2;
            if (hi - lo >= width + 2 * pad) left = Math.Clamp(left, lo + pad, hi - pad - width);
            else left = Math.Clamp(lo + pad, first, last);
            return Math.Clamp(left, first, last);
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
