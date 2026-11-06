using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The portable maths (NeoBuzz.Dsp/PortableMath.cs, docs/specs/machines.md §4) that make Neo renders
    /// bit-identical on Windows x64, Linux x64 and macOS arm64: as accurate as the platform's own libm,
    /// the same bits everywhere, and the only maths the audio paths use.
    /// </summary>
    [TestFixture]
    public class PortableMathTests
    {
        /// <summary>A fixed generator (not System.Random), so every platform sweeps the same inputs.</summary>
        sealed class Sweep
        {
            ulong s = 0x9E3779B97F4A7C15UL;

            ulong Next()
            {
                s ^= s << 13;
                s ^= s >> 7;
                s ^= s << 17;
                return s;
            }

            /// <summary>
            /// Uniform in [lo, hi), or log-uniform (lo &gt; 0) with <paramref name="log"/>; a random sign
            /// with <paramref name="signed"/>. Made with the portable maths, so the same everywhere.
            /// </summary>
            public double Next(double lo, double hi, bool log = false, bool signed = false)
            {
                double u = (Next() >> 11) * (1.0 / 9007199254740992.0);
                double v = log ? PortableMath.Exp(PortableMath.Log(lo) + (PortableMath.Log(hi) - PortableMath.Log(lo)) * u) : lo + (hi - lo) * u;
                return signed && (Next() & 1) == 1 ? -v : v;
            }
        }

        sealed record Fn(string Name, Func<double, double, double> Ours, Func<double, double, double> Libm, int Ulps,
            Func<Sweep, (double X, double Y)> Inputs);

        static readonly Fn[] Doubles =
        {
            new("Exp", (x, _) => PortableMath.Exp(x), (x, _) => Math.Exp(x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(-745, 709.7) : r.Next(-2, 2), 0)),
            new("Expm1", (x, _) => PortableMath.Expm1(x), (x, _) => Math.Abs(x) >= 1 ? Math.Exp(x) - 1 : double.NaN, 3, r => (r.Next(0, 1) < 0.5 ? r.Next(-40, 40) : r.Next(1e-12, 2, log: true, signed: true), 0)),
            new("Log", (x, _) => PortableMath.Log(x), (x, _) => Math.Log(x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(1e-300, 1e300, log: true) : r.Next(0.5, 2), 0)),
            new("Log2", (x, _) => PortableMath.Log2(x), (x, _) => Math.Log2(x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(1e-300, 1e300, log: true) : r.Next(0.5, 2), 0)),
            new("Log10", (x, _) => PortableMath.Log10(x), (x, _) => Math.Log10(x), 3, r => (r.Next(0, 1) < 0.5 ? r.Next(1e-300, 1e300, log: true) : r.Next(0.5, 2), 0)),
            new("Exp2", (x, _) => PortableMath.Exp2(x), (x, _) => Math.Pow(2, x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(-1074, 1023.9) : r.Next(-30, 30), 0)),
            new("Exp10", (x, _) => PortableMath.Exp10(x), (x, _) => Math.Pow(10, x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(-307, 308) : r.Next(-12, 4), 0)),
            new("Pow", (x, y) => PortableMath.Pow(x, y), (x, y) => Math.Pow(x, y), 2, r => r.Next(0, 1) < 0.5 ? (r.Next(1e-3, 1e3, log: true), r.Next(-30, 30)) : (r.Next(0, 1), r.Next(0, 300))),
            new("Sin", (x, _) => PortableMath.Sin(x), (x, _) => Math.Sin(x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(-1e5, 1e5) : r.Next(-7, 7), 0)),
            new("Cos", (x, _) => PortableMath.Cos(x), (x, _) => Math.Cos(x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(-1e5, 1e5) : r.Next(-7, 7), 0)),
            new("Tan", (x, _) => PortableMath.Tan(x), (x, _) => Math.Tan(x), 2, r => (r.Next(0, 1) < 0.5 ? r.Next(-1e5, 1e5) : r.Next(-1.6, 1.6), 0)),
            new("Tanh", (x, _) => PortableMath.Tanh(x), (x, _) => Math.Tanh(x), 3, r => (r.Next(0, 1) < 0.5 ? r.Next(-25, 25) : r.Next(1e-12, 2, log: true, signed: true), 0)),
            new("Log1p", (x, _) => PortableMath.Log1p(x), (x, _) => Math.Abs(x) >= 1 ? Math.Log(1 + x) : double.NaN, 3, r => (r.Next(0, 1) < 0.5 ? r.Next(-0.99, 3) : r.Next(1e-12, 0.5, log: true, signed: true), 0)),
            new("Asinh", (x, _) => PortableMath.Asinh(x), (x, _) => Math.Asinh(x), 3, r => (r.Next(0, 1) < 0.5 ? r.Next(1e-10, 1e300, log: true, signed: true) : r.Next(-3, 3), 0)),
            new("Atan", (x, _) => PortableMath.Atan(x), (x, _) => Math.Atan(x), 2, r => (r.Next(1e-10, 1e20, log: true, signed: true), 0)),
            new("Atan2", (y, x) => PortableMath.Atan2(y, x), (y, x) => Math.Atan2(y, x), 3, r => (r.Next(-10, 10), r.Next(-10, 10))),
        };

        static readonly Fn[] Floats =
        {
            new("ExpF", (x, _) => PortableMathF.Exp((float)x), (x, _) => MathF.Exp((float)x), 2, r => (r.Next(-100, 88), 0)),
            new("LogF", (x, _) => PortableMathF.Log((float)x), (x, _) => MathF.Log((float)x), 2, r => (r.Next(1e-30, 1e30, log: true), 0)),
            new("Log10F", (x, _) => PortableMathF.Log10((float)x), (x, _) => MathF.Log10((float)x), 2, r => (r.Next(1e-30, 1e30, log: true), 0)),
            new("PowF", (x, y) => PortableMathF.Pow((float)x, (float)y), (x, y) => MathF.Pow((float)x, (float)y), 2, r => (r.Next(1e-2, 1e2, log: true), r.Next(-8, 8))),
            new("SinF", (x, _) => PortableMathF.Sin((float)x), (x, _) => MathF.Sin((float)x), 2, r => (r.Next(-7, 7), 0)),
            new("CosF", (x, _) => PortableMathF.Cos((float)x), (x, _) => MathF.Cos((float)x), 2, r => (r.Next(-7, 7), 0)),
            new("TanF", (x, _) => PortableMathF.Tan((float)x), (x, _) => MathF.Tan((float)x), 2, r => (r.Next(-1.5, 1.5), 0)),
            new("TanhF", (x, _) => PortableMathF.Tanh((float)x), (x, _) => MathF.Tanh((float)x), 3, r => (r.Next(-10, 10), 0)),
        };

        const int Count = 20000;

        static long Ulps(double a, double b)
        {
            if (a == b || (double.IsNaN(a) && double.IsNaN(b))) return 0;
            if (double.IsNaN(a) || double.IsNaN(b) || double.IsInfinity(a) || double.IsInfinity(b)) return long.MaxValue;
            long ia = BitConverter.DoubleToInt64Bits(a), ib = BitConverter.DoubleToInt64Bits(b);
            if (ia < 0) ia = long.MinValue - ia;
            if (ib < 0) ib = long.MinValue - ib;
            return Math.Abs(ia - ib);
        }

        static long UlpsF(float a, float b)
        {
            if (a == b || (float.IsNaN(a) && float.IsNaN(b))) return 0;
            if (float.IsNaN(a) || float.IsNaN(b) || float.IsInfinity(a) || float.IsInfinity(b)) return long.MaxValue;
            long ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b);
            if (ia < 0) ia = int.MinValue - ia;
            if (ib < 0) ib = int.MinValue - ib;
            return Math.Abs(ia - ib);
        }

        [Test]
        public void CloseToThePlatformsLibm()
        {
            // Ours is within 1 ulp of the exact result (Atan2 1.2, Asinh 1.3, Tanh 1.8, measured against
            // 200-bit references); a libm within 1 to 2 (glibc's tanhf: 2). They agree within a few ulps.
            foreach (var f in Doubles)
            {
                var sweep = new Sweep();
                long worst = 0;
                for (int i = 0; i < Count; i++)
                {
                    var (x, y) = f.Inputs(sweep);
                    double libm = f.Libm(x, y);
                    if (double.IsNaN(libm)) continue;
                    long u = Ulps(f.Ours(x, y), libm);
                    Assert.That(u, Is.LessThanOrEqualTo(f.Ulps), $"{f.Name}({x:R}, {y:R}): {f.Ours(x, y):R}, libm {libm:R}");
                    worst = Math.Max(worst, u);
                }
                TestContext.Out.WriteLine($"{f.Name}: within {worst} ulp of libm");
            }
            foreach (var f in Floats)
            {
                var sweep = new Sweep();
                for (int i = 0; i < Count; i++)
                {
                    var (x, y) = f.Inputs(sweep);
                    float ours = (float)f.Ours(x, y), libm = (float)f.Libm(x, y);
                    Assert.That(UlpsF(ours, libm), Is.LessThanOrEqualTo(f.Ulps), $"{f.Name}({(float)x:R}, {(float)y:R}): {ours:R}, libm {libm:R}");
                }
            }
        }

        [Test]
        public void SpecialCasesAsC99()
        {
            double[] v =
            {
                0.0, -0.0, 1.0, -1.0, 2.0, -2.0, 0.5, -0.5, 3.0, -3.0, 10.0, 1e-310, -1e-310, double.Epsilon,
                1e300, -1e300, double.PositiveInfinity, double.NegativeInfinity, double.NaN,
                709.78, 710, -745.1, -746, 1024, -1074, -1075, -1076, Math.PI / 2, Math.PI, 22, -22,
            };
            void Same(string what, double ours, double libm)
            {
                bool same = double.IsNaN(ours) ? double.IsNaN(libm) : BitConverter.DoubleToInt64Bits(ours) == BitConverter.DoubleToInt64Bits(libm);
                // Finite, non-zero results may differ in the last bit (as any two libms do).
                bool near = !same && double.IsFinite(ours) && double.IsFinite(libm) && ours != 0 && libm != 0 && Ulps(ours, libm) <= 2;
                Assert.That(same || near, Is.True, $"{what}: {ours:R}, libm {libm:R}");
            }
            foreach (double x in v)
            {
                Same($"Exp({x})", PortableMath.Exp(x), Math.Exp(x));
                Same($"Log({x})", PortableMath.Log(x), Math.Log(x));
                Same($"Log2({x})", PortableMath.Log2(x), Math.Log2(x));
                Same($"Log10({x})", PortableMath.Log10(x), Math.Log10(x));
                Same($"Exp2({x})", PortableMath.Exp2(x), Math.Pow(2, x));
                if (!(Math.Abs(x) > 1e6))   // beyond, a different reduction (below)
                {
                    Same($"Sin({x})", PortableMath.Sin(x), Math.Sin(x));
                    Same($"Cos({x})", PortableMath.Cos(x), Math.Cos(x));
                    Same($"Tan({x})", PortableMath.Tan(x), Math.Tan(x));
                }
                Same($"Tanh({x})", PortableMath.Tanh(x), Math.Tanh(x));
                Same($"Atan({x})", PortableMath.Atan(x), Math.Atan(x));
                Same($"Asinh({x})", PortableMath.Asinh(x), Math.Asinh(x));
                foreach (double y in v)
                {
                    Same($"Pow({x}, {y})", PortableMath.Pow(x, y), Math.Pow(x, y));
                    Same($"Atan2({x}, {y})", PortableMath.Atan2(x, y), Math.Atan2(x, y));
                }
            }
            // Exact results stay exact.
            Assert.That(PortableMath.Pow(10, 3), Is.EqualTo(1000.0));
            Assert.That(PortableMath.Pow(2, -3), Is.EqualTo(0.125));
            Assert.That(PortableMath.Pow(16, 0.25), Is.EqualTo(2.0));
            Assert.That(PortableMath.Log10(1000), Is.EqualTo(3.0));
            Assert.That(PortableMath.Log2(1024), Is.EqualTo(10.0));
            Assert.That(PortableMath.Exp(0), Is.EqualTo(1.0));
            // Beyond 2^19·π/2 the reduction is modulo the double nearest 2π: less accurate, still bounded.
            Assert.That(Math.Abs(PortableMath.Sin(1e300)), Is.LessThanOrEqualTo(1.0));
            Assert.That(Math.Abs(PortableMath.Cos(-1e300)), Is.LessThanOrEqualTo(1.0));
        }

        // FNV-1a over every result's bits, per function: the same on every platform, or a render
        // would not be. Pinned from Linux x64; a change to PortableMath changes them on purpose.
        static readonly Dictionary<string, ulong> Pinned = new()
        {
            ["Exp"] = 0x212DC3FB6BC73A2EUL,
            ["Expm1"] = 0xE2D3E100B1BFC83EUL,
            ["Log"] = 0xD90C6DF1DAC4E496UL,
            ["Log2"] = 0xA36B0A73B148DD97UL,
            ["Log10"] = 0xFCA2B32503E7D3F9UL,
            ["Exp2"] = 0xC0AE023A307B473FUL,
            ["Exp10"] = 0x29509B354D55FA07UL,
            ["Pow"] = 0x4F0BA28CA3BF5039UL,
            ["Sin"] = 0x1D153446A1A47E89UL,
            ["Cos"] = 0xC161B0E304820123UL,
            ["Tan"] = 0xF4C54AB86996B37CUL,
            ["Tanh"] = 0x5F0C4380507A828AUL,
            ["Log1p"] = 0x580F41CD011E1DE5UL,
            ["Asinh"] = 0x2EC2866779173D40UL,
            ["Atan"] = 0x20BB3592714A176EUL,
            ["Atan2"] = 0xD00BC011A41205B0UL,
            ["ExpF"] = 0x6D982A45279E50A8UL,
            ["LogF"] = 0x6C544786EB390FC8UL,
            ["Log10F"] = 0x632373ECC783D999UL,
            ["PowF"] = 0xE0F210D3A7EBAA7CUL,
            ["SinF"] = 0xFE4721466B767A6EUL,
            ["CosF"] = 0xAD1C15B9BA68E9EBUL,
            ["TanF"] = 0xAF87B5F4EDCB26A4UL,
            ["TanhF"] = 0x39B2D0B7E660C7B0UL,
        };

        static ulong Hash(Fn f)
        {
            var sweep = new Sweep();
            ulong h = 0xcbf29ce484222325UL;
            for (int i = 0; i < Count; i++)
            {
                var (x, y) = f.Inputs(sweep);
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(f.Ours(x, y));
                for (int b = 0; b < 64; b += 8)
                {
                    h ^= (bits >> b) & 0xFF;
                    h *= 0x100000001b3UL;
                }
            }
            return h;
        }

        [Test]
        public void SameBitsOnEveryPlatform()
        {
            var got = Doubles.Concat(Floats).ToDictionary(f => f.Name, Hash);
            foreach (var (name, hash) in got)
                Assert.That(hash, Is.EqualTo(Pinned[name]), $"{name}: 0x{hash:X16} (pinned 0x{Pinned[name]:X16}); this platform computes different bits");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static float Half(float x) => x * 0.5f;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static double Half(double x) => x * 0.5;

        [Test]
        public void SubnormalsAreKept()
        {
            // IEEE subnormals on every platform (no flush-to-zero or denormals-are-zero mode), so tiny
            // values round the same everywhere; the machines flush feedback state themselves (DspMath.Flush).
            Assert.That(Half(1.17549435e-38f), Is.EqualTo(5.877472e-39f), "a subnormal float result");
            Assert.That(Half(5.877472e-39f), Is.EqualTo(2.938736e-39f), "a subnormal float input");
            Assert.That(Half(2.2250738585072014e-308), Is.EqualTo(1.1125369292536007e-308), "a subnormal double result");
            Assert.That(Half(1.1125369292536007e-308), Is.EqualTo(5.562684646268003e-309), "a subnormal double input");
        }

        // ── the rule for audio paths (docs/specs/machines.md §4) ─────────────────────

        static string MachinesDir([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "Machines"));

        static readonly Regex Libm = new(
            @"\b(Math|MathF)\.(Sin|Cos|Tan|Asin|Acos|Atan|Atan2|Sinh|Cosh|Tanh|Asinh|Acosh|Atanh|Exp|Exp2|Exp10|ExpM1|Log|Log2|Log10|LogP1|Pow|Cbrt|Hypot|SinCos|SinPi|CosPi|TanPi|IEEERemainder|FusedMultiplyAdd|ReciprocalEstimate|ReciprocalSqrtEstimate)\s*\(");

        static readonly Regex WidthDependent = new(
            @"\bVector<|\bVector(64|128|256|512)?\.(Sum|Dot)\s*\(|\bFusedMultiplyAdd\b|\bMultiplyAddEstimate\b");

        [Test]
        public void AudioPathsUseNoLibmAndNoWidthDependentSums()
        {
            // The machines' DSP: the shared library, the Buzz glue and every engine. Neo Analyzer's
            // engine measures (audio passes through it untouched), and editors draw: neither is audio.
            string root = MachinesDir();
            var files = Directory.GetFiles(Path.Combine(root, "NeoBuzz.Dsp"), "*.cs")
                .Concat(Directory.GetFiles(Path.Combine(root, "NeoBuzz.Machine"), "*.cs"))
                .Concat(Directory.GetDirectories(root, "Neo*").Append(Path.Combine(root, "DyeEssence"))
                    .Where(d => Directory.Exists(Path.Combine(d, "Engine")) && Path.GetFileName(d) != "NeoAnalyzer")
                    .SelectMany(d => Directory.GetFiles(Path.Combine(d, "Engine"), "*.cs", SearchOption.AllDirectories)))
                .Where(f => Path.GetFileName(f) != "PortableMath.cs")
                .ToList();
            Assert.That(files.Count, Is.GreaterThan(20));
            var found = new List<string>();
            foreach (var file in files)
            {
                var lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    string code = lines[i];
                    int comment = code.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0) code = code[..comment];
                    if (Libm.IsMatch(code) || WidthDependent.IsMatch(code))
                        found.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
            Assert.That(found, Is.Empty, "use PortableMath / PortableMathF, and sums in a fixed order:\n" + string.Join("\n", found));
        }
    }
}
