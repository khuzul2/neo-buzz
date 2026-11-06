#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.1): eight lanes of single-precision maths, written once
// and run three ways: one Vector256 (x64 with AVX2), two Vector128 halves (arm64, or x64 without
// AVX2) and eight plain floats (the reference the tests hold the other two to). Table reads load
// each lane's four consecutive points at once from that lane's own table and transpose them (faster
// than four gathers on x64, and arm64 has no gather). Every operation is the same IEEE basic
// operation on each lane in each implementation, nothing is fused, and every sum across lanes runs
// in one fixed order, so all three give the same bits on every CPU (docs/specs/machines.md §4). The
// kernels are generic over the implementation (a struct with static members), so the JIT compiles
// one specialised copy of each kernel per implementation, with every call inlined.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace NeoBuzz.DyeEssence
{
    /// <summary>Where each of eight lanes reads its table: a reference into that lane's array.</summary>
    internal ref struct LaneRefs
    {
        public ref float R0;
        public ref float R1;
        public ref float R2;
        public ref float R3;
        public ref float R4;
        public ref float R5;
        public ref float R6;
        public ref float R7;

        /// <summary>Lane <paramref name="lane"/> reads <paramref name="data"/> from <paramref name="offset"/>.</summary>
        public void Set(int lane, float[] data, int offset)
        {
            ref float r = ref data[offset];
            switch (lane)
            {
                case 0: R0 = ref r; break;
                case 1: R1 = ref r; break;
                case 2: R2 = ref r; break;
                case 3: R3 = ref r; break;
                case 4: R4 = ref r; break;
                case 5: R5 = ref r; break;
                case 6: R6 = ref r; break;
                default: R7 = ref r; break;
            }
        }

        public ref float At(int lane)
        {
            switch (lane)
            {
                case 0: return ref R0;
                case 1: return ref R1;
                case 2: return ref R2;
                case 3: return ref R3;
                case 4: return ref R4;
                case 5: return ref R5;
                case 6: return ref R6;
                default: return ref R7;
            }
        }
    }

    /// <summary>
    /// Eight lanes: <typeparamref name="TF"/> holds eight floats, <typeparamref name="TU"/> eight
    /// 32-bit integers (oscillator phases, indices). Comparisons give masks as <typeparamref name="TF"/>.
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
        static abstract TU Xor(TU a, TU b);
        static abstract TU ShiftRight(TU a, int n);
        static abstract TU ShiftLeft(TU a, int n);
        /// <summary>Each lane shifted right by its own count (0 … 31).</summary>
        static abstract TU ShiftRightVar(TU a, TU n);
        /// <summary>Each lane shifted left by its own count (0 … 31).</summary>
        static abstract TU ShiftLeftVar(TU a, TU n);
        /// <summary>Unsigned integers below 2^24 to float (exact).</summary>
        static abstract TF ToFloat(TU a);
        /// <summary>Signed 32-bit integers to float (rounded to nearest, the same everywhere).</summary>
        static abstract TF IntToFloat(TU a);
        /// <summary>Floats to signed 32-bit integers, toward zero (only for values well inside the range).</summary>
        static abstract TU Truncate(TF a);
        /// <summary>
        /// Lane l's table at index[l] + 0 … 3: four consecutive points of each lane's read, as four
        /// vectors (one 16-byte load per lane, then a transpose: data movement only, so exact).
        /// </summary>
        static abstract void Read4(in LaneRefs tables, TU index, out TF p0, out TF p1, out TF p2, out TF p3);
        /// <summary>The same with one table for every lane (a uniform group: fewer live references).</summary>
        static abstract void Read4(ref float table, TU index, out TF p0, out TF p1, out TF p2, out TF p3);
        /// <summary>a[at + index[l]] per lane (one load per lane: exact).</summary>
        static abstract TF Gather(float[] a, int at, TU index);
        /// <summary>The lanes' sum in one fixed order: ((0+4) + (1+5)) + ((2+6) + (3+7)).</summary>
        static abstract float Sum(TF v);
        /// <summary>The sums of eight vectors, each in <see cref="Sum"/>'s order: lane j holds v_j's.</summary>
        static abstract TF Sum8(TF v0, TF v1, TF v2, TF v3, TF v4, TF v5, TF v6, TF v7);
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> Xor(Vector256<uint> a, Vector256<uint> b) => a ^ b;
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> ShiftRight(Vector256<uint> a, int n) => Vector256.ShiftRightLogical(a, n);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> ShiftLeft(Vector256<uint> a, int n) => Vector256.ShiftLeft(a, n);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> ShiftRightVar(Vector256<uint> a, Vector256<uint> n) => Avx2.ShiftRightLogicalVariable(a, n);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> ShiftLeftVar(Vector256<uint> a, Vector256<uint> n) => Avx2.ShiftLeftLogicalVariable(a, n);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> ToFloat(Vector256<uint> a) => Vector256.ConvertToSingle(a.AsInt32());
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<float> IntToFloat(Vector256<uint> a) => Vector256.ConvertToSingle(a.AsInt32());
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector256<uint> Truncate(Vector256<float> a) => Vector256.ConvertToInt32Native(a).AsUInt32();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read4(in LaneRefs t, Vector256<uint> index, out Vector256<float> p0, out Vector256<float> p1, out Vector256<float> p2, out Vector256<float> p3)
        {
            // Row j: lane j's four points (low half) and lane j + 4's (high half); then a 4×4
            // transpose within each half.
            var lo = index.GetLower();
            var hi = index.GetUpper();
            var r0 = Vector256.Create(Vector128.LoadUnsafe(ref t.R0, lo.GetElement(0)), Vector128.LoadUnsafe(ref t.R4, hi.GetElement(0)));
            var r1 = Vector256.Create(Vector128.LoadUnsafe(ref t.R1, lo.GetElement(1)), Vector128.LoadUnsafe(ref t.R5, hi.GetElement(1)));
            var r2 = Vector256.Create(Vector128.LoadUnsafe(ref t.R2, lo.GetElement(2)), Vector128.LoadUnsafe(ref t.R6, hi.GetElement(2)));
            var r3 = Vector256.Create(Vector128.LoadUnsafe(ref t.R3, lo.GetElement(3)), Vector128.LoadUnsafe(ref t.R7, hi.GetElement(3)));
            Transpose(r0, r1, r2, r3, out p0, out p1, out p2, out p3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read4(ref float t, Vector256<uint> index, out Vector256<float> p0, out Vector256<float> p1, out Vector256<float> p2, out Vector256<float> p3)
        {
            var lo = index.GetLower();
            var hi = index.GetUpper();
            var r0 = Vector256.Create(Vector128.LoadUnsafe(ref t, lo.GetElement(0)), Vector128.LoadUnsafe(ref t, hi.GetElement(0)));
            var r1 = Vector256.Create(Vector128.LoadUnsafe(ref t, lo.GetElement(1)), Vector128.LoadUnsafe(ref t, hi.GetElement(1)));
            var r2 = Vector256.Create(Vector128.LoadUnsafe(ref t, lo.GetElement(2)), Vector128.LoadUnsafe(ref t, hi.GetElement(2)));
            var r3 = Vector256.Create(Vector128.LoadUnsafe(ref t, lo.GetElement(3)), Vector128.LoadUnsafe(ref t, hi.GetElement(3)));
            Transpose(r0, r1, r2, r3, out p0, out p1, out p2, out p3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Transpose(Vector256<float> r0, Vector256<float> r1, Vector256<float> r2, Vector256<float> r3,
            out Vector256<float> c0, out Vector256<float> c1, out Vector256<float> c2, out Vector256<float> c3)
        {
            var t0 = Avx.UnpackLow(r0, r1);
            var t1 = Avx.UnpackHigh(r0, r1);
            var t2 = Avx.UnpackLow(r2, r3);
            var t3 = Avx.UnpackHigh(r2, r3);
            c0 = Avx.Shuffle(t0, t2, 0x44);
            c1 = Avx.Shuffle(t0, t2, 0xEE);
            c2 = Avx.Shuffle(t1, t3, 0x44);
            c3 = Avx.Shuffle(t1, t3, 0xEE);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Gather(float[] a, int at, Vector256<uint> index)
        {
            ref float r = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), at);
            var lo = index.GetLower();
            var hi = index.GetUpper();
            return Vector256.Create(
                Unsafe.Add(ref r, lo.GetElement(0)), Unsafe.Add(ref r, lo.GetElement(1)), Unsafe.Add(ref r, lo.GetElement(2)), Unsafe.Add(ref r, lo.GetElement(3)),
                Unsafe.Add(ref r, hi.GetElement(0)), Unsafe.Add(ref r, hi.GetElement(1)), Unsafe.Add(ref r, hi.GetElement(2)), Unsafe.Add(ref r, hi.GetElement(3)));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sum(Vector256<float> v)
        {
            var f = v.GetLower() + v.GetUpper();
            return (f.GetElement(0) + f.GetElement(1)) + (f.GetElement(2) + f.GetElement(3));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector256<float> Sum8(Vector256<float> v0, Vector256<float> v1, Vector256<float> v2, Vector256<float> v3,
            Vector256<float> v4, Vector256<float> v5, Vector256<float> v6, Vector256<float> v7)
        {
            // f_j = lanes 0–3 + lanes 4–7 of v_j; rows (f_j, f_j+4), transposed: column k holds every
            // row's k-th; then (c0 + c1) + (c2 + c3) is each row's sum in Sum's order.
            var g0 = Vector256.Create(v0.GetLower() + v0.GetUpper(), v4.GetLower() + v4.GetUpper());
            var g1 = Vector256.Create(v1.GetLower() + v1.GetUpper(), v5.GetLower() + v5.GetUpper());
            var g2 = Vector256.Create(v2.GetLower() + v2.GetUpper(), v6.GetLower() + v6.GetUpper());
            var g3 = Vector256.Create(v3.GetLower() + v3.GetUpper(), v7.GetLower() + v7.GetUpper());
            Transpose(g0, g1, g2, g3, out var c0, out var c1, out var c2, out var c3);
            return (c0 + c1) + (c2 + c3);
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
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> Xor(Pair<uint> a, Pair<uint> b) => new(a.Lo ^ b.Lo, a.Hi ^ b.Hi);
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> ShiftRight(Pair<uint> a, int n) => new(Vector128.ShiftRightLogical(a.Lo, n), Vector128.ShiftRightLogical(a.Hi, n));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> ShiftLeft(Pair<uint> a, int n) => new(Vector128.ShiftLeft(a.Lo, n), Vector128.ShiftLeft(a.Hi, n));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> ShiftRightVar(Pair<uint> a, Pair<uint> n) => new(ShiftVar(a.Lo, n.Lo, right: true), ShiftVar(a.Hi, n.Hi, right: true));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> ShiftLeftVar(Pair<uint> a, Pair<uint> n) => new(ShiftVar(a.Lo, n.Lo, right: false), ShiftVar(a.Hi, n.Hi, right: false));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> ToFloat(Pair<uint> a) => new(Vector128.ConvertToSingle(a.Lo.AsInt32()), Vector128.ConvertToSingle(a.Hi.AsInt32()));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<float> IntToFloat(Pair<uint> a) => new(Vector128.ConvertToSingle(a.Lo.AsInt32()), Vector128.ConvertToSingle(a.Hi.AsInt32()));
        [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Pair<uint> Truncate(Pair<float> a) => new(Vector128.ConvertToInt32Native(a.Lo).AsUInt32(), Vector128.ConvertToInt32Native(a.Hi).AsUInt32());

        /// <summary>Each lane by its own count: AdvSimd's USHL (negative counts shift right), AVX2's, or lane by lane.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static Vector128<uint> ShiftVar(Vector128<uint> a, Vector128<uint> n, bool right)
        {
            if (AdvSimd.IsSupported)
                return AdvSimd.ShiftLogical(a, right ? Vector128<int>.Zero - n.AsInt32() : n.AsInt32());
            if (Avx2.IsSupported)
                return right ? Avx2.ShiftRightLogicalVariable(a, n) : Avx2.ShiftLeftLogicalVariable(a, n);
            return right
                ? Vector128.Create(a.GetElement(0) >> (int)n.GetElement(0), a.GetElement(1) >> (int)n.GetElement(1), a.GetElement(2) >> (int)n.GetElement(2), a.GetElement(3) >> (int)n.GetElement(3))
                : Vector128.Create(a.GetElement(0) << (int)n.GetElement(0), a.GetElement(1) << (int)n.GetElement(1), a.GetElement(2) << (int)n.GetElement(2), a.GetElement(3) << (int)n.GetElement(3));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read4(in LaneRefs t, Pair<uint> index, out Pair<float> p0, out Pair<float> p1, out Pair<float> p2, out Pair<float> p3)
        {
            var lo = index.Lo;
            var hi = index.Hi;
            Transpose(Vector128.LoadUnsafe(ref t.R0, lo.GetElement(0)), Vector128.LoadUnsafe(ref t.R1, lo.GetElement(1)),
                Vector128.LoadUnsafe(ref t.R2, lo.GetElement(2)), Vector128.LoadUnsafe(ref t.R3, lo.GetElement(3)),
                out var a0, out var a1, out var a2, out var a3);
            Transpose(Vector128.LoadUnsafe(ref t.R4, hi.GetElement(0)), Vector128.LoadUnsafe(ref t.R5, hi.GetElement(1)),
                Vector128.LoadUnsafe(ref t.R6, hi.GetElement(2)), Vector128.LoadUnsafe(ref t.R7, hi.GetElement(3)),
                out var b0, out var b1, out var b2, out var b3);
            p0 = new Pair<float>(a0, b0);
            p1 = new Pair<float>(a1, b1);
            p2 = new Pair<float>(a2, b2);
            p3 = new Pair<float>(a3, b3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Read4(ref float t, Pair<uint> index, out Pair<float> p0, out Pair<float> p1, out Pair<float> p2, out Pair<float> p3)
        {
            var lo = index.Lo;
            var hi = index.Hi;
            Transpose(Vector128.LoadUnsafe(ref t, lo.GetElement(0)), Vector128.LoadUnsafe(ref t, lo.GetElement(1)),
                Vector128.LoadUnsafe(ref t, lo.GetElement(2)), Vector128.LoadUnsafe(ref t, lo.GetElement(3)),
                out var a0, out var a1, out var a2, out var a3);
            Transpose(Vector128.LoadUnsafe(ref t, hi.GetElement(0)), Vector128.LoadUnsafe(ref t, hi.GetElement(1)),
                Vector128.LoadUnsafe(ref t, hi.GetElement(2)), Vector128.LoadUnsafe(ref t, hi.GetElement(3)),
                out var b0, out var b1, out var b2, out var b3);
            p0 = new Pair<float>(a0, b0);
            p1 = new Pair<float>(a1, b1);
            p2 = new Pair<float>(a2, b2);
            p3 = new Pair<float>(a3, b3);
        }

        /// <summary>Four rows of four to four columns (data movement only).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Transpose(Vector128<float> r0, Vector128<float> r1, Vector128<float> r2, Vector128<float> r3,
            out Vector128<float> c0, out Vector128<float> c1, out Vector128<float> c2, out Vector128<float> c3)
        {
            if (AdvSimd.Arm64.IsSupported)
            {
                var t0 = AdvSimd.Arm64.TransposeEven(r0, r1);   // a0 b0 a2 b2
                var t1 = AdvSimd.Arm64.TransposeOdd(r0, r1);    // a1 b1 a3 b3
                var t2 = AdvSimd.Arm64.TransposeEven(r2, r3);   // c0 d0 c2 d2
                var t3 = AdvSimd.Arm64.TransposeOdd(r2, r3);    // c1 d1 c3 d3
                c0 = AdvSimd.Arm64.TransposeEven(t0.AsUInt64(), t2.AsUInt64()).AsSingle();
                c2 = AdvSimd.Arm64.TransposeOdd(t0.AsUInt64(), t2.AsUInt64()).AsSingle();
                c1 = AdvSimd.Arm64.TransposeEven(t1.AsUInt64(), t3.AsUInt64()).AsSingle();
                c3 = AdvSimd.Arm64.TransposeOdd(t1.AsUInt64(), t3.AsUInt64()).AsSingle();
            }
            else if (Sse.IsSupported)
            {
                var t0 = Sse.UnpackLow(r0, r1);    // a0 b0 a1 b1
                var t1 = Sse.UnpackHigh(r0, r1);   // a2 b2 a3 b3
                var t2 = Sse.UnpackLow(r2, r3);    // c0 d0 c1 d1
                var t3 = Sse.UnpackHigh(r2, r3);   // c2 d2 c3 d3
                c0 = Sse.MoveLowToHigh(t0, t2);
                c1 = Sse.MoveHighToLow(t2, t0);
                c2 = Sse.MoveLowToHigh(t1, t3);
                c3 = Sse.MoveHighToLow(t3, t1);
            }
            else
            {
                c0 = Vector128.Create(r0.GetElement(0), r1.GetElement(0), r2.GetElement(0), r3.GetElement(0));
                c1 = Vector128.Create(r0.GetElement(1), r1.GetElement(1), r2.GetElement(1), r3.GetElement(1));
                c2 = Vector128.Create(r0.GetElement(2), r1.GetElement(2), r2.GetElement(2), r3.GetElement(2));
                c3 = Vector128.Create(r0.GetElement(3), r1.GetElement(3), r2.GetElement(3), r3.GetElement(3));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Pair<float> Gather(float[] a, int at, Pair<uint> index)
        {
            ref float r = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(a), at);
            var lo = index.Lo;
            var hi = index.Hi;
            return new Pair<float>(
                Vector128.Create(Unsafe.Add(ref r, lo.GetElement(0)), Unsafe.Add(ref r, lo.GetElement(1)), Unsafe.Add(ref r, lo.GetElement(2)), Unsafe.Add(ref r, lo.GetElement(3))),
                Vector128.Create(Unsafe.Add(ref r, hi.GetElement(0)), Unsafe.Add(ref r, hi.GetElement(1)), Unsafe.Add(ref r, hi.GetElement(2)), Unsafe.Add(ref r, hi.GetElement(3))));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sum(Pair<float> v)
        {
            var f = v.Lo + v.Hi;
            return (f.GetElement(0) + f.GetElement(1)) + (f.GetElement(2) + f.GetElement(3));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Pair<float> Sum8(Pair<float> v0, Pair<float> v1, Pair<float> v2, Pair<float> v3,
            Pair<float> v4, Pair<float> v5, Pair<float> v6, Pair<float> v7)
        {
            Transpose(v0.Lo + v0.Hi, v1.Lo + v1.Hi, v2.Lo + v2.Hi, v3.Lo + v3.Hi, out var a0, out var a1, out var a2, out var a3);
            Transpose(v4.Lo + v4.Hi, v5.Lo + v5.Hi, v6.Lo + v6.Hi, v7.Lo + v7.Hi, out var b0, out var b1, out var b2, out var b3);
            return new Pair<float>((a0 + a1) + (a2 + a3), (b0 + b1) + (b2 + b3));
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
        public static Eight<uint> Xor(Eight<uint> a, Eight<uint> b) { for (int l = 0; l < 8; l++) a[l] = a[l] ^ b[l]; return a; }
        public static Eight<uint> ShiftRight(Eight<uint> a, int n) { for (int l = 0; l < 8; l++) a[l] = a[l] >> n; return a; }
        public static Eight<uint> ShiftLeft(Eight<uint> a, int n) { for (int l = 0; l < 8; l++) a[l] = a[l] << n; return a; }
        public static Eight<uint> ShiftRightVar(Eight<uint> a, Eight<uint> n) { for (int l = 0; l < 8; l++) a[l] = a[l] >> (int)n[l]; return a; }
        public static Eight<uint> ShiftLeftVar(Eight<uint> a, Eight<uint> n) { for (int l = 0; l < 8; l++) a[l] = a[l] << (int)n[l]; return a; }
        public static Eight<float> ToFloat(Eight<uint> a) { var r = new Eight<float>(); for (int l = 0; l < 8; l++) r[l] = (int)a[l]; return r; }
        public static Eight<float> IntToFloat(Eight<uint> a) { var r = new Eight<float>(); for (int l = 0; l < 8; l++) r[l] = (int)a[l]; return r; }
        public static Eight<uint> Truncate(Eight<float> a) { var r = new Eight<uint>(); for (int l = 0; l < 8; l++) r[l] = unchecked((uint)(int)a[l]); return r; }

        public static void Read4(in LaneRefs t, Eight<uint> index, out Eight<float> p0, out Eight<float> p1, out Eight<float> p2, out Eight<float> p3)
        {
            p0 = new Eight<float>(); p1 = new Eight<float>(); p2 = new Eight<float>(); p3 = new Eight<float>();
            for (int l = 0; l < 8; l++)
            {
                ref float r = ref Unsafe.Add(ref t.At(l), index[l]);
                p0[l] = r;
                p1[l] = Unsafe.Add(ref r, 1);
                p2[l] = Unsafe.Add(ref r, 2);
                p3[l] = Unsafe.Add(ref r, 3);
            }
        }

        public static void Read4(ref float t, Eight<uint> index, out Eight<float> p0, out Eight<float> p1, out Eight<float> p2, out Eight<float> p3)
        {
            p0 = new Eight<float>(); p1 = new Eight<float>(); p2 = new Eight<float>(); p3 = new Eight<float>();
            for (int l = 0; l < 8; l++)
            {
                ref float r = ref Unsafe.Add(ref t, index[l]);
                p0[l] = r;
                p1[l] = Unsafe.Add(ref r, 1);
                p2[l] = Unsafe.Add(ref r, 2);
                p3[l] = Unsafe.Add(ref r, 3);
            }
        }

        public static Eight<float> Gather(float[] a, int at, Eight<uint> index)
        {
            var r = new Eight<float>();
            for (int l = 0; l < 8; l++) r[l] = a[at + (int)index[l]];
            return r;
        }

        public static float Sum(Eight<float> v) => ((v[0] + v[4]) + (v[1] + v[5])) + ((v[2] + v[6]) + (v[3] + v[7]));

        public static Eight<float> Sum8(Eight<float> v0, Eight<float> v1, Eight<float> v2, Eight<float> v3,
            Eight<float> v4, Eight<float> v5, Eight<float> v6, Eight<float> v7)
        {
            var r = new Eight<float>();
            r[0] = Sum(v0); r[1] = Sum(v1); r[2] = Sum(v2); r[3] = Sum(v3);
            r[4] = Sum(v4); r[5] = Sum(v5); r[6] = Sum(v6); r[7] = Sum(v7);
            return r;
        }

        public static float Lane(Eight<float> v, int lane) => v[lane];
    }

    /// <summary>Which implementation runs: the fastest the CPU has, unless a test asks for another.</summary>
    internal enum LaneKind { Auto, Avx2, Pair, Scalar }
}
