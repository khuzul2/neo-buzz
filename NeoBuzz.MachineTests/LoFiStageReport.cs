using System.Globalization;
using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.LoFi;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// What each stage of a Neo Lo-Fi setting does, measured on test signals with the other stages switched off
    /// (docs/specs/neo-lofi.md section 4): effective bits, hold rate, hiss and crackle level, wow, flutter and drift depth in cents, the tone's
    /// -3 dB point. Used by the pack and by the Amount gates; the explicit test writes a table of every factory preset.
    ///   NEO_LOFI_REPORT=/some/file.md dotnet test NeoBuzz.MachineTests -c Release --filter "FullyQualifiedName~LoFiStageReport"
    /// </summary>
    [TestFixture]
    public class LoFiStageReport
    {
        const int Rate = Measure.Rate;
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        internal sealed class Stages
        {
            public double Bits = double.NaN;        // effective bits (from the signal-to-error ratio of a -3 dBFS sine); NaN: off
            public double HoldHz = double.NaN;      // NaN: off
            public double HissDb = double.NaN;      // RMS dBFS; NaN: off
            public double CracklePerSec, CrackleDb = double.NaN;
            public double WowCt, FlutterCt, DriftCt;
            public double ToneMinus3Hz = double.NaN, ToneShelfDb;
        }

        /// <summary>A fresh value set with every stage off, the listed parameters copied from <paramref name="src"/> (and Amount, when the machine has it).</summary>
        internal static ParamValues Isolate(ParamValues src, params int[] keep)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Bits, Params.Table[P.Bits].Raw(24));
            v.Set(P.Rate, Params.Table[P.Rate].Max);
            foreach (int k in keep) v.Set(k, src[k]);
            if (Params.Table.Length > 16) v.Set(16, src[16]);
            return v;
        }

        static float[] Run(ParamValues v, float[] x)
        {
            var e = new LoFiEngine(v);
            e.SetSampleRate(Rate);
            var y = (float[])x.Clone();
            var r = (float[])x.Clone();
            var bl = new float[256];
            var br = new float[256];
            for (int pos = 0; pos < x.Length; pos += 256)
            {
                int n = Math.Min(256, x.Length - pos);
                Array.Copy(y, pos, bl, 0, n);
                Array.Copy(r, pos, br, 0, n);
                e.Process(bl, br, n);
                Array.Copy(bl, 0, y, pos, n);
            }
            return y;
        }

        static double RmsDb(float[] x, int from, int n) => Measure.Db(Measure.Rms(x, from, n));

        internal static double EffectiveBits(ParamValues src)
        {
            var v = Isolate(src, P.Bits, P.Dither);
            var x = Measure.Sine(997, Rate * 2, 0.7079);
            var y = Run(v, x);
            double sig = 0, err = 0;
            for (int i = Rate / 2; i < x.Length; i++) { sig += (double)x[i] * x[i]; double d = y[i] - x[i]; err += d * d; }
            if (err < 1e-20) return double.NaN;
            double snr = 10 * Math.Log10(sig / err);
            return (snr - 1.76 - 20 * Math.Log10(0.7079)) / 6.02;
        }

        internal static double HoldRate(ParamValues src)
        {
            var v = Isolate(src, P.Rate, P.Jitter);
            var x = new float[Rate * 4];
            for (int i = 0; i < x.Length; i++) x[i] = i * 3.5e-6f;
            var y = Run(v, x);
            int changes = 0;
            for (int i = Rate / 2; i < y.Length; i++) if (y[i] != y[i - 1]) changes++;
            double hz = changes / 3.5;
            return Math.Abs(hz - Rate) < 0.02 * Rate || changes > 3.4 * Rate ? double.NaN : hz;
        }

        internal static double Hiss(ParamValues src)
        {
            var v = Isolate(src, P.Hiss, P.HissFollow);
            bool follow = src.On(P.HissFollow);
            var x = follow ? Measure.PinkNoise(Rate * 4, 0.2, 9) : new float[Rate * 4];
            var y = Run(v, x);
            var d = new float[y.Length];
            for (int i = 0; i < d.Length; i++) d[i] = y[i] - x[i];
            double db = RmsDb(d, Rate, Rate * 3);
            return db < -120 ? double.NaN : db;
        }

        internal static (double PerSec, double PeakDb) Crackle(ParamValues src)
        {
            var v = Isolate(src, P.Crackle, P.CrackleLevel);
            const int Seconds = 20;
            var y = Run(v, new float[Rate * Seconds]);
            double peak = Measure.Peak(y, 0, y.Length);
            if (peak == 0) return (0, double.NaN);
            double thr = 0.2 * peak;
            int count = 0;
            bool armed = true;
            foreach (float s in y)
            {
                double a = Math.Abs(s);
                if (armed && a > thr) { count++; armed = false; }
                else if (!armed && a < thr / 2) armed = true;
            }
            return (count / (double)Seconds, Measure.Db(peak));
        }

        /// <summary>Peak pitch deviation in cents of a 330 Hz tone through the tracker.</summary>
        static double Depth(ParamValues v, double seconds, double skip)
        {
            const double hz = 330;
            int n = (int)(seconds * Rate);
            var y = Run(v, Measure.Sine(hz, n, 0.3));
            var tracker = new PitchTracker(Rate, PitchRange.Voice);
            var frames = new PitchEstimate[tracker.MaxFrames(n)];
            int count = tracker.Process(y, 0, n, frames, 0);
            var cents = new List<double>();
            for (int i = 0; i < count; i++)
                if (frames[i].Voiced && frames[i].EndSample > (long)(skip * Rate)) cents.Add(1200 * Math.Log2(frames[i].F0 / hz));
            if (cents.Count < 20) return 0;
            return (cents.Max() - cents.Min()) / 2;
        }

        internal static double Wow(ParamValues src) => src.Value(P.Wow) == 0 ? 0 : Depth(Isolate(src, P.Wow, P.WowRate), 8, 1.5);
        internal static double Flutter(ParamValues src) => src.Value(P.Flutter) == 0 ? 0 : Depth(Isolate(src, P.Flutter), 6, 1);
        internal static double Drift(ParamValues src) => src.Value(P.Drift) == 0 ? 0 : Depth(Isolate(src, P.Drift), 40, 1);

        static double ToneGain(ParamValues v, double hz)
        {
            var y = Run(v, Measure.Sine(hz, Rate, 0.3));
            double a = 0, b = 0;
            int from = Rate / 2, n = Rate / 2;
            for (int i = 0; i < n; i++)
            {
                double ph = 2 * Math.PI * hz * (from + i) / Rate;
                a += y[from + i] * Math.Cos(ph);
                b += y[from + i] * Math.Sin(ph);
            }
            return Measure.Db(2 * Math.Sqrt(a * a + b * b) / n / 0.3);
        }

        internal static (double Minus3Hz, double ShelfDb) Tone(ParamValues src)
        {
            var v = Isolate(src, P.Tone);
            if (v[P.Tone] == 500) return (double.NaN, 0);
            if (src.Value(P.Tone) > 0) return (double.NaN, ToneGain(v, 12000));
            double prev = 0, prevHz = 100;
            for (double hz = 100; hz <= 21000; hz *= 1.05)
            {
                double g = ToneGain(v, hz);
                if (g < -3) return (prevHz + (hz - prevHz) * (-3 - prev) / (g - prev), 0);
                prev = g; prevHz = hz;
            }
            return (double.NaN, 0);
        }

        internal static Stages Measure_(ParamValues src)
        {
            var s = new Stages { Bits = EffectiveBits(src), HoldHz = HoldRate(src), HissDb = Hiss(src) };
            (s.CracklePerSec, s.CrackleDb) = Crackle(src);
            s.WowCt = Wow(src); s.FlutterCt = Flutter(src); s.DriftCt = Drift(src);
            (s.ToneMinus3Hz, s.ToneShelfDb) = Tone(src);
            return s;
        }

        static string F(double v, string fmt, string off = "off") => double.IsNaN(v) ? off : v.ToString(fmt, C);

        internal static string Row(string name, Stages s) =>
            $"| {name} | {F(s.Bits, "0.0")} | {F(s.HoldHz, "0")} | {F(s.HissDb, "0.0")} | " +
            (s.CracklePerSec > 0 ? $"{s.CracklePerSec:0.0}/s at {s.CrackleDb:0.0} dB" : "off") + " | " +
            $"{(s.WowCt > 0 ? s.WowCt.ToString("0.0", C) : "-")} / {(s.FlutterCt > 0 ? s.FlutterCt.ToString("0.0", C) : "-")} / {(s.DriftCt > 0 ? s.DriftCt.ToString("0.0", C) : "-")} | " +
            (!double.IsNaN(s.ToneMinus3Hz) ? $"{s.ToneMinus3Hz:0} Hz" : s.ToneShelfDb != 0 ? $"+{s.ToneShelfDb:0.0} dB at 12 kHz" : "flat") + " |";

        internal const string Header =
            "| Preset | Effective bits | Hold rate (Hz) | Hiss (dBFS RMS) | Crackle | Wow / flutter / drift (ct) | Tone |\n|---|---|---|---|---|---|---|";

        [Test, Explicit("writes a table of every factory preset's measured stages: set NEO_LOFI_REPORT to the output file")]
        public void WriteTheStageTable()
        {
            string file = Environment.GetEnvironmentVariable("NEO_LOFI_REPORT");
            if (string.IsNullOrEmpty(file)) Assert.Inconclusive("set NEO_LOFI_REPORT");
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Lo-Fi"))
            {
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                sb.AppendLine(Row(p.Preset, Measure_(v)));
            }
            File.WriteAllText(file, sb.ToString());
        }
    }
}
