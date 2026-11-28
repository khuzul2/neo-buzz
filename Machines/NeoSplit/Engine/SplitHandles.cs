#nullable disable
// Neo Split's crossover handles (docs/specs/neo-split.md section 6): a line per crossover over the frequency strip, with a grip at
// the top; dragging one moves its frequency (Shift: fine), never closer than a third of an octave to a neighbour (the engine keeps
// the same distance), and the wheel steps it by a twelfth of an octave. Toolkit-free, like the kit's other graph inputs
// (Core/CrossoverHandles.cs): pointer input arrives as plain calls and the toolkit control redraws when a call returns true.

using System;
using System.Globalization;
using NeoBuzz.Editor;

namespace NeoBuzz.Split
{
    internal interface ISplitCrossovers
    {
        /// <summary>The bands now (2 to 4): one crossover fewer.</summary>
        int Bands { get; }
        double Hz(int crossover);
        void Move(int crossover, double hz);
    }

    internal sealed class SplitHandleInput : IGraphInput
    {
        public const double Reach = 6, GripRadius = 6.5;
        const double FineScale = 0.1;

        readonly ResponseGraph graph;
        readonly ISplitCrossovers crossovers;
        int dragging = -1;
        double startX, startHx;
        bool moved;

        public SplitHandleInput(ResponseGraph graph, ISplitCrossovers crossovers)
        {
            this.graph = graph;
            this.crossovers = crossovers;
        }

        public int Count => crossovers.Bands - 1;

        public int Hover { get; private set; } = -1;

        public bool Capturing => dragging >= 0;

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

        /// <summary>A crossover's limits: a third of an octave from its neighbours, inside the graph.</summary>
        public (double Lo, double Hi) Limits(int i)
        {
            double lo = i > 0 ? crossovers.Hz(i - 1) * SplitEngine.MinRatio : graph.MinHz;
            double hi = i < Count - 1 ? crossovers.Hz(i + 1) / SplitEngine.MinRatio : graph.MaxHz;
            return (Math.Min(lo, hi), hi);
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            int c = CrossoverAt(x, y);
            if (c < 0) return false;
            dragging = c;
            moved = false;
            startX = x;
            startHx = graph.X(crossovers.Hz(c));
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
            if (delta == 0) return false;
            int c = CrossoverAt(x, y);
            if (c < 0) return false;
            int notches = Math.Max(1, Math.Abs(delta) / 120) * Math.Sign(delta);
            double step = fine ? 1.0 / 48 : 1.0 / 12;     // octaves
            double hz = crossovers.Hz(c) * Octaves(notches * step);
            var (lo, hi) = Limits(c);
            crossovers.Move(c, Math.Clamp(hz, lo, hi));
            return true;
        }

        // 2^x by the portable maths (the library's rule for anything in an Engine folder).
        static double Octaves(double x) => NeoBuzz.Dsp.PortableMath.Pow(2.0, x);

        public bool Leave()
        {
            if (Hover < 0) return false;
            Hover = -1;
            return true;
        }

        /// <summary>"120 Hz", "1.50 kHz".</summary>
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
                bool right = x < graph.Plot.Right - 50;
                g.Text(Label(hz), right ? x + GripRadius + 3 : x - GripRadius - 3, graph.Plot.Top + 1, 9.5, colour,
                    align: right ? NeoAlign.Left : NeoAlign.Right);
            }
        }

        /// <summary>The span of each band on the graph (left and right x), for shading and labels.</summary>
        public (double Left, double Right)[] Spans()
        {
            var spans = new (double, double)[Count + 1];
            double left = graph.Plot.Left;
            for (int i = 0; i < Count; i++)
            {
                double x = Math.Clamp(graph.X(crossovers.Hz(i)), graph.Plot.Left, graph.Plot.Right);
                spans[i] = (left, x);
                left = x;
            }
            spans[Count] = (left, graph.Plot.Right);
            return spans;
        }
    }
}
