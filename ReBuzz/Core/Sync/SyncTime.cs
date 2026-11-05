#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): tempo and phase sync with another
// clock (Ableton Link, MIDI clock), pure. No dependency on the rest of ReBuzz, so
// ReBuzz.SessionTests links this file.
//
// Times are host microseconds: Stopwatch (QueryPerformanceCounter) ticks scaled to µs, the
// clock Link's Windows platform uses, so a time from the Link helper process means the same
// moment here. Beats are quarter notes; a "quantum" is the beats of a bar, the span phases
// align over (Link's term).

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace ReBuzz.Core.Sync
{
    /// <summary>A tempo line: the beat at a host time, and the tempo (BPM) from there on.</summary>
    internal readonly record struct Timeline(double Micros, double Beat, double Tempo)
    {
        public bool IsValid => Tempo > 0 && double.IsFinite(Tempo) && double.IsFinite(Beat) && double.IsFinite(Micros);

        public double BeatAt(double micros) => Beat + (micros - Micros) * Tempo / 60e6;

        public double MicrosAt(double beat) => Micros + (beat - Beat) * 60e6 / Tempo;

        /// <summary>Where in its bar of <paramref name="quantum"/> beats the line is at a time, 0 to the quantum.</summary>
        public double PhaseAt(double micros, double quantum) => SyncMath.Mod(BeatAt(micros), quantum);

        /// <summary>The first time at or after <paramref name="micros"/> the line starts a bar of <paramref name="quantum"/> beats.</summary>
        public double NextBarAt(double micros, double quantum)
        {
            double beat = BeatAt(micros);
            double bar = Math.Ceiling(beat / quantum - 1e-9) * quantum;
            return MicrosAt(bar);
        }
    }

    internal static class SyncMath
    {
        public static double Mod(double x, double m) => m <= 0 ? 0 : x - Math.Floor(x / m) * m;

        /// <summary><paramref name="x"/> wrapped into [-m/2, m/2).</summary>
        public static double Wrap(double x, double m) => m <= 0 ? x : Mod(x + m / 2, m) - m / 2;
    }

    /// <summary>Host time in microseconds (Stopwatch, which is QueryPerformanceCounter on Windows, as Link's clock).</summary>
    internal static class HostClock
    {
        static readonly double MicrosPerTick = 1e6 / Stopwatch.Frequency;

        public static double Micros(long stopwatchTicks) => stopwatchTicks * MicrosPerTick;

        /// <summary>Now; tests replace it.</summary>
        public static Func<double> Now = () => Micros(Stopwatch.GetTimestamp());
    }

    /// <summary>
    /// The audio clock against host time (Link's HostTimeFilter): a least-squares line through
    /// the last points (sample index at a callback's start, host time then), so jitter in when
    /// callbacks run averages out and the audio device's own rate is learnt. No allocation
    /// after construction; one thread at a time.
    /// </summary>
    internal sealed class HostTimeFilter
    {
        readonly double[] xs;
        readonly double[] ys;
        readonly double nominalSlope;
        int count, next;
        double baseX, baseY;
        double meanX, meanY, slope;

        /// <param name="points">How many points the line is fitted through.</param>
        /// <param name="nominalSlope">Host µs per unit (per sample: 1e6 / sample rate): used until there are points enough, and as the bounds a fitted slope must keep (±1%); 0 for none.</param>
        public HostTimeFilter(int points, double nominalSlope)
        {
            xs = new double[Math.Max(2, points)];
            ys = new double[xs.Length];
            this.nominalSlope = nominalSlope;
            slope = nominalSlope;
        }

        public int Count => count;

        public double Slope => slope;

        public void Reset()
        {
            count = next = 0;
            slope = nominalSlope;
        }

        // Fully optimised at its first compile (the warm-up's), so the audio thread never compiles an on-stack replacement of its loops (AudioWarmUp).
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Add(double x, double y)
        {
            if (count == 0) { baseX = x; baseY = y; }
            xs[next] = x - baseX;
            ys[next] = y - baseY;
            next = (next + 1) % xs.Length;
            if (count < xs.Length) count++;

            double sx = 0, sy = 0;
            for (int i = 0; i < count; i++) { sx += xs[i]; sy += ys[i]; }
            meanX = sx / count;
            meanY = sy / count;
            if (count < 8 && nominalSlope > 0) { slope = nominalSlope; return; }
            double sxy = 0, sxx = 0;
            for (int i = 0; i < count; i++)
            {
                double dx = xs[i] - meanX;
                sxy += dx * (ys[i] - meanY);
                sxx += dx * dx;
            }
            double fitted = sxx > 0 ? sxy / sxx : nominalSlope;
            if (nominalSlope > 0 && (double.IsNaN(fitted) || Math.Abs(fitted / nominalSlope - 1) > 0.01)) fitted = nominalSlope;
            slope = fitted;
        }

        /// <summary>The host time of <paramref name="x"/> on the line (NaN without points).</summary>
        public double YAt(double x) => count == 0 ? double.NaN : baseY + meanY + slope * (x - baseX - meanX);

        /// <summary>The <paramref name="y"/>'s place on the line (NaN without points).</summary>
        public double XAt(double y) => count == 0 || slope == 0 ? double.NaN : baseX + meanX + (y - baseY - meanY) / slope;
    }

    /// <summary>
    /// The phase lock: how long a tick is so the tick after it starts, as heard, where the clock
    /// followed has the same place in its bar. Each tick aims at an absolute time, so rounding
    /// never adds up; a tick is at most <see cref="MaxDeviation"/> longer or shorter than the
    /// clock's own, so a large error closes smoothly.
    /// </summary>
    internal static class PhaseLock
    {
        public const double MaxDeviation = 0.08;

        /// <summary>Microseconds a tick lasts at the line's tempo.</summary>
        public static double TickMicros(double tempo, int ticksPerBeat) => 60e6 / (tempo * Math.Max(1, ticksPerBeat));

        /// <param name="external">The clock followed.</param>
        /// <param name="quantum">Beats in a bar: phases match modulo this.</param>
        /// <param name="nextBeat">Our beat at the end of the tick (counted from a bar line).</param>
        /// <param name="heardMicros">When the tick's first sample is heard.</param>
        /// <param name="microsPerSample">The audio clock (<see cref="HostTimeFilter.Slope"/>).</param>
        public static int TickLength(in Timeline external, double quantum, double nextBeat, double heardMicros, double microsPerSample, int ticksPerBeat, double maxDeviation = MaxDeviation)
        {
            double nominal = TickMicros(external.Tempo, ticksPerBeat);
            double expected = heardMicros + nominal;
            double beatThere = external.BeatAt(expected);
            double target = external.MicrosAt(beatThere + SyncMath.Wrap(nextBeat - beatThere, quantum));
            double micros = Math.Clamp(target - heardMicros, nominal * (1 - maxDeviation), nominal * (1 + maxDeviation));
            return Math.Max(1, (int)Math.Round(micros / microsPerSample));
        }

        /// <summary>Beats we are ahead of the clock followed (behind if negative), within half a bar.</summary>
        public static double Error(in Timeline external, double quantum, double beat, double heardMicros) =>
            SyncMath.Wrap(beat - external.BeatAt(heardMicros), quantum);
    }

    /// <summary>
    /// A <see cref="Timeline"/> one thread writes and others read without locks or allocation
    /// (a sequence lock): a read never sees half a write.
    /// </summary>
    internal sealed class TimelineCell
    {
        int version;
        double micros, beat, tempo;

        public void Write(in Timeline t)
        {
            Interlocked.Increment(ref version);              // odd: writing
            Volatile.Write(ref micros, t.Micros);
            Volatile.Write(ref beat, t.Beat);
            Volatile.Write(ref tempo, t.Tempo);
            Interlocked.Increment(ref version);              // even: done
        }

        /// <summary>Back to never written.</summary>
        public void Clear()
        {
            Interlocked.Increment(ref version);
            Volatile.Write(ref tempo, 0);
            Interlocked.Increment(ref version);
        }

        public bool TryRead(out Timeline t)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                int v1 = Volatile.Read(ref version);
                if ((v1 & 1) != 0) { Thread.SpinWait(4); continue; }
                double m = Volatile.Read(ref micros), b = Volatile.Read(ref beat), tp = Volatile.Read(ref tempo);
                if (Volatile.Read(ref version) != v1) continue;
                t = new Timeline(m, b, tp);
                return t.IsValid;
            }
            t = default;
            return false;
        }
    }
}

