// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70; the parameter panel, List mode): the device view's list, drawn by one control. Only the
// items in view are drawn and hit-tested (433 parameters cost what 20 do: the old window made a control tree of every row, 0.3-0.4 s). A row is
// 24 pt: its name, then a bar filled from zero (centre-filled when bipolar) with the value inside, right-aligned in tabular figures (or a
// segmented control of up to four choices, or a toggle). The rows flow into columns of about 320 pt with a header over each group: floating,
// as many columns as fit, balanced, scrolling down; docked, columns as tall as the panel, scrolling sideways. A double click types a value,
// Alt-click (⌥) resets, a drag is one undo step, the wheel and arrows are steps that merge, and the track parameters are one table.
// Embedded (the table under the knob cards) it draws everything it holds and measures itself.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    internal sealed class DeviceList : Control, IModulationDropSurface
    {
        public const double Pad = 12;
        /// <summary>The width a column of rows is about (List mode).</summary>
        public const double ColumnTarget = 320;
        const double LabelColumn = 44;        // a table's track labels
        const double MinColumn = 64;          // a table's narrowest column
        const double DragThreshold = 3;
        const double BarHeight = 18;
        static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-us");
        static readonly Typeface Font = new Typeface(StudioTokens.UiFont);
        static readonly Typeface Bold = new Typeface(StudioTokens.UiFont, FontStyle.Normal, FontWeight.SemiBold);

        readonly IMachine machine;
        readonly TextBox editor;
        readonly Dictionary<DeviceParam, (int Raw, string Text)> texts = new();
        List<DeviceItem> items = new();
        Rect[] rects = Array.Empty<Rect>();
        double contentW, contentH;
        double scrollLeft, scrollTop, viewW = 600, viewH = 600;
        double layoutW = -1, layoutH = -1;
        DeviceParam focus, hover;
        DeviceParam editing;
        int visibleRows;

        // a drag
        DeviceParam dragParam;
        Rect dragArea;
        int dragFrom;
        Point dragStart;
        bool dragMoved;

        public DeviceList(IMachine machine)
        {
            this.machine = machine;
            Focusable = true;
            ClipToBounds = true;
            editor = new TextBox
            {
                IsVisible = false,
                MinHeight = 0,
                Height = 22,
                Padding = new Thickness(4, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = StudioTokens.FontBody,
                HorizontalContentAlignment = HorizontalAlignment.Right,
            };
            Avalonia.Automation.AutomationProperties.SetName(editor, "Value");
            editor.KeyDown += EditorKeyDown;
            editor.LostFocus += (s, e) => CloseEditor();
            VisualChildren.Add(editor);
            LogicalChildren.Add(editor);
            Cursor = new Cursor(StandardCursorType.Arrow);
        }

        // ------------------------------------------------------------------ what it shows

        public IReadOnlyList<DeviceItem> Items => items;

        /// <summary>The parameter the explain strip speaks of (the last one clicked or arrowed to).</summary>
        public DeviceParam Focused => focus;

        /// <summary>Items drawn by the last render (virtualised: the ones in view).</summary>
        public int RenderedItems => visibleRows;

        public bool IsTyping => editing != null;

        public DeviceParam Typing => editing;

        public string TypedText { get => editor.Text; set => editor.Text = value; }

        public TextBox Editor => editor;

        public string TypeError { get; private set; }

        public double ContentHeight => contentH;

        public double ContentWidth => contentW;

        /// <summary>
        /// Drawn inside another control's scroll (the track table under the knob cards): everything it holds is drawn, it measures itself and
        /// takes no scroll of its own.
        /// </summary>
        public bool Embedded { get; init; }

        bool sideways;

        /// <summary>
        /// Docked: columns as tall as the panel and scrolling sideways (set with the scroll viewer's vertical bar off). Else the columns
        /// are as many as fit the width, balanced, and the list scrolls down.
        /// </summary>
        public bool Sideways
        {
            get => sideways;
            set
            {
                if (sideways == value) return;
                sideways = value;
                layoutW = -1;
                InvalidateMeasure();
            }
        }

        /// <summary>How many columns the rows are in.</summary>
        public int Columns { get; private set; } = 1;

        public event Action<DeviceParam> FocusChanged;

        /// <summary>A header was clicked: its section closes or opens.</summary>
        public event Action<DeviceSection> HeaderClicked;

        /// <summary>The menu of a parameter (a right click): the control it opens over.</summary>
        public event Action<DeviceParam, Control> ContextRequested;

        public void SetItems(IEnumerable<DeviceItem> list)
        {
            CloseEditor();
            items = list.ToList();
            layoutW = -1;
            Layout(Math.Max(1, Bounds.Width > 0 ? Bounds.Width : ViewportWidth()), Bounds.Height > 0 ? Bounds.Height : viewH);
            MeasureNames();
            if (focus != null && !Contains(focus)) SetFocus(null);
            InvalidateMeasure();
            InvalidateVisual();
        }

        bool Contains(DeviceParam p) => items.Any(i => i is ParamItem pi && pi.Param == p || i is TableItem ti && ti.Section.TableCells.Contains(p));

        /// <summary>What is in view (the scroller tells it; tests set it).</summary>
        public void SetViewport(double top, double height) => SetViewport(scrollLeft, top, viewW, height);

        public void SetViewport(double left, double top, double width, double height)
        {
            scrollLeft = Math.Max(0, left);
            scrollTop = Math.Max(0, top);
            viewW = Math.Max(1, width);
            viewH = Math.Max(1, height);
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            ModulationBoard.DragChanged += OnDragChanged;
            if (!Embedded && this.FindAncestorOfType<ScrollViewer>() is { } sv)
            {
                scroller = sv;
                sv.ScrollChanged += Scrolled;
                Scrolled(sv, null);
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            ModulationBoard.DragChanged -= OnDragChanged;
            if (scroller != null) scroller.ScrollChanged -= Scrolled;
            scroller = null;
        }

        ScrollViewer scroller;

        void OnDragChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(InvalidateVisual);

        /// <summary>Drag-to-modulate (ux-overhaul.md P5b): the parameter under the pointer takes a modulator dropped on it.</summary>
        public bool ModulationTargetAt(double x, double y, out IMachine target, out IParameter parameter, out int track)
        {
            var p = HitTest(new Point(x, y)).Param;
            target = machine;
            parameter = p?.Parameter;
            track = p?.Track ?? 0;
            return p != null && !p.IsInput && p.Parameter != null && p.Parameter.Type != ParameterType.Note;
        }

        double ViewportWidth() => scroller is { } sv && sv.Viewport.Width > 0 ? sv.Viewport.Width : scroller is { Bounds.Width: > 0 } b ? b.Bounds.Width : 600;

        void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            if (scroller == null) return;
            double left = scroller.Offset.X, top = scroller.Offset.Y;
            double w = scroller.Viewport.Width > 0 ? scroller.Viewport.Width : scroller.Bounds.Width;
            double h = scroller.Viewport.Height > 0 ? scroller.Viewport.Height : scroller.Bounds.Height;
            // The viewport's width (or, docked, its height) decides the columns: lay them out again when it changed.
            if (!sideways && Math.Abs(w - layoutW) > 0.5 || sideways && Math.Abs(h - layoutH) > 0.5) InvalidateMeasure();
            if (left == scrollLeft && top == scrollTop && w == viewW && h == viewH) return;
            SetViewport(left, top, w, h);
        }

        // ------------------------------------------------------------------ geometry

        /// <summary>The width a track table needs: its labels and a minimum for each column.</summary>
        static double TableNeed(TableItem t) => Pad * 2 + LabelColumn + MinColumn * t.Columns.Count;

        /// <summary>
        /// Lays the items out: header and rows in columns, then (after them) the track table. Vertically (floating): as many columns of
        /// <see cref="ColumnTarget"/> or more as fit <paramref name="width"/>, as short as they can be. Sideways (docked): columns of
        /// <see cref="ColumnTarget"/> as tall as <paramref name="height"/>.
        /// </summary>
        void Layout(double width, double height)
        {
            layoutW = width;
            layoutH = height;
            rects = new Rect[items.Count];
            // The table block (its header and the table) is laid out apart from the columns.
            int block = items.FindIndex(i => i is HeaderItem { Section.IsTable: true } || i is TableItem);
            int n = block < 0 ? items.Count : block;
            double colW, colH;
            int cols = 1;
            if (n == 0) { colW = Math.Max(width, ColumnTarget); colH = 0; }
            else if (sideways)
            {
                colW = ColumnTarget;
                colH = Math.Max(height, DeviceItem.HeaderHeight + DeviceItem.RowHeight * 3);
            }
            else
            {
                int fit = Math.Max(1, (int)(width / ColumnTarget));
                colW = width / fit;
                colH = BalancedHeight(n, fit);
                cols = fit;
            }
            double x = 0, y = 0, bottom = 0;
            int used = n == 0 ? 0 : 1;
            for (int i = 0; i < n; i++)
            {
                double h = items[i].Height;
                // A header never ends a column alone: it goes with its first row.
                double need = h + (items[i] is HeaderItem && i + 1 < n ? items[i + 1].Height : 0);
                if (y > 0 && y + need > colH + 0.5)
                {
                    x += colW;
                    y = 0;
                    used++;
                }
                rects[i] = new Rect(x, y, colW, h);
                y += h;
                bottom = Math.Max(bottom, y);
            }
            Columns = sideways ? used : cols;
            double right = n == 0 ? 0 : (sideways ? x + colW : colW * cols);
            contentW = right;
            contentH = bottom;
            if (block >= 0)
            {
                double tw = items.OfType<TableItem>().Select(TableNeed).DefaultIfEmpty(0).Max();
                double bx = sideways ? right : 0, by = sideways ? 0 : bottom;
                double w = sideways ? tw : Math.Max(width, tw);
                for (int i = block; i < items.Count; i++)
                {
                    double h = items[i].Height;
                    rects[i] = new Rect(bx, by, w, h);
                    by += h;
                }
                contentW = Math.Max(contentW, bx + w);
                contentH = Math.Max(contentH, by);
            }
            if (!sideways) contentW = Math.Max(contentW, width);
        }

        /// <summary>The shortest column height at which the first <paramref name="n"/> items fit in <paramref name="cols"/> columns.</summary>
        double BalancedHeight(int n, int cols)
        {
            double total = 0, tallest = 0;
            for (int i = 0; i < n; i++)
            {
                total += items[i].Height;
                tallest = Math.Max(tallest, items[i].Height);
            }
            if (cols <= 1 || n == 0) return total;
            double h = Math.Max(tallest + DeviceItem.RowHeight, Math.Ceiling(total / cols));
            for (int guard = 0; guard < 200; guard++, h += DeviceItem.RowHeight / 2)
            {
                int used = 1;
                double y = 0;
                for (int i = 0; i < n; i++)
                {
                    double need = items[i].Height + (items[i] is HeaderItem && i + 1 < n ? items[i + 1].Height : 0);
                    if (y > 0 && y + need > h + 0.5)
                    {
                        used++;
                        y = 0;
                    }
                    y += items[i].Height;
                }
                if (used <= cols) return h;
            }
            return total;
        }

        protected override Size MeasureOverride(Size available)
        {
            editor.Measure(new Size(200, 22));
            double width, height;
            if (Embedded)
            {
                width = double.IsInfinity(available.Width) ? items.OfType<TableItem>().Select(TableNeed).DefaultIfEmpty(240).Max() : available.Width;
                height = 400;
            }
            else
            {
                width = double.IsInfinity(available.Width) ? Math.Max(240, ViewportWidth()) : available.Width;
                height = double.IsInfinity(available.Height) ? (scroller is { Viewport.Height: > 0 } sv ? sv.Viewport.Height : viewH) : available.Height;
            }
            Layout(width, height);
            return new Size(Math.Max(1, contentW), contentH);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            PlaceEditor();
            return finalSize;
        }

        sealed class Cols
        {
            public double Name, ControlX, ControlW;
        }

        /// <summary>The widest a name column goes (a longer name is cut, its tooltip says it whole).</summary>
        public const double MaxNameColumn = 160;
        const double MinNameColumn = 56;

        /// <summary>The name column: as wide as the longest name shown, up to <see cref="MaxNameColumn"/>.</summary>
        public double NameColumn => nameColumn;
        double nameColumn = MinNameColumn;

        void MeasureNames()
        {
            double widest = 0;
            foreach (var item in items)
                if (item is ParamItem pi)
                    widest = Math.Max(widest, Text(pi.Param.Label, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush, 1000).WidthIncludingTrailingWhitespace);
            nameColumn = Math.Clamp(Math.Ceiling(widest) + 6, MinNameColumn, MaxNameColumn);
        }

        /// <summary>A row's parts, as offsets in the row: the name, then the bar (or segments, or toggle) to the right padding.</summary>
        Cols ColumnsOf(double width)
        {
            double nameW = Math.Min(nameColumn, Math.Max(MinNameColumn, width * 0.45));
            double controlX = Pad + nameW + 8;
            double controlW = Math.Max(48, width - Pad - controlX);
            return new Cols { Name = nameW, ControlX = controlX, ControlW = controlW };
        }

        /// <summary>The items in view.</summary>
        IEnumerable<int> Visible()
        {
            if (Embedded)
            {
                for (int i = 0; i < items.Count; i++) yield return i;
                yield break;
            }
            var view = new Rect(scrollLeft, scrollTop, viewW, viewH);
            for (int i = 0; i < items.Count; i++)
                if (rects[i].Intersects(view)) yield return i;
        }

        int IndexOf(Point pt)
        {
            for (int i = 0; i < items.Count; i++)
                if (rects[i].Contains(pt)) return i;
            return -1;
        }

        /// <summary>The row of a parameter in the list (header and table excluded), in the list's coordinates; false when it isn't shown.</summary>
        public bool TryGetBounds(DeviceParam p, out Rect row)
        {
            for (int i = 0; i < items.Count; i++)
            {
                switch (items[i])
                {
                    case ParamItem pi when pi.Param == p:
                        row = rects[i];
                        return true;
                    case TableItem ti when ti.Section.TableCells.Contains(p):
                    {
                        int at = ti.Section.TableCells.IndexOf(p);
                        row = TableCellRect(ti, rects[i], at / ti.Section.Params.Count, ti.Columns.IndexOf(at % ti.Section.Params.Count));
                        return row.Width > 0;
                    }
                }
            }
            row = default;
            return false;
        }

        /// <summary>The control of a parameter's row (the bar, the segments, the toggle), in the list's coordinates.</summary>
        public Rect ControlBounds(DeviceParam p)
        {
            if (!TryGetBounds(p, out var row)) return default;
            if (p.IsTrack) return row;
            var c = ColumnsOf(row.Width);
            return new Rect(row.X + c.ControlX, row.Y + 3, c.ControlW, DeviceItem.RowHeight - 6);
        }

        /// <summary>The rectangle of a segment of a segmented control.</summary>
        public Rect SegmentBounds(DeviceParam p, int choice)
        {
            var r = ControlBounds(p);
            double w = r.Width / Math.Max(1, p.Choices.Count);
            return new Rect(r.X + choice * w, r.Y, w, r.Height);
        }

        /// <summary>Where the value is: inside the bar, at its right end.</summary>
        public Rect ValueBounds(DeviceParam p)
        {
            if (!TryGetBounds(p, out var row)) return default;
            if (p.IsTrack) return row;
            var bar = ControlBounds(p);
            double w = Math.Min(96, bar.Width);
            return new Rect(bar.Right - w, row.Y, w, DeviceItem.RowHeight);
        }

        Rect TableCellRect(TableItem t, Rect at, int track, int columnIndex)
        {
            if (columnIndex < 0) return default;
            double colW = TableColumnWidth(t, at.Width);
            return new Rect(at.X + Pad + LabelColumn + columnIndex * colW, at.Y + DeviceItem.RowHeight * (track + 1), colW, DeviceItem.RowHeight);
        }

        static double TableColumnWidth(TableItem t, double width) => Math.Max(MinColumn, (width - Pad * 2 - LabelColumn) / Math.Max(1, t.Columns.Count));

        // ------------------------------------------------------------------ drawing

        static readonly FontFeatureCollection Tabular = FontFeatureCollection.Parse("tnum");

        FormattedText Text(string s, double size, IBrush brush, double width, bool bold = false, TextAlignment align = TextAlignment.Left, bool tabular = false)
        {
            var ft = new FormattedText(s ?? "", Culture, FlowDirection.LeftToRight, bold ? Bold : Font, size, brush)
            {
                MaxTextWidth = Math.Max(1, width),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = align,
            };
            if (tabular) ft.SetFontFeatures(Tabular);    // the digits are all as wide: decimals line up down a column
            return ft;
        }

        void Draw(DrawingContext ctx, string s, double x, double y, double width, double height, double size, IBrush brush, bool bold = false, TextAlignment align = TextAlignment.Left, bool tabular = false)
        {
            var ft = Text(s, size, brush, width, bold, align, tabular);
            ctx.DrawText(ft, new Point(x, y + (height - ft.Height) / 2));
        }

        string TextOf(DeviceParam p, int raw)
        {
            if (texts.TryGetValue(p, out var t) && t.Raw == raw) return t.Text;
            string text = p.Describe(raw);
            texts[p] = (raw, text);
            return text;
        }

        public double ScrollLeft => scrollLeft;

        public double ScrollTop => scrollTop;

        IBrush identBrush = StudioTokens.AccentBrush;
        Color identColor = StudioTokens.Accent;

        /// <summary>The colour every bar, segment and switch here is filled with: the machine's identity colour, as its knobs are, at 3:1 or more against the panel.</summary>
        public Color Identity => StudioTokens.Of(DeviceColour.Of(machine));

        public override void Render(DrawingContext ctx)
        {
            identColor = StudioTokens.Of(DeviceColour.Of(machine));
            identBrush = new SolidColorBrush(identColor);
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));       // the whole list takes the pointer
            visibleRows = 0;
            foreach (int i in Visible())
            {
                visibleRows++;
                switch (items[i])
                {
                    case HeaderItem h: DrawHeader(ctx, h, rects[i]); break;
                    case ParamItem p: DrawRow(ctx, p.Param, rects[i]); break;
                    case TableItem t: DrawTable(ctx, t, rects[i]); break;
                }
            }
        }

        void DrawHeader(DrawingContext ctx, HeaderItem h, Rect r)
        {
            Draw(ctx, (h.Collapsed ? "▸ " : "▾ ") + h.Section.Name, r.X + Pad, r.Y + 4, r.Width - Pad * 2 - 40, DeviceItem.HeaderHeight - 4, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush, bold: true);
            Draw(ctx, h.Count.ToString(CultureInfo.InvariantCulture), r.Right - Pad - 40, r.Y + 4, 40, DeviceItem.HeaderHeight - 4, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush, align: TextAlignment.Right, tabular: true);
            ctx.FillRectangle(StudioTokens.SurfaceBorderBrush, new Rect(r.X + Pad, r.Y + DeviceItem.HeaderHeight - 1, Math.Max(0, r.Width - Pad * 2), 1));
        }

        void DrawRow(DrawingContext ctx, DeviceParam p, Rect row)
        {
            var c = ColumnsOf(row.Width);
            if (p == focus) ctx.FillRectangle(StudioTokens.SelectionFillBrush, row);
            else if (p == hover) ctx.FillRectangle(StudioTokens.SurfaceControlBrush, row);
            Draw(ctx, p.Label, row.X + Pad, row.Y, c.Name, DeviceItem.RowHeight, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush);
            int raw = p.Value;
            var area = new Rect(row.X + c.ControlX, row.Y + 3, c.ControlW, DeviceItem.RowHeight - 6);
            DrawControl(ctx, p, raw, area);
        }

        /// <summary>The control of one parameter in <paramref name="area"/>: a bar filled from zero with its value inside, segments or a toggle.</summary>
        void DrawControl(DrawingContext ctx, DeviceParam p, int raw, Rect area)
        {
            switch (p.Kind)
            {
                case DeviceKind.Segmented when p.Choices.Count > DeviceChoice.StripMax:
                {
                    var box = new Rect(area.X, area.Y, Math.Min(area.Width, 200), area.Height);
                    ctx.DrawRectangle(StudioTokens.SurfaceControlBrush, new Pen(StudioTokens.SurfaceBorderBrush, 1), box.Deflate(0.5), 4, 4);
                    if (editing != p)
                        Draw(ctx, TextOf(p, raw), box.X + 6, box.Y, box.Width - 22, box.Height, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush);
                    Draw(ctx, "\u25BE", box.Right - 16, box.Y, 12, box.Height, StudioTokens.FontCaption, identBrush);
                    break;
                }
                case DeviceKind.Segmented:
                    double sw = area.Width / Math.Max(1, p.Choices.Count);
                    for (int i = 0; i < p.Choices.Count; i++)
                    {
                        bool on = raw == p.Min + i;
                        var seg = new Rect(area.X + i * sw, area.Y, sw, area.Height);
                        var back = on ? identBrush : StudioTokens.SurfaceControlBrush;
                        ctx.DrawRectangle(back, new Pen(StudioTokens.SurfaceBorderBrush, 1), seg.Deflate(0.5), 4, 4);
                        Draw(ctx, p.Choices[i], seg.X + 3, seg.Y, seg.Width - 6, seg.Height, StudioTokens.FontCaption,
                            on ? StudioTokens.TextOnBrush(identColor) : StudioTokens.TextSecondaryBrush, align: TextAlignment.Center);
                    }
                    break;
                case DeviceKind.Toggle:
                    bool isOn = raw != p.Min;
                    double tw = Math.Min(36, area.Width), th = 16;
                    var track = new Rect(area.X, area.Y + (area.Height - th) / 2, tw, th);
                    ctx.DrawRectangle(isOn ? identBrush : StudioTokens.ControlTrackBrush, null, track, 8, 8);
                    double knob = th - 6;
                    ctx.DrawEllipse(isOn ? StudioTokens.TextOnBrush(identColor) : StudioTokens.TextPrimaryBrush, null,
                        new Point(isOn ? track.Right - 3 - knob / 2 : track.X + 3 + knob / 2, track.Center.Y), knob / 2, knob / 2);
                    if (editing != p)
                        Draw(ctx, TextOf(p, raw), area.X + tw + 8, area.Y, Math.Max(1, area.Width - tw - 8), area.Height, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush, align: TextAlignment.Right, tabular: true);
                    break;
                default:
                    DrawBar(ctx, p, raw, area);
                    break;
            }
        }

        // Modulation (neo-patch.md §3, phase NM): the modulations that drive this machine's parameters, looked at a few times a second.
        ModulationInfo[] incoming = Array.Empty<ModulationInfo>();
        long incomingAt;

        ModulationInfo ModulatedBy(DeviceParam p)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - incomingAt > System.Diagnostics.Stopwatch.Frequency / 10)
            {
                incomingAt = now;
                incoming = ModulationBoard.Incoming(machine).ToArray();
            }
            foreach (var i in incoming)
                if (i.Param == p.Parameter.Name && i.IsTrack == p.IsTrack && (!i.IsTrack || i.Track == p.Track)) return i;
            return null;
        }

        /// <summary>The live range ring: where the modulation reaches (an outline in the Control identity colour), the value you set (a tick) and the value now (a dot).</summary>
        void DrawModulationRing(DrawingContext ctx, DeviceParam p, Rect rail)
        {
            var m = ModulatedBy(p);
            if (m == null) return;
            var colour = StudioTokens.Identity(NeoBuzz.Editor.MachineCategory.Control);
            double lo = rail.X + p.Fraction(m.Low) * rail.Width, hi = rail.X + p.Fraction(m.High) * rail.Width;
            var ring = new Rect(lo - 2, rail.Y - 2, Math.Max(6, hi - lo + 4), rail.Height + 4);
            ctx.DrawRectangle(new SolidColorBrush(colour, 0.18), new Pen(new SolidColorBrush(colour), 1.5), ring, 5, 5);
            double bx = rail.X + p.Fraction(m.Base) * rail.Width;
            ctx.FillRectangle(new SolidColorBrush(colour), new Rect(bx - 0.5, rail.Y - 3, 1, rail.Height + 6));
            double cx = rail.X + p.Fraction(m.Current) * rail.Width;
            ctx.DrawEllipse(new SolidColorBrush(colour), new Pen(StudioTokens.SurfaceBaseBrush, 1), new Point(cx, rail.Center.Y), 4, 4);
        }

        /// <summary>Whether a parameter in view is being modulated (tests).</summary>
        internal bool IsModulated(DeviceParam p) => ModulatedBy(p) != null;

        /// <summary>
        /// A slider as a bar (List mode): the track at 3:1 against the panel (WCAG 1.4.11), the fill from zero (the middle, for a range through
        /// it) in the accent up to the value, and the value inside the bar, right-aligned in tabular figures on a backing that keeps it readable.
        /// </summary>
        void DrawBar(DrawingContext ctx, DeviceParam p, int raw, Rect area)
        {
            var rail = new Rect(area.X, area.Center.Y - BarHeight / 2, area.Width, BarHeight);
            ctx.DrawRectangle(StudioTokens.ControlTrackBrush, null, rail, 4, 4);
            double a = p.ZeroFraction, b = p.Fraction(raw);
            double x0 = rail.X + Math.Min(a, b) * rail.Width, x1 = rail.X + Math.Max(a, b) * rail.Width;
            if (x1 - x0 >= 1) ctx.DrawRectangle(identBrush, null, new Rect(x0, rail.Y, x1 - x0, rail.Height), 4, 4);
            double tx = rail.X + b * rail.Width;
            ctx.FillRectangle(StudioTokens.TextPrimaryBrush, new Rect(Math.Clamp(tx - 1, rail.X, rail.Right - 2), rail.Y, 2, rail.Height));
            DrawModulationRing(ctx, p, rail);
            if (editing != p)
            {
                string text = TextOf(p, raw);
                double natural = Text(text, StudioTokens.FontCaption, StudioTokens.TextPrimaryBrush, 1000, tabular: true).WidthIncludingTrailingWhitespace;
                double w = Math.Min(rail.Width - 4, natural + 10);
                var back = new Rect(rail.Right - 3 - w, rail.Y + 2, w, rail.Height - 4);
                ctx.DrawRectangle(StudioTokens.SurfaceControlBrush, null, back, 3, 3);
                var ft = Text(text, StudioTokens.FontCaption, StudioTokens.TextPrimaryBrush, w - 8, align: TextAlignment.Right, tabular: true);
                ctx.DrawText(ft, new Point(back.X + 4, back.Y + (back.Height - ft.Height) / 2));
            }
            if (ModulationBoard.Dragging != null)   // a modulator is being dragged: this takes it
                ctx.DrawRectangle(null, new Pen(new SolidColorBrush(StudioTokens.Identity(NeoBuzz.Editor.MachineCategory.Control)), 1.5, dashStyle: DashStyle.Dash), rail.Inflate(3), 5, 5);
        }

        void DrawTable(DrawingContext ctx, TableItem t, Rect r)
        {
            double colW = TableColumnWidth(t, r.Width);
            for (int k = 0; k < t.Columns.Count; k++)
                Draw(ctx, t.Section.Params[t.Columns[k]].Label, r.X + Pad + LabelColumn + k * colW + 4, r.Y, colW - 8, DeviceItem.RowHeight, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush);
            for (int tr = 0; tr < t.Section.Tracks; tr++)
            {
                double ry = r.Y + DeviceItem.RowHeight * (tr + 1);
                Draw(ctx, "T" + tr.ToString(CultureInfo.InvariantCulture), r.X + Pad, ry, LabelColumn - 4, DeviceItem.RowHeight, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush);
                for (int k = 0; k < t.Columns.Count; k++)
                {
                    var p = t.Section.Cell(tr, t.Columns[k]);
                    var cell = new Rect(r.X + Pad + LabelColumn + k * colW, ry, colW, DeviceItem.RowHeight).Deflate(new Thickness(2, 2));
                    DrawCell(ctx, p, cell);
                }
            }
        }

        /// <summary>A table cell: the value as a bar from zero with its text over it (a choice or a switch: the text alone).</summary>
        void DrawCell(DrawingContext ctx, DeviceParam p, Rect cell)
        {
            int raw = p.Value;
            bool isFocus = p == focus;
            ctx.DrawRectangle(StudioTokens.SurfaceControlBrush, new Pen(isFocus ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush, 1), cell, 3, 3);
            if (p.Kind == DeviceKind.Slider)
            {
                double a = p.ZeroFraction, b = p.Fraction(raw);
                double x0 = cell.X + Math.Min(a, b) * cell.Width, x1 = cell.X + Math.Max(a, b) * cell.Width;
                if (x1 - x0 >= 1) ctx.DrawRectangle(StudioTokens.SelectionFillBrush, null, new Rect(x0, cell.Y + 1, x1 - x0, Math.Max(0, cell.Height - 2)), 2, 2);
                ctx.FillRectangle(identBrush, new Rect(Math.Clamp(cell.X + b * cell.Width - 1, cell.X + 1, Math.Max(cell.X + 1, cell.Right - 3)), cell.Y + 1, 2, Math.Max(0, cell.Height - 2)));
            }
            else if (p.Kind == DeviceKind.Toggle && raw != p.Min)
                ctx.DrawRectangle(StudioTokens.SelectionFillBrush, null, cell.Deflate(1), 2, 2);
            if (editing != p)
            {
                // "50 · 0 st": in a narrow cell the machine's detail alone (the bar already says where in the range it is), and a backing
                // under the text so the marker line never runs through it (it looked overprinted).
                string text = TextOf(p, raw);
                if (cell.Width < 90 && text.IndexOf(" · ", StringComparison.Ordinal) is int dot and > 0) text = text.Substring(dot + 3);
                var ft = Text(text, StudioTokens.FontCaption, StudioTokens.TextPrimaryBrush, cell.Width - 8, align: TextAlignment.Center, tabular: true);
                if (p.Kind == DeviceKind.Slider)
                {
                    double w = Math.Min(cell.Width - 4, ft.Width + 8);
                    ctx.DrawRectangle(StudioTokens.SurfaceControlBrush, null, new Rect(cell.Center.X - w / 2, cell.Y + 3, w, cell.Height - 6), 3, 3);
                }
                ctx.DrawText(ft, new Point(cell.X + 4, cell.Y + (cell.Height - ft.Height) / 2));
            }
        }

        // ------------------------------------------------------------------ hit testing

        public enum Part { None, Header, Name, Control, Value, Cell }

        public sealed class Hit
        {
            public Part Part;
            public DeviceParam Param;
            public Rect Area;
            public HeaderItem Header;
            public int Choice = -1;
        }

        public Hit HitTest(Point pt)
        {
            int i = IndexOf(pt);
            var none = new Hit { Part = Part.None };
            if (i < 0) return none;
            var r = rects[i];
            switch (items[i])
            {
                case HeaderItem h:
                    return new Hit { Part = Part.Header, Header = h, Area = r };
                case ParamItem pi:
                {
                    var c = ColumnsOf(r.Width);
                    var p = pi.Param;
                    var area = new Rect(r.X + c.ControlX, r.Y + 3, c.ControlW, DeviceItem.RowHeight - 6);
                    var hit = new Hit { Param = p, Area = area };
                    if (pt.X < area.X - 4) hit.Part = Part.Name;
                    else
                    {
                        hit.Part = Part.Control;
                        if (p.Kind == DeviceKind.Segmented && p.Choices.Count <= DeviceChoice.StripMax) hit.Choice = (int)Math.Clamp((pt.X - area.X) / (area.Width / p.Choices.Count), 0, p.Choices.Count - 1);
                    }
                    return hit;
                }
                case TableItem ti:
                {
                    int track = (int)((pt.Y - r.Y) / DeviceItem.RowHeight) - 1;
                    if (track < 0 || track >= ti.Section.Tracks) return none;
                    double colW = TableColumnWidth(ti, r.Width);
                    int k = (int)Math.Floor((pt.X - r.X - Pad - LabelColumn) / colW);
                    if (k < 0 || k >= ti.Columns.Count) return none;
                    var p = ti.Section.Cell(track, ti.Columns[k]);
                    return new Hit { Part = Part.Cell, Param = p, Area = new Rect(r.X + Pad + LabelColumn + k * colW, r.Y + DeviceItem.RowHeight * (track + 1), colW, DeviceItem.RowHeight).Deflate(new Thickness(2, 2)) };
                }
            }
            return none;
        }

        // ------------------------------------------------------------------ focus

        public void SetFocus(DeviceParam p)
        {
            if (focus == p) return;
            focus = p;
            InvalidateVisual();
            FocusChanged?.Invoke(p);
        }

        /// <summary>Scrolls a parameter's row into view.</summary>
        public void Reveal(DeviceParam p)
        {
            if (scroller == null || !TryGetBounds(p, out var row)) return;
            var off = scroller.Offset;
            double left = off.X, top = off.Y;
            double w = scroller.Viewport.Width, h = scroller.Viewport.Height;
            if (row.Y < top) top = row.Y;
            else if (row.Bottom > top + h) top = row.Bottom - h;
            if (row.X < left) left = row.X;
            else if (row.Right > left + w) left = Math.Max(row.X, row.Right - w);
            if (left != off.X || top != off.Y) scroller.Offset = new Vector(left, top);
        }

        /// <summary>Scrolls a section's header to the start of the view (a chip was chosen); false when it isn't in the list.</summary>
        public bool JumpTo(DeviceSection section)
        {
            if (scroller == null) return false;
            for (int i = 0; i < items.Count; i++)
                if (items[i] is HeaderItem h && h.Section == section)
                {
                    scroller.Offset = sideways ? new Vector(Math.Max(0, rects[i].X), scroller.Offset.Y) : new Vector(scroller.Offset.X, Math.Max(0, rects[i].Y));
                    return true;
                }
            return false;
        }

        /// <summary>Where a section's header is (its column's left edge, its top), in the list's coordinates; null when it isn't listed.</summary>
        public Rect? HeaderBounds(DeviceSection section)
        {
            for (int i = 0; i < items.Count; i++)
                if (items[i] is HeaderItem h && h.Section == section) return rects[i];
            return null;
        }

        IEnumerable<DeviceParam> Navigable() => items.SelectMany(i => ParamsOf(i));

        // ------------------------------------------------------------------ the pointer

        ReBuzzCore Core => DeviceEdits.CoreOf(machine);

        int ValueAt(DeviceParam p, Rect area, double x)
        {
            double t = area.Width <= 0 ? 0 : Math.Clamp((x - area.X) / area.Width, 0, 1);
            return p.Min + (int)Math.Round(t * (p.Max - p.Min));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pt = e.GetPosition(this);
            if (dragParam != null)
            {
                if (!dragMoved && Math.Abs(pt.X - dragStart.X) < DragThreshold) return;
                dragMoved = true;
                DeviceEdits.SetLive(dragParam, ValueAt(dragParam, dragArea, pt.X));
                InvalidateVisual();
                return;
            }
            var hit = HitTest(pt);
            var h = hit.Param;
            string tip = hit.Part == Part.Name && h != null ? NameTip(h) : null;
            if (tip != nameTip)
            {
                nameTip = tip;
                ToolTip.SetTip(this, tip);
            }
            if (h != hover)
            {
                hover = h;
                InvalidateVisual();
            }
        }

        string nameTip;

        /// <summary>A name's tooltip: the machine's own name for the parameter (the cut one, whole), and its description.</summary>
        public static string NameTip(DeviceParam p) =>
            p.Description.Length > 0 && !string.Equals(p.Description, p.FullName, StringComparison.OrdinalIgnoreCase) ? p.FullName + "\n" + p.Description : p.FullName;

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (hover != null && dragParam == null)
            {
                hover = null;
                InvalidateVisual();
            }
        }

        /// <summary>A drag is on (the view leaves "Changed only" alone until it ends).</summary>
        public bool Dragging => dragParam != null;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                var rp = HitTest(e.GetPosition(this)).Param;
                if (rp == null) return;
                e.Handled = true;
                SetFocus(rp);
                ContextRequested?.Invoke(rp, this);
                return;
            }
            if (!props.IsLeftButtonPressed) return;
            if (editing != null) CloseEditor();
            Focus();
            var hit = HitTest(e.GetPosition(this));
            if (hit.Part == Part.Header)
            {
                HeaderClicked?.Invoke(hit.Header.Section);
                e.Handled = true;
                return;
            }
            var p = hit.Param;
            if (p == null) return;
            e.Handled = true;
            SetFocus(p);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                DeviceEdits.Reset(Core, p);
                InvalidateVisual();
                return;
            }
            if (hit.Part == Part.Name) return;
            if (e.ClickCount == 2 && hit.Part is Part.Control or Part.Value or Part.Cell)
            {
                BeginType(p);
                return;
            }
            switch (p.Kind)
            {
                case DeviceKind.Segmented when hit.Part == Part.Control && p.Choices.Count > DeviceChoice.StripMax:
                {
                    var names = p.Choices.ToArray();
                    var at = hit.Area.BottomLeft;
                    var param = p;
                    PickerPopup.Show(this, at.X - scrollLeft, at.Y - scrollTop, names, i => { DeviceEdits.Set(Core, param, param.Min + i); InvalidateVisual(); }, names.Length > 12);
                    break;
                }
                case DeviceKind.Segmented when hit.Part == Part.Control:
                    DeviceEdits.Set(Core, p, p.Min + hit.Choice);
                    break;
                case DeviceKind.Toggle:
                    DeviceEdits.Set(Core, p, p.Value == p.Min ? p.Max : p.Min);
                    break;
                case DeviceKind.Segmented:                  // a table cell of choices: the next choice
                    DeviceEdits.Set(Core, p, p.Value >= p.Max ? p.Min : p.Value + 1);
                    break;
                default:
                    dragParam = p;
                    dragArea = hit.Area;
                    dragFrom = p.Parameter.GetValue(p.Track);
                    dragStart = e.GetPosition(this);
                    dragMoved = false;
                    e.Pointer.Capture(this);
                    break;
            }
            InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (dragParam == null) return;
            var p = dragParam;
            dragParam = null;
            e.Pointer.Capture(null);
            if (dragMoved) DeviceEdits.Changed(Core, p, dragFrom, p.Parameter.GetValue(p.Track), stepped: false);
            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var p = HitTest(e.GetPosition(this)) is { Part: Part.Control or Part.Cell or Part.Value } h ? h.Param : null;
            if (p == null || Math.Abs(e.Delta.Y) < 0.0001) return;
            e.Handled = true;
            SetFocus(p);
            Nudge(p, e.Delta.Y > 0 ? 1 : -1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }

        /// <summary>A wheel or arrow step; steps on one parameter that follow each other are one undo step.</summary>
        public void Nudge(DeviceParam p, int direction, bool coarse = false)
        {
            int step = p.Kind == DeviceKind.Slider ? DeviceEdits.Step(p) * (coarse ? 10 : 1) : 1;
            int to = Math.Clamp(p.Value + direction * step, p.Min, p.Max);
            DeviceEdits.Set(Core, p, to, stepped: true);
            InvalidateVisual();
        }

        // ------------------------------------------------------------------ the keys

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || editing != null) return;
            var all = Navigable().ToList();
            int at = focus == null ? -1 : all.IndexOf(focus);
            switch (e.Key)
            {
                case Key.Down:
                case Key.Up:
                    if (all.Count == 0) return;
                    var target = all[Math.Clamp(at < 0 ? 0 : at + (e.Key == Key.Down ? 1 : -1), 0, all.Count - 1)];
                    SetFocus(target);
                    Reveal(target);
                    break;
                case Key.Left or Key.Right when focus != null:
                    Nudge(focus, e.Key == Key.Right ? 1 : -1, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                    break;
                case Key.Enter or Key.F2 when focus != null:
                    BeginType(focus);
                    break;
                case Key.Delete or Key.Back when focus != null:
                    DeviceEdits.Reset(Core, focus);
                    InvalidateVisual();
                    break;
                case Key.Home when all.Count > 0:
                    SetFocus(all[0]);
                    Reveal(all[0]);
                    break;
                case Key.End when all.Count > 0:
                    SetFocus(all[^1]);
                    Reveal(all[^1]);
                    break;
                default:
                    return;
            }
            e.Handled = true;
        }

        // ------------------------------------------------------------------ typing a value

        /// <summary>Opens the value for typing over the row (a double click, Enter): "800 Hz", "-6 dB", "35%", a choice's name.</summary>
        public void BeginType(DeviceParam p)
        {
            if (!TryGetBounds(p, out _)) return;
            editing = p;
            TypeError = null;
            editor.Text = TextOf(p, p.Value);
            editor.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.BorderBrushProperty);
            Avalonia.Controls.ToolTip.SetTip(editor, "Type a value with its unit: Enter sets it, Esc cancels");
            editor.IsVisible = true;
            PlaceEditor();
            InvalidateVisual();
            editor.Focus();
            editor.SelectAll();
        }

        void PlaceEditor()
        {
            if (editing == null || !TryGetBounds(editing, out _)) return;
            var r = ValueBounds(editing);
            var box = editing.IsTrack ? r.Deflate(new Thickness(2, 1)) : new Rect(r.X, r.Y + 1, r.Width, r.Height - 2);
            editor.Measure(box.Size);
            editor.Arrange(new Rect(box.X, box.Y, Math.Max(60, box.Width), box.Height));
        }

        void EditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseEditor();
                Focus();
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Return)
            {
                CommitTyped();
                e.Handled = true;
            }
        }

        /// <summary>Sets what was typed as one undo step and closes the box; where it isn't a value the box stays open and says why.</summary>
        public bool CommitTyped()
        {
            if (editing == null) return false;
            string error = DeviceEdits.Type(Core, editing, editor.Text);
            if (error != null)
            {
                TypeError = error;
                editor.BorderBrush = StudioTokens.ErrorTextBrush;
                Avalonia.Controls.ToolTip.SetTip(editor, error);
                return false;
            }
            CloseEditor();
            Focus();
            return true;
        }

        public void CloseEditor()
        {
            if (editing == null) return;
            editing = null;
            editor.IsVisible = false;
            InvalidateVisual();
        }

        /// <summary>Redraws if any value in view changed (the machine, an agent, a pattern, undo).</summary>
        public void Poll()
        {
            bool changed = false;
            foreach (int i in Visible())
            {
                foreach (var p in ParamsOf(items[i]))
                {
                    int v = p.Value;
                    if (!texts.TryGetValue(p, out var t) || t.Raw != v)
                    {
                        changed = true;
                        break;
                    }
                }
                if (changed) break;
            }
            if (changed) InvalidateVisual();
        }

        static IEnumerable<DeviceParam> ParamsOf(DeviceItem item) => item switch
        {
            ParamItem pi => new[] { pi.Param },
            TableItem ti => ti.Columns.SelectMany(c => Enumerable.Range(0, ti.Section.Tracks).Select(t => ti.Section.Cell(t, c))),
            _ => Array.Empty<DeviceParam>(),
        };
    }
}
