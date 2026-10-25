// Compat.cs - .NET Framework 4.8 shims for the Buzz 1503 port.
//
// net48 has no MathF, no Math.Clamp and no BitConverter.SingleToInt32Bits /
// Int32BitsToSingle. Adding NuGet packages for them would put extra DLLs next
// to the machine, so these small shims stand in for them instead and the DSP
// code stays the same as the ReBuzz version. (Buzz1503 notes 1.3.)
//
// MathF here lives in the machine's namespace, so unqualified MathF.* calls
// in this assembly resolve to it.

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace PedalMComp
{
    internal static class MathF
    {
        public const float PI = (float)Math.PI;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Pow(float x, float y) => (float)Math.Pow(x, y);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Exp(float x) => (float)Math.Exp(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Log10(float x) => (float)Math.Log10(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sin(float x) => (float)Math.Sin(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Cos(float x) => (float)Math.Cos(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Sqrt(float x) => (float)Math.Sqrt(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Floor(float x) => (float)Math.Floor(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Round(float x) => (float)Math.Round(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Abs(float x) => Math.Abs(x);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Max(float a, float b) => Math.Max(a, b);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Min(float a, float b) => Math.Min(a, b);
    }

    internal static class MathEx
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    // Float <-> int bit reinterpretation without allocation or unsafe code.
    [StructLayout(LayoutKind.Explicit)]
    internal struct FloatBits
    {
        [FieldOffset(0)] public float F;
        [FieldOffset(0)] public int I;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int ToInt(float f) { FloatBits b = default; b.F = f; return b.I; }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static float ToFloat(int i) { FloatBits b = default; b.I = i; return b.F; }
    }
}
