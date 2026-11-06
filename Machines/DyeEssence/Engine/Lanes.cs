#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.1): eight lanes of single-precision maths, written once
// and run three ways: one Vector256 (x64 with AVX2), two Vector128 halves (arm64, or x64 without
// AVX2) and eight plain floats (the reference the tests hold the other two to). Table reads load
// each lane's four consecutive points at once and transpose them (faster than four gathers on x64,
// and arm64 has no gather). Every operation is the same IEEE basic operation on each lane in
// each implementation, nothing is fused, and the one sum across lanes runs in one fixed order, so
// all three give the same bits on every CPU (docs/specs/machines.md §4). The kernels are generic
// over the implementation (a struct with static members), so the JIT compiles one specialised copy
// of each kernel per implementation, with every call inlined.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace NeoBuzz.DyeEssence
{
    /// <summary>
    /// Eight lanes: <typeparamref name="TF"/> holds eight floats, <typeparamref name="TU"/> eight
    /// 32-bit unsigned integers (oscillator phases). Comparisons give masks as <typeparamref name="TF"/>.
    /// </summary>
    internal interface ILanes<TF, TU> where TF : struct where TU : struct
    {
        static abstract TF Splat(float x);
        static abstract TU SplatU(uint x);
        static abstract TF Load(float[] a, int i);
        static abstract TU LoadU(uint[] a, int i);
        static abstract void Store(TF v, float[] a, int i);
        static abstract void StoreU(TU v, uint[] a, int i);
        static abstract TF Add(TF a, TF b);
        static abstract TF Sub(TF a, TF b);
        static abstract TF Mul(TF a, TF b);
        static abstract TF Div(TF a, TF b);
        /// <summary>Lanes where a &gt; b (all ones), else zero.</summary>
        static abstract TF Greater(TF a, TF b);
        /// <summary>Per lane: mask ? a : b (bitwise).</summary>
        static abstract TF Select(TF mask, TF a, TF b);
        static abstract TU AddU(TU a, TU b);
        static abstract TU ShiftRight(TU a, int n);
        static abstract TU ShiftLeft(TU a, int n);
        /// <summary>Unsigned integers below 2^24 to float (exact).</summary>
        static abstract TF ToFloat(TU a);
        /// <summary>
        /// table[index + 0 … 3] per lane: four consecutive points of each lane's read, as four
        /// vectors (one 16-byte load per lane, then a transpose: data movement only, so exact).
        /// </summary>
        static abstract void Read4(ref float table, TU index, out TF p0, out TF p1, out TF p2, out TF p3);
        /// <summary>The lanes' sum in one fixed order: ((0+4) + (1+5)) + ((2+6) + (3+7)).</summary>
        static abstract float Sum(TF v);
        static abstract float Lane(TF v, int lane);
    }

    /// <summary>One Vector256 (x64 with AVX2).</summary>
    internal struct Avx2Lanes : ILanes<Vector256<float>, Vector256<uint>>
    {
        public static bool Supported => Avx2.IsSupported;

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Splat(float x) => Vector256.Create(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> SplatU(uint x) => Vector256.Create(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Load(float[] a, int i) => Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> LoadU(uint[] a, int i) => Vector256.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Store(Vector256<float> v, float[] a, int i) => v.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void StoreU(Vector256<uint> v, uint[] a, int i) => v.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(a), (nuint)i);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Add(Vector256<float> a, Vector256<float> b) => a + b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Sub(Vector256<float> a, Vector256<float> b) => a - b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Mul(Vector256<float> a, Vector256<float> b) => a * b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Div(Vector256<float> a, Vector256<float> b) => a / b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Greater(Vector256<float> a, Vector256<float> b) => Vector256.GreaterThan(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> Select(Vector256<float> m, Vector256<float> a, Vector256<float> b) => Vector256.ConditionalSelect(m, a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> AddU(Vector256<uint> a, Vector256<uint> b) => a + b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> ShiftRight(Vector256<uint> a, int n) => Vector256.ShiftRightLogical(a, n);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> ShiftLeft(Vector256<uint> a, int n) => Vector256.ShiftLeft(a, n);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> ToFloat(Vector256<uint> a) => Vector256.ConvertToSingle(a.AsInt32());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read4(ref float table, Vector256<uint> index, out Vector256<float> p0, out Vector256<float> p1, out Vector256<float> p2, out Vector256<float> p3)
        {
            // Row j: lane j's four points (low half) and lane j + 4's (high half); then a 4×4
            // transpose within each half.
            var lo = index.GetLower();
            var hi = index.GetUpper();
            var r0 = Vector256.Create(Vector128.LoadUnsafe(ref table, lo.GetElement(0)), Vector128.LoadUnsafe(ref table, hi.GetElement(0)));
            var r1 = Vector256.Create(Vector128.LoadUnsafe(ref table, lo.GetElement(1)), Vector128.LoadUnsafe(ref table, hi.GetElement(1)));
            var r2 = Vector256.Create(Vector128.LoadUnsafe(ref table, lo.GetElement(2)), Vector128.LoadUnsafe(ref table, hi.GetElement(2)));
            var r3 = Vector256.Create(Vector128.LoadUnsafe(ref table, lo.GetElement(3)), Vector128.LoadUnsafe(ref table, hi.GetElement(3)));
            var t0 = Avx.UnpackLow(r0, r1);
            var t1 = Avx.UnpackHigh(r0, r1);
            var t2 = Avx.UnpackLow(r2, r3);
            var t3 = Avx.UnpackHigh(r2, r3);
            p0 = Avx.Shuffle(t0, t2, 0x44);
            p1 = Avx.Shuffle(t0, t2, 0xEE);
            p2 = Avx.Shuffle(t1, t3, 0x44);
            p3 = Avx.Shuffle(t1, t3, 0xEE);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sum(Vector256<float> v)
        {
            var f = v.GetLower() + v.GetUpper();
            return (f.GetElement(0) + f.GetElement(1)) + (f.GetElement(2) + f.GetElement(3));
        }

        public static float Lane(Vector256<float> v, int lane) => v.GetElement(lane);
    }

    /// <summary>Two Vector128 halves, lanes 0–3 and 4–7.</summary>
    internal struct Pair<T> where T : struct
    {
        public Vector128<T> Lo, Hi;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Pair(Vector128<T> lo, Vector128<T> hi) { Lo = lo; Hi = hi; }
    }

    /// <summary>Two Vector128 halves (arm64; x64 without AVX2).</summary>
    internal struct PairLanes : ILanes<Pair<float>, Pair<uint>>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> Splat(float x) { var v = Vector128.Create(x); return new Pair<float>(v, v); }
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> SplatU(uint x) { var v = Vector128.Create(x); return new Pair<uint>(v, v); }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Pair<float> Load(float[] a, int i)
        {
            ref float r = ref MemoryMarshal.GetArrayDataReference(a);
            return new Pair<float>(Vector128.LoadUnsafe(ref r, (nuint)i), Vector128.LoadUnsafe(ref r, (nuint)(i + 4)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Pair<uint> LoadU(uint[] a, int i)
        {
            ref uint r = ref MemoryMarshal.GetArrayDataReference(a);
            return new Pair<uint>(Vector128.LoadUnsafe(ref r, (nuint)i), Vector128.LoadUnsafe(ref r, (nuint)(i + 4)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Store(Pair<float> v, float[] a, int i)
        {
            ref float r = ref MemoryMarshal.GetArrayDataReference(a);
            v.Lo.StoreUnsafe(ref r, (nuint)i);
            v.Hi.StoreUnsafe(ref r, (nuint)(i + 4));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void StoreU(Pair<uint> v, uint[] a, int i)
        {
            ref uint r = ref MemoryMarshal.GetArrayDataReference(a);
            v.Lo.StoreUnsafe(ref r, (nuint)i);
            v.Hi.StoreUnsafe(ref r, (nuint)(i + 4));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> Add(Pair<float> a, Pair<float> b) => new(a.Lo + b.Lo, a.Hi + b.Hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> Sub(Pair<float> a, Pair<float> b) => new(a.Lo - b.Lo, a.Hi - b.Hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> Mul(Pair<float> a, Pair<float> b) => new(a.Lo * b.Lo, a.Hi * b.Hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> Div(Pair<float> a, Pair<float> b) => new(a.Lo / b.Lo, a.Hi / b.Hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> Greater(Pair<float> a, Pair<float> b) => new(Vector128.GreaterThan(a.Lo, b.Lo), Vector128.GreaterThan(a.Hi, b.Hi));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> Select(Pair<float> m, Pair<float> a, Pair<float> b) => new(Vector128.ConditionalSelect(m.Lo, a.Lo, b.Lo), Vector128.ConditionalSelect(m.Hi, a.Hi, b.Hi));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> AddU(Pair<uint> a, Pair<uint> b) => new(a.Lo + b.Lo, a.Hi + b.Hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> ShiftRight(Pair<uint> a, int n) => new(Vector128.ShiftRightLogical(a.Lo, n), Vector128.ShiftRightLogical(a.Hi, n));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> ShiftLeft(Pair<uint> a, int n) => new(Vector128.ShiftLeft(a.Lo, n), Vector128.ShiftLeft(a.Hi, n));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> ToFloat(Pair<uint> a) => new(Vector128.ConvertToSingle(a.Lo.AsInt32()), Vector128.ConvertToSingle(a.Hi.AsInt32()));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read4(ref float table, Pair<uint> index, out Pair<float> p0, out Pair<float> p1, out Pair<float> p2, out Pair<float> p3)
        {
            Transpose(ref table, index.Lo, out var a0, out var a1, out var a2, out var a3);
            Transpose(ref table, index.Hi, out var b0, out var b1, out var b2, out var b3);
            p0 = new Pair<float>(a0, b0);
            p1 = new Pair<float>(a1, b1);
            p2 = new Pair<float>(a2, b2);
            p3 = new Pair<float>(a3, b3);
        }

        /// <summary>Four lanes' four points: one 16-byte load per lane, then a 4×4 transpose.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Transpose(ref float table, Vector128<uint> index, out Vector128<float> p0, out Vector128<float> p1, out Vector128<float> p2, out Vector128<float> p3)
        {
            var r0 = Vector128.LoadUnsafe(ref table, index.GetElement(0));
            var r1 = Vector128.LoadUnsafe(ref table, index.GetElement(1));
            var r2 = Vector128.LoadUnsafe(ref table, index.GetElement(2));
            var r3 = Vector128.LoadUnsafe(ref table, index.GetElement(3));
            if (AdvSimd.Arm64.IsSupported)
            {
                var t0 = AdvSimd.Arm64.TransposeEven(r0, r1);   // a0 b0 a2 b2
                var t1 = AdvSimd.Arm64.TransposeOdd(r0, r1);    // a1 b1 a3 b3
                var t2 = AdvSimd.Arm64.TransposeEven(r2, r3);   // c0 d0 c2 d2
                var t3 = AdvSimd.Arm64.TransposeOdd(r2, r3);    // c1 d1 c3 d3
                p0 = AdvSimd.Arm64.TransposeEven(t0.AsUInt64(), t2.AsUInt64()).AsSingle();
                p2 = AdvSimd.Arm64.TransposeOdd(t0.AsUInt64(), t2.AsUInt64()).AsSingle();
                p1 = AdvSimd.Arm64.TransposeEven(t1.AsUInt64(), t3.AsUInt64()).AsSingle();
                p3 = AdvSimd.Arm64.TransposeOdd(t1.AsUInt64(), t3.AsUInt64()).AsSingle();
            }
            else if (Sse.IsSupported)
            {
                var t0 = Sse.UnpackLow(r0, r1);    // a0 b0 a1 b1
                var t1 = Sse.UnpackHigh(r0, r1);   // a2 b2 a3 b3
                var t2 = Sse.UnpackLow(r2, r3);    // c0 d0 c1 d1
                var t3 = Sse.UnpackHigh(r2, r3);   // c2 d2 c3 d3
                p0 = Sse.MoveLowToHigh(t0, t2);
                p1 = Sse.MoveHighToLow(t2, t0);
                p2 = Sse.MoveLowToHigh(t1, t3);
                p3 = Sse.MoveHighToLow(t3, t1);
            }
            else
            {
                p0 = Vector128.Create(r0.GetElement(0), r1.GetElement(0), r2.GetElement(0), r3.GetElement(0));
                p1 = Vector128.Create(r0.GetElement(1), r1.GetElement(1), r2.GetElement(1), r3.GetElement(1));
                p2 = Vector128.Create(r0.GetElement(2), r1.GetElement(2), r2.GetElement(2), r3.GetElement(2));
                p3 = Vector128.Create(r0.GetElement(3), r1.GetElement(3), r2.GetElement(3), r3.GetElement(3));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sum(Pair<float> v)
        {
            var f = v.Lo + v.Hi;
            return (f.GetElement(0) + f.GetElement(1)) + (f.GetElement(2) + f.GetElement(3));
        }

        public static float Lane(Pair<float> v, int lane) => lane < 4 ? v.Lo.GetElement(lane) : v.Hi.GetElement(lane - 4);
    }

    [InlineArray(8)]
    internal struct Eight<T> where T : struct
    {
        T e0;
    }

    /// <summary>Eight plain floats: the reference (no SIMD), for the tests.</summary>
    internal struct ScalarLanes : ILanes<Eight<float>, Eight<uint>>
    {
        public static Eight<float> Splat(float x) { var r = new Eight<float>(); for (int i = 0; i < 8; i++) r[i] = x; return r; }
        public static Eight<uint> SplatU(uint x) { var r = new Eight<uint>(); for (int i = 0; i < 8; i++) r[i] = x; return r; }
        public static Eight<float> Load(float[] a, int i) { var r = new Eight<float>(); for (int l = 0; l < 8; l++) r[l] = a[i + l]; return r; }
        public static Eight<uint> LoadU(uint[] a, int i) { var r = new Eight<uint>(); for (int l = 0; l < 8; l++) r[l] = a[i + l]; return r; }
        public static void Store(Eight<float> v, float[] a, int i) { for (int l = 0; l < 8; l++) a[i + l] = v[l]; }
        public static void StoreU(Eight<uint> v, uint[] a, int i) { for (int l = 0; l < 8; l++) a[i + l] = v[l]; }
        public static Eight<float> Add(Eight<float> a, Eight<float> b) { for (int l = 0; l < 8; l++) a[l] = a[l] + b[l]; return a; }
        public static Eight<float> Sub(Eight<float> a, Eight<float> b) { for (int l = 0; l < 8; l++) a[l] = a[l] - b[l]; return a; }
        public static Eight<float> Mul(Eight<float> a, Eight<float> b) { for (int l = 0; l < 8; l++) a[l] = a[l] * b[l]; return a; }
        public static Eight<float> Div(Eight<float> a, Eight<float> b) { for (int l = 0; l < 8; l++) a[l] = a[l] / b[l]; return a; }

        public static Eight<float> Greater(Eight<float> a, Eight<float> b)
        {
            var r = new Eight<float>();
            for (int l = 0; l < 8; l++) r[l] = BitConverter.UInt32BitsToSingle(a[l] > b[l] ? 0xFFFFFFFFu : 0u);
            return r;
        }

        public static Eight<float> Select(Eight<float> m, Eight<float> a, Eight<float> b)
        {
            var r = new Eight<float>();
            for (int l = 0; l < 8; l++)
            {
                uint mm = BitConverter.SingleToUInt32Bits(m[l]);
                r[l] = BitConverter.UInt32BitsToSingle((BitConverter.SingleToUInt32Bits(a[l]) & mm) | (BitConverter.SingleToUInt32Bits(b[l]) & ~mm));
            }
            return r;
        }

        public static Eight<uint> AddU(Eight<uint> a, Eight<uint> b) { for (int l = 0; l < 8; l++) a[l] = unchecked(a[l] + b[l]); return a; }
        public static Eight<uint> ShiftRight(Eight<uint> a, int n) { for (int l = 0; l < 8; l++) a[l] = a[l] >> n; return a; }
        public static Eight<uint> ShiftLeft(Eight<uint> a, int n) { for (int l = 0; l < 8; l++) a[l] = a[l] << n; return a; }
        public static Eight<float> ToFloat(Eight<uint> a) { var r = new Eight<float>(); for (int l = 0; l < 8; l++) r[l] = (int)a[l]; return r; }

        public static void Read4(ref float table, Eight<uint> index, out Eight<float> p0, out Eight<float> p1, out Eight<float> p2, out Eight<float> p3)
        {
            p0 = new Eight<float>(); p1 = new Eight<float>(); p2 = new Eight<float>(); p3 = new Eight<float>();
            for (int l = 0; l < 8; l++)
            {
                p0[l] = Unsafe.Add(ref table, index[l]);
                p1[l] = Unsafe.Add(ref table, index[l] + 1);
                p2[l] = Unsafe.Add(ref table, index[l] + 2);
                p3[l] = Unsafe.Add(ref table, index[l] + 3);
            }
        }

        public static float Sum(Eight<float> v) => ((v[0] + v[4]) + (v[1] + v[5])) + ((v[2] + v[6]) + (v[3] + v[7]));
        public static float Lane(Eight<float> v, int lane) => v[lane];
    }

    /// <summary>Which implementation runs: the fastest the CPU has, unless a test asks for another.</summary>
    internal enum LaneKind { Auto, Avx2, Pair, Scalar }
}
