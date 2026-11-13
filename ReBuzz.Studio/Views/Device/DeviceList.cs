// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70): the device view's list, drawn by one control. Only the rows in view are drawn
// and hit-tested (433 parameters cost what 20 do: the old window made a control tree of every row, 0.3-0.4 s), so opening a machine
// with a very large parameter set is a layout of one panel. Rows are 24 pt: name, a slider filled from zero (or a segmented control
// of up to four choices, or a toggle) and the value with its unit. A double click types a value, Alt-click (⌥) resets, a drag is one
// undo step, the wheel and arrows are steps that merge, the track parameters are one table and the macro strip is eight sliders.

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
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    internal sealed class DeviceList : Control
    {
        public const double Pad = 12;
        const double LabelColumn = 44;        // a table's track labels
        const double MinColumn = 64;          // a table's narrowest column
        const double DragThreshold = 3;
        static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-us");
        static readonly Typeface Font = new Typeface(StudioTokens.UiFont);
        static readonly Typeface Bold = new Typeface(StudioTokens.UiFont, FontStyle.Normal, FontWeight.SemiBold);

        readonly IMachine machine;
        readonly TextBox editor;
        readonly Dictionary<DeviceParam, (int Raw, string Text)> texts = new();
        List<DeviceItem> items = new();
        double[] tops = Array.Empty<double>();
        double total, scrollTop, viewport = 600;
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

        /// <summary>Rows, headers and tables drawn by the last render (virtualised: the ones in view).</summary>
        public int RenderedItems => visibleRows;

        public bool IsTyping => editing != null;

        public DeviceParam Typing => editing;

        public string TypedText { get => editor.Text; set => editor.Text = value; }

        public TextBox Editor => editor;

        public string TypeError { get; private set; }

        public double ContentHeight => total;

        public event Action<DeviceParam> FocusChanged;

        /// <summary>A header was clicked: its section closes or opens.</summary>
        public event Action<DeviceSection> HeaderClicked;

        public void SetItems(IEnumerable<DeviceItem> list)
        {
            CloseEditor();
            items = list.ToList();
            tops = new double[items.Count + 1];
            for (int i = 0; i < items.Count; i++) tops[i + 1] = tops[i] + items[i].Height;
            total = tops[^1];
            if (focus != null && !Contains(focus)) SetFocus(null);
            InvalidateMeasure();
            InvalidateVisual();
        }

        bool Contains(DeviceParam p) => items.Any(i => i is ParamItem pi && pi.Param == p || i is MacroItem mi && mi.Cells.Contains(p) || i is TableItem ti && ti.Section.TableCells.Contains(p));

        /// <summary>What is in view (the scroller tells it; tests set it).</summary>
        public void SetViewport(double top, double height)
        {
            scrollTop = Math.Max(0, top);
            viewport = Math.Max(1, height);
            InvalidateVisual();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (this.FindAncestorOfType<ScrollViewer>() is { } sv)
            {
                scroller = sv;
                sv.ScrollChanged += Scrolled;
                Scrolled(sv, null);
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (scroller != null) scroller.ScrollChanged -= Scrolled;
            scroller = null;
        }

        ScrollViewer scroller;

        void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            if (scroller == null) return;
            double top = scroller.Offset.Y, height = scroller.Viewport.Height > 0 ? scroller.Viewport.Height : scroller.Bounds.Height;
            if (top == scrollTop && height == viewport) return;
            SetViewport(top, height);
        }

        protected override Size MeasureOverride(Size available)
        {
            editor.Measure(new Size(200, 22));
            double need = items.OfType<TableItem>().Select(t => Pad * 2 + LabelColumn + MinColumn * t.Columns.Count).DefaultIfEmpty(0).Max();
            double w = double.IsInfinity(available.Width) ? Math.Max(240, need) : Math.Max(available.Width, need);
            return new Size(w, total);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            PlaceEditor(finalSize.Width);
            return finalSize;
        }

        // ------------------------------------------------------------------ geometry

        sealed class Cols
        {
            public double Name, ControlX, ControlW, ValueX, ValueW;
        }

        static Cols ColumnsOf(double width)
        {
            double nameW = Math.Clamp(width * 0.34, 110, 240);
            double valueW = 96;
            double controlX = Pad + nameW + 8;
            double controlW = Math.Max(48, width - Pad - valueW - 8 - controlX);
            return new Cols { Name = nameW, ControlX = controlX, ControlW = controlW, ValueX = width - Pad - valueW, ValueW = valueW };
        }

        int IndexAt(double y)
        {
            if (items.Count == 0 || y < 0 || y >= total) return -1;
            int lo = 0, hi = items.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (tops[mid] <= y) lo = mid; else hi = mid - 1;
            }
            return lo;
        }

        /// <summary>The first and last items in view (one more each side).</summary>
        (int First, int Last) Range()
        {
            if (items.Count == 0) return (0, -1);
            int first = Math.Max(0, IndexAt(scrollTop));
            int last = IndexAt(Math.Min(total - 0.5, scrollTop + viewport));
            if (last < 0) last = items.Count - 1;
            return (first, last);
        }

        /// <summary>The row of a parameter in the list (header and table excluded), in the list's coordinates; false when it isn't shown.</summary>
        public bool TryGetBounds(DeviceParam p, out Rect row)
        {
            double width = Bounds.Width > 0 ? Bounds.Width : 600;
            for (int i = 0; i < items.Count; i++)
            {
                switch (items[i])
                {
                    case ParamItem pi when pi.Param == p:
                        row = new Rect(0, tops[i], width, DeviceItem.RowHeight);
                        return true;
                    case MacroItem mi when mi.Cells.Contains(p):
                        row = CellRect(mi, mi.Cells.ToList().IndexOf(p), width);
                        return true;
                    case TableItem ti when ti.Section.TableCells.Contains(p):
                    {
                        int at = ti.Section.TableCells.IndexOf(p);
                        row = TableCellRect(ti, tops[i], at / ti.Section.Params.Count, ti.Columns.IndexOf(at % ti.Section.Params.Count), width);
                        return row.Width > 0;
                    }
                }
            }
            row = default;
            return false;
        }

        /// <summary>The control of a parameter's row (the slider, the segments, the toggle), in the list's coordinates.</summary>
        public Rect ControlBounds(DeviceParam p)
        {
            if (!TryGetBounds(p, out var row)) return default;
            if (p.IsTrack || items.OfType<MacroItem>().Any(m => m.Cells.Contains(p))) return row;
            var c = ColumnsOf(row.Width);
            return new Rect(c.ControlX, row.Y + 3, c.ControlW, DeviceItem.RowHeight - 6);
        }

        /// <summary>The rectangle of a segment of a segmented control.</summary>
        public Rect SegmentBounds(DeviceParam p, int choice)
        {
            var r = ControlBounds(p);
            double w = r.Width / Math.Max(1, p.Choices.Count);
            return new Rect(r.X + choice * w, r.Y, w, r.Height);
        }

        public Rect ValueBounds(DeviceParam p)
        {
            if (!TryGetBounds(p, out var row)) return default;
            if (p.IsTrack || items.OfType<MacroItem>().Any(m => m.Cells.Contains(p))) return row;
            var c = ColumnsOf(row.Width);
            return new Rect(c.ValueX, row.Y, c.ValueW, DeviceItem.RowHeight);
        }

        static Rect CellRect(MacroItem m, int index, double width)
        {
            double w = width / Math.Max(4, Math.Min(m.Cells.Count, DeviceLayout.MacroCount));
            return new Rect(index * w, 0, w, MacroItem.StripHeight);
        }

        Rect TableCellRect(TableItem t, double top, int track, int columnIndex, double width)
        {
            if (columnIndex < 0) return default;
            double colW = TableColumnWidth(t, width);
            return new Rect(Pad + LabelColumn + columnIndex * colW, top + DeviceItem.RowHeight * (track + 1), colW, DeviceItem.RowHeight);
        }

        static double TableColumnWidth(TableItem t, double width) => Math.Max(MinColumn, (width - Pad * 2 - LabelColumn) / Math.Max(1, t.Columns.Count));

        // ------------------------------------------------------------------ drawing

        static IBrush Brush(Color c) => StudioTokens.Brush(c);

        FormattedText Text(string s, double size, IBrush brush, double width, bool bold = false, TextAlignment align = TextAlignment.Left)
        {
            return new FormattedText(s ?? "", Culture, FlowDirection.LeftToRight, bold ? Bold : Font, size, brush)
            {
                MaxTextWidth = Math.Max(1, width),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = align,
            };
        }

        void Draw(DrawingContext ctx, string s, double x, double y, double width, double height, double size, IBrush brush, bool bold = false, TextAlignment align = TextAlignment.Left)
        {
            var ft = Text(s, size, brush, width, bold, align);
            ctx.DrawText(ft, new Point(x, y + (height - ft.Height) / 2));
        }

        string TextOf(DeviceParam p, int raw)
        {
            if (texts.TryGetValue(p, out var t) && t.Raw == raw) return t.Text;
            string text = p.Describe(raw);
            texts[p] = (raw, text);
            return text;
        }

        public override void Render(DrawingContext ctx)
        {
            double width = Bounds.Width;
            ctx.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));       // the whole list takes the pointer
            var (first, last) = Range();
            visibleRows = 0;
            for (int i = first; i <= last && i < items.Count; i++)
            {
                visibleRows++;
                double y = tops[i];
                switch (items[i])
                {
                    case HeaderItem h: DrawHeader(ctx, h, y, width); break;
                    case ParamItem p: DrawRow(ctx, p.Param, y, width); break;
                    case TableItem t: DrawTable(ctx, t, y, width); break;
                    case MacroItem m: DrawMacros(ctx, m, width); break;
                }
            }
        }

        void DrawHeader(DrawingContext ctx, HeaderItem h, double y, double width)
        {
            Draw(ctx, (h.Collapsed ? "▸ " : "▾ ") + h.Section.Name, Pad, y + 4, width - Pad * 2 - 40, DeviceItem.HeaderHeight - 4, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush, bold: true);
            Draw(ctx, h.Count.ToString(CultureInfo.InvariantCulture), width - Pad - 40, y + 4, 40, DeviceItem.HeaderHeight - 4, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush, align: TextAlignment.Right);
            ctx.FillRectangle(StudioTokens.SurfaceBorderBrush, new Rect(Pad, y + DeviceItem.HeaderHeight - 1, Math.Max(0, width - Pad * 2), 1));
        }

        void DrawRow(DrawingContext ctx, DeviceParam p, double y, double width)
        {
            var c = ColumnsOf(width);
            var row = new Rect(0, y, width, DeviceItem.RowHeight);
            if (p == focus) ctx.FillRectangle(StudioTokens.SelectionFillBrush, row);
            else if (p == hover) ctx.FillRectangle(StudioTokens.SurfaceControlBrush, row);
            Draw(ctx, p.Name, Pad, y, c.Name, DeviceItem.RowHeight, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush);
            int raw = p.Value;
            var area = new Rect(c.ControlX, y + 3, c.ControlW, DeviceItem.RowHeight - 6);
            DrawControl(ctx, p, raw, area);
            if (editing != p) Draw(ctx, TextOf(p, raw), c.ValueX, y, c.ValueW, DeviceItem.RowHeight, StudioTokens.FontBody, StudioTokens.TextPrimaryBrush, align: TextAlignment.Right);
        }

        /// <summary>The control of one parameter in <paramref name="area"/>: a slider filled from zero, segments or a toggle.</summary>
        void DrawControl(DrawingContext ctx, DeviceParam p, int raw, Rect area)
        {
            switch (p.Kind)
            {
                case DeviceKind.Segmented:
                    double sw = area.Width / Math.Max(1, p.Choices.Count);
                    for (int i = 0; i < p.Choices.Count; i++)
                    {
                        bool on = raw == p.Min + i;
                        var seg = new Rect(area.X + i * sw, area.Y, sw, area.Height);
                        var back = on ? StudioTokens.AccentBrush : StudioTokens.SurfaceControlBrush;
                        ctx.DrawRectangle(back, new Pen(StudioTokens.SurfaceBorderBrush, 1), seg.Deflate(0.5), 4, 4);
                        Draw(ctx, p.Choices[i], seg.X + 3, seg.Y, seg.Width - 6, seg.Height, StudioTokens.FontCaption,
                            on ? StudioTokens.TextOnBrush(StudioTokens.Accent) : StudioTokens.TextSecondaryBrush, align: TextAlignment.Center);
                    }
                    break;
                case DeviceKind.Toggle:
                    bool isOn = raw != p.Min;
                    double tw = Math.Min(36, area.Width), th = 16;
                    var track = new Rect(area.X, area.Y + (area.Height - th) / 2, tw, th);
                    ctx.DrawRectangle(isOn ? StudioTokens.AccentBrush : StudioTokens.SurfaceControlBrush, new Pen(StudioTokens.SurfaceBorderBrush, 1), track, 8, 8);
                    double knob = th - 6;
                    ctx.DrawEllipse(isOn ? StudioTokens.TextOnBrush(StudioTokens.Accent) : StudioTokens.TextSecondaryBrush, null,
                        new Point(isOn ? track.Right - 3 - knob / 2 : track.X + 3 + knob / 2, track.Center.Y), knob / 2, knob / 2);
                    break;
                default:
                    DrawSlider(ctx, p, raw, area);
                    break;
            }
        }

        void DrawSlider(DrawingContext ctx, DeviceParam p, int raw, Rect area)
        {
            const double bar = 6;
            var rail = new Rect(area.X, area.Center.Y - bar / 2, area.Width, bar);
            ctx.DrawRectangle(StudioTokens.SurfaceControlBrush, new Pen(StudioTokens.SurfaceBorderBrush, 1), rail, 3, 3);
            double a = p.ZeroFraction, b = p.Fraction(raw);
            double x0 = rail.X + Math.Min(a, b) * rail.Width, x1 = rail.X + Math.Max(a, b) * rail.Width;
            if (x1 - x0 >= 1) ctx.DrawRectangle(StudioTokens.AccentBrush, null, new Rect(x0, rail.Y, x1 - x0, bar), 3, 3);
            double tx = rail.X + b * rail.Width;
            ctx.FillRectangle(StudioTokens.TextPrimaryBrush, new Rect(Math.Clamp(tx - 1, rail.X, rail.Right - 2), area.Y + 1, 2, Math.Max(2, area.Height - 2)));
        }

        void DrawTable(DrawingContext ctx, TableItem t, double y, double width)
        {
            double colW = TableColumnWidth(t, width);
            for (int k = 0; k < t.Columns.Count; k++)
                Draw(ctx, t.Section.Params[t.Columns[k]].Name, Pad + LabelColumn + k * colW + 4, y, colW - 8, DeviceItem.RowHeight, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush);
            for (int tr = 0; tr < t.Section.Tracks; tr++)
            {
                double ry = y + DeviceItem.RowHeight * (tr + 1);
                Draw(ctx, "T" + tr.ToString(CultureInfo.InvariantCulture), Pad, ry, LabelColumn - 4, DeviceItem.RowHeight, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush);
                for (int k = 0; k < t.Columns.Count; k++)
                {
                    var p = t.Section.Cell(tr, t.Columns[k]);
                    var cell = new Rect(Pad + LabelColumn + k * colW, ry, colW, DeviceItem.RowHeight).Deflate(new Thickness(2, 2));
                    DrawCell(ctx, p, cell);
                }
            }
        }

        /// <summary>A table cell or a macro: the value as a bar from zero with its text over it (a choice or a switch: the text alone).</summary>
        void DrawCell(DrawingContext ctx, DeviceParam p, Rect cell)
        {
            int raw = p.Value;
            bool isFocus = p == focus;
            ctx.DrawRectangle(StudioTokens.SurfaceControlBrush, new Pen(isFocus ? StudioTokens.AccentBrush : StudioTokens.SurfaceBorderBrush, 1), cell, 3, 3);
            if (p.Kind == DeviceKind.Slider)
            {
                double a = p.ZeroFraction, b = p.Fraction(raw);
                double x0 = cell.X + Math.Min(a, b) * cell.Width, x1 = cell.X + Math.Max(a, b) * cell.Width;
                if (x1 - x0 >= 1) ctx.DrawRectangle(StudioTokens.SelectionFillBrush, null, new Rect(x0, cell.Y + 1, x1 - x0, cell.Height - 2), 2, 2);
                ctx.FillRectangle(StudioTokens.AccentBrush, new Rect(Math.Clamp(cell.X + b * cell.Width - 1, cell.X + 1, cell.Right - 3), cell.Y + 1, 2, cell.Height - 2));
            }
            else if (p.Kind == DeviceKind.Toggle && raw != p.Min)
                ctx.DrawRectangle(StudioTokens.SelectionFillBrush, null, cell.Deflate(1), 2, 2);
            if (editing != p)
            {
                // "50 · 0 st": in a narrow cell the machine's detail alone (the bar already says where in the range it is), and a backing
                // under the text so the marker line never runs through it (it looked overprinted).
                string text = TextOf(p, raw);
                if (cell.Width < 90 && text.IndexOf(" · ", StringComparison.Ordinal) is int dot and > 0) text = text.Substring(dot + 3);
                var ft = Text(text, StudioTokens.FontCaption, StudioTokens.TextPrimaryBrush, cell.Width - 8, align: TextAlignment.Center);
                if (p.Kind == DeviceKind.Slider)
                {
                    double w = Math.Min(cell.Width - 4, ft.Width + 8);
                    ctx.DrawRectangle(StudioTokens.SurfaceControlBrush, null, new Rect(cell.Center.X - w / 2, cell.Y + 3, w, cell.Height - 6), 3, 3);
                }
                ctx.DrawText(ft, new Point(cell.X + 4, cell.Y + (cell.Height - ft.Height) / 2));
            }
        }

        void DrawMacros(DrawingContext ctx, MacroItem m, double width)
        {
            for (int i = 0; i < m.Cells.Count; i++)
            {
                var r = CellRect(m, i, width).Deflate(new Thickness(4, 4));
                var p = m.Cells[i];
                Draw(ctx, p.MacroLabel, r.X + 2, r.Y, r.Width - 4, 16, StudioTokens.FontCaption, StudioTokens.TextSecondaryBrush);
                DrawCell(ctx, p, new Rect(r.X, r.Y + 20, r.Width, r.Height - 20));
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
            int i = IndexAt(pt.Y);
            var none = new Hit { Part = Part.None };
            if (i < 0) return none;
            double width = Bounds.Width > 0 ? Bounds.Width : 600;
            double y = tops[i];
            switch (items[i])
            {
                case HeaderItem h:
                    return new Hit { Part = Part.Header, Header = h, Area = new Rect(0, y, width, DeviceItem.HeaderHeight) };
                case ParamItem pi:
                {
                    var c = ColumnsOf(width);
                    var p = pi.Param;
                    var area = new Rect(c.ControlX, y + 3, c.ControlW, DeviceItem.RowHeight - 6);
                    var hit = new Hit { Param = p, Area = area };
                    if (pt.X < c.ControlX - 4) hit.Part = Part.Name;
                    else if (pt.X >= c.ValueX) { hit.Part = Part.Value; hit.Area = new Rect(c.ValueX, y, c.ValueW, DeviceItem.RowHeight); }
                    else
                    {
                        hit.Part = Part.Control;
                        if (p.Kind == DeviceKind.Segmented) hit.Choice = (int)Math.Clamp((pt.X - area.X) / (area.Width / p.Choices.Count), 0, p.Choices.Count - 1);
                    }
                    return hit;
                }
                case TableItem ti:
                {
                    int track = (int)((pt.Y - y) / DeviceItem.RowHeight) - 1;
                    if (track < 0 || track >= ti.Section.Tracks) return none;
                    double colW = TableColumnWidth(ti, width);
                    int k = (int)Math.Floor((pt.X - Pad - LabelColumn) / colW);
                    if (k < 0 || k >= ti.Columns.Count) return none;
                    var p = ti.Section.Cell(track, ti.Columns[k]);
                    return new Hit { Part = Part.Cell, Param = p, Area = new Rect(Pad + LabelColumn + k * colW, y + DeviceItem.RowHeight * (track + 1), colW, DeviceItem.RowHeight).Deflate(new Thickness(2, 2)) };
                }
                case MacroItem mi:
                {
                    double w = width / Math.Max(4, mi.Cells.Count);
                    int k = (int)Math.Clamp(pt.X / w, 0, mi.Cells.Count - 1);
                    var r = CellRect(mi, k, width).Deflate(new Thickness(4, 4));
                    if (pt.Y < r.Y + 20) return new Hit { Part = Part.Name, Param = mi.Cells[k], Area = r };
                    return new Hit { Part = Part.Cell, Param = mi.Cells[k], Area = new Rect(r.X, r.Y + 20, r.Width, r.Height - 20) };
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
            double top = scroller.Offset.Y, h = scroller.Viewport.Height;
            if (row.Y < top) scroller.Offset = new Vector(scroller.Offset.X, row.Y);
            else if (row.Bottom > top + h) scroller.Offset = new Vector(scroller.Offset.X, row.Bottom - h);
        }

        IEnumerable<DeviceParam> Navigable() => items.SelectMany(i => i switch
        {
            ParamItem pi => new[] { pi.Param },
            MacroItem mi => mi.Cells,
            TableItem ti => ti.Columns.SelectMany(c => Enumerable.Range(0, ti.Section.Tracks).Select(t => ti.Section.Cell(t, c))),
            _ => Array.Empty<DeviceParam>(),
        });

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
            var h = HitTest(pt).Param;
            if (h != hover)
            {
                hover = h;
                InvalidateVisual();
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (hover != null && dragParam == null)
            {
                hover = null;
                InvalidateVisual();
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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
                case DeviceKind.Segmented when hit.Part == Part.Control:
                    DeviceEdits.Set(Core, p, p.Min + hit.Choice);
                    break;
                case DeviceKind.Toggle:
                    DeviceEdits.Set(Core, p, p.Value == p.Min ? p.Max : p.Min);
                    break;
                case DeviceKind.Segmented:                  // a table or macro cell of choices: the next choice
                    DeviceEdits.Set(Core, p, p.Value >= p.Max ? p.Min : p.Value + 1);
                    break;
                default:
                    if (hit.Part == Part.Value) break;
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
            PlaceEditor(Bounds.Width);
            InvalidateVisual();
            editor.Focus();
            editor.SelectAll();
        }

        void PlaceEditor(double width)
        {
            if (editing == null || !TryGetBounds(editing, out _)) return;
            var r = ValueBounds(editing);
            var box = editing.IsTrack || items.OfType<MacroItem>().Any(m => m.Cells.Contains(editing)) ? r.Deflate(new Thickness(2, 1)) : new Rect(r.X, r.Y + 1, r.Width, r.Height - 2);
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
            var (first, last) = Range();
            bool changed = false;
            for (int i = first; i <= last && i < items.Count; i++)
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
            MacroItem mi => mi.Cells,
            TableItem ti => ti.Columns.SelectMany(c => Enumerable.Range(0, ti.Section.Tracks).Select(t => ti.Section.Cell(t, c))),
            _ => Array.Empty<DeviceParam>(),
        };
    }
}
