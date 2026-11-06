#nullable disable
// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): what the manifest editor draws besides its controls,
// on the kit's canvas (INeoCanvas): an envelope's ADSR picture from its parameters' values, and the
// response plot (a computed curve, or markers labelled as such, Core/ResponseModels.cs).

using System;
using System.Globalization;
using System.Linq;

namespace NeoBuzz.Editor
{
    internal static class FacePainters
    {
        public const double EnvelopeWidth = 168, EnvelopeHeight = 64;
        public const double ResponseWidth = 360, ResponseHeight = 150;

        /// <summary>
        /// An envelope's ADSR shape (EnvelopePainter): times in seconds when every stage is, else by their positions
        /// in their ranges (the editor says so under it); a stage the machine lacks takes no time, and without a
        /// sustain the level falls to 0 after the decay (or holds at the top with no decay either).
        /// </summary>
        public static void Envelope(INeoCanvas c, NeoRect r, FaceEnvelope env, Func<FaceParam, int> raw, NeoColor accent)
        {
            double Time(FaceParam p)
            {
                if (p == null) return 0;
                int v = raw(p);
                if (env.InSeconds)
                {
                    double s = p.SafeValue(v);
                    return double.IsFinite(s) ? Math.Max(0, s) : 0;
                }
                // Bare scales: their position, as a share of a few seconds, so the shape reads.
                return p.Position(v) * 4;
            }
            double sustain = env.Sustain != null ? env.Sustain.Level(raw(env.Sustain)) : env.Decay != null ? 0 : 1;
            EnvelopePainter.Adsr(c, r, Time(env.Attack), Time(env.Decay), sustain, Time(env.Release), accent);
        }

        /// <summary>The response plot: a grid in Hz and dB, the curve (computed plots), and a line at each marked frequency.</summary>
        public static void Response(INeoCanvas c, NeoRect r, FaceResponse response, Func<FaceParam, int> raw, NeoColor accent)
        {
            var graph = new ResponseGraph(response.MinDb, response.MaxDb, response.DbStep);
            graph.Layout(r);
            graph.DrawGrid(c);
            var plot = graph.Plot;
            // The markers: a line at each frequency, its label at the top.
            double labelX = double.NegativeInfinity;
            foreach (var p in response.Markers.OrderBy(m => m.SafeValue(raw(m))))
            {
                double hz = p.SafeValue(raw(p));
                if (!double.IsFinite(hz) || hz < graph.MinHz || hz > graph.MaxHz) continue;
                double x = graph.X(hz);
                c.Line(new NeoPoint(x, plot.Top), new NeoPoint(x, plot.Bottom), new NeoStroke(accent.WithAlpha(response.Computed ? (byte)0x50 : (byte)0xB0), 1, false));
                string label = Short(p.Label);
                double w = c.MeasureText(label, 9.5);
                // Labels that would overlap the previous one step down a line.
                bool low = x - w / 2 < labelX + 4;
                c.Text(label, x, plot.Top + (low ? 13 : 2), 9.5, response.Computed ? Palette.DimText : accent, align: NeoAlign.Center);
                if (!low) labelX = x + w / 2;
            }
            if (response.Computed && response.DbAt != null)
            {
                // Inside the plot, never over the frequency labels.
                graph.InPlot(c, () =>
                {
                    var area = graph.Area(hz => Clamp(response.DbAt(hz), response));
                    c.Path(area, accent.WithAlpha(0x22), NeoStroke.None);
                    c.Path(graph.Curve(hz => Clamp(response.DbAt(hz), response)), NeoFill.None, new NeoStroke(accent, 2));
                });
            }
            else
            {
                c.Text("markers only: no curve", plot.Right - 4, plot.Bottom - 15, 10, Palette.DimText, align: NeoAlign.Right);
            }
        }

        static double Clamp(double db, FaceResponse r) => double.IsFinite(db) ? Math.Clamp(db, r.MinDb - 6, r.MaxDb + 6) : r.MinDb - 6;

        /// <summary>A label short enough to sit over a line ("Low-mid frequency" → "Low-mid").</summary>
        static string Short(string label)
        {
            string s = (label ?? "").Replace(" frequency", "").Replace(" freq", "").Replace(" Freq", "");
            return s.Length > 18 ? s.Substring(0, 17) + "…" : s;
        }

        /// <summary>"800 Hz", "1.2 kHz".</summary>
        public static string Hz(double hz) =>
            hz >= 1000 ? (hz / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " kHz" : hz.ToString("0.#", CultureInfo.InvariantCulture) + " Hz";
    }
}
