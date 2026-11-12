#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the tabs over an editor's
// pages (Pages.cs on WPF and Avalonia), for a machine whose controls don't fit one screen with its
// keyboard under them (Dye Essence, docs/specs/dye-essence.md §8). Layout, hit-testing and drawing;
// the toolkit controls hold the selection and route the pointer here.

using System;

namespace NeoBuzz.Editor
{
    /// <summary>A row of tabs, one per page, named; the page shown lit in the accent and underlined.</summary>
    internal sealed class PageStrip
    {
        public const double Height = 28, TextSize = 12, Padding = 14, Gap = 2;
        readonly string[] names;
        readonly double[] lefts, widths;

        public PageStrip(string[] names, INeoTextMeasure measure)
        {
            this.names = names ?? Array.Empty<string>();
            lefts = new double[this.names.Length];
            widths = new double[this.names.Length];
            double x = 0;
            for (int i = 0; i < this.names.Length; i++)
            {
                // Measured bold, as the page shown draws it, so a tab keeps its width when it lights.
                widths[i] = Math.Ceiling(measure.MeasureText(this.names[i], TextSize, bold: true)) + 2 * Padding;
                lefts[i] = x;
                x += widths[i] + Gap;
            }
            Width = Math.Max(0, x - Gap);
        }

        public int Count => names.Length;

        /// <summary>The tabs' width together.</summary>
        public double Width { get; }

        public string Name(int i) => names[i];

        public NeoRect Tab(int i) => new NeoRect(lefts[i], 0, widths[i], Height - 2);

        /// <summary>The tab under a point, or −1.</summary>
        public int TabAt(double x, double y)
        {
            if (y < 0 || y >= Height) return -1;
            for (int i = 0; i < names.Length; i++)
                if (x >= lefts[i] && x < lefts[i] + widths[i]) return i;
            return -1;
        }

        /// <param name="width">The strip's width (its line runs under the whole of it).</param>
        public void Draw(INeoCanvas c, double width, int selected, int hover, NeoColor accent)
        {
            c.Line(new NeoPoint(0, Height - 0.5), new NeoPoint(width, Height - 0.5), new NeoStroke(Palette.PanelEdge, 1));
            for (int i = 0; i < names.Length; i++)
            {
                var r = Tab(i);
                bool on = i == selected;
                if (on) c.Rect(r, accent.WithAlpha(0x26), NeoStroke.None, 4);
                else if (i == hover) c.Rect(r, Palette.Track, NeoStroke.None, 4);
                if (on) c.Rect(new NeoRect(r.X, Height - 2, r.Width, 2), accent, NeoStroke.None);
                c.Text(names[i], r.X + r.Width / 2, (Height - 2 - 17) / 2, TextSize, on ? accent : Palette.Text, on, NeoAlign.Center);
            }
        }
    }
}
