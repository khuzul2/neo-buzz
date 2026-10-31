#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): the audio thread's side of tempo
// and phase sync, pure (ReBuzz.SessionTests links this file). WorkManager asks it how long
// each tick is and whether the transport may start now; it publishes the session's own beat
// line for MIDI clock out and the Session View.

using System;
using System.Threading;

namespace ReBuzz.Core.Sync
{
    /// <summary>A clock the session can follow (Ableton Link, MIDI clock in).</summary>
    internal interface ISyncSource
    {
        /// <summary>The clock's beat line now, if there is one to follow (Link with peers, MIDI clock running).</summary>
        bool TryGetTimeline(out Timeline timeline);

        /// <summary>The beats phases match over: the session's bar (Link's quantum; MIDI clock's bars count from its Start).</summary>
        double QuantumFor(int beatsPerBar);

        /// <summary>
        /// How late (in beats) a start may land and catch up instead of waiting a bar: 0 for Link
        /// (Live waits for the bar line); a little for MIDI clock, whose beat 0 is one pulse after
        /// its Start, nearly always sooner than a start can be heard.
        /// </summary>
        double CatchUpBeats { get; }

        /// <summary>
        /// The earliest a start may be heard, from <paramref name="heardMicros"/> (the earliest it
        /// can be): at once for Link, not before beat 0 for MIDI clock. The start then waits for
        /// the clock's phase to match the session's at its start position.
        /// </summary>
        double EarliestStart(in Timeline timeline, double heardMicros);
    }

    /// <summary>
    /// The audio thread's sync state (one thread; others read <see cref="Ours"/>, <see cref="ErrorMicros"/>).
    /// Ticks follow the source's phase (<see cref="PhaseLock"/>); without a source, the engine's own length.
    /// </summary>
    internal sealed class SyncClock
    {
        public const int FilterPoints = 512;

        HostTimeFilter filter;
        int sampleRate;
        long frames;
        double lastCallback = double.NaN;
        double errorMicros = double.NaN;
        double startedHeard = double.NaN;
        bool wasPlaying;
        volatile ISyncSource source;
        bool startDue = true;
        bool following;

        /// <summary>The session's beat line as heard: beats since the transport started, at the tick played. Cleared while stopped.</summary>
        public TimelineCell Ours { get; } = new TimelineCell();

        /// <summary>The clock followed (null: none). Any thread.</summary>
        public ISyncSource Source { get => source; set => source = value; }

        /// <summary>How far ahead of the clock followed the last tick started, in µs (behind if negative); NaN when not following.</summary>
        public double ErrorMicros => Volatile.Read(ref errorMicros);

        /// <summary>When the transport's last start was heard (host µs; NaN before one).</summary>
        public double StartedHeardMicros => Volatile.Read(ref startedHeard);

        /// <summary>Whether the last tick followed a clock.</summary>
        public bool IsFollowing => Volatile.Read(ref following);

        /// <summary>Whether the transport may start at the end of this tick (false while a start waits for the clock's bar line).</summary>
        public bool StartDue => startDue;

        /// <summary>Microseconds of host time a sample lasts (the audio clock as learnt).</summary>
        public double MicrosPerSample => filter?.Slope ?? 0;

        /// <summary>Audio thread, at a callback's start: the host time now, the sample rate.</summary>
        public void OnCallback(double hostMicros, int rate)
        {
            if (rate <= 0) return;
            if (filter == null || rate != sampleRate)
            {
                sampleRate = rate;
                filter = new HostTimeFilter(FilterPoints, 1e6 / rate);
            }
            // A gap (the device restarted, the process was paused): the old points say nothing.
            if (!double.IsNaN(lastCallback) && (hostMicros < lastCallback || hostMicros - lastCallback > 250_000))
                filter.Reset();
            lastCallback = hostMicros;
            filter.Add(frames, hostMicros);
        }

        /// <summary>Audio thread: frames rendered.</summary>
        public void OnFrames(int count) => frames += count;

