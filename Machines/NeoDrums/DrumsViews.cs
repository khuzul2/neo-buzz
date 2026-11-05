#nullable disable
// Neo Drums' editor pictures (docs/specs/machines.md §5), toolkit-free on the kit's canvas (INeoCanvas) with their
// pointer input (IGraphInput), so WPF and the studio's Avalonia draw and handle them the same: the pads (one per voice,
// lit as it plays; a click plays it, harder towards the top, and shows it in the hit picture) and the hit picture (the
// selected voice's hit as its settings make it now, rendered by a copy of the engine: its waveform over its decay).

using System;
using System.Globalization;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Drums
{
    /// <summary>What the pictures read and do: the parameters, the voices' levels, a hit played from a pad.</summary>
    internal interface IDrumsView
    {
        int Raw(int index);
        /// <summary>Voice <paramref name="voice"/>'s level now (a gain).</summary>
        float Peak(int voice);
        void Audition(int voice, int velocity);
    }

    /// <summary>Twelve pads, two rows of six, in the voices' order.</summary>
    internal sealed class DrumPads : IGraphInput
    {
        public const int Columns = 6, Rows = 2;
        const double Gap = 6;
        readonly IDrumsView view;
        NeoRect bounds = new NeoRect(0, 0, 480, 200);
        int hover = -1;

        public DrumPads(IDrumsView view)
        {
            this.view = view;
        }

        public int Selected { get; private set; }
        public event Action<int> SelectedChanged;
        /// <summary>The last pad played from here and its velocity (tests).</summary>
        public (int Voice, int Velocity) LastPlayed { get; private set; } = (-1, 0);
        public bool Capturing => false;

        public void Select(int voice)
        {
            voice = Math.Clamp(voice, 0, DrumsEngine.VoiceCount - 1);
            if (voice == Selected) return;
            Selected = voice;
            SelectedChanged?.Invoke(voice);
        }

        /// <summary>Pad <paramref name="voice"/>'s rectangle.</summary>
        public NeoRect Pad(int voice)
        {
            var inner = new NeoRect(bounds.X + Gap, bounds.Y + Gap, bounds.Width - 2 * Gap, bounds.Height - 2 * Gap);
            double w = (inner.Width - (Columns - 1) * Gap) / Columns, h = (inner.Height - (Rows - 1) * Gap) / Rows;
            int col = voice % Columns, row = voice / Columns;
            return new NeoRect(inner.X + col * (w + Gap), inner.Y + row * (h + Gap), w, h);
        }

        public int PadAt(double x, double y)
        {
            for (int v = 0; v < DrumsEngine.VoiceCount; v++)
            {
                var p = Pad(v);
                if (x >= p.X && x < p.Right && y >= p.Y && y < p.Bottom) return v;
            }
            return -1;
        }

        /// <summary>The velocity a click at <paramref name="y"/> plays on pad <paramref name="voice"/>: 127 at its top, 40 at its bottom.</summary>
        public int VelocityAt(int voice, double y)
        {
            var p = Pad(voice);
            double t = Math.Clamp((y - p.Y) / Math.Max(1, p.Height), 0, 1);
            return (int)Math.Round(127 - t * 87);
        }

        static string KeyName(int note)
        {
            string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
            return names[note % 12] + (note / 12 - 1).ToString(CultureInfo.InvariantCulture);
        }

        public void Draw(INeoCanvas c, NeoRect r, NeoColor accent)
        {
            bounds = r;
            for (int v = 0; v < DrumsEngine.VoiceCount; v++)
            {
                var p = Pad(v);
                double db = DspMath.GainToDb(Math.Max(1e-9f, view.Peak(v)));
                double lit = Math.Clamp((db + 48) / 48, 0, 1);
                var fill = lit > 0 ? accent.WithAlpha((byte)(0x18 + 0xA0 * lit)) : Palette.Panel;
                var edge = v == Selected ? new NeoStroke(accent, 2) : new NeoStroke(v == hover ? Palette.DimText : Palette.PanelEdge, 1);
                c.Rect(p, Palette.Panel, NeoStroke.None, 5);
                c.Rect(p, fill, edge, 5);
                c.Text(DrumsEngine.Names[v], p.X + 7, p.Y + 6, 12, lit > 0.6 ? Palette.Background : Palette.Text, bold: true, maxWidth: p.Width - 10);
                c.Text(DrumsEngine.Short[v] + " · " + KeyName(DrumsEngine.Keys[v]), p.X + 7, p.Bottom - 18, 10.5, lit > 0.6 ? Palette.Background : Palette.DimText);
            }
        }

        public bool Down(double x, double y, int clicks, bool fine)
        {
            int v = PadAt(x, y);
            if (v < 0) return false;
            Select(v);
            int velocity = VelocityAt(v, y);
            LastPlayed = (v, velocity);
            view.Audition(v, velocity);
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            int v = PadAt(x, y);
            if (v == hover) return false;
            hover = v;
            return true;
        }

        public bool Up() => false;

        public bool Wheel(double x, double y, int delta, bool fine) => false;

        public bool Leave()
        {
            if (hover < 0) return false;
            hover = -1;
            return true;
        }
    }

    /// <summary>
    /// The selected voice's hit at velocity 100 as its settings make it now, rendered by a copy of the engine (24 kHz, up to
    /// 4 s, centred): its waveform's lowest and highest value per pixel over the hit, its peak, and when it has fallen 60 dB.
    /// </summary>
    internal sealed class HitPicture
    {
        public const int Rate = 24000;
        public const double MaxSeconds = 4;
        readonly ParamValues mirror = new ParamValues(Params.Table);
        readonly DrumsEngine engine;
        readonly float[] samples = new float[(int)(Rate * MaxSeconds)];
        readonly float[] l = new float[DrumsEngine.MaxBlock], r = new float[DrumsEngine.MaxBlock];
        int length, voice = -1;
        bool dirty = true;
        float peak;

        public HitPicture()
        {
            engine = new DrumsEngine(mirror);
        }

        /// <summary>Samples rendered for the voice shown (tests).</summary>
        public int Length => length;
        public float PeakGain => peak;

        public void Invalidate() => dirty = true;

        void Render(IDrumsView view, int v)
        {
            for (int i = 0; i < Params.Table.Length; i++) mirror.Set(i, view.Raw(i));
            mirror.Set(DrumsEngine.Voice[v].Pan, Params.Table[DrumsEngine.Voice[v].Pan].Raw(0));
            engine.SetSampleRate(Rate);
            engine.Hit(v, 100f / 127f);
            length = 0;
            peak = 0;
            while (length < samples.Length)
            {
                int n = Math.Min(DrumsEngine.MaxBlock, samples.Length - length);
                bool sound = engine.Process(l, r, n);
                for (int i = 0; i < n; i++)
                {
                    float x = 0.5f * (l[i] + r[i]);
                    samples[length + i] = x;
                    peak = Math.Max(peak, Math.Abs(x));
                }
                length += n;
                if (!sound || !engine.IsActive) break;
            }
            voice = v;
            dirty = false;
        }

        public void Draw(INeoCanvas c, NeoRect area, IDrumsView view, int v, NeoColor accent)
        {
            if (dirty || v != voice) Render(view, v);
            var ci = CultureInfo.InvariantCulture;
            var plot = new NeoRect(area.X + 8, area.Y + 22, area.Width - 16, area.Height - 40);
            double seconds = Math.Max(0.1, length / (double)Rate);
            double span = seconds <= 0.25 ? 0.25 : seconds <= 0.5 ? 0.5 : seconds <= 1 ? 1 : seconds <= 2 ? 2 : 4;
            var grid = new NeoStroke(Palette.Grid, 1);
            double mid = plot.Y + plot.Height / 2;
            c.Line(new NeoPoint(plot.X, mid), new NeoPoint(plot.Right, mid), grid);
            int ticks = 4;
            for (int k = 0; k <= ticks; k++)
            {
                double x = plot.X + k * plot.Width / ticks;
                c.Line(new NeoPoint(x, plot.Y), new NeoPoint(x, plot.Bottom), grid);
                double t = span * k / ticks;
                string label = t < 1 ? (t * 1000).ToString("0", ci) + " ms" : t.ToString("0.#", ci) + " s";
                c.Text(label, Math.Min(x, plot.Right - 30), plot.Bottom + 3, 9.5, Palette.DimText);
            }
            float scale = peak > 1e-9f ? 1f / peak : 1f;
            int columns = Math.Max(1, (int)plot.Width);
            var band = new NeoPath();
            var lows = new double[columns];
            for (int col = 0; col < columns; col++)
            {
                int from = (int)((long)col * (long)(span * Rate) / columns), to = (int)((long)(col + 1) * (long)(span * Rate) / columns);
                float lo = 0, hi = 0;
                for (int i = from; i < Math.Min(to, length); i++)
                {
                    float x = samples[i] * scale;
                    if (x < lo) lo = x;
                    if (x > hi) hi = x;
                }
                double px = plot.X + col + 0.5;
                band.LineTo(px, mid - hi * (plot.Height / 2 - 1));
                lows[col] = mid - lo * (plot.Height / 2 - 1);
            }
            for (int col = columns - 1; col >= 0; col--) band.LineTo(plot.X + col + 0.5, lows[col]);
            band.Close();
            c.Path(band, accent.WithAlpha(0x90), new NeoStroke(accent, 1));
            c.Text(DrumsEngine.Names[v], area.X + 8, area.Y + 4, 11.5, Palette.Text, bold: true);
            string peakText = peak > 1e-9f ? (20 * Math.Log10(peak)).ToString("+0.0;−0.0;0.0", ci) + " dBFS peak" : "silent";
            c.Text($"velocity 100 · {peakText} · {seconds.ToString(seconds < 1 ? "0.00" : "0.0", ci)} s", area.Right - 8, area.Y + 5, 10, Palette.DimText, align: NeoAlign.Right);
        }
    }
}
