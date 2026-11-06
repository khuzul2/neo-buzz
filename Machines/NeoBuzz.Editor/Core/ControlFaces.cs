#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): layout, hit-testing and
// drawing of the kit's small controls (choice buttons, drop-down, button, preset bar). The toolkit
// controls in Controls.cs and NeoEditor.cs hold the state and route input here.

using System;

namespace NeoBuzz.Editor
{
    /// <summary>A label over a row (or column) of buttons, one per choice.</summary>
    internal sealed class ChoiceStrip
    {
        public const double LabelHeight = 18, TextSize = 11;
        readonly string[] names;

        /// <param name="cellWidth">The width of a button; 0 sizes them to the longest name.</param>
        public ChoiceStrip(string label, string[] names, double cellWidth, bool vertical, INeoTextMeasure measure)
        {
            Label = label ?? "";
            this.names = names;
            Vertical = vertical;
            CellWidth = cellWidth > 0 ? cellWidth : Math.Max(34, Longest(names, measure) + 14);
        }

        public string Label { get; }
        public bool Vertical { get; }
        public double CellWidth { get; }
        public int Count => names.Length;
        public double Width => Vertical ? CellWidth : CellWidth * names.Length;
        public double Height => Vertical ? LabelHeight + names.Length * 22 : LabelHeight + 24;

        static double Longest(string[] names, INeoTextMeasure measure)
        {
            double w = 0;
            foreach (var n in names) w = Math.Max(w, measure.MeasureText(n, TextSize));
            return w;
        }

        public NeoRect Cell(int i) => Vertical
            ? new NeoRect(0, LabelHeight + i * 22, CellWidth, 20)
            : new NeoRect(i * CellWidth, LabelHeight, CellWidth - 2, 22);

        /// <summary>The choice under a point, or −1.</summary>
        public int CellAt(double x, double y)
        {
            if (y < LabelHeight) return -1;
            int i = Vertical ? (int)((y - LabelHeight) / 22) : (int)(x / CellWidth);
            return i >= 0 && i < names.Length ? i : -1;
        }

        public void Draw(INeoCanvas c, int selected, int hover, NeoColor accent)
        {
            c.Text(Label, 1, 0, TextSize, Palette.DimText);
            var accentFill = accent.WithAlpha(0x38);
            for (int i = 0; i < names.Length; i++)
            {
                var r = Cell(i);
                bool on = i == selected;
                c.Rect(r, on ? accentFill : i == hover ? Palette.Track : Palette.Panel,
                    new NeoStroke(on ? accent : Palette.PanelEdge, 1), 3);
                c.Text(names[i], r.X + r.Width / 2, r.Y + (r.Height - 15) / 2, TextSize, on ? accent : Palette.Text, on, NeoAlign.Center);
            }
        }
    }

    /// <summary>
    /// An Off/On switch as one toggle named for its function ("Solo", "Auto makeup"): lit in the accent, with its lamp
    /// on, when on; plain when off. It stands where a row of choice buttons would (the same height, the button on
    /// the buttons' line), so layouts keep their places. UI gallery review: drawn as "Off | On" segments, the lit
    /// segment was often "Off", so an accent-lit control could mean off.
    /// </summary>
    internal sealed class ToggleFace
    {
        public const double TextSize = 11, Lamp = 3, Padding = 6, Gap = 4;

        /// <param name="minWidth">The least width (a row of two choice cells' width keeps a layout's place).</param>
        public ToggleFace(string label, double minWidth, INeoTextMeasure measure)
        {
            Label = label ?? "";
            double text = measure?.MeasureText(Label, TextSize, bold: true) ?? Label.Length * 6;
            Width = Math.Ceiling(Math.Max(Math.Max(minWidth, 34), Padding + 2 * Lamp + Gap + text + Padding + 2));
        }

        public string Label { get; }
        public double Width { get; }
        public double Height => ChoiceStrip.LabelHeight + 24;

        /// <summary>The button (on the line choice buttons stand on).</summary>
        public NeoRect Button => new NeoRect(0, ChoiceStrip.LabelHeight, Width - 2, 22);

        public bool Hit(double x, double y) => Button.Contains(x, y);

