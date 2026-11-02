#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7 plot): images the agent looks at, drawn to PNG with
// the same code headless and in the studio: spectrum (third-octave curve on a log axis, several
// sources overlaid), spectrogram, waveform, loudness over time, goniometer (stereo field) and
// per-track band bars. Plain and readable at 900×400: axes, ticks, labels, a legend when there is
// more than one series. Colours: the reference categorical order, a one-hue (blue) ramp for the
// spectrogram, text in neutral inks.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NeoBuzz.Analysis;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Audio
{
    public static class AudioPlot
    {
        public const int DefaultWidth = 900, DefaultHeight = 400;

        public static readonly string[] Kinds = { "spectrum", "spectrogram", "waveform", "loudness", "goniometer", "bands" };

        static readonly CultureInfo C = CultureInfo.InvariantCulture;
        static readonly Rgb Surface = new(0xfcfcfb), Ink = new(0x0b0b0b), Ink2 = new(0x52514e), Muted = new(0x8a8984),
                            Grid = new(0xe8e7e3), GridMinor = new(0xf2f1ee), Axis = new(0xb5b4ae), Clip = new(0xe34948);
        static readonly Rgb[] Series =
        {
            new(0x2a78d6), new(0xeb6834), new(0x1baf7a), new(0xeda100), new(0xe87ba4), new(0x008300), new(0x6250d6), new(0xe34948),
        };
        static readonly Rgb[] Ramp = { new(0xfcfcfb), new(0xcde2fb), new(0x86b6ef), new(0x3987e5), new(0x1c5cab), new(0x0d366b) };

        /// <summary>
        /// Draws <paramref name="kind"/> (one of <see cref="Kinds"/>) as PNG bytes. "spectrum" overlays
        /// every source and "bands" groups them (up to 8 each); the others draw the first source.
        /// </summary>
        public static byte[] Draw(string kind, IReadOnlyList<(string Name, StereoAudio Audio)> sources, string title = null, int width = DefaultWidth, int height = DefaultHeight)
        {
            if (sources == null || sources.Count == 0) throw new ToolError("plot needs at least one source.");
            width = Math.Clamp(width, 320, 2400);
            height = Math.Clamp(height, 200, 1600);
            var (name, audio) = sources[0];
            return kind switch
            {
                "spectrum" => Spectrum(sources, title, width, height),
                "spectrogram" => Spectrogram(audio, title ?? $"Spectrogram: {name}", width, height),
                "waveform" => Waveform(audio, title ?? $"Waveform: {name}", width, height),
                "loudness" => Loudness(audio, title ?? $"Loudness: {name}", width, height),
                "goniometer" => Goniometer(audio, title ?? $"Stereo field: {name}", width, height),
                "bands" => Bands(sources, title, width, height),
                _ => throw new ToolError($"Unknown plot \"{kind}\". Plots: {string.Join(", ", Kinds)}."),
            };
        }

        // ---- Frame -------------------------------------------------------------------------

        /// <summary>The plot area and its value-to-pixel mapping.</summary>
        sealed class Frame
        {
            public int Left, Top, Right, Bottom;
            public double X0, X1, Y0, Y1;
            public bool LogX, LogY;
            /// <summary>Where the title ends, so a legend can share its row.</summary>
            public int TitleEnd;
            public double Px(double x) => Left + (LogX ? Math.Log(x / X0) / Math.Log(X1 / X0) : (x - X0) / (X1 - X0)) * (Right - Left);
            public double Py(double y) => Bottom - (LogY ? Math.Log(y / Y0) / Math.Log(Y1 / Y0) : (y - Y0) / (Y1 - Y0)) * (Bottom - Top);
            public double ClampY(double y) => Math.Clamp(y, Math.Min(Y0, Y1), Math.Max(Y0, Y1));
        }

        static Canvas Start(int width, int height, string title, out Frame f, int rightMargin = 24)
        {
            var c = new Canvas(width, height, Surface);
            title = Fit(title ?? "", (width - 32) / 12);
            c.Text(16, 12, title, Ink, 2);
            f = new Frame { Left = 84, Top = 44, Right = width - rightMargin, Bottom = height - 54, TitleEnd = 16 + Canvas.TextWidth(title, 2) };
            return c;
        }

        static string Fit(string s, int chars) => s.Length <= chars ? s : s.Substring(0, Math.Max(0, chars - 3)) + "...";

        static void YAxis(Canvas c, Frame f, IEnumerable<double> ticks, Func<double, string> label, string title)
        {
            foreach (double t in ticks)
            {
                int y = (int)Math.Round(f.Py(t));
                if (y < f.Top - 1 || y > f.Bottom + 1) continue;
                c.HLine(f.Left, f.Right, y, Grid);
                c.Text(f.Left - 8, y - 7, label(t), Ink2, 2, Align.Right);
            }
            c.TextUp(14, (f.Top + f.Bottom) / 2, title, Ink2, 2);
        }

        static void XAxis(Canvas c, Frame f, IEnumerable<double> ticks, Func<double, string> label, string title, IEnumerable<double> minor = null, bool grid = true)
        {
            if (minor != null)
                foreach (double t in minor)
                {
                    int x = (int)Math.Round(f.Px(t));
                    if (x > f.Left && x < f.Right) c.VLine(x, f.Top, f.Bottom, GridMinor);
                }
            foreach (double t in ticks)
            {
                int x = (int)Math.Round(f.Px(t));
                if (x < f.Left - 1 || x > f.Right + 1) continue;
                if (grid) c.VLine(x, f.Top, f.Bottom, Grid);
                c.VLine(x, f.Bottom, f.Bottom + 4, Axis);
                c.Text(x, f.Bottom + 8, label(t), Ink2, 2, Align.Center);
            }
            c.Text((f.Left + f.Right) / 2, f.Bottom + 30, title, Ink2, 2, Align.Center);
        }

        static void Border(Canvas c, Frame f)
        {
            c.HLine(f.Left, f.Right, f.Bottom, Axis);
            c.VLine(f.Left, f.Top, f.Bottom, Axis);
        }

        /// <summary>
        /// A legend: a swatch (line or block) and the name per entry, in a row at the right of the
        /// title line, so it covers no data; inside the plot's top-right corner when the title leaves no room.
        /// </summary>
        static void Legend(Canvas c, Frame f, IReadOnlyList<(string Name, Rgb Color, bool Line)> entries, int insideTop = 8)
        {
            if (entries.Count == 0) return;
            int rowW = entries.Sum(e => 30 + Canvas.TextWidth(Fit(e.Name, 24), 2) + 18) - 18;
            if (f.TitleEnd + 32 + rowW <= c.Width - 16)
            {
                int ex = c.Width - 16 - rowW;
                foreach (var e in entries)
                {
                    if (e.Line) c.Polyline(new[] { (ex + 0.0, 19.0), (ex + 22.0, 19.0) }, e.Color, 3, 0, 0, c.Width - 1, c.Height - 1);
                    else c.Fill(ex + 2, 13, 16, 12, e.Color);
                    c.Text(ex + 30, 12, Fit(e.Name, 24), Ink, 2);
                    ex += 30 + Canvas.TextWidth(Fit(e.Name, 24), 2) + 18;
                }
                return;
            }
            int rowH = 20, w = entries.Max(e => Canvas.TextWidth(Fit(e.Name, 24), 2)) + 44, h = entries.Count * rowH + 10;
            int x = f.Right - w - 8, y = f.Top + insideTop;
            c.Fill(x, y, w, h, Surface);
            c.HLine(x, x + w, y, Grid); c.HLine(x, x + w, y + h, Grid); c.VLine(x, y, y + h, Grid); c.VLine(x + w, y, y + h, Grid);
            for (int i = 0; i < entries.Count; i++)
            {
                int ry = y + 6 + i * rowH;
                if (entries[i].Line) c.Polyline(new[] { (x + 8.0, ry + 7.0), (x + 30.0, ry + 7.0) }, entries[i].Color, 3, 0, 0, c.Width - 1, c.Height - 1);
                else c.Fill(x + 10, ry + 1, 16, 12, entries[i].Color);
                c.Text(x + 36, ry, Fit(entries[i].Name, 24), Ink, 2);
            }
        }

        static IEnumerable<double> Steps(double from, double to, double step)
        {
            double first = Math.Ceiling(from / step - 1e-9) * step;
            for (double v = first; v <= to + 1e-9 * Math.Abs(step); v += step) yield return Math.Round(v / step) * step + 0.0;   // + 0.0: no "-0"
        }

        /// <summary>A 1-2-5 step giving at most <paramref name="max"/> ticks over <paramref name="span"/>.</summary>
        static double NiceStep(double span, int max)
        {
            if (span <= 0) return 1;
            double raw = span / max, mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            foreach (double m in new[] { 1, 2, 2.5, 5, 10 }) if (m * mag >= raw) return m * mag;
            return 10 * mag;
        }

        static void TimeAxis(Canvas c, Frame f, string title = "time", bool grid = true)
        {
            double span = f.X1 - f.X0, step = NiceStep(span, Math.Max(4, (f.Right - f.Left) / 90));
            bool minutes = f.X1 >= 120;
            if (minutes) step = new[] { 15.0, 30, 60, 120, 300, 600 }.First(s => s >= step);
            XAxis(c, f, Steps(f.X0, f.X1, step), t => minutes ? $"{(int)(t / 60)}:{(int)(t % 60):00}" : t.ToString("0.##", C),
                  minutes ? $"{title} (m:ss)" : $"{title} (s)", grid: grid);
        }

        static string Hz(double f) => f >= 1000 ? (f / 1000).ToString("0.#", C) + "k" : f.ToString("0", C);

        static readonly double[] HzTicks = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

        static IEnumerable<double> HzMinor(double lo, double hi)
        {
            for (double d = 10; d < hi; d *= 10)
                for (int m = 2; m <= 9; m++) if (d * m >= lo && d * m <= hi) yield return d * m;
        }

        static string Db(double d) => d.ToString("0", C);

        // ---- Spectrum ----------------------------------------------------------------------

        public static byte[] Spectrum(IReadOnlyList<(string Name, StereoAudio Audio)> series, string title = null, int width = DefaultWidth, int height = DefaultHeight)
        {
            var shown = series.Take(Series.Length).ToList();
            var curves = shown.Select(s => NeoBuzz.Analysis.Spectrum.Curve(new Analysed(s.Audio).Psd)).ToList();
            title ??= shown.Count == 1 ? $"Spectrum: {shown[0].Name}" : "Spectrum: " + string.Join(" vs ", shown.Select(s => s.Name));
            if (series.Count > shown.Count) title += $" (first {shown.Count} of {series.Count})";
            var c = Start(width, height, title, out var f);
            double max = curves.SelectMany(cv => cv.Db).Where(d => !double.IsInfinity(d)).DefaultIfEmpty(0).Max();
            double top = Math.Ceiling((max + 1) / 10) * 10;
            f.X0 = 20; f.X1 = 20000; f.LogX = true;
            f.Y0 = top - 80; f.Y1 = top;
            XAxis(c, f, HzTicks, Hz, "frequency (Hz)", HzMinor(20, 20000));
            YAxis(c, f, Steps(f.Y0, f.Y1, 10), Db, "dB (third-octave band)");
            // The named bands, faintly, along the top.
            foreach (var b in NeoBuzz.Analysis.Bands.Named)
            {
                int x0 = (int)Math.Round(f.Px(b.Lo)), x1 = (int)Math.Round(f.Px(b.Hi));
                if (b.Lo > 20) c.VLine(x0, f.Top, f.Top + 14, Axis);
                c.Text((x0 + x1) / 2, f.Top + 4, Short(b.Name), Muted, 1, Align.Center);
            }
            Border(c, f);
            for (int i = 0; i < curves.Count; i++)
            {
                var (hz, db) = curves[i];
                var pts = hz.Select((h, k) => (f.Px(h), double.IsNegativeInfinity(db[k]) ? f.Py(f.Y0) : f.Py(f.ClampY(db[k])))).ToList();
                c.Polyline(pts, Series[i], 2.5, f.Left, f.Top, f.Right, f.Bottom);
            }
            if (shown.Count > 1) Legend(c, f, shown.Select((s, i) => (s.Name, Series[i], true)).ToList(), insideTop: 22);
            return c.Png();
        }

        static string Short(string band) => band switch { "low_mid" => "lo-mid", "high_mid" => "hi-mid", "presence" => "pres", _ => band };

        // ---- Spectrogram -------------------------------------------------------------------

        public static byte[] Spectrogram(StereoAudio audio, string title = null, int width = DefaultWidth, int height = DefaultHeight)
        {
            var c = Start(width, height, title ?? "Spectrogram", out var f, rightMargin: 96);
            int rate = audio.Rate, size = Fft.Nearest(0.085 * rate);
            int plotW = f.Right - f.Left, plotH = f.Bottom - f.Top;
            int hop = Math.Max(size / 4, (int)Math.Ceiling(Math.Max(0, audio.Frames - size) / (double)plotW));
            int frames = Stft.Frames(audio.Frames, size, hop);
            double top = Math.Min(20000, rate / 2.0);
            f.X0 = 0; f.X1 = Math.Max(audio.Seconds, 1e-3);
            f.Y0 = 20; f.Y1 = top; f.LogY = true;
            // Each pixel row's frequency range on the log axis.
            var lo = new double[plotH];
            var hi = new double[plotH];
            for (int r = 0; r < plotH; r++)
            {
                lo[r] = f.Y0 * Math.Pow(f.Y1 / f.Y0, (plotH - r - 1) / (double)plotH);
                hi[r] = f.Y0 * Math.Pow(f.Y1 / f.Y0, (plotH - r) / (double)plotH);
            }
            var grid = new double[frames][];
            double binHz = rate / (double)size, max = double.NegativeInfinity;
            Stft.Run(audio.Left, audio.Right, size, hop, (fr, p) =>
            {
                var col = new double[plotH];
                for (int r = 0; r < plotH; r++)
                {
                    col[r] = NeoBuzz.Analysis.Spectrum.Db(NeoBuzz.Analysis.Spectrum.Integrate(p, binHz, lo[r], hi[r]) / (hi[r] - lo[r]));
                    if (col[r] > max) max = col[r];
                }
                grid[fr] = col;
            });
            const double Range = 90;
            for (int x = 0; x < plotW && frames > 0; x++)
            {
                // The frame whose centre is nearest this column's time.
                double t = (x + 0.5) / plotW * audio.Frames;
                int fr = Math.Clamp((int)Math.Round((t - size / 2.0) / hop), 0, frames - 1);
                for (int r = 0; r < plotH; r++) c.Set(f.Left + x, f.Top + r, RampColor((grid[fr][r] - (max - Range)) / Range));
            }
            TimeAxis(c, f, grid: false);
            YAxisHz(c, f);
            Border(c, f);
            // Colour bar.
            int bx = f.Right + 18, bw = 16;
            for (int y = f.Top; y <= f.Bottom; y++) c.HLine(bx, bx + bw, y, RampColor(1 - (y - f.Top) / (double)(f.Bottom - f.Top)));
            c.HLine(bx, bx + bw, f.Top, Axis); c.HLine(bx, bx + bw, f.Bottom, Axis); c.VLine(bx, f.Top, f.Bottom, Axis); c.VLine(bx + bw, f.Top, f.Bottom, Axis);
            foreach (double d in new[] { 0.0, -30, -60, -90 })
            {
                int y = (int)Math.Round(f.Top + (-d / Range) * (f.Bottom - f.Top));
                c.Text(bx + bw + 6, y - 7, Db(d), Ink2, 2);
            }
            c.Text(bx - 4, f.Top - 30, "dB re", Ink2, 1);
            c.Text(bx - 4, f.Top - 20, "loudest", Ink2, 1);
            return c.Png();
        }

        static Rgb RampColor(double t)
        {
            if (double.IsNaN(t) || t <= 0) return Ramp[0];
            if (t >= 1) return Ramp[^1];
            double pos = t * (Ramp.Length - 1);
            int i = (int)Math.Floor(pos);
            return Rgb.Lerp(Ramp[i], Ramp[i + 1], pos - i);
        }

        static void YAxisHz(Canvas c, Frame f)
        {
            foreach (double t in HzTicks.Where(t => t >= f.Y0 && t <= f.Y1))
            {
                int y = (int)Math.Round(f.Py(t));
                c.HLine(f.Left - 4, f.Left, y, Axis);
                c.Text(f.Left - 8, y - 7, Hz(t), Ink2, 2, Align.Right);
            }
            c.TextUp(14, (f.Top + f.Bottom) / 2, "frequency (Hz)", Ink2, 2);
        }

        // ---- Waveform ----------------------------------------------------------------------

        public static byte[] Waveform(StereoAudio audio, string title = null, int width = DefaultWidth, int height = DefaultHeight)
        {
            var lanes = audio.Right != null && !ReferenceEquals(audio.Right, audio.Left)
                ? new[] { ("L", audio.Left), ("R", audio.Right) } : new[] { ("mono", audio.Left) };
            double peak = Math.Max(audio.Left.Select(Math.Abs).DefaultIfEmpty(0).Max(), audio.Right?.Select(Math.Abs).DefaultIfEmpty(0).Max() ?? 0);
            string peakText = peak > 0 ? (20 * Math.Log10(peak)).ToString("+0.0;-0.0", C) + " dBFS" : "-inf dBFS";
            var c = Start(width, height, (title ?? "Waveform") + $" (peak {peakText})", out var f);
            f.X0 = 0; f.X1 = Math.Max(audio.Seconds, 1e-3);
            TimeAxis(c, f);
            int plotW = f.Right - f.Left;
            const int Gap = 20;
            int laneH = (f.Bottom - f.Top - (lanes.Length - 1) * Gap) / lanes.Length;
            var light = new Rgb(0x86b6ef);
            var dark = Series[0];
            for (int l = 0; l < lanes.Length; l++)
            {
                var (name, x) = lanes[l];
                int top = f.Top + l * (laneH + Gap), mid = top + laneH / 2;
                double half = laneH / 2.0 - 2;
                foreach (double v in new[] { 1.0, 0.5, 0, -0.5, -1 })
                {
                    int y = (int)Math.Round(mid - v * half);
                    c.HLine(f.Left, f.Right, y, v == 0 ? Axis : Grid);
                    if (v != 0.5 && v != -0.5) c.Text(f.Left - 8, y - 7, v.ToString("0", C), Ink2, 2, Align.Right);
                }
                for (int px = 0; px < plotW; px++)
                {
                    int a = (int)((long)px * x.Length / plotW), b = Math.Max(a + 1, (int)((long)(px + 1) * x.Length / plotW));
                    if (a >= x.Length) break;
                    double lo = double.MaxValue, hi = double.MinValue, sum = 0;
                    bool clipped = false;
                    for (int i = a; i < Math.Min(b, x.Length); i++)
                    {
                        double v = x[i];
                        lo = Math.Min(lo, v); hi = Math.Max(hi, v); sum += v * v;
                        if (Math.Abs(v) >= 0.999) clipped = true;
                    }
                    double rms = Math.Sqrt(sum / Math.Max(1, Math.Min(b, x.Length) - a));
                    int X = f.Left + px;
                    c.VLine(X, (int)Math.Round(mid - Math.Clamp(hi, -1, 1) * half), (int)Math.Round(mid - Math.Clamp(lo, -1, 1) * half), light);
                    c.VLine(X, (int)Math.Round(mid - Math.Min(rms, 1) * half), (int)Math.Round(mid + Math.Min(rms, 1) * half), dark);
                    if (clipped) { c.Fill(X, top, 1, 4, Clip); c.Fill(X, top + laneH - 4, 1, 4, Clip); }
                }
                c.Text(f.Left + 6, top + 4, name, Ink, 2);
            }
            c.TextUp(14, (f.Top + f.Bottom) / 2, "amplitude (full scale 1)", Ink2, 2);
            Border(c, f);
            Legend(c, f, new List<(string, Rgb, bool)> { ("peak", light, false), ("RMS", dark, false) }.Concat(
                lanes.Any(ln => ln.Item2.Any(v => Math.Abs(v) >= 0.999)) ? new[] { ("clipping", Clip, false) } : Array.Empty<(string, Rgb, bool)>()).ToList());
            return c.Png();
        }

        // ---- Loudness ----------------------------------------------------------------------

        public static byte[] Loudness(StereoAudio audio, string title = null, int width = DefaultWidth, int height = DefaultHeight)
        {
            var r = NeoBuzz.Analysis.Loudness.Measure(audio.Left, audio.Right != null && !ReferenceEquals(audio.Right, audio.Left) ? audio.Right : null, audio.Rate);
            string integrated = double.IsNegativeInfinity(r.Integrated) ? "-inf" : r.Integrated.ToString("0.0", C);
            var c = Start(width, height, (title ?? "Loudness") + $" (integrated {integrated} LUFS)", out var f);
            double max = r.Momentary.Concat(r.ShortTerm).Where(v => !double.IsInfinity(v)).DefaultIfEmpty(-20).Max();
            double top = Math.Ceiling((max + 2) / 5) * 5;
            f.X0 = 0; f.X1 = Math.Max(audio.Seconds, 1e-3);
            f.Y0 = top - 40; f.Y1 = top;
            TimeAxis(c, f);
            YAxis(c, f, Steps(f.Y0, f.Y1, 5), Db, "LUFS");
            Border(c, f);
            // Values every 100 ms, each stamped at the end of its window (400 ms, 3 s).
            List<(double, double)> Points(double[] v, double window) =>
                v.Select((d, i) => (f.Px(window + i * 0.1), double.IsInfinity(d) || d < f.Y0 - 10 ? double.NaN : f.Py(f.ClampY(d)))).ToList();
            c.Polyline(Points(r.Momentary, 0.4), Series[0], 1.5, f.Left, f.Top, f.Right, f.Bottom);
            c.Polyline(Points(r.ShortTerm, 3.0), Series[1], 3, f.Left, f.Top, f.Right, f.Bottom);
            if (!double.IsInfinity(r.Integrated) && r.Integrated >= f.Y0)
            {
                int y = (int)Math.Round(f.Py(r.Integrated));
                c.HLine(f.Left, f.Right, y, Ink2, dash: 6);
                c.Text(f.Left + 8, y - 18, $"integrated {integrated}", Ink2, 2);
            }
            Legend(c, f, new List<(string, Rgb, bool)> { ("momentary (400 ms)", Series[0], true), ("short-term (3 s)", Series[1], true), ("integrated", Ink2, true) });
            return c.Png();
        }

        // ---- Goniometer --------------------------------------------------------------------

        public static byte[] Goniometer(StereoAudio audio, string title = null, int width = DefaultWidth, int height = DefaultHeight)
        {
            var c = new Canvas(width, height, Surface);
            c.Text(16, 12, Fit(title ?? "Stereo field", (width - 32) / 12), Ink, 2);
            var left = audio.Left;
            var right = audio.Right ?? audio.Left;
            var s = Stereo.Measure(left, ReferenceEquals(right, left) ? null : right, audio.Rate);
            int size = Math.Min(height - 64, width / 2 - 40), x0 = 24, y0 = 44, cx = x0 + size / 2, cy = y0 + size / 2;
            double radius = size / 2.0 - 6;
            // Guides: M up, S across, L and R on the diagonals.
            c.Fill(x0, y0, size, size, new Rgb(0xf6f6f4));
            c.VLine(cx, y0, y0 + size, Grid);
            c.HLine(x0, x0 + size, cy, Grid);
            c.Polyline(new[] { ((double)x0, (double)y0), ((double)x0 + size, (double)y0 + size) }, Grid, 1, x0, y0, x0 + size, y0 + size);
            c.Polyline(new[] { ((double)x0 + size, (double)y0), ((double)x0, (double)y0 + size) }, Grid, 1, x0, y0, x0 + size, y0 + size);
            c.Text(x0 + 6, y0 + 6, "L", Ink2, 2);
            c.Text(x0 + size - 6, y0 + 6, "R", Ink2, 2, Align.Right);
            c.Text(cx + 6, y0 + 6, "M", Ink2, 2);
            c.Text(x0 + 6, cy + 6, "+S", Ink2, 2);
            c.Text(x0 + size - 6, cy + 6, "-S", Ink2, 2, Align.Right);
            // Points: x from side (L-heavy to the left), y from mid; scaled so the largest fills the scope.
            double peak = 1e-9;
            for (int i = 0; i < left.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i] + right[i]), Math.Abs(left[i] - right[i])) / 2);
            int step = Math.Max(1, left.Length / 250000);
            double k = radius / peak;
            var dot = Series[0];
            for (int i = 0; i < left.Length; i += step)
            {
                double m = (left[i] + right[i]) / 2, sd = (left[i] - right[i]) / 2;
                c.Blend((int)Math.Round(cx - sd * k), (int)Math.Round(cy - m * k), dot, 0.12);
            }
            c.HLine(x0, x0 + size, y0, Axis); c.HLine(x0, x0 + size, y0 + size, Axis); c.VLine(x0, y0, y0 + size, Axis); c.VLine(x0 + size, y0, y0 + size, Axis);
            c.Text(cx, y0 + size + 6, $"auto gain: edge = {(20 * Math.Log10(peak)).ToString("0.0", C)} dBFS", Muted, 1, Align.Center);

            // The numbers, a correlation meter and width per band.
            int px = x0 + size + 40, pr = width - 24, py = y0 + 4;
            string F(double v, string fmt) => double.IsPositiveInfinity(v) ? "inf" : double.IsNegativeInfinity(v) ? "-inf" : v.ToString(fmt, C);
            c.Text(px, py, $"correlation {F(s.Correlation, "+0.00;-0.00;0.00")}", Ink, 2);
            c.Text(px, py + 22, $"width (side/mid) {F(s.Width, "0.00")}", Ink, 2);
            c.Text(px, py + 44, $"mono loss {F(s.MonoLossDb, "0.0")} dB", Ink, 2);
            int my = py + 78, mw = pr - px;
            c.Text(px, my, "correlation: range over time, overall", Ink2, 2);
            my += 20;
            c.Fill(px, my, mw, 18, new Rgb(0xf0efec));
            var times = s.CorrelationOverTime.Where(v => !double.IsNaN(v)).ToList();
            if (times.Count > 0)
            {
                int a = (int)Math.Round(px + (times.Min() + 1) / 2 * mw), b = (int)Math.Round(px + (times.Max() + 1) / 2 * mw);
                c.Fill(a, my + 3, Math.Max(2, b - a), 12, new Rgb(0x86b6ef));
            }
            int mark = (int)Math.Round(px + (Math.Clamp(s.Correlation, -1, 1) + 1) / 2 * mw);
            c.Fill(mark - 1, my - 3, 3, 24, Ink);
            c.VLine(px + mw / 2, my, my + 18, Axis);
            c.Text(px, my + 24, "-1", Ink2, 2);
            c.Text(px + mw / 2, my + 24, "0", Ink2, 2, Align.Center);
            c.Text(pr, my + 24, "+1", Ink2, 2, Align.Right);

            int by = my + 62, bh = y0 + size - by - 30;
            c.Text(px, by, "width per band (side/mid; 1 = unrelated)", Ink2, 2);
            by += 36;   // the header, and room for the values over full bars
            bh -= 36;
            int n = s.Bands.Length, gw = mw / n;
            int baseline = by + bh;
            int oneY = baseline - (int)Math.Round(bh / 2.0);
            c.HLine(px, pr, oneY, Grid, dash: 4);
            c.HLine(px, pr, baseline, Axis);
            for (int i = 0; i < n; i++)
            {
                var band = s.Bands[i];
                double v = double.IsPositiveInfinity(band.Width) ? 2 : Math.Min(2, band.Width);
                int h = (int)Math.Round(v / 2 * bh);
                c.Fill(px + i * gw + 4, baseline - h, gw - 8, h, Series[0]);
                string value = double.IsPositiveInfinity(band.Width) ? "inf" : band.Width > 10 ? ">10" : band.Width.ToString("0.00", C);
                c.Text(px + i * gw + gw / 2, baseline - h - 10, value, Ink, 1, Align.Center);
                c.Text(px + i * gw + gw / 2, baseline + 6, Short(band.Name), Ink2, 1, Align.Center);
            }
            c.Text(pr, oneY - 10, "1", Muted, 1, Align.Right);
            return c.Png();
        }

        // ---- Per-track band bars -----------------------------------------------------------

        public static byte[] Bands(IReadOnlyList<(string Name, StereoAudio Audio)> tracks, string title = null, int width = DefaultWidth, int height = DefaultHeight)
        {
            var shown = tracks.Take(Series.Length).ToList();
            title ??= shown.Count == 1 ? $"Bands: {shown[0].Name}" : "Bands per track";
            if (tracks.Count > shown.Count) title += $" (first {shown.Count} of {tracks.Count})";
            var levels = shown.Select(t => new Analysed(t.Audio).SpectrumResult.NamedDb).ToList();
            var c = Start(width, height, title, out var f);
            double max = levels.SelectMany(l => l).Where(d => !double.IsInfinity(d)).DefaultIfEmpty(0).Max();
            double top = Math.Ceiling((max + 1) / 10) * 10;
            f.Y0 = top - 60; f.Y1 = top;
            YAxis(c, f, Steps(f.Y0, f.Y1, 10), Db, "dB (band level)");
            var named = NeoBuzz.Analysis.Bands.Named;
            int groups = named.Length, gw = (f.Right - f.Left) / groups;
            for (int g = 0; g < groups; g++)
            {
                int gx = f.Left + g * gw;
                int inner = gw - 16, bw = Math.Max(2, inner / Math.Max(1, shown.Count));
                for (int t = 0; t < shown.Count; t++)
                {
                    double d = levels[t][g];
                    if (double.IsNegativeInfinity(d) || d <= f.Y0) continue;
                    int y = (int)Math.Round(f.Py(Math.Min(d, f.Y1)));
                    c.Fill(gx + 8 + t * bw + 1, y, bw - 2, f.Bottom - y, Series[t]);
                }
                if (g > 0) c.VLine(gx, f.Bottom, f.Bottom + 4, Axis);
                c.Text(gx + gw / 2, f.Bottom + 8, named[g].Name.Replace('_', '-'), Ink2, 2, Align.Center);
                c.Text(gx + gw / 2, f.Bottom + 30, $"{Hz(named[g].Lo)}-{Hz(named[g].Hi)}", Muted, 1, Align.Center);
            }
            Border(c, f);
            if (shown.Count > 1) Legend(c, f, shown.Select((s, i) => (s.Name, Series[i], false)).ToList());
            return c.Png();
        }
    }
}
