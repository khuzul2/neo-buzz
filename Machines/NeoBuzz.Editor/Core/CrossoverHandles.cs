#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): crossover handles on a
// frequency-response graph (Core/ResponseGraph.cs), for Neo Multiband: a line per crossover with a
// grip at the top; dragging one moves its frequency (Shift: fine), never closer to a neighbour than a
// sixth of an octave; a press between them selects that band, a double-click there solos it, and the
// wheel moves its threshold. Pointer input arrives as plain calls (IGraphInput); a toolkit control
// routes its events here and redraws when a call returns true.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeoBuzz.Editor
{
    /// <summary>Where the crossover handles come from and where their moves go (the machine's parameters).</summary>
    internal interface ICrossovers
    {
        /// <summary>The bands now (3 or 4): one crossover fewer.</summary>
        int Bands { get; }
        double Hz(int crossover);
        void Move(int crossover, double hz);
        /// <summary>A double-click on a band.</summary>
        void ToggleSolo(int band);
        /// <summary>The wheel over a band: its threshold by <paramref name="tenths"/> of a dB.</summary>
        void StepThreshold(int band, int tenths);
    }

    internal sealed class CrossoverHandleInput : IGraphInput
    {
        /// <summary>How close (px) the pointer must come to a crossover's line to take it.</summary>
        public const double Reach = 6, GripRadius = 6.5;
        /// <summary>The closest two crossovers may come: a sixth of an octave.</summary>
        public static readonly double MinRatio = Math.Pow(2, 1.0 / 6);
        const double FineScale = 0.1;

        readonly ResponseGraph graph;
        readonly ICrossovers crossovers;
        int dragging = -1;
        double startX, startHx;
        bool moved;

        public CrossoverHandleInput(ResponseGraph graph, ICrossovers crossovers)
        {
            this.graph = graph;
            this.crossovers = crossovers;
        }

        public int Count => crossovers.Bands - 1;

        /// <summary>The band selected (shown below the graph).</summary>
        public int Selected { get; private set; }

        /// <summary>The crossover under the pointer (−1: none).</summary>
        public int Hover { get; private set; } = -1;

        public bool Capturing => dragging >= 0;

        public event Action<int> SelectedChanged;

        public void Select(int band)
        {
            band = Math.Clamp(band, 0, crossovers.Bands - 1);
            if (band == Selected) return;
            Selected = band;
            SelectedChanged?.Invoke(band);
        }

        /// <summary>The crossover whose line is within reach of <paramref name="x"/> (the nearest), or −1.</summary>
        public int CrossoverAt(double x, double y)
        {
            if (y < graph.Plot.Top - 4 || y > graph.Plot.Bottom + 4) return -1;
            int best = -1;
            double bestD = Reach;
            for (int i = 0; i < Count; i++)
            {
                double d = Math.Abs(graph.X(crossovers.Hz(i)) - x);
                if (d <= bestD)
                {
                    best = i;
                    bestD = d;
                }
            }
            return best;
        }

        /// <summary>The band under <paramref name="x"/> (between which crossovers it falls).</summary>
        public int BandAt(double x)
        {
            double hz = graph.Hz(x);
            for (int i = 0; i < Count; i++)
                if (hz < crossovers.Hz(i)) return i;
            return Count;
        }

        /// <summary>A crossover's frequency limits: a sixth of an octave from its neighbours, inside the graph.</summary>
        public (double Lo, double Hi) Limits(int i)
        {
            double lo = i > 0 ? crossovers.Hz(i - 1) * MinRatio : graph.MinHz;
            double hi = i < Count - 1 ? crossovers.Hz(i + 1) / MinRatio : graph.MaxHz;
            return (Math.Min(lo, hi), hi);
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            int c = CrossoverAt(x, y);
            if (c >= 0 && clicks < 2)
            {
                dragging = c;
                moved = false;
                startX = x;
                startHx = graph.X(crossovers.Hz(c));
                return true;
            }
            if (y < graph.Plot.Top || y > graph.Plot.Bottom || x < graph.Plot.Left || x > graph.Plot.Right) return false;
            int band = BandAt(x);
            Select(band);
            if (clicks >= 2) crossovers.ToggleSolo(band);
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragging < 0)
            {
                int h = CrossoverAt(x, y);
                if (h == Hover) return false;
                Hover = h;
                return true;
            }
            if (!moved && Math.Abs(x - startX) < 2) return false;
            moved = true;
            double hx = startHx + (x - startX) * (fine ? FineScale : 1);
            var (lo, hi) = Limits(dragging);
            crossovers.Move(dragging, Math.Clamp(graph.Hz(hx), lo, hi));
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
            if (delta == 0 || x < graph.Plot.Left || x > graph.Plot.Right) return false;
            int band = BandAt(x);
            Select(band);
            int notches = Math.Max(1, Math.Abs(delta) / 120) * Math.Sign(delta);
            crossovers.StepThreshold(band, notches * (fine ? 1 : 10));
            return true;
        }

        public bool Leave()
        {
            if (Hover < 0) return false;
            Hover = -1;
            return true;
        }

        /// <summary>"120 Hz", "1.5 kHz".</summary>
        public static string Label(double hz) =>
            hz >= 1000 ? (hz / 1000).ToString(hz >= 10000 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " kHz" : hz.ToString("0", CultureInfo.InvariantCulture) + " Hz";

        /// <summary>Each crossover's line, with a grip at the top and its frequency; the one held or under the pointer brighter.</summary>
        public void Draw(INeoCanvas g, NeoColor accent)
        {
            for (int i = 0; i < Count; i++)
            {
                double hz = crossovers.Hz(i), x = graph.X(hz);
                bool hot = i == dragging || i == Hover;
                var colour = hot ? Palette.Text : accent;
                g.Line(x, graph.Plot.Top + GripRadius, x, graph.Plot.Bottom, new NeoStroke(colour.WithAlpha(hot ? (byte)0xE0 : (byte)0x90), hot ? 1.6 : 1.1));
                var grip = new NeoPoint(x, graph.Plot.Top + GripRadius);
                g.Ellipse(grip, GripRadius, GripRadius, hot ? accent : Palette.Panel, new NeoStroke(colour, 1.4));
                string text = Label(hz);
                bool right = x < graph.Plot.Right - 50;
                g.Text(text, right ? x + GripRadius + 3 : x - GripRadius - 3, graph.Plot.Top + 1, 9.5, colour, align: right ? NeoAlign.Left : NeoAlign.Right);
            }
        }

        /// <summary>The frequency span of each band on the graph (left and right x), for shading and labels.</summary>
        public IEnumerable<(int Band, double Left, double Right)> Spans()
        {
            double left = graph.Plot.Left;
            for (int i = 0; i < Count; i++)
            {
                double x = Math.Clamp(graph.X(crossovers.Hz(i)), graph.Plot.Left, graph.Plot.Right);
                yield return (i, left, x);
                left = x;
            }
            yield return (Count, left, graph.Plot.Right);
        }
    }
}
