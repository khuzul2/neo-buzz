#nullable disable
// Neo machines shared DSP (docs/specs/neo-patch.md §3, phase NM): the targets of a canvas modulator. Up to 8 slots,
// each a parameter of some machine (behind IModPort) with a depth, a polarity, an optional range and a curve. Once per
// update the bank turns the modulator's output (0 … 1) into a value for each target: base + depth × output × span,
// kept inside the target's range and its safe range, slewed so no write jumps further than the glide allows, and
// written once. A parameter someone else changes (a hand, the agent, automation) moves the base: the modulation rides
// on top of the new value. Removing a target (or the modulator) puts the base back. No allocation, and nothing but
// IEEE basic operations and PortableMath, so the null test holds on every platform.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    /// <summary>One parameter, as the bank sees it (the host's IParameter behind it; fakes in the tests).</summary>
    internal interface IModPort
    {
        int Min { get; }
        int Max { get; }
        /// <summary>The range modulation may use (the manifest's safe range); the parameter's own when there is none.</summary>
        int SafeMin { get; }
        int SafeMax { get; }
        int Get();
        void Put(int raw);
    }

    internal sealed class ModSlot
    {
        public const int None = int.MinValue;

        // The setup (saved with the song).
        public bool Used;
        public string Machine = "";
        public string Param = "";
        public int ParamIndex = -1;
        public bool IsTrack;
        public int Track;
        public int Depth = 100;
        public bool Bipolar;
        public int RangeMin = None, RangeMax = None;
        public int Curve;
        /// <summary>What the song said the base and the last write were (a reload resumes from them).</summary>
        public int SavedBase = None, SavedApplied = None;

        // The run (under the bank's lock).
        public IModPort Port;
        public int State;          // BuzzGUI.Interfaces.ModulationState
        public bool Has;           // a value was written, so Applied and Base mean something
        public int Base, Applied;
        public double Y;
        public object Target;      // the machine, by identity (the host's IMachine)

        public bool HasRange => RangeMin != None && RangeMax != None;
    }

    internal sealed class ModBank
    {
        public const int Max = 8;
        public readonly ModSlot[] Slots = new ModSlot[Max];
        public readonly object Gate = new object();

        /// <summary>Seconds a write may take to cross the whole span (the glide).</summary>
        public double GlideSeconds = 0.03;

        public ModBank()
        {
            for (int i = 0; i < Max; i++) Slots[i] = new ModSlot();
        }

        /// <summary>The curve's effect on a 0 … 1 value: 0 straight, + eases in (slow start), − eases out.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double Bend(double u, int curve)
        {
            if (curve == 0 || u <= 0 || u >= 1) return u;
            return PortableMath.Pow(u, PortableMath.Pow(2.0, curve / 50.0));
        }

        /// <summary>The bounds a target may take given its base: its range, narrowed to the safe range, widened to the base.</summary>
        public static void Bounds(ModSlot s, IModPort p, int baseValue, out int lo, out int hi)
        {
            lo = p.Min;
            hi = p.Max;
            if (s.HasRange)
            {
                lo = Math.Max(lo, Math.Min(s.RangeMin, s.RangeMax));
                hi = Math.Min(hi, Math.Max(s.RangeMin, s.RangeMax));
                if (hi < lo) { lo = p.Min; hi = p.Max; }
            }
            // Never past the safe range: but a base the person put outside it is theirs, not moved.
            lo = Math.Max(lo, Math.Min(p.SafeMin, baseValue));
            hi = Math.Min(hi, Math.Max(p.SafeMax, baseValue));
            if (lo > baseValue) lo = baseValue;
            if (hi < baseValue) hi = baseValue;
        }

        /// <summary>The span the depth is a percentage of: the range's width.</summary>
        public static int Span(ModSlot s, IModPort p)
        {
            int lo = p.Min, hi = p.Max;
            if (s.HasRange)
            {
                lo = Math.Max(lo, Math.Min(s.RangeMin, s.RangeMax));
                hi = Math.Min(hi, Math.Max(s.RangeMin, s.RangeMax));
                if (hi < lo) { lo = p.Min; hi = p.Max; }
            }
            return Math.Max(1, hi - lo);
        }

        /// <summary>The value the modulation asks for at output <paramref name="u"/> (0 … 1), before the glide.</summary>
        public static double Desired(ModSlot s, IModPort p, int baseValue, double u)
        {
            double m = Bend(u, s.Curve);
            if (s.Bipolar) m = 2 * m - 1;
            double v = baseValue + s.Depth / 100.0 * m * Span(s, p);
            Bounds(s, p, baseValue, out int lo, out int hi);
            return v < lo ? lo : v > hi ? hi : v;
        }

        /// <summary>
        /// One update: <paramref name="u"/> is the modulator's output (0 … 1), <paramref name="dt"/> the seconds since the last
        /// update. Writes each active target at most once; returns how many writes it made.
        /// </summary>
        public int Update(double u, double dt)
        {
            int writes = 0;
            lock (Gate)
            {
                for (int i = 0; i < Max; i++)
                {
                    var s = Slots[i];
                    var p = s.Port;
                    if (!s.Used || p == null) continue;
                    int cur = p.Get();
                    if (!s.Has || cur != s.Applied)
                    {
                        // First look, or someone else wrote it: that is the base now (what the person set).
                        if (!s.Has && s.SavedBase != ModSlot.None && cur == s.SavedApplied)
                            s.Base = Math.Clamp(s.SavedBase, p.Min, p.Max);   // a reload: resume from the saved base
                        else
                            s.Base = cur;
                        s.SavedBase = s.SavedApplied = ModSlot.None;
                        s.Y = cur;
                        s.Has = true;
                        s.Applied = cur;
                    }
                    double want = Desired(s, p, s.Base, u);
                    double span = Span(s, p);
                    double step = GlideSeconds <= 0 ? double.MaxValue : span * dt / GlideSeconds;
                    double d = want - s.Y;
                    s.Y += d > step ? step : d < -step ? -step : d;
                    int write = (int)Math.Round(s.Y);
                    if (write < p.Min) write = p.Min;
                    else if (write > p.Max) write = p.Max;
                    if (write == s.Applied) continue;
                    p.Put(write);
                    s.Applied = write;
                    writes++;
                }
            }
            return writes;
        }

        /// <summary>Puts a slot's parameter back at its base (when nobody changed it since) and clears the run.</summary>
        public void Release(int slot)
        {
            lock (Gate) ReleaseLocked(Slots[slot]);
        }

        public void ReleaseAll()
        {
            lock (Gate)
                for (int i = 0; i < Max; i++) ReleaseLocked(Slots[i]);
        }

        static void ReleaseLocked(ModSlot s)
        {
            var p = s.Port;
            if (p != null && s.Has)
            {
                try
                {
                    // Someone else's value (a hand edit since the last write) stays: it is the base.
                    if (p.Get() == s.Applied && s.Applied != s.Base) p.Put(s.Base);
                }
                catch (Exception) { }
            }
            s.Has = false;
            s.Y = 0;
        }

        /// <summary>Where a slot's modulation can reach from its base: the live range the device view shows.</summary>
        public static void Reach(ModSlot s, IModPort p, int baseValue, out int low, out int high)
        {
            double a = Desired(s, p, baseValue, 0), b = Desired(s, p, baseValue, 1);
            low = (int)Math.Round(Math.Min(a, b));
            high = (int)Math.Round(Math.Max(a, b));
        }
    }
}
