#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): MIDI clock in and out, pure.
// No dependency on the rest of ReBuzz, so ReBuzz.SessionTests links this file.
//
// MIDI clock: 24 pulses (0xF8) a beat; Start (0xFA) means the next pulse is beat 0, Continue
// (0xFB) goes on from the Song Position Pointer (0xF2, in 16ths: 6 pulses), Stop (0xFC).

using System;

namespace ReBuzz.Core.Sync
{
    internal enum MidiClockEvent { None, Pulse, Start, Continue, Stop, Position }

    /// <summary>
    /// MIDI clock in: the tempo and beat line the pulses make (a least-squares line through the
    /// last two beats of pulses, so their jitter averages out), and the transport messages. One
    /// thread (the MIDI input's) calls <see cref="OnMessage"/>; others read <see cref="Timeline"/>.
    /// </summary>
    internal sealed class MidiClockReceiver
    {
        public const int PulsesPerBeat = 24;
        /// <summary>A pause this long between pulses starts the fit over (a new tempo, a restart).</summary>
        public const double GapMicros = 500_000;

        readonly HostTimeFilter fit = new HostTimeFilter(2 * PulsesPerBeat, 0);
        long pulse = -1;                 // the last pulse's place (beat = pulse / 24)
        long count;                      // pulses since the start: the fit's axis, which never jumps
        double lastMicros = double.NaN;
        volatile bool running;

        /// <summary>Beat (pulse / 24, from Start or the Song Position Pointer) against host time, once a few pulses came.</summary>
        public TimelineCell Timeline { get; } = new TimelineCell();

        /// <summary>Between Start (or Continue) and Stop.</summary>
        public bool Running => running;

        /// <summary>The beat the next pulse will be.</summary>
        public double NextBeat => (pulse + 1) / (double)PulsesPerBeat;

        /// <summary>
        /// A message from the clock's input, with when it came (host µs); <paramref name="message"/>
        /// is a short MIDI message (status in the low byte, then the data bytes).
        /// </summary>
        public MidiClockEvent OnMessage(int message, double micros)
        {
            switch (message & 0xFF)
            {
                case 0xF8:
                    Pulse(micros);
                    return MidiClockEvent.Pulse;
                case 0xFA:
                    pulse = -1;
                    running = true;
                    Project();
                    return MidiClockEvent.Start;
                case 0xFB:
                    running = true;
                    return MidiClockEvent.Continue;
                case 0xFC:
                    running = false;
                    return MidiClockEvent.Stop;
                case 0xF2:
                    int sixteenths = ((message >> 8) & 0x7F) | (((message >> 16) & 0x7F) << 7);
                    pulse = sixteenths * (PulsesPerBeat / 4) - 1;
                    Project();
                    return MidiClockEvent.Position;
                default:
                    return MidiClockEvent.None;
            }
        }

        void Pulse(double micros)
        {
            if (!double.IsNaN(lastMicros) && (micros - lastMicros > GapMicros || micros < lastMicros)) fit.Reset();
            lastMicros = micros;
            pulse++;
            count++;
            // The fit runs on the pulse count, so a Start or a position (which move the beat) doesn't bend it.
            fit.Add(count, micros);
            if (TempoOfFit(out double tempo))
                Timeline.Write(new Timeline(fit.YAt(count), pulse / (double)PulsesPerBeat, tempo));
        }

        bool TempoOfFit(out double tempo)
        {
            tempo = 0;
            if (fit.Count < 6 || fit.Slope <= 0) return false;
            tempo = 60e6 / (PulsesPerBeat * fit.Slope);
            return tempo >= 10 && tempo <= 1000;
        }

        /// <summary>After Start or a position: where the next pulse lands, at the tempo the pulses had, is its beat.</summary>
        void Project()
        {
            if (TempoOfFit(out double tempo))
                Timeline.Write(new Timeline(fit.YAt(count + 1), (pulse + 1) / (double)PulsesPerBeat, tempo));
        }
    }

    /// <summary>
    /// MIDI clock out: what to send next and when, on our beat line (the session's beats since
    /// the transport started, as heard). The sending thread asks (<see cref="Peek"/>), waits
    /// until the time, sends, and says so (<see cref="Sent"/>); it asks again on each wake, so a
    /// change of tempo or phase is followed at once.
    /// </summary>
    internal sealed class MidiClockSender
    {
        public const int Clock = 0xF8, Start = 0xFA, Continue = 0xFB, Stop = 0xFC, SongPosition = 0xF2;
        public const int PulsesPerBeat = MidiClockReceiver.PulsesPerBeat;
        /// <summary>Start goes this long before the first pulse.</summary>
        public const double StartLead = 1000;
        /// <summary>How long to sleep with nothing to send.</summary>
        public const double IdleMicros = 5000;

        enum Phase { Stopped, Positioned, Running }

        Phase phase;
        long next;

        public bool IsRunning => phase == Phase.Running;

        /// <summary>
        /// The next message (a short MIDI message; 0 for none) and when to send it (host µs; no
        /// later than <paramref name="now"/> means at once).
        /// </summary>
        public int Peek(bool playing, in Timeline ours, double now, out double at)
        {
            if (!playing || !ours.IsValid)
            {
                at = phase == Phase.Stopped ? now + IdleMicros : now;
                return phase == Phase.Stopped ? 0 : Stop;
            }
            switch (phase)
            {
                case Phase.Stopped:
                    long first = FirstPulse(ours, now);
                    if (first == 0)
                    {
                        at = Math.Max(now, ours.MicrosAt(0) - StartLead);
                        return Start;
                    }
                    // Joined while playing: the position (to the next 16th), then Continue there.
                    at = now;
                    long sixteenth = (first + 5) / 6;
                    return SongPosition | (int)((sixteenth & 0x7F) << 8) | (int)(((sixteenth >> 7) & 0x7F) << 16);
                case Phase.Positioned:
                    at = Math.Max(now, ours.MicrosAt(next / (double)PulsesPerBeat) - StartLead);
                    return Continue;
                default:
                    at = ours.MicrosAt(next / (double)PulsesPerBeat);
                    return Clock;
            }
        }

        /// <summary>The message <see cref="Peek"/> gave has gone.</summary>
        public void Sent(int message, in Timeline ours, double now)
        {
            switch (message & 0xFF)
            {
                case Stop:
                    phase = Phase.Stopped;
                    break;
                case Start:
                    phase = Phase.Running;
                    next = 0;                       // late or not, the first pulse after Start is beat 0
                    break;
                case SongPosition:
                    phase = Phase.Positioned;
                    long sixteenth = ((message >> 8) & 0x7F) | (((message >> 16) & 0x7F) << 7);
                    next = sixteenth * 6;
                    break;
                case Continue:
                    phase = Phase.Running;
                    break;
                case Clock:
                    next++;
                    break;
            }
        }

        static long FirstPulse(in Timeline ours, double now) =>
            Math.Max(0, (long)Math.Ceiling(ours.BeatAt(now) * PulsesPerBeat - 1e-6));
    }
}