        /// <summary>
        /// Audio thread, at a tick's start: its length in samples. <paramref name="nominal"/>
        /// unless a clock is followed: then so the next tick starts, as heard, at the clock's
        /// phase; and while a start waits, so a tick ends on the clock's start time.
        /// </summary>
        /// <param name="clock">The session clock of the tick starting (its index; -1 while stopped).</param>
        /// <param name="gridOrigin">The clock value bars count from.</param>
        /// <param name="startClock">Where the session clock starts when the transport does (the song position).</param>
        /// <param name="latencyMicros">From rendering a sample to hearing it.</param>
        public int TickLength(int nominal, bool playing, bool startPending, long clock, long gridOrigin, long startClock, int ticksPerBeat, int beatsPerBar, double latencyMicros)
        {
            ticksPerBeat = Math.Max(1, ticksPerBeat);
            var src = source;
            double quantum = Math.Max(1e-6, src?.QuantumFor(Math.Max(1, beatsPerBar)) ?? Math.Max(1, beatsPerBar));
            if (filter == null || filter.Count == 0)
            {
                startDue = true;
                Volatile.Write(ref following, false);
                return nominal;
            }
            double heard = filter.YAt(frames) + latencyMicros;
            double slope = filter.Slope;
            Timeline external = default;
            bool follow = src != null && src.TryGetTimeline(out external) && external.IsValid;
            Volatile.Write(ref following, follow);

            if (playing && clock >= 0)
            {
                if (!wasPlaying) Volatile.Write(ref startedHeard, heard);
                wasPlaying = true;
                int length = nominal;
                double barBeat = (clock - gridOrigin) / (double)ticksPerBeat;
                if (follow)
                {
                    length = PhaseLock.TickLength(external, quantum, barBeat + 1.0 / ticksPerBeat, heard, slope, ticksPerBeat);
                    Volatile.Write(ref errorMicros, PhaseLock.Error(external, quantum, barBeat, heard) * 60e6 / external.Tempo);
                }
                else
                    Volatile.Write(ref errorMicros, double.NaN);
                Ours.Write(new Timeline(heard, clock / (double)ticksPerBeat, 60e6 / (Math.Max(1, length) * slope * ticksPerBeat)));
                startDue = true;
                return length;
            }

            wasPlaying = false;
            Ours.Clear();
            Volatile.Write(ref errorMicros, double.NaN);
            if (!follow)
            {
                startDue = true;
                return nominal;
            }
            // Following: a start waits until the clock's phase is the session's at its start
            // position (bars count from 0 when the transport starts), and a tick ends right then.
            double earliest = src.EarliestStart(external, heard);
            double from = Math.Max(heard + slope, earliest);      // a sample ahead at least
            double fromBeat = external.BeatAt(from);
            double startPhase = SyncMath.Mod(Math.Max(0, startClock) / (double)ticksPerBeat, quantum);
            double catchUp = src.CatchUpBeats;
            if (startPending && catchUp > 0)
            {
                // Starting at this tick's end lands a little late: start, and the lock catches up.
                double end = heard + nominal * slope;
                if (end >= earliest)
                {
                    double late = SyncMath.Wrap(external.BeatAt(end) - startPhase, quantum);
                    if (late >= 0 && late <= catchUp)
                    {
                        startDue = true;
                        return nominal;
                    }
                }
            }
            double target = external.MicrosAt(fromBeat + SyncMath.Mod(startPhase - fromBeat, quantum));
            double remaining = (target - heard) / slope;
            if (remaining < 0.5)
            {
                startDue = true;
                return nominal;
            }
            if (!startPending)
            {
                // Not asked yet: no start at this tick's end (asked during it, the next tick aims).
                startDue = false;
                return nominal;
            }
            if (remaining <= nominal * 1.5)
            {
                startDue = true;
                return Math.Max(1, (int)Math.Round(remaining));
            }
            startDue = false;
            return nominal;
        }
    }
}
