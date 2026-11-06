#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): a frequency-response plot
// (log frequency across, dB up) with its grid, curves and a spectrum, and draggable band handles
// on it. Pointer input arrives as plain calls (IGraphInput); a toolkit control routes its events
// here (InteractiveGraph.cs for WPF) and redraws when a call returns true.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace NeoBuzz.Editor
{
    /// <summary>Pointer input for a graph, toolkit-free. Each call returns true when the graph should redraw.</summary>
    internal interface IGraphInput
    {
        bool Down(double x, double y, int clicks, bool fine);
        bool Move(double x, double y, bool fine);
        bool Up();
        bool Wheel(double x, double y, int delta, bool fine);
        bool Leave();
        /// <summary>True while a drag holds the pointer.</summary>
        bool Capturing { get; }
    }

    /// <summary>A frequency-response plot: 20 Hz – 20 kHz on a log scale across, dB up.</summary>
    internal sealed class ResponseGraph
    {
        static readonly double[] Major = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

        public ResponseGraph(double minDb = -24, double maxDb = 24, double dbStep = 6)
        {
            MinDb = minDb;
            MaxDb = maxDb;
            DbStep = dbStep;
        }

        public double MinHz { get; } = 20;
        public double MaxHz { get; } = 20000;
        public double MinDb { get; }
        public double MaxDb { get; }
        public double DbStep { get; }

        /// <summary>The plotting area (inside the frame, leaving room for the labels).</summary>
        public NeoRect Plot { get; private set; } = new NeoRect(0, 0, 1, 1);

        NeoRect frame = new NeoRect(0, 0, 0, 0);
        double handleRoom;

        /// <summary>
        /// Room (px) kept clear around the plot for what is drawn over its edges (band handles, slider caps): the
        /// axis labels sit beyond it, so a handle at 20 Hz or at the top or bottom of the range never covers them.
        /// Handle inputs ask for what they need (<see cref="NeedRoom"/>).
        /// </summary>
        public double HandleRoom
        {
            get => handleRoom;
            set
            {
                handleRoom = Math.Max(0, value);
                if (frame.Width > 0) Layout(frame);
            }
        }

        /// <summary>Makes <see cref="HandleRoom"/> at least <paramref name="room"/>.</summary>
        public void NeedRoom(double room)
        {
            if (room > handleRoom) HandleRoom = room;
        }

        public void Layout(NeoRect frame)
        {
            this.frame = frame;
            double room = handleRoom, top = Math.Max(8, room + 1), right = Math.Max(8, room + 1);
            Plot = new NeoRect(frame.X + 30 + room, frame.Y + top, Math.Max(1, frame.Width - 30 - room - right), Math.Max(1, frame.Height - top - 18 - room));
        }

        /// <summary>
        /// Draws <paramref name="body"/> clipped to the plot: curves, areas and spectra end at its edges and never run
        /// over the axis labels. Handles are drawn after it, unclipped, in <see cref="HandleRoom"/>. A drawing error
        /// ends the body there (as in GraphFrame), and the clip is always popped.
        /// </summary>
        public void InPlot(INeoCanvas g, Action body)
        {
            g.PushClip(Plot);
            try
            {
                body?.Invoke();
            }
            catch (Exception)
            {
            }
            finally
            {
                g.PopClip();
            }
        }

        public double X(double hz) => Plot.Left + Math.Log(Math.Max(1e-3, hz) / MinHz) / Math.Log(MaxHz / MinHz) * Plot.Width;

        public double Hz(double x) => Math.Clamp(MinHz * Math.Pow(MaxHz / MinHz, (x - Plot.Left) / Plot.Width), MinHz, MaxHz);

        public double Y(double db) => Plot.Bottom - (db - MinDb) / (MaxDb - MinDb) * Plot.Height;

        public double Db(double y) => Math.Clamp(MinDb + (Plot.Bottom - y) / Plot.Height * (MaxDb - MinDb), MinDb, MaxDb);

        /// <summary>"1k", "20k", "500".</summary>
        public static string FreqLabel(double hz) =>
            hz >= 1000 ? (hz / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "k" : hz.ToString("0", CultureInfo.InvariantCulture);

        /// <summary>Frequency lines (decades brighter) with labels below, dB lines with labels at the left, both beyond <see cref="HandleRoom"/>.</summary>
        public void DrawGrid(INeoCanvas g)
        {
            double room = handleRoom;
            var minor = new NeoStroke(Palette.Grid, 1, false);
            var major = new NeoStroke(Palette.Track, 1, false);
            for (double decade = 10; decade <= 10000; decade *= 10)
                for (int m = 2; m <= 9; m++)
                {
                    double f = decade * m;
                    if (f < MinHz || f > MaxHz) continue;
                    g.Line(X(f), Plot.Top, X(f), Plot.Bottom, minor);
                }
            foreach (double f in Major)
            {
                bool decade = f == 100 || f == 1000 || f == 10000;
                if (decade) g.Line(X(f), Plot.Top, X(f), Plot.Bottom, major);
                g.Text(FreqLabel(f), X(f), Plot.Bottom + 3 + room, 9.5, Palette.DimText, align: NeoAlign.Center);
            }
            for (double d = Math.Ceiling(MinDb / DbStep) * DbStep; d <= MaxDb + 1e-9; d += DbStep)
            {
                g.Line(Plot.Left, Y(d), Plot.Right, Y(d), d == 0 ? major : minor);
                g.Text(d.ToString("+0;−0;0", CultureInfo.InvariantCulture), Plot.Left - 4 - room, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
        }

        /// <summary>A curve of <paramref name="dbAt"/> across the plot, <paramref name="points"/> points log-spaced.</summary>
        public NeoPath Curve(Func<double, double> dbAt, int points = 240)
        {
            var path = new NeoPath();
            for (int i = 0; i <= points; i++)
            {
                double hz = MinHz * Math.Pow(MaxHz / MinHz, i / (double)points);
                double db = dbAt(hz);
                if (double.IsNaN(db)) db = MinDb;
                path.LineTo(X(hz), Y(Math.Clamp(db, MinDb - 6, MaxDb + 6)));
            }
            return path;
        }

        /// <summary>A curve filled to the 0 dB line (a band's own contribution).</summary>
        public NeoPath Area(Func<double, double> dbAt, int points = 160)
        {
            var path = new NeoPath().MoveTo(Plot.Left, Y(0));
            for (int i = 0; i <= points; i++)
            {
                double hz = MinHz * Math.Pow(MaxHz / MinHz, i / (double)points);
                double db = dbAt(hz);
                if (double.IsNaN(db)) db = MinDb;
                path.LineTo(X(hz), Y(Math.Clamp(db, MinDb - 6, MaxDb + 6)));
            }
            return path.LineTo(Plot.Right, Y(0)).Close();
        }

        /// <summary>
        /// A spectrum (magnitudes in dB for bin frequencies <paramref name="binHz"/>) as a filled
        /// shape from the bottom, on its own scale: <paramref name="topDb"/> at the top of the plot,
        /// <paramref name="rangeDb"/> below it at the bottom.
        /// </summary>
        public NeoPath Spectrum(IReadOnlyList<double> binHz, IReadOnlyList<double> db, double topDb = 0, double rangeDb = 90)
        {
            var path = new NeoPath().MoveTo(Plot.Left, Plot.Bottom);
            for (int i = 0; i < binHz.Count; i++)
            {
                double hz = binHz[i];
                if (hz < MinHz || hz > MaxHz) continue;
                double t = Math.Clamp((db[i] - (topDb - rangeDb)) / rangeDb, 0, 1);
                path.LineTo(X(hz), Plot.Bottom - t * Plot.Height);
            }
            return path.LineTo(Plot.Right, Plot.Bottom).Close();
        }
    }

    /// <summary>Where the band handles come from and where their moves go (the machine's parameters).</summary>
    internal interface IBandHandles
    {
        int Count { get; }
        /// <summary>A band's frequency, gain (dB) and state; bands without a gain sit on the 0 dB line.</summary>
        (double Hz, double Db, bool On, bool HasGain) Band(int i);
        /// <summary>Moves a band; <paramref name="db"/> is null for bands without a gain.</summary>
        void Move(int i, double hz, double? db);
        void StepQ(int i, int steps);
        void SetOn(int i, bool on);
    }

    /// <summary>
    /// Draggable band handles on a response graph: a drag sets frequency and gain (Shift: fine;
    /// dragging a band that is off switches it on), the wheel sets Q, a double-click switches the
    /// band on or off. Selects the band it touches.
    /// </summary>
    internal sealed class BandHandleInput : IGraphInput
    {
        public const double Radius = 8;
        const double FineScale = 0.1;

        readonly ResponseGraph graph;
        readonly IBandHandles bands;
        int dragging = -1;
        double startX, startY, startHx, startHy;
        bool moved;

        public BandHandleInput(ResponseGraph graph, IBandHandles bands)
        {
            this.graph = graph;
            this.bands = bands;
            // A selected handle (its radius and ring) at an edge of the plot stays clear of the axis labels.
            graph.NeedRoom(Room);
        }

        /// <summary>How far a handle reaches past its centre: the selected radius and its ring.</summary>
        public const double Room = Radius + 3;

        public int Selected { get; private set; }
        public int Hover { get; private set; } = -1;
        public bool Capturing => dragging >= 0;

        /// <summary>Raised when a band gets selected (by touching its handle, or <see cref="Select"/>).</summary>
        public event Action<int> SelectedChanged;

        public void Select(int i)
        {
            if (i == Selected || i < 0 || i >= bands.Count) return;
            Selected = i;
            SelectedChanged?.Invoke(i);
        }

        /// <summary>Where band <paramref name="i"/>'s handle sits.</summary>
        public NeoPoint Position(int i)
        {
            var b = bands.Band(i);
            double db = b.HasGain ? Math.Clamp(b.Db, graph.MinDb, graph.MaxDb) : 0;
            return new NeoPoint(graph.X(b.Hz), graph.Y(db));
        }

        /// <summary>The handle under a point (the nearest within reach; the selected one wins ties), or −1.</summary>
        public int HandleAt(double x, double y)
        {
            int best = -1;
            double bestD = (Radius + 3) * (Radius + 3);
            for (int k = 0; k < bands.Count; k++)
            {
                int i = (Selected + k) % bands.Count;
                var p = Position(i);
                double d = (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y);
                if (d < bestD)
                {
                    best = i;
                    bestD = d;
                }
            }
            return best;
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            int i = HandleAt(x, y);
            if (i < 0) return false;
            Select(i);
            if (clicks >= 2)
            {
                bands.SetOn(i, !bands.Band(i).On);
                dragging = -1;
                return true;
            }
            var p = Position(i);
            dragging = i;
            moved = false;
            startX = x;
            startY = y;
            startHx = p.X;
            startHy = p.Y;
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragging < 0)
            {
                int h = HandleAt(x, y);
                if (h == Hover) return false;
                Hover = h;
                return true;
            }
            double s = fine ? FineScale : 1;
            double hx = startHx + (x - startX) * s, hy = startHy + (y - startY) * s;
            if (!moved && Math.Abs(x - startX) + Math.Abs(y - startY) < 2) return false;
            var b = bands.Band(dragging);
            if (!moved && !b.On) bands.SetOn(dragging, true);
            moved = true;
            bands.Move(dragging, graph.Hz(hx), b.HasGain ? graph.Db(hy) : (double?)null);
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
            int i = HandleAt(x, y);
            if (i < 0) i = Selected;
            if (i < 0 || delta == 0) return false;
            Select(i);
            int notches = Math.Max(1, Math.Abs(delta) / 120) * Math.Sign(delta);
            bands.StepQ(i, notches * (fine ? 1 : 10));
            return true;
        }

        public bool Leave()
        {
            if (Hover < 0) return false;
            Hover = -1;
            return true;
        }

        /// <summary>The handles: numbered dots in each band's colour (outlined when off), the selected one ringed.</summary>
        public void Draw(INeoCanvas g, IReadOnlyList<NeoColor> colours)
        {
            for (int k = 1; k <= bands.Count; k++)
            {
                // The selected handle last, on top.
                int i = (Selected + k) % bands.Count;
                var b = bands.Band(i);
                var p = Position(i);
                var c = colours[i % colours.Count];
                bool sel = i == Selected;
                double r = sel || i == Hover ? Radius + 1.5 : Radius;
                if (b.On) g.Ellipse(p, r, r, c, new NeoStroke(sel ? Palette.Text : c.Tint(0.3), sel ? 2 : 1));
                else g.Ellipse(p, r, r, Palette.Panel, new NeoStroke(c.WithAlpha(sel ? (byte)0xFF : (byte)0x90), sel ? 2 : 1.2));
                g.Text((i + 1).ToString(CultureInfo.InvariantCulture), p.X, p.Y - 7.5, 10.5, b.On ? Palette.Background : c, true, NeoAlign.Center);
            }
        }
    }
}
