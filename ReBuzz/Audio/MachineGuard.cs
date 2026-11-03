// Fork (docs/specs/machine-safety.md §3.2): a guard on every machine. After a machine's Work and
// before its output reaches any input, the block it produced is checked in one vectorised pass:
// NaN, infinity or a sample above +60 dBFS (1000 × full scale) trips the guard. The block becomes
// silence and the machine is marked diverged: the engine then skips its Work and sends silence
// (MachineWorkInstance), it reads as muted (MachineCore.IsMuted), and the UI thread stops it
// (Buzz's Stop) and tells the views. Unmuting it (Reset and unmute) clears it. A block that
// passes is never touched, so renders stay bit-identical; the check allocates nothing.

using Buzz.MachineInterface;
using ReBuzz.Core;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReBuzz.Audio
{
    /// <summary>Why a machine's guard tripped.</summary>
    internal enum DivergeKind
    {
        None,
        NaN,
        Infinity,
        Level,
    }

    internal static class MachineGuard
    {
        /// <summary>+60 dBFS in ReBuzz's sample scale (32768 is full scale).</summary>
        internal const float TripLevel = 32768f * 1000f;

        static volatile bool enabled = true;
        static long trips;

        /// <summary>
        /// On by default. Off only for the song's session (the setting asks first; it is never
        /// saved): a new or opened song turns it on again (ReBuzzCore.NewSong).
        /// </summary>
        internal static bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        /// <summary>How many times a guard tripped since ReBuzz started.</summary>
        internal static long Trips => Interlocked.Read(ref trips);

        /// <summary>
        /// Checks a machine's block (its first <paramref name="n"/> samples); on a trip silences it
        /// and marks the machine diverged. True when the block passed.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Check(MachineCore machine, Sample[] samples, int n)
        {
            if (!enabled || samples == null || Passes(samples, n)) return true;
            Trip(machine, samples, n);
            return false;
        }

        /// <summary>The same for a multi-I/O machine's outputs (null channels are unused).</summary>
        internal static bool Check(MachineCore machine, List<Sample[]> outputs, int n)
        {
            if (!enabled) return true;
            for (int c = 0; c < outputs.Count; c++)
            {
                var block = outputs[c];
                if (block != null && !Passes(block, n))
                {
                    var (kind, peak) = Diagnose(block, n);
                    for (int k = 0; k < outputs.Count; k++)
                        if (outputs[k] != null) Array.Clear(outputs[k], 0, n);
                    machine.MarkDiverged(kind, peak);
                    Interlocked.Increment(ref trips);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// True when every sample is finite and within ±<see cref="TripLevel"/>: one pass, a
        /// compare per vector (NaN compares false, so it fails the test too).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Passes(Sample[] samples, int n)
        {
            var f = MemoryMarshal.Cast<Sample, float>(samples.AsSpan(0, Math.Min(n, samples.Length)));
            var limit = new Vector<float>(TripLevel);
            int vw = Vector<float>.Count;
            int i = 0;
            for (; i <= f.Length - vw; i += vw)
            {
                if (!Vector.LessThanOrEqualAll(Vector.Abs(new Vector<float>(f.Slice(i, vw))), limit))
                    return false;
            }
            for (; i < f.Length; i++)
            {
                if (!(MathF.Abs(f[i]) <= TripLevel))
                    return false;
            }
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Trip(MachineCore machine, Sample[] samples, int n)
        {
            var (kind, peak) = Diagnose(samples, n);
            Array.Clear(samples, 0, Math.Min(n, samples.Length));
            machine.MarkDiverged(kind, peak);
            Interlocked.Increment(ref trips);
        }

        /// <summary>What tripped: NaN first, then infinity, else the level (the block's peak).</summary>
        static (DivergeKind Kind, float Peak) Diagnose(Sample[] samples, int n)
        {
            var f = MemoryMarshal.Cast<Sample, float>(samples.AsSpan(0, Math.Min(n, samples.Length)));
            bool inf = false;
            float peak = 0;
            foreach (float x in f)
            {
                if (float.IsNaN(x)) return (DivergeKind.NaN, 0);
                if (float.IsInfinity(x)) inf = true;
                else peak = MathF.Max(peak, MathF.Abs(x));
            }
            return inf ? (DivergeKind.Infinity, 0) : (DivergeKind.Level, peak);
        }

        /// <summary>The reason in words: "NaN", "infinity" or "+87.3 dBFS".</summary>
        internal static string Reason(DivergeKind kind, float peak) => kind switch
        {
            DivergeKind.NaN => "NaN",
            DivergeKind.Infinity => "infinity",
            DivergeKind.Level => $"{(peak > 0 ? 20 * Math.Log10(peak / 32768.0) : double.NegativeInfinity):+0.0;-0.0} dBFS",
            _ => "",
        };
    }
}
