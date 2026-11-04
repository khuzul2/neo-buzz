#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): a graphic EQ's sliders on a
// response graph (Neo EQ's graphic mode, machines.md §5). A track per band at its centre frequency
// and a cap at its value; the pointer arrives as plain calls (IGraphInput), routed by InteractiveGraph
// on WPF and Avalonia alike.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeoBuzz.Editor
{
    /// <summary>Where a graphic EQ's sliders come from and where their moves go (the machine's parameters).</summary>
    internal interface IGraphicSliders
    {
        int Count { get; }
        /// <summary>The band's centre (where its track stands).</summary>
        double Hz(int i);
        /// <summary>"1 kHz".</summary>
        string Label(int i);
        double Db(int i);
        /// <summary>Sets a slider (the implementation clamps and rounds to its steps).</summary>
        void Set(int i, double db);
        double MinDb { get; }
        double MaxDb { get; }
    }

    /// <summary>
    /// Sliders on a response graph. Pressing on a band's cap and dragging moves it with the pointer;
    /// pressing elsewhere sets the band under the pointer to the pointer's level, and dragging on draws:
    /// every band the pointer passes takes the level where it crossed (Shift: the band first touched
    /// moves by a tenth of the pointer's movement instead). The wheel steps the band under the pointer
    /// by 0.5 dB (Shift: 0.1 dB); a double-click puts it back at 0 dB. Selects the band it touches.
    /// </summary>
    internal sealed class GraphicSliderInput : IGraphInput
    {
        public const double CapWidth = 11, CapHeight = 6;
        const double FineScale = 0.1;

        readonly ResponseGraph graph;
        readonly IGraphicSliders sliders;
        int dragging = -1;
        bool fineDrag, relative;
        double startY, startDb, lastX, lastDb;

        public GraphicSliderInput(ResponseGraph graph, IGraphicSliders sliders, int selected = 0)
        {
            this.graph = graph;
            this.sliders = sliders;
            Selected = selected;
        }

        public int Selected { get; private set; }
        public int Hover { get; private set; } = -1;
        public bool Capturing => dragging >= 0;

        /// <summary>Raised when a band gets selected (by touching it, or <see cref="Select"/>).</summary>
        public event Action<int> SelectedChanged;

        public void Select(int i)
        {
            if (i == Selected || i < 0 || i >= sliders.Count) return;
            Selected = i;
            SelectedChanged?.Invoke(i);
        }

        /// <summary>Where band <paramref name="i"/>'s track stands.</summary>
        public double X(int i) => graph.X(sliders.Hz(i));

        /// <summary>The middle of band <paramref name="i"/>'s cap.</summary>
        public NeoPoint Cap(int i) => new NeoPoint(X(i), graph.Y(Math.Clamp(sliders.Db(i), sliders.MinDb, sliders.MaxDb)));

        /// <summary>The band whose track is nearest to <paramref name="x"/> (within half the spacing either side), or −1.</summary>
        public int BandAt(double x)
        {
            int best = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < sliders.Count; i++)
            {
                double d = Math.Abs(X(i) - x);
                if (d < bestD)
                {
                    best = i;
                    bestD = d;
                }
            }
            if (best < 0) return -1;
            // Third-octave bands stand evenly on the log axis: half the spacing either side is the band's.
            double half = sliders.Count > 1 ? Math.Abs(X(1) - X(0)) / 2 : graph.Plot.Width;
            return bestD <= Math.Max(half, CapWidth / 2) ? best : -1;
        }

        double Level(double y) => Math.Clamp(graph.Db(y), sliders.MinDb, sliders.MaxDb);

        bool Inside(double y) => y >= graph.Plot.Top - CapHeight && y <= graph.Plot.Bottom + CapHeight;

        public bool Down(double x, double y, int clicks, bool fine)
        {
            int i = BandAt(x);
            if (i < 0 || !Inside(y)) return false;
            Select(i);
            if (clicks >= 2)
            {
                sliders.Set(i, 0);
                dragging = -1;
                return true;
            }
            dragging = i;
            fineDrag = fine;
            startY = y;
            startDb = sliders.Db(i);
            relative = fine || Math.Abs(Cap(i).Y - y) <= CapHeight + 2;
            if (!relative) sliders.Set(i, Level(y));
            lastX = x;
            lastDb = relative ? startDb : Level(y);
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragging < 0)
            {
                int h = Inside(y) ? BandAt(x) : -1;
                if (h == Hover) return false;
                Hover = h;
                return true;
            }
            if (relative)
            {
                // The cap follows the pointer (a tenth as far with Shift); no drawing.
                double perPixel = (graph.MaxDb - graph.MinDb) / graph.Plot.Height;
                double db = startDb + (startY - y) * perPixel * (fineDrag ? FineScale : 1);
                sliders.Set(dragging, Math.Clamp(db, sliders.MinDb, sliders.MaxDb));
                return true;
            }
            // Drawing: every band whose track the pointer crossed since the last move takes the level there.
            double level = Level(y);
            int a = BandAt(lastX), b = BandAt(x);
            if (a < 0) a = b;
            if (b >= 0)
            {
                int lo = Math.Min(a, b), hi = Math.Max(a, b);
                for (int i = lo; i <= hi; i++)
                {
                    double t = x == lastX ? 1 : Math.Clamp((X(i) - lastX) / (x - lastX), 0, 1);
                    sliders.Set(i, lastDb + (level - lastDb) * t);
                }
                Select(b);
            }
            lastX = x;
            lastDb = level;
            return true;
        }

        public bool Up()
        {
            if (dragging < 0) return false;
            dragging = -1;
            return true;
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            int i = BandAt(x);
            if (i < 0) i = Selected;
            if (i < 0 || delta == 0) return false;
            Select(i);
            int notches = Math.Max(1, Math.Abs(delta) / 120) * Math.Sign(delta);
            sliders.Set(i, Math.Clamp(sliders.Db(i) + notches * (fine ? 0.1 : 0.5), sliders.MinDb, sliders.MaxDb));
            return true;
        }

        public bool Leave()
        {
            if (Hover < 0) return false;
            Hover = -1;
            return true;
        }

        /// <summary>The tracks, from the bottom of the range to its top (drawn under the curve).</summary>
        public void DrawTracks(INeoCanvas g)
        {
            var track = new NeoStroke(Palette.Track.WithAlpha(0xC0), 1);
            double top = graph.Y(sliders.MaxDb), bottom = graph.Y(sliders.MinDb);
            for (int i = 0; i < sliders.Count; i++) g.Line(X(i), top, X(i), bottom, track);
        }

        /// <summary>The caps at the sliders' values (drawn over the curve); the selected and hovered ones brighter.</summary>
        public void DrawCaps(INeoCanvas g, NeoColor accent)
        {
            for (int i = 0; i < sliders.Count; i++)
            {
                var c = Cap(i);
                bool sel = i == Selected, hover = i == Hover;
                var rect = new NeoRect(c.X - CapWidth / 2, c.Y - CapHeight / 2, CapWidth, CapHeight);
                g.Rect(rect, sel ? accent : hover ? accent.Tint(0.25) : Palette.KnobTop, new NeoStroke(sel ? Palette.Text : accent.WithAlpha(0xC0), sel ? 1.5 : 1), 2);
            }
        }

        /// <summary>"1 kHz +3.0 dB".</summary>
        public string Caption(int i) =>
            sliders.Label(i) + " " + sliders.Db(i).ToString("+0.0;−0.0;0.0", CultureInfo.InvariantCulture) + " dB";
    }
}
