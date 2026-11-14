#nullable disable
// Session recording (0.7, fork addition): the pure parts. See docs/specs/session-view.md, 0.7.
//
// No dependency on the rest of ReBuzz, so ReBuzz.SessionTests links this file: what the
// listener hears when, how stamped MIDI becomes a clip's events, and the metronome click.

using System;
using System.Collections.Generic;

namespace ReBuzz.Core.Session
{
    /// <summary>Pattern positions are in 1/240 of a tick (BuzzGUI's PatternEvent.TimeBase).</summary>
    internal static class TakeTime
    {
        public const int TimeBase = 240;

        /// <summary>
        /// Session position the listener hears now, in ticks: where the render is
        /// (<paramref name="renderedTicks"/>, at the end of the last audio callback) minus the part
        /// of the output latency that hasn't played out since. Never later than the render; -1
        /// when nothing has been rendered.
        /// </summary>
        public static double Heard(double renderedTicks, double samplesPerTick, double latencySamples, double samplesSinceRender)
        {
            if (renderedTicks < 0) return -1;
            double behind = latencySamples - Math.Max(0, samplesSinceRender);
            if (behind <= 0 || samplesPerTick <= 0) return renderedTicks;
            return Math.Max(0, renderedTicks - behind / samplesPerTick);
        }

        /// <summary>A position in ticks as pattern time (1/240 tick), rounded to the nearest unit.</summary>
        public static long ToTime(double ticks) => (long)Math.Round(ticks * TimeBase);
    }

    /// <summary>
    /// One MIDI message as heard on a recording track (0.7): the message, when it was heard
    /// (session clock, 1/240 tick), and where the track's clip was then (1/240 tick into its
    /// pattern) with the end of the clip's region (exclusive).
    /// </summary>
    internal readonly struct TakeStamp
    {
        public TakeStamp(int data, long time, int position, int end)
        {
            Data = data;
            Time = time;
            Position = position;
            End = end;
        }

        public int Data { get; }
        public long Time { get; }
        public int Position { get; }
        public int End { get; }
    }

    /// <summary>A pattern event of a clip's MIDI column: time and duration in 1/240 tick, the MIDI message as value.</summary>
    internal readonly record struct TakeEvent(int Time, int Value, int Duration);

    /// <summary>
    /// Turns one track's stamped MIDI into events of the clip it records into (0.7). A note is
    /// one event: its note-on message at the position it was heard, lasting until its release
    /// (the engine sends the note-off), cut at the end of the clip's region. Controllers, pitch
    /// bend and aftertouch are events of no length. Notes are written when released, or when
    /// recording stops (<see cref="CloseAll"/>).
    /// </summary>
    internal sealed class TakeWriter
    {
        readonly Dictionary<int, TakeStamp> held = new Dictionary<int, TakeStamp>();

        /// <summary>Notes pressed and not yet released.</summary>
        public int Holding => held.Count;

        /// <summary>Takes one message; appends the events it completes to <paramref name="output"/>.</summary>
        public void Add(TakeStamp stamp, List<TakeEvent> output)
        {
            int status = stamp.Data & 0xFF;
            int kind = status & 0xF0;
            int data1 = (stamp.Data >> 8) & 0x7F;
            int data2 = (stamp.Data >> 16) & 0x7F;
            int key = ((status & 0x0F) << 7) | data1;

            if (kind == 0x90 && data2 > 0)
            {
                if (held.TryGetValue(key, out var earlier))
                    output.Add(Note(earlier, stamp.Time));        // pressed again: the earlier one ends here
                held[key] = stamp;
            }
            else if (kind == 0x80 || kind == 0x90)
            {
                if (held.Remove(key, out var on))
                    output.Add(Note(on, stamp.Time));
            }
            else if (kind == 0xB0 || kind == 0xE0 || kind == 0xD0 || kind == 0xA0)
            {
                output.Add(new TakeEvent(stamp.Position, stamp.Data & 0xFFFFFF, 0));
            }
        }

        /// <summary>Whether this message is the release of a note this writer holds (daw-modes.md AR5: a note held across a loop's wrap is released in the next pass, but belongs to the pass it began in).</summary>
        public bool Holds(int data)
        {
            int status = data & 0xFF;
            int kind = status & 0xF0;
            if (kind != 0x80 && !(kind == 0x90 && ((data >> 16) & 0x7F) == 0)) return false;
            return held.ContainsKey(((status & 0x0F) << 7) | ((data >> 8) & 0x7F));
        }

        /// <summary>Ends every held note at <paramref name="time"/> (session clock, 1/240 tick).</summary>
        public void CloseAll(long time, List<TakeEvent> output)
        {
            foreach (var on in held.Values)
                output.Add(Note(on, time));
            held.Clear();
        }

        /// <summary>Forgets held notes without writing them (the clip went away).</summary>
        public void Drop() => held.Clear();

        static TakeEvent Note(TakeStamp on, long offTime)
        {
            long length = offTime - on.Time;
            long room = on.End - on.Position;
            long duration = Math.Min(length, room);
            if (duration < 1) duration = 1;
            return new TakeEvent(on.Position, on.Data & 0xFFFFFF, (int)Math.Min(duration, int.MaxValue));
        }
    }

    /// <summary>
    /// The metronome (0.7): a short decaying sine, mixed into the output (not a machine, and
    /// not recorded). Allocation-free; owned by the audio thread.
    /// </summary>
    internal sealed class MetronomeClick
    {
        public const double Length = 0.03;          // seconds
        public const double Decay = 0.006;          // seconds (time constant)
        public const float Level = 0.22f * 32768f;  // Buzz scale
        public const float AccentLevel = 0.32f * 32768f;

        int remaining;
        int n;
        double omega;
        double amplitude;
        double decayPerSample;

        /// <summary>Samples of the current click still to play (0: silent).</summary>
        public int Remaining => remaining;

        /// <summary>Starts a click: higher and louder on the downbeat.</summary>
        public void Trigger(bool accent, int sampleRate)
        {
            if (sampleRate <= 0) return;
            remaining = (int)(Length * sampleRate);
            n = 0;
            omega = 2 * Math.PI * (accent ? 1600.0 : 1000.0) / sampleRate;
            amplitude = accent ? AccentLevel : Level;
            decayPerSample = Math.Exp(-1.0 / (Decay * sampleRate));
        }

        public void Stop() => remaining = 0;

        /// <summary>Adds the click to an interleaved stereo buffer (<paramref name="frames"/> frames from <paramref name="offset"/>).</summary>
        public void MixInto(float[] buffer, int offset, int frames)
        {
            if (remaining <= 0) return;
            int count = Math.Min(frames, remaining);
            double env = amplitude * Math.Pow(decayPerSample, n);
            for (int i = 0; i < count; i++)
            {
                float s = (float)(env * Math.Sin(omega * n));
                buffer[offset + 2 * i] += s;
                buffer[offset + 2 * i + 1] += s;
                env *= decayPerSample;
                n++;
            }
            remaining -= count;
        }
    }
}