        public void Draw(INeoCanvas c, bool on, bool hover, NeoColor accent)
        {
            var r = Button;
            c.Rect(r, on ? accent.WithAlpha(0x38) : hover ? Palette.Track : Palette.Panel, new NeoStroke(on ? accent : Palette.PanelEdge, 1), 3);
            double cy = r.Y + r.Height / 2;
            var lamp = new NeoPoint(r.X + Padding + Lamp, cy);
            if (on) c.Ellipse(lamp, Lamp, Lamp, accent, NeoStroke.None);
            else c.Ellipse(lamp, Lamp, Lamp, NeoFill.None, new NeoStroke(Palette.DimText, 1));
            double textX = r.X + Padding + 2 * Lamp + Gap;
            c.Text(Label, textX, r.Y + (r.Height - 15) / 2, TextSize, on ? accent : Palette.Text, on, NeoAlign.Left, maxWidth: Math.Max(1, r.Right - textX - 2));
        }
    }

    /// <summary>A label over a box showing the current choice (the list itself is the toolkit's menu).</summary>
    internal static class DropdownFace
    {
        public const double Height = 18 + 22;

        public static string TextOf(string[] names, int selected) =>
            selected >= 0 && selected < names.Length ? names[selected] : "?";

        public static void Draw(INeoCanvas c, double width, string label, string text, NeoColor accent)
        {
            c.Text(label, 1, 0, 11, Palette.DimText);
            var r = new NeoRect(0, 18, width, 22);
            c.Rect(r, Palette.Background, new NeoStroke(Palette.PanelEdge, 1), 3);
            c.Text(text, 6, 21, 11.5, Palette.Text, maxWidth: Math.Max(10, width - 22));
            c.Text("▾", width - 14, 21, 11, accent);
        }
    }

    /// <summary>A small button in the theme: outlined in the accent, filled while hovered.</summary>
    internal static class ButtonFace
    {
        public const double Height = 24;

        public static void Draw(INeoCanvas c, double width, double height, string text, bool hover, NeoColor accent)
        {
            var r = new NeoRect(0, 0, width, height);
            c.Rect(r, hover ? accent.WithAlpha(0x38) : Palette.Panel, new NeoStroke(accent, 1), 4);
            c.Text(text, width / 2, 4, 11.5, accent, align: NeoAlign.Center);
        }
    }

    /// <summary>
    /// The preset bar: previous, the current preset's name (click for the list) with an "edited" mark once the
    /// parameters differ from it, next, and Save.
    /// </summary>
    internal static class PresetBarFace
    {
        public const double ArrowW = 22, NameW = 170, SaveW = 44, H = 24;
        /// <summary>The mark after the name when the parameters differ from the preset.</summary>
        public const string EditedMark = "edited";
        const double MarkSize = 10;
        public const double Width = ArrowW * 2 + NameW + SaveW + 8;
        const double SaveX = ArrowW * 2 + NameW + 8;

        public enum Zone { None, Previous, List, Next, Save }

        public static Zone ZoneAt(double x)
        {
            if (x < ArrowW) return Zone.Previous;
            if (x < ArrowW + NameW) return Zone.List;
            if (x < ArrowW * 2 + NameW) return Zone.Next;
            if (x > SaveX) return Zone.Save;
            return Zone.None;
        }

        public static void Draw(INeoCanvas c, string label, NeoColor accent, bool edited = false)
        {
            var edge = new NeoStroke(Palette.PanelEdge, 1);
            c.Rect(new NeoRect(0, 0, ArrowW * 2 + NameW, H), Palette.Panel, edge, 4);
            c.Text("◀", ArrowW / 2, 4, 11, Palette.DimText, align: NeoAlign.Center);
            c.Text("▶", ArrowW * 1.5 + NameW, 4, 11, Palette.DimText, align: NeoAlign.Center);
            c.Line(new NeoPoint(ArrowW, 3), new NeoPoint(ArrowW, H - 3), edge);
            c.Line(new NeoPoint(ArrowW + NameW, 3), new NeoPoint(ArrowW + NameW, H - 3), edge);
            double nameRoom = NameW - 12;
            if (edited)
            {
                // "edited" at the right of the name's field, in the accent; the name gives way to it.
                double mark = c.MeasureText(EditedMark, MarkSize);
                c.Text(EditedMark, ArrowW + NameW - 6, 6, MarkSize, accent, align: NeoAlign.Right);
                nameRoom -= mark + 6;
            }
            c.Text(label, ArrowW + 6, 4, 12, Palette.Text, maxWidth: nameRoom);
            double sx = SaveX;
            c.Rect(new NeoRect(sx, 0, SaveW, H), Palette.Panel, new NeoStroke(accent, 1), 4);
            c.Text("Save", sx + SaveW / 2, 4, 11, accent, align: NeoAlign.Center);
        }
    }
}
