#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): portable maths, so Neo renders are bit-identical
// on Windows x64, Linux x64 and macOS arm64 (docs/specs/mac-engine.md decision 6).
//
// Math.Sin, Exp, Pow, Tanh … and the MathF functions call the platform's C runtime (the UCRT, glibc,
// Apple's libm), and those round the last bit differently. These are built only from IEEE basic
// operations (+ − × ÷, sqrt, comparisons, exact roundings and conversions, moving bits), which give
// the same bits everywhere. No FusedMultiplyAdd and no intrinsics: .NET never contracts a·b + c into
// an FMA, so every line rounds the same on x64 and arm64. Audio paths use these, never Math's or
// MathF's transcendental functions (Sqrt, Abs, Floor, Round, Min and Max are exact and stay);
// NeoBuzz.MachineTests' PortableMathTests checks the sources for it.
//
// Exp and 2^x (so Exp2, Exp10 and Pow) are table-driven (Tang's method: no division); the rest are
// fdlibm's. Accuracy, measured against 200-bit references: Exp, Exp2, Exp10, Pow, Log2 and Log10
// within 0.51 ulp (0.63 for subnormal results); Log, Log1p, Expm1, Sin, Cos, Tan, Atan under 0.8 ulp;
// Atan2 1.2, Asinh 1.3, Tanh 1.8. Sin, Cos and Tan are that accurate for |x| up to 2^19·π/2 (about
// 8e5); beyond, the argument is first reduced modulo the double nearest 2π (exact, so still the same
// bits everywhere, but less accurate; no audio path goes there). PortableMathF gives the float
// versions through the double ones. Speed, per call on x64: Exp, Exp2, Exp10 and Pow(2 or 10, y) as
// fast as glibc's or faster; Log, Sin, Cos, Tan, Tanh, Atan within 10–40 %; Log2, Log10 and a
// general Pow 2–4 times glibc's (one division in series); the float versions about twice glibc's
// float functions.
//
// Ported from fdlibm 5.3 (e_log.c, e_pow.c, k_sin.c, k_cos.c, k_tan.c, e_rem_pio2.c, s_expm1.c,
// s_log1p.c, s_asinh.c, s_tanh.c, s_atan.c, e_atan2.c), whose notice follows:
//
//   Copyright (C) 1993 by Sun Microsystems, Inc. All rights reserved.
//   Developed at SunSoft, a Sun Microsystems, Inc. business.
//   Permission to use, copy, modify, and distribute this software is freely granted, provided that
//   this notice is preserved.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>Deterministic double-precision maths (see the file header); mirrors <see cref="Math"/>.</summary>
    internal static class PortableMath
    {
        // ---- words of a double -------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int Hi(double x) => (int)(BitConverter.DoubleToInt64Bits(x) >> 32);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static uint Lo(double x) => (uint)BitConverter.DoubleToInt64Bits(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double Words(int hi, uint lo) => BitConverter.Int64BitsToDouble(((long)hi << 32) | lo);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double SetHi(double x, int hi) => Words(hi, Lo(x));

        /// <summary>x with its low word cleared (21 significant bits).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double ClearLo(double x) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(x) & unchecked((long)0xFFFFFFFF00000000UL));

        // Constants are const (decimal literals that parse to the bits given), not static readonly
        // fields: no static constructor, so no class-initialisation checks in the audio paths.
        const double Two1023 = 8.98846567431158e+307;          // 2^1023, 0x7fe00000 00000000
        const double TwoM1022 = 2.2250738585072014e-308;       // 2^-1022
        const double Two53 = 9007199254740992.0;
        const double Two54 = 1.80143985094819840000e+16;
        const double Huge = 1.0e300, Tiny = 1.0e-300;

        /// <summary>x·2^n, exact unless the result is subnormal (then one rounding).</summary>
        public static double ScaleB(double x, int n)
        {
            if (n > 1023)
            {
                x *= Two1023;
                n -= 1023;
                if (n > 1023) { x *= Two1023; n -= 1023; if (n > 1023) n = 1023; }
            }
            else if (n < -1022)
            {
                // Scale by 2^-1022·2^53 first, so the last step rounds once.
                x *= TwoM1022 * Two53;
                n += 1022 - 53;
                if (n < -1022) { x *= TwoM1022 * Two53; n += 1022 - 53; if (n < -1022) n = -1022; }
            }
            return x * BitConverter.Int64BitsToDouble((long)(0x3ff + n) << 52);
        }

        // ---- exp ---------------------------------------------------------------------------------

        const double OThreshold = 7.09782712893383973096e+02;     // 0x40862E42 FEFA39EF
        const double UThreshold = -7.45133219101941108420e+02;    // 0xc0874910 D52D3051
        const double Ln2Hi = 6.93147180369123816490e-01;          // 0x3fe62e42 fee00000
        const double Ln2Lo = 1.90821492927058770002e-10;          // 0x3dea39ef 35793c76
        const double InvLn2 = 1.44269504088896338700e+00;         // 0x3ff71547 652b82fe

        // Exp is table-driven (Tang's method; no division): x = (64m + j)·ln2/64 + r, |r| ≤ ln2/128, and
        // e^x = 2^m · 2^(j/64) · e^r, with 2^(j/64) as a double-double from the table (200-bit values).
        const double ExpN = 92.33248261689366;                 // 64/ln2
        const double ExpStepHi = 0.010830424696223417;         // ln2/64, 36 bits (0x3f862e42 fefa0000): k·it is exact
        const double ExpStepLo = 2.572804622327669e-14;        // ln2/64 − ExpStepHi

        /// <summary>2^(j/64) for j = 0 … 63, as pairs: the nearest double, then the remainder.</summary>
        static ReadOnlySpan<double> ExpTable => new double[]
        {
            1.0, 0.0,
            1.0108892860517005, -1.5234778603368577e-17,
            1.0218971486541166, 5.109225028973444e-17,
            1.0330248790212284, 7.600838874027088e-18,
            1.0442737824274138, 8.551889705537965e-17,
            1.0556451783605572, 1.759325738772092e-18,
            1.0671404006768237, -7.899853966841582e-17,
            1.0787607977571199, -6.656660436056593e-17,
            1.0905077326652577, -3.046782079812471e-17,
            1.102382583307841, 5.2660368715706944e-17,
            1.1143867425958924, 1.0410278456845571e-16,
            1.1265216186082418, 5.165856758795457e-17,
            1.1387886347566916, 8.912812676025408e-17,
            1.1511892299529827, 3.250710218863827e-17,
            1.1637248587775775, 3.8292048369240935e-17,
            1.1763969916502812, 5.554203254218079e-17,
            1.189207115002721, 3.982015231465646e-17,
            1.202156731452703, 6.644981499252301e-17,
            1.215247359980469, -7.712630692681488e-17,
            1.22848053610687, -1.89878163130253e-17,
            1.241857812073484, 4.658027591836937e-17,
            1.255380757024691, -6.7113898212968784e-18,
            1.2690509571917332, 2.667932131342186e-18,
            1.2828700160787783, 1.713594918243561e-17,
            1.2968395546510096, 2.5382502794888315e-17,
            1.3109612115247644, -7.181536135519454e-17,
            1.3252366431597413, -2.8587312100388614e-17,
            1.339667524053303, 8.927282594831732e-17,
            1.3542555469368927, 7.70094837980299e-17,
            1.3690024229745905, 9.593797919118849e-17,
            1.383909881963832, -6.770511658794786e-17,
            1.3989796725383112, -9.614213209051323e-17,
            1.4142135623730951, -9.667293313452913e-17,
            1.42961333839197, -1.2031642489053655e-17,
            1.4451808069770467, -3.0237581349939873e-17,
            1.460917794180647, -5.600377186075216e-17,
            1.4768261459394993, -3.483994556892796e-17,
            1.4929077282912648, 1.4192920154284036e-17,
            1.5091644275934228, -1.016455327754295e-16,
            1.5255981507445384, -1.1024941712342561e-16,
            1.5422108254079407, 7.949834809697621e-17,
            1.559004400237837, 3.7812070533575275e-17,
            1.5759808451078865, -1.0136916471278304e-17,
            1.593142151342267, -1.0094406542311964e-16,
            1.6104903319492543, 2.4707192569797888e-17,
            1.6280274218573478, -6.712955084707084e-17,
            1.645755478153965, -1.0125679913674773e-16,
            1.6636765803267364, 5.8909926967131e-17,
            1.681792830507429, 8.199010020581497e-17,
            1.7001063537185235, -8.0237193703977e-18,
            1.718619298122478, -1.851380418263111e-17,
            1.7373338352737062, 3.164389299292957e-17,
            1.7562521603732995, 2.960140695448873e-17,
            1.7753764925265212, 6.429731796556572e-17,
            1.7947090750031072, 1.8227458427912087e-17,
            1.8142521755003989, -9.969531538920349e-17,
            1.8340080864093424, 3.283107224245627e-17,
            1.8539791250833855, 9.761887490727594e-17,
            1.8741676341103, -6.122763413004143e-17,
            1.8945759815869656, 3.4034035352165297e-17,
            1.9152065613971474, -1.0619946056195963e-16,
            1.9360617934922943, 1.0332385960676326e-16,
            1.9571441241754002, 8.960767791036668e-17,
            1.978456026387951, 4.0388753109278167e-17,
        };

        /// <summary>e^x.</summary>
        public static double Exp(double x)
        {
            if (double.IsNaN(x)) return x;
            if (x > OThreshold) return double.PositiveInfinity;
            if (x < UThreshold) return 0.0;
            double kd = Math.Round(x * ExpN);
            double r = (x - kd * ExpStepHi) - kd * ExpStepLo;
            int k = (int)kd;
            int j = (k & 63) << 1;
            int m = k >> 6;
            double p = r + r * r * (0.5 + r * (1.0 / 6 + r * (1.0 / 24 + r * (1.0 / 120 + r * (1.0 / 720)))));
            var t = ExpTable;
            double th = t[j];
            double y = th + (t[j + 1] + th * p);
            if (m >= -1022 && m <= 1023) return y * BitConverter.Int64BitsToDouble((long)(0x3ff + m) << 52);
            return ScaleB(y, m);
        }

        // ---- expm1 -------------------------------------------------------------------------------

        const double Q1 = -3.33333333333331316428e-02;            // BFA11111 111110F4
        const double Q2 = 1.58730158725481460165e-03;             // 3F5A01A0 19FE5585
        const double Q3 = -7.93650757867487942473e-05;            // BF14CE19 9EAADBB7
        const double Q4 = 4.00821782732936239552e-06;             // 3ED0CFCA 86E65239
        const double Q5 = -2.01099218183624371326e-07;            // BE8AFDB7 6E09C32D

        /// <summary>e^x − 1, accurate near 0.</summary>
        public static double Expm1(double x)
        {
            int hx = Hi(x);
            int xsb = hx & unchecked((int)0x80000000);
            hx &= 0x7fffffff;
            double hi, lo, c = 0, t, e, y;
            int k;

            if (hx >= 0x4043687A)
            {
                // |x| ≥ 56·ln2.
                if (hx >= 0x40862E42)
                {
                    if (hx >= 0x7ff00000)
                    {
                        if (((hx & 0xfffff) | (int)Lo(x)) != 0) return x + x;
                        return xsb == 0 ? x : -1.0;
                    }
                    if (x > OThreshold) return double.PositiveInfinity;
                }
                if (xsb != 0) return -1.0;
            }

            if (hx > 0x3fd62e42)
            {
                if (hx < 0x3FF0A2B2)
                {
                    if (xsb == 0) { hi = x - Ln2Hi; lo = Ln2Lo; k = 1; }
                    else { hi = x + Ln2Hi; lo = -Ln2Lo; k = -1; }
                }
                else
                {
                    k = (int)(InvLn2 * x + (xsb == 0 ? 0.5 : -0.5));
                    t = k;
                    hi = x - t * Ln2Hi;
                    lo = t * Ln2Lo;
                }
                x = hi - lo;
                c = (hi - x) - lo;
            }
            else if (hx < 0x3c900000)
            {
                return x;   // |x| < 2^-54
            }
            else k = 0;

            double hfx = 0.5 * x;
            double hxs = x * hfx;
            double r1 = 1.0 + hxs * (Q1 + hxs * (Q2 + hxs * (Q3 + hxs * (Q4 + hxs * Q5))));
            t = 3.0 - r1 * hfx;
            e = hxs * ((r1 - t) / (6.0 - x * t));
            if (k == 0) return x - (x * e - hxs);
            e = x * (e - c) - c;
            e -= hxs;
            if (k == -1) return 0.5 * (x - e) - 0.5;
            if (k == 1)
            {
                if (x < -0.25) return -2.0 * (e - (x + 0.5));
                return 1.0 + 2.0 * (x - e);
            }
            if (k <= -2 || k > 56)
            {
                y = 1.0 - (e - x);
                y = k == 1024 ? y * 2.0 * Two1023 : SetHi(y, Hi(y) + (k << 20));
                return y - 1.0;
            }
            if (k < 20)
            {
                t = Words(0x3ff00000 - (0x200000 >> k), 0);   // 1 − 2^-k
                y = t - (e - x);
            }
            else
            {
                t = Words((0x3ff - k) << 20, 0);               // 2^-k
                y = x - (e + t);
                y += 1.0;
            }
            return SetHi(y, Hi(y) + (k << 20));
        }

        // ---- log ---------------------------------------------------------------------------------

        const double Lg1 = 6.666666666666735130e-01;              // 3FE55555 55555593
        const double Lg2 = 3.999999999940941908e-01;              // 3FD99999 9997FA04
        const double Lg3 = 2.857142874366239149e-01;              // 3FD24924 94229359
        const double Lg4 = 2.222219843214978396e-01;              // 3FCC71C5 1D8E78AF
        const double Lg5 = 1.818357216161805012e-01;              // 3FC74664 96CB03DE
        const double Lg6 = 1.531383769920937332e-01;              // 3FC39A09 D078C69F
        const double Lg7 = 1.479819860511658591e-01;              // 3FC2F112 DF3E5244

        /// <summary>Natural logarithm.</summary>
        public static double Log(double x)
        {
            int hx = Hi(x);
            uint lx = Lo(x);
            int k = 0;
            if (hx < 0x00100000)
            {
                // x < 2^-1022: zero, negative, NaN with the sign set, or subnormal.
                if (((hx & 0x7fffffff) | (int)lx) == 0) return double.NegativeInfinity;
                if (hx < 0) return double.NaN;
                k -= 54;
                x *= Two54;
                hx = Hi(x);
            }
            if (hx >= 0x7ff00000) return x + x;
            k += (hx >> 20) - 1023;
            hx &= 0x000fffff;
            int i = (hx + 0x95f64) & 0x100000;
            x = SetHi(x, hx | (i ^ 0x3ff00000));   // x or x/2, in [sqrt(2)/2, sqrt(2))
            k += i >> 20;
            double f = x - 1.0;
            double dk;
            if ((0x000fffff & (2 + hx)) < 3)
            {
                // −2^-20 ≤ f < 2^-20.
                if (f == 0.0)
                {
                    if (k == 0) return 0.0;
                    dk = k;
                    return dk * Ln2Hi + dk * Ln2Lo;
                }
                double rr = f * f * (0.5 - 0.33333333333333333 * f);
                if (k == 0) return f - rr;
                dk = k;
                return dk * Ln2Hi - ((rr - dk * Ln2Lo) - f);
            }
            double s = f / (2.0 + f);
            dk = k;
            double z = s * s;
            i = hx - 0x6147a;
            double w = z * z;
            int j = 0x6b851 - hx;
            double t1 = w * (Lg2 + w * (Lg4 + w * Lg6));
            double t2 = z * (Lg1 + w * (Lg3 + w * (Lg5 + w * Lg7)));
            i |= j;
            double r = t2 + t1;
            if (i > 0)
            {
                double hfsq = 0.5 * f * f;
                if (k == 0) return f - (hfsq - s * (hfsq + r));
                return dk * Ln2Hi - ((hfsq - (s * (hfsq + r) + dk * Ln2Lo)) - f);
            }
            if (k == 0) return f - s * (f - r);
            return dk * Ln2Hi - ((s * (f - r) - dk * Ln2Lo) - f);
        }

        /// <summary>ln(1 + x), accurate near 0.</summary>
        public static double Log1p(double x)
        {
            int hx = Hi(x);
            int ax = hx & 0x7fffffff;
            double f = 0, c = 0, u;
            int hu = 0;
            int k = 1;
            if (hx < 0x3FDA827A)
            {
                // x < 0.41422.
                if (ax >= 0x3ff00000)
                {
                    // x ≤ −1.
                    if (x == -1.0) return double.NegativeInfinity;
                    return double.NaN;
                }
                if (ax < 0x3e200000)
                {
                    // |x| < 2^-29.
                    if (ax < 0x3c900000) return x;
                    return x - x * x * 0.5;
                }
                if (hx > 0 || hx <= unchecked((int)0xbfd2bec3))
                {
                    // −0.2929 < x < 0.41422.
                    k = 0;
                    f = x;
                    hu = 1;
                }
            }
            if (hx >= 0x7ff00000) return x + x;
            if (k != 0)
            {
                if (hx < 0x43400000)
                {
                    u = 1.0 + x;
                    hu = Hi(u);
                    k = (hu >> 20) - 1023;
                    c = k > 0 ? 1.0 - (u - x) : x - (u - 1.0);   // the rounding of 1 + x
                    c /= u;
                }
                else
                {
                    u = x;
                    hu = Hi(u);
                    k = (hu >> 20) - 1023;
                    c = 0;
                }
                hu &= 0x000fffff;
                if (hu < 0x6a09e)
                {
                    u = SetHi(u, hu | 0x3ff00000);   // u
                }
                else
                {
                    k += 1;
                    u = SetHi(u, hu | 0x3fe00000);   // u/2
                    hu = (0x00100000 - hu) >> 2;
                }
                f = u - 1.0;
            }
            double hfsq = 0.5 * f * f;
            double rr;
            if (hu == 0)
            {
                // |f| < 2^-20.
                if (f == 0.0)
                {
                    if (k == 0) return 0.0;
                    c += k * Ln2Lo;
                    return k * Ln2Hi + c;
                }
                rr = hfsq * (1.0 - 0.66666666666666666 * f);
                if (k == 0) return f - rr;
                return k * Ln2Hi - ((rr - (k * Ln2Lo + c)) - f);
            }
            double s = f / (2.0 + f);
            double z = s * s;
            rr = z * (Lg1 + z * (Lg2 + z * (Lg3 + z * (Lg4 + z * (Lg5 + z * (Lg6 + z * Lg7))))));
            if (k == 0) return f - (hfsq - s * (hfsq + rr));
            return k * Ln2Hi - ((hfsq - (s * (hfsq + rr) + (k * Ln2Lo + c))) - f);
        }

        /// <summary>asinh(x) = ln(x + sqrt(x² + 1)).</summary>
        public static double Asinh(double x)
        {
            int hx = Hi(x);
            int ix = hx & 0x7fffffff;
            if (ix >= 0x7ff00000) return x + x;   // ±∞, NaN
            if (ix < 0x3e300000) return x;        // |x| < 2^-28
            double w, t;
            if (ix > 0x41b00000)
            {
                w = Log(Math.Abs(x)) + 6.93147180559945286227e-01;   // |x| > 2^28: ln 2|x|
            }
            else if (ix > 0x40000000)
            {
                t = Math.Abs(x);                                      // 2 < |x| ≤ 2^28
                w = Log(2.0 * t + 1.0 / (Math.Sqrt(x * x + 1.0) + t));
            }
            else
            {
                t = x * x;                                            // |x| ≤ 2
                w = Log1p(Math.Abs(x) + t / (1.0 + Math.Sqrt(1.0 + t)));
            }
            return hx > 0 ? w : -w;
        }

        // ---- log2 in extra precision, 2^x, pow (e_pow.c) -----------------------------------------

        const double DpH1 = 5.84962487220764160156e-01;           // 0x3FE2B803 40000000: log2(1.5) high
        const double DpL1 = 1.35003920212974897128e-08;           // 0x3E4CFDEB 43CFD006: its tail
        const double L1 = 5.99999999999994648725e-01;             // 0x3FE33333 33333303
        const double L2 = 4.28571428578550184252e-01;             // 0x3FDB6DB6 DB6FABFF
        const double L3 = 3.33333329818377432918e-01;             // 0x3FD55555 518F264D
        const double L4 = 2.72728123808534006489e-01;             // 0x3FD17460 A91D4101
        const double L5 = 2.30660745775561754067e-01;             // 0x3FCD864A 93C9DB65
        const double L6 = 2.06975017800338417784e-01;             // 0x3FCA7E28 4A454EEF
        const double Ovt = 8.0085662595372944372e-17;             // −(1024 − log2(ovfl + .5ulp))
        const double Cp = 9.61796693925975554329e-01;             // 0x3FEEC709 DC3A03FD: 2/(3 ln2)
        const double CpH = 9.61796700954437255859e-01;            // 0x3FEEC709 E0000000
        const double CpL = -7.02846165095275826516e-09;           // 0xBE3E2FE0 145B01F5
        const double IvLn2 = 1.44269504088896338700e+00;          // 0x3FF71547 652B82FE
        const double IvLn2H = 1.44269502162933349609e+00;         // 0x3FF71547 60000000
        const double IvLn2L = 1.92596299112661746887e-08;         // 0x3E54AE0B F85DDF44
        // log10(2) = Log10Of2Hi + Log10Of2Lo (to 2^-76): the high part has 21 bits, so t1 times it is exact.
        const double Log10Of2Hi = 0.30102992057800293;         // 0x3fd34413 00000000
        const double Log10Of2Lo = 7.508597826552624e-08;       // 0x3e7427de 7fbcc47c

        /// <summary>
        /// log2(ax) as t1 + t2 to about 64 bits, for a positive finite ax: t1 has 21 significant
        /// bits, so y·t1 splits exactly.
        /// </summary>
        static void Log2Ext(double ax, out double t1, out double t2)
        {
            int ix = Hi(ax) & 0x7fffffff;
            int n = 0;
            if (ix < 0x00100000)
            {
                ax *= Two53;
                n -= 53;
                ix = Hi(ax);
            }
            n += (ix >> 20) - 0x3ff;
            int j = ix & 0x000fffff;
            ix = j | 0x3ff00000;
            int k;
            if (j <= 0x3988E) k = 0;            // |x| < sqrt(3/2)
            else if (j < 0xBB67A) k = 1;        // |x| < sqrt(3)
            else { k = 0; n += 1; ix -= 0x00100000; }
            ax = SetHi(ax, ix);

            // ss = s_h + s_l = (x − 1)/(x + 1) or (x − 1.5)/(x + 1.5).
            double bp = k == 0 ? 1.0 : 1.5;
            double dpH = k == 0 ? 0.0 : DpH1, dpL = k == 0 ? 0.0 : DpL1;
            double u = ax - bp;
            double v = 1.0 / (ax + bp);
            double ss = u * v;
            double sH = ClearLo(ss);
            double tH = Words(((ix >> 1) | 0x20000000) + 0x00080000 + (k << 18), 0);
            double tL = ax - (tH - bp);
            double sL = v * ((u - sH * tH) - sH * tL);

            double s2 = ss * ss;
            double r = s2 * s2 * (L1 + s2 * (L2 + s2 * (L3 + s2 * (L4 + s2 * (L5 + s2 * L6)))));
            r += sL * (sH + ss);
            s2 = sH * sH;
            tH = ClearLo(3.0 + s2 + r);
            tL = r - ((tH - 3.0) - s2);
            u = sH * tH;
            v = sL * tH + tL * ss;
            double pH = ClearLo(u + v);
            double pL = v - (pH - u);
            double zH = CpH * pH;
            double zL = CpL * pH + pL * Cp + dpL;
            double t = n;
            t1 = ClearLo(((zH + zL) + dpH) + t);
            t2 = zL - (((t1 - t) - dpH) - zH);
        }

        /// <summary>
        /// 2^(pH + pL), |pH + pL| ≤ 1075 and pL small beside pH: through <see cref="ExpTable"/>, as
        /// <see cref="Exp"/>. pH − k/64 is exact, and what is left is below ln2/128, so rounding it
        /// costs under 2^-59: the result is within about 0.51 ulp.
        /// </summary>
        static double Exp2Ext(double pH, double pL)
        {
            double kd = Math.Round(pH * 64.0);
            double r = ((pH - kd * (1.0 / 64)) + pL) * 0.6931471805599453;
            int k = (int)kd;
            int j = (k & 63) << 1;
            int m = k >> 6;
            double p = r + r * r * (0.5 + r * (1.0 / 6 + r * (1.0 / 24 + r * (1.0 / 120 + r * (1.0 / 720)))));
            var t = ExpTable;
            double th = t[j];
            double y = th + (t[j + 1] + th * p);
            if (m >= -1022 && m <= 1023) return y * BitConverter.Int64BitsToDouble((long)(0x3ff + m) << 52);
            return ScaleB(y, m);
        }

        /// <summary>x^y, with C99's special cases. Bases 2 and 10 take <see cref="Exp2"/> and <see cref="Exp10"/>.</summary>
        public static double Pow(double x, double y)
        {
            if (x == 2.0) return Exp2(y);
            if (x == 10.0) return Exp10(y);
            long xb = BitConverter.DoubleToInt64Bits(x), yb = BitConverter.DoubleToInt64Bits(y);
            int hy = (int)(yb >> 32);
            if (xb > 0 && xb < 0x7ff0000000000000L && (hy & 0x7fffffff) <= 0x41e00000 && (yb & 0x7fffffffffffffffL) != 0)
            {
                // The usual case, x positive and finite, y finite, nonzero, |y| ≤ 2^31: the same
                // results as the full routine below, without its special cases.
                if ((uint)yb == 0)
                {
                    if (hy == 0x3ff00000) return x;
                    if (hy == unchecked((int)0xbff00000)) return 1.0 / x;
                    if (hy == 0x40000000) return x * x;
                    if (hy == 0x3fe00000) return Math.Sqrt(x);
                }
                Log2Ext(x, out double l1, out double l2);
                return Exp2Times(y, l1, l2, 1.0);
            }
            return PowFull(x, y, xb, yb);
        }

        /// <summary>s·2^(y·(t1 + t2)), with overflow and underflow.</summary>
        static double Exp2Times(double y, double t1, double t2, double s)
        {
            // (y1 + y2)·(t1 + t2), y1 with 21 bits.
            double y1 = ClearLo(y);
            double pL = (y - y1) * t1 + y * t2;
            double pH = y1 * t1;
            double zz = pL + pH;
            int jz = Hi(zz);
            int iz = (int)Lo(zz);
            if (jz >= 0x40900000)
            {
                // z ≥ 1024.
                if (((jz - 0x40900000) | iz) != 0) return s * Huge * Huge;
                if (pL + Ovt > zz - pH) return s * Huge * Huge;
            }
            else if ((jz & 0x7fffffff) >= 0x4090cc00)
            {
                // z ≤ −1075.
                if (((jz - unchecked((int)0xc090cc00)) | iz) != 0) return s * Tiny * Tiny;
                if (pL <= zz - pH) return s * Tiny * Tiny;
            }
            return s * Exp2Ext(pH, pL);
        }

        /// <summary>fdlibm's pow with every special case.</summary>
        static double PowFull(double x, double y, long xb, long yb)
        {
            int hx = (int)(xb >> 32), hy = (int)(yb >> 32);
            uint lx = (uint)xb, ly = (uint)yb;
            int ix = hx & 0x7fffffff, iy = hy & 0x7fffffff;

            if ((iy | (int)ly) == 0) return 1.0;                   // x^0 = 1
            if (hx == 0x3ff00000 && lx == 0) return 1.0;           // 1^y = 1, even for NaN
            if (ix > 0x7ff00000 || (ix == 0x7ff00000 && lx != 0) || iy > 0x7ff00000 || (iy == 0x7ff00000 && ly != 0))
                return x + y;                                      // NaN

            // For x < 0: yisint 0 (y not an integer), 1 (odd), 2 (even).
            int yisint = 0;
            if (hx < 0)
            {
                if (iy >= 0x43400000) yisint = 2;
                else if (iy >= 0x3ff00000)
                {
                    int e = (iy >> 20) - 0x3ff;
                    if (e > 20)
                    {
                        uint jj = ly >> (52 - e);
                        if ((jj << (52 - e)) == ly) yisint = 2 - (int)(jj & 1);
                    }
                    else if (ly == 0)
                    {
                        int jj = iy >> (20 - e);
                        if ((jj << (20 - e)) == iy) yisint = 2 - (jj & 1);
                    }
                }
            }

            if (ly == 0)
            {
                if (iy == 0x7ff00000)
                {
                    // y = ±∞.
                    if (((ix - 0x3ff00000) | (int)lx) == 0) return 1.0;      // (−1)^±∞
                    if (ix >= 0x3ff00000) return hy >= 0 ? y : 0.0;          // |x| > 1
                    return hy < 0 ? -y : 0.0;                                // |x| < 1
                }
                if (iy == 0x3ff00000) return hy < 0 ? 1.0 / x : x;           // y = ±1
                if (hy == 0x40000000) return x * x;                          // y = 2
                if (hy == 0x3fe00000 && hx >= 0) return Math.Sqrt(x);        // y = 0.5
            }

            double ax = Math.Abs(x);
            if (lx == 0 && (ix == 0x7ff00000 || ix == 0 || ix == 0x3ff00000))
            {
                // x = ±0, ±∞, ±1.
                double z = ax;
                if (hy < 0) z = 1.0 / z;
                if (hx < 0)
                {
                    if (((ix - 0x3ff00000) | yisint) == 0) z = double.NaN;    // (−1)^non-integer
                    else if (yisint == 1) z = -z;
                }
                return z;
            }

            int nsign = (int)((uint)hx >> 31) - 1;
            if ((nsign | yisint) == 0) return double.NaN;                    // (x < 0)^non-integer
            double s = (nsign | (yisint - 1)) == 0 ? -1.0 : 1.0;             // (x < 0)^odd

            double t1, t2;
            if (iy > 0x41e00000)
            {
                // |y| > 2^31.
                if (iy > 0x43f00000)
                {
                    if (ix <= 0x3fefffff) return hy < 0 ? Huge * Huge : Tiny * Tiny;
                    if (ix >= 0x3ff00000) return hy > 0 ? Huge * Huge : Tiny * Tiny;
                }
                if (ix < 0x3fefffff) return hy < 0 ? s * Huge * Huge : s * Tiny * Tiny;
                if (ix > 0x3ff00000) return hy > 0 ? s * Huge * Huge : s * Tiny * Tiny;
                // |1 − x| ≤ 2^-20: log(x) by x − x²/2 + x³/3 − x⁴/4.
                double t = ax - 1.0;
                double w = (t * t) * (0.5 - t * (0.3333333333333333333333 - t * 0.25));
                double u = IvLn2H * t;
                double v = t * IvLn2L - w * IvLn2;
                t1 = ClearLo(u + v);
                t2 = v - (t1 - u);
            }
            else
            {
                Log2Ext(ax, out t1, out t2);
            }

            return Exp2Times(y, t1, t2, s);
        }

        /// <summary>2^x (what <see cref="Pow"/> gives for x = 2).</summary>
        public static double Exp2(double x)
        {
            if (double.IsNaN(x)) return x;
            if (x >= 1024.0) return double.PositiveInfinity;
            if (x < -1075.0) return 0.0;
            return Exp2Ext(x, 0.0);
        }

        // log2(10) = Log2Of10Hi + Log2Of10Lo (to 2^-84); the high part has 21 bits.
        const double Log2Of10Hi = 3.321928024291992;           // 0x400a934f 00000000
        const double Log2Of10Lo = 7.059537016037032e-08;       // 0x3e72f346 e2bf924b

        /// <summary>10^x (what <see cref="Pow"/> gives for x = 10).</summary>
        public static double Exp10(double x)
        {
            if (double.IsNaN(x)) return x;
            if (x > 309.0) return double.PositiveInfinity;
            if (x < -324.0) return 0.0;
            // x·log2(10) as pH + pL: x1 and Log2Of10Hi have 21 bits each, so pH is exact.
            double x1 = ClearLo(x);
            double pH = x1 * Log2Of10Hi;
            double pL = (x - x1) * Log2Of10Hi + x * Log2Of10Lo;
            double z = pL + pH;
            if (z >= 1024.0) return double.PositiveInfinity;
            if (z <= -1075.0) return 0.0;
            return Exp2Ext(pH, pL);
        }

        /// <summary>log2(x).</summary>
        public static double Log2(double x)
        {
            if (!(x > 0.0)) return x == 0.0 ? double.NegativeInfinity : double.NaN;
            if (double.IsPositiveInfinity(x)) return x;
            Log2Ext(x, out double t1, out double t2);
            return t1 + t2;
        }

        /// <summary>log10(x).</summary>
        public static double Log10(double x)
        {
            if (!(x > 0.0)) return x == 0.0 ? double.NegativeInfinity : double.NaN;
            if (double.IsPositiveInfinity(x)) return x;
            Log2Ext(x, out double t1, out double t2);
            return t1 * Log10Of2Hi + (t1 * Log10Of2Lo + t2 * (Log10Of2Hi + Log10Of2Lo));
        }

        // ---- sin, cos, tan -----------------------------------------------------------------------

        const double S1 = -1.66666666666666324348e-01;            // BFC55555 55555549
        const double S2 = 8.33333333332248946124e-03;             // 3F811111 1110F8A6
        const double S3 = -1.98412698298579493134e-04;            // BF2A01A0 19C161D5
        const double S4 = 2.75573137070700676789e-06;             // 3EC71DE3 57B1FE7D
        const double S5 = -2.50507602534068634195e-08;            // BE5AE5E6 8A2B9CEB
        const double S6 = 1.58969099521155010221e-10;             // 3DE5D93A 5ACFD57C
        const double C1 = 4.16666666666666019037e-02;             // 3FA55555 5555554C
        const double C2 = -1.38888888888741095749e-03;            // BF56C16C 16C15177
        const double C3 = 2.48015872894767294178e-05;             // 3EFA01A0 19CB1590
        const double C4 = -2.75573143513906633035e-07;            // BE927E4F 809C52AD
        const double C5 = 2.08757232129817482790e-09;             // 3E21EE9E BDB4B1C4
        const double C6 = -1.13596475577881948265e-11;            // BDA8FAE9 BE8838D4
        const double InvPio2 = 6.36619772367581382433e-01;        // 3FE45F30 6DC9C883
        const double Pio2_1 = 1.57079632673412561417e+00;         // 3FF921FB 54400000: first 33 bits of π/2
        const double Pio2_1t = 6.07710050650619224932e-11;        // 3DD0B461 1A626331: π/2 − Pio2_1
        const double Pio2_2 = 6.07710050630396597660e-11;         // 3DD0B461 1A600000: next 33 bits
        const double Pio2_2t = 2.02226624879595063154e-21;        // 3BA3198A 2E037073
        const double Pio2_3 = 2.02226624871116645580e-21;         // 3BA3198A 2E000000: next 33 bits
        const double Pio2_3t = 8.47842766036889956997e-32;        // 397B839A 252049C1

        /// <summary>sin(x + y) for |x| ≤ π/4, y the tail of x (iy = 0: y is 0).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double KSin(double x, double y, int iy)
        {
            double z = x * x;
            double v = z * x;
            double r = S2 + z * (S3 + z * (S4 + z * (S5 + z * S6)));
            if (iy == 0) return x + v * (S1 + z * r);
            return x - ((z * (0.5 * y - v * r) - y) - v * S1);
        }

        /// <summary>cos(x + y) for |x| ≤ π/4.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static double KCos(double x, double y)
        {
            int ix = Hi(x) & 0x7fffffff;
            if (ix < 0x3e400000) return 1.0;   // |x| < 2^-27
            double z = x * x;
            double r = z * (C1 + z * (C2 + z * (C3 + z * (C4 + z * (C5 + z * C6)))));
            if (ix < 0x3FD33333) return 1.0 - (0.5 * z - (z * r - x * y));   // |x| < 0.3
            double qx = ix > 0x3fe90000 ? 0.28125 : Words(ix - 0x00200000, 0);   // x/4
            double hz = 0.5 * z - qx;
            double a = 1.0 - qx;
            return a - (hz - (z * r - x * y));
        }

        const double T0 = 3.33333333333334091986e-01;    // 3FD55555 55555563
        const double T1 = 1.33333333333201242699e-01;    // 3FC11111 1110FE7A
        const double T2 = 5.39682539762260521377e-02;    // 3FABA1BA 1BB341FE
        const double T3 = 2.18694882948595424599e-02;    // 3F9664F4 8406D637
        const double T4 = 8.86323982359930005737e-03;    // 3F8226E3 E96E8493
        const double T5 = 3.59207910759131235356e-03;    // 3F6D6D22 C9560328
        const double T6 = 1.45620945432529025516e-03;    // 3F57DBC8 FEE08315
        const double T7 = 5.88041240820264096874e-04;    // 3F4344D8 F2F26501
        const double T8 = 2.46463134818469906812e-04;    // 3F3026F7 1A8D1068
        const double T9 = 7.81794442939557092300e-05;    // 3F147E88 A03792A6
        const double T10 = 7.14072491382608190305e-05;    // 3F12B80F 32F0A7E9
        const double T11 = -1.85586374855275456654e-05;   // BEF375CB DB605373
        const double T12 = 2.59073051863633712884e-05;    // 3EFB2A70 74BF7AD4
        const double Pio4 = 7.85398163397448278999e-01;           // 3FE921FB 54442D18
        const double Pio4Lo = 3.06161699786838301793e-17;         // 3C81A626 33145C07

        /// <summary>tan(x + y) for |x| ≤ π/4 (iy = 1), or −1/tan(x + y) (iy = −1).</summary>
        static double KTan(double x, double y, int iy)
        {
            int hx = Hi(x);
            int ix = hx & 0x7fffffff;
            double z, r, v, w, s;
            if (ix < 0x3e300000)
            {
                // |x| < 2^-28.
                if ((int)x == 0)
                {
                    if (((ix | (int)Lo(x)) | (iy + 1)) == 0) return 1.0 / Math.Abs(x);
                    if (iy == 1) return x;
                    // −1/(x + y), carefully.
                    z = w = x + y;
                    z = ClearLo(z);
                    v = y - (z - x);
                    double a0 = -1.0 / w;
                    double t0 = ClearLo(a0);
                    s = 1.0 + t0 * z;
                    return t0 + a0 * (s + t0 * v);
                }
            }
            if (ix >= 0x3FE59428)
            {
                // |x| ≥ 0.6744: tan(π/4 − x).
                if (hx < 0) { x = -x; y = -y; }
                z = Pio4 - x;
                w = Pio4Lo - y;
                x = z + w;
                y = 0.0;
            }
            z = x * x;
            w = z * z;
            r = T1 + w * (T3 + w * (T5 + w * (T7 + w * (T9 + w * T11))));
            v = z * (T2 + w * (T4 + w * (T6 + w * (T8 + w * (T10 + w * T12)))));
            s = z * x;
            r = y + z * (s * (r + v) + y);
            r += T0 * s;
            w = x + r;
            if (ix >= 0x3FE59428)
            {
                v = iy;
                return (1 - ((hx >> 30) & 2)) * (v - 2.0 * (x - (w * w / (w + v) - r)));
            }
            if (iy == 1) return w;
            // −1/(x + r), carefully.
            z = ClearLo(w);
            v = r - (z - x);
            double a = -1.0 / w;
            double t = ClearLo(a);
            s = 1.0 + t * z;
            return t + a * (s + t * v);
        }

        /// <summary>x = n·π/2 + (y0 + y1), |y0| ≤ π/4; returns n. For |x| &gt; π/4.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int RemPio2(double x, out double y0, out double y1)
        {
            int hx = Hi(x);
            int ix = hx & 0x7fffffff;
            double z;
            if (ix > 0x413921fb)
            {
                // |x| > 2^19·π/2: first modulo the double nearest 2π. The remainder is exact (so
                // deterministic), the result less accurate; no audio path gets here.
                x %= 2.0 * Math.PI;
                hx = Hi(x);
                ix = hx & 0x7fffffff;
                if (ix <= 0x3fe921fb) { y0 = x; y1 = 0.0; return 0; }
            }
            if (ix < 0x4002d97c)
            {
                // |x| < 3π/4: n = ±1.
                if (hx > 0)
                {
                    z = x - Pio2_1;
                    if (ix != 0x3ff921fb) { y0 = z - Pio2_1t; y1 = (z - y0) - Pio2_1t; }
                    else { z -= Pio2_2; y0 = z - Pio2_2t; y1 = (z - y0) - Pio2_2t; }
                    return 1;
                }
                z = x + Pio2_1;
                if (ix != 0x3ff921fb) { y0 = z + Pio2_1t; y1 = (z - y0) + Pio2_1t; }
                else { z += Pio2_2; y0 = z + Pio2_2t; y1 = (z - y0) + Pio2_2t; }
                return -1;
            }
            // Cody–Waite with π/2 in 33-bit pieces, refined while the reduction cancels.
            double t = Math.Abs(x);
            int n = (int)(t * InvPio2 + 0.5);
            double fn = n;
            double r = t - fn * Pio2_1;
            double w = fn * Pio2_1t;
            int j = ix >> 20;
            y0 = r - w;
            int i = j - ((Hi(y0) >> 20) & 0x7ff);
            if (i > 16)
            {
                t = r;
                w = fn * Pio2_2;
                r = t - w;
                w = fn * Pio2_2t - ((t - r) - w);
                y0 = r - w;
                i = j - ((Hi(y0) >> 20) & 0x7ff);
                if (i > 49)
                {
                    t = r;
                    w = fn * Pio2_3;
                    r = t - w;
                    w = fn * Pio2_3t - ((t - r) - w);
                    y0 = r - w;
                }
            }
            y1 = (r - y0) - w;
            if (hx < 0) { y0 = -y0; y1 = -y1; return -n; }
            return n;
        }

        /// <summary>sin(x).</summary>
        public static double Sin(double x)
        {
            int ix = Hi(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb)
            {
                if (ix < 0x3e400000) return x;   // |x| < 2^-27
                return KSin(x, 0.0, 0);
            }
            if (ix >= 0x7ff00000) return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            switch (n & 3)
            {
                case 0: return KSin(y0, y1, 1);
                case 1: return KCos(y0, y1);
                case 2: return -KSin(y0, y1, 1);
                default: return -KCos(y0, y1);
            }
        }

        /// <summary>cos(x).</summary>
        public static double Cos(double x)
        {
            int ix = Hi(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb) return KCos(x, 0.0);
            if (ix >= 0x7ff00000) return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            switch (n & 3)
            {
                case 0: return KCos(y0, y1);
                case 1: return -KSin(y0, y1, 1);
                case 2: return -KCos(y0, y1);
                default: return KSin(y0, y1, 1);
            }
        }

        /// <summary>tan(x).</summary>
        public static double Tan(double x)
        {
            int ix = Hi(x) & 0x7fffffff;
            if (ix <= 0x3fe921fb) return KTan(x, 0.0, 1);
            if (ix >= 0x7ff00000) return x - x;
            int n = RemPio2(x, out double y0, out double y1);
            return KTan(y0, y1, 1 - ((n & 1) << 1));
        }

        // ---- tanh, atan, atan2 -------------------------------------------------------------------

        /// <summary>tanh(x).</summary>
        public static double Tanh(double x)
        {
            int jx = Hi(x);
            int ix = jx & 0x7fffffff;
            if (ix >= 0x7ff00000) return jx >= 0 ? 1.0 / x + 1.0 : 1.0 / x - 1.0;   // ±∞ → ±1, NaN
            double z;
            if (ix < 0x40360000)
            {
                // |x| < 22.
                if (ix < 0x3c800000) return x * (1.0 + x);   // |x| < 2^-55
                if (ix >= 0x3ff00000)
                {
                    double t = Expm1(2.0 * Math.Abs(x));
                    z = 1.0 - 2.0 / (t + 2.0);
                }
                else
                {
                    double t = Expm1(-2.0 * Math.Abs(x));
                    z = -t / (t + 2.0);
                }
            }
            else z = 1.0;
            return jx >= 0 ? z : -z;
        }

        const double AtanHi0 = 4.63647609000806093515e-01;    // atan(0.5) high, 3FDDAC67 0561BB4F
        const double AtanHi1 = 7.85398163397448278999e-01;    // atan(1.0) high, 3FE921FB 54442D18
        const double AtanHi2 = 9.82793723247329054082e-01;    // atan(1.5) high, 3FEF730B D281F69B
        const double AtanHi3 = 1.57079632679489655800e+00;    // atan(∞) high,   3FF921FB 54442D18
        const double AtanLo0 = 2.26987774529616870924e-17;    // 3C7A2B7F 222F65E2
        const double AtanLo1 = 3.06161699786838301793e-17;    // 3C81A626 33145C07
        const double AtanLo2 = 1.39033110312309984516e-17;    // 3C700788 7AF0CBBD
        const double AtanLo3 = 6.12323399573676603587e-17;    // 3C91A626 33145C07
        const double AT0 = 3.33333333333329318027e-01;            // 3FD55555 5555550D
        const double AT1 = -1.99999999998764832476e-01;           // BFC99999 9998EBC4
        const double AT2 = 1.42857142725034663711e-01;            // 3FC24924 920083FF
        const double AT3 = -1.11111104054623557880e-01;           // BFBC71C6 FE231671
        const double AT4 = 9.09088713343650656196e-02;            // 3FB745CD C54C206E
        const double AT5 = -7.69187620504482999495e-02;           // BFB3B0F2 AF749A6D
        const double AT6 = 6.66107313738753120669e-02;            // 3FB10D66 A0D03D51
        const double AT7 = -5.83357013379057348645e-02;           // BFADDE2D 52DEFD9A
        const double AT8 = 4.97687799461593236017e-02;            // 3FA97B4B 24760DEB
        const double AT9 = -3.65315727442169155270e-02;           // BFA2B444 2C6A6C2F
        const double AT10 = 1.62858201153657823623e-02;           // 3F90AD3A E322DA11

        /// <summary>atan(x).</summary>
        public static double Atan(double x)
        {
            int hx = Hi(x);
            int ix = hx & 0x7fffffff;
            int id;
            if (ix >= 0x44100000)
            {
                // |x| ≥ 2^66, ∞ or NaN.
                if (ix > 0x7ff00000 || (ix == 0x7ff00000 && Lo(x) != 0)) return x + x;
                return hx > 0 ? AtanHi3 + AtanLo3 : -AtanHi3 - AtanLo3;
            }
            if (ix < 0x3fdc0000)
            {
                // |x| < 0.4375.
                if (ix < 0x3e200000) return x;   // |x| < 2^-29
                id = -1;
            }
            else
            {
                x = Math.Abs(x);
                if (ix < 0x3ff30000)
                {
                    if (ix < 0x3fe60000) { id = 0; x = (2.0 * x - 1.0) / (2.0 + x); }   // 7/16 ≤ |x| < 11/16
                    else { id = 1; x = (x - 1.0) / (x + 1.0); }                          // 11/16 ≤ |x| < 19/16
                }
                else
                {
                    if (ix < 0x40038000) { id = 2; x = (x - 1.5) / (1.0 + 1.5 * x); }   // |x| < 2.4375
                    else { id = 3; x = -1.0 / x; }
                }
            }
            double z = x * x;
            double w = z * z;
            double s1 = z * (AT0 + w * (AT2 + w * (AT4 + w * (AT6 + w * (AT8 + w * AT10)))));
            double s2 = w * (AT1 + w * (AT3 + w * (AT5 + w * (AT7 + w * AT9))));
            if (id < 0) return x - x * (s1 + s2);
            double aHi = id == 0 ? AtanHi0 : id == 1 ? AtanHi1 : id == 2 ? AtanHi2 : AtanHi3;
            double aLo = id == 0 ? AtanLo0 : id == 1 ? AtanLo1 : id == 2 ? AtanLo2 : AtanLo3;
            z = aHi - ((x * (s1 + s2) - aLo) - x);
            return hx < 0 ? -z : z;
        }

        const double PiO4 = 7.8539816339744827900E-01;
        const double PiO2 = 1.5707963267948965580E+00;
        const double PiD = 3.1415926535897931160E+00;
        const double PiLo = 1.2246467991473531772E-16;            // 3CA1A626 33145C07

        /// <summary>atan2(y, x), the angle of (x, y).</summary>
        public static double Atan2(double y, double x)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) return x + y;
            long xb = BitConverter.DoubleToInt64Bits(x), yb = BitConverter.DoubleToInt64Bits(y);
            int hx = (int)(xb >> 32), hy = (int)(yb >> 32);
            uint lx = (uint)xb, ly = (uint)yb;
            int ix = hx & 0x7fffffff, iy = hy & 0x7fffffff;
            if (hx == 0x3ff00000 && lx == 0) return Atan(y);   // x = 1
            int m = ((hy >> 31) & 1) | ((hx >> 30) & 2);       // 2·sign(x) + sign(y)

            if ((iy | (int)ly) == 0)
            {
                // y = ±0.
                switch (m)
                {
                    case 0: case 1: return y;
                    case 2: return PiD;
                    default: return -PiD;
                }
            }
            if ((ix | (int)lx) == 0) return hy < 0 ? -PiO2 : PiO2;   // x = ±0
            if (ix == 0x7ff00000)
            {
                if (iy == 0x7ff00000)
                {
                    switch (m)
                    {
                        case 0: return PiO4;
                        case 1: return -PiO4;
                        case 2: return 3.0 * PiO4;
                        default: return -3.0 * PiO4;
                    }
                }
                switch (m)
                {
                    case 0: return 0.0;
                    case 1: return -0.0;
                    case 2: return PiD;
                    default: return -PiD;
                }
            }
            if (iy == 0x7ff00000) return hy < 0 ? -PiO2 : PiO2;

            int k = (iy - ix) >> 20;
            double z;
            if (k > 60) z = PiO2 + 0.5 * PiLo;                 // |y/x| > 2^60
            else if (hx < 0 && k < -60) z = 0.0;               // |y|/x < −2^60
            else z = Atan(Math.Abs(y / x));
            switch (m)
            {
                case 0: return z;
                case 1: return -z;
                case 2: return PiD - (z - PiLo);
                default: return (z - PiLo) - PiD;
            }
        }
    }

    /// <summary>
    /// Deterministic single-precision maths through <see cref="PortableMath"/> (the double result,
    /// rounded once to float); mirrors <see cref="MathF"/>.
    /// </summary>
    internal static class PortableMathF
    {
        public const float PI = MathF.PI;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sin(float x) => (float)PortableMath.Sin(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Cos(float x) => (float)PortableMath.Cos(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Tan(float x) => (float)PortableMath.Tan(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Tanh(float x) => (float)PortableMath.Tanh(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Exp(float x) => (float)PortableMath.Exp(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Log(float x) => (float)PortableMath.Log(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Log10(float x) => (float)PortableMath.Log10(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Log2(float x) => (float)PortableMath.Log2(x);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Pow(float x, float y) => (float)PortableMath.Pow(x, y);
    }
}