namespace ReBuzz.Core.Sync
{
    /// <summary>What the Link helper process reports (LinkHelper/main.cpp), one line at a time.</summary>
    internal readonly record struct LinkState(Timeline Timeline, int Peers, bool Playing, bool Enabled);

    /// <summary>The Link helper's line protocol (LinkHelper/main.cpp), both ways.</summary>
    internal static class LinkProtocol
    {
        public const int Version = 1;
        static readonly System.Globalization.CultureInfo Invariant = System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>A "hello" line's protocol version, or -1.</summary>
        public static int ParseHello(string line)
        {
            if (line == null || !line.StartsWith("hello ", StringComparison.Ordinal)) return -1;
            return int.TryParse(line.AsSpan(6).Trim(), System.Globalization.NumberStyles.Integer, Invariant, out int v) ? v : -1;
        }

        /// <summary>A "state" line: micros beat bpm peers playing enabled.</summary>
        public static bool TryParseState(string line, out LinkState state)
        {
            state = default;
            if (line == null || !line.StartsWith("state ", StringComparison.Ordinal)) return false;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 7) return false;
            const System.Globalization.NumberStyles Float = System.Globalization.NumberStyles.Float;
            if (!long.TryParse(parts[1], System.Globalization.NumberStyles.Integer, Invariant, out long micros)) return false;
            if (!double.TryParse(parts[2], Float, Invariant, out double beat)) return false;
            if (!double.TryParse(parts[3], Float, Invariant, out double tempo)) return false;
            if (!int.TryParse(parts[4], System.Globalization.NumberStyles.Integer, Invariant, out int peers)) return false;
            var timeline = new Timeline(micros, beat, tempo);
            if (!timeline.IsValid) return false;
            state = new LinkState(timeline, Math.Max(0, peers), parts[5] == "1", parts[6] == "1");
            return true;
        }

        public static string Enable(bool on) => on ? "enable 1" : "enable 0";
        public static string StartStopSync(bool on) => on ? "startstop 1" : "startstop 0";
        public static string Quantum(double beats) => "quantum " + beats.ToString("R", Invariant);
        public static string Tempo(double bpm) => "tempo " + bpm.ToString("0.######", Invariant);
        public static string Beat(double beat, double micros) => "beat " + beat.ToString("R", Invariant) + " " + ((long)Math.Round(micros)).ToString(Invariant);
        public static string Play(bool on, double micros) => (on ? "play 1 " : "play 0 ") + ((long)Math.Round(micros)).ToString(Invariant);
        public const string Quit = "quit";
    }
}
