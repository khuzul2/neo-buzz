#nullable disable
// Shared by Neo Voice to MIDI and Neo Audio to MIDI: the small parts of a note-making engine that do not depend on what is listened to.
//  - VelocityMap: a level (dB) to a velocity, Fixed or Dynamic between the softest and loudest.
//  - PitchBendMap: the distance of the pitch from its note as 14-bit bend messages (Free, or Anchored: still until the pitch leaves the anchor), rate limited.
//  - NoteOffQueue: notes with a length (drum hits, trigger notes): the note off follows by itself; the same note again ends the old one first.
//  - OnsetTrainer: a pass that files every onset's features as examples of one class (the audio thread offers, the machine's pump takes).
//  - HitQueue: onsets waiting for the slice of audio their features need.
// Allocation-free after construction.

using System;
using System.Threading;

namespace NeoBuzz.Dsp
{
    internal static class VelocityMap
    {
        /// <summary>A pitch note's velocity from its peak level (linear RMS): the fixed velocity, or between <paramref name="velMin"/> and <paramref name="fixedVelocity"/>.</summary>
        public static int FromPeak(float peak, bool fixedVel, int fixedVelocity, int velMin, float softDb, float loudDb)
        {
            if (fixedVel) return Math.Clamp(fixedVelocity, 1, 127);
            double db = peak > 1e-6f ? 20.0 * PortableMath.Log10(peak) : -120;
            double t = Math.Clamp((db - softDb) / (loudDb - softDb), 0.0, 1.0);
            return Math.Clamp((int)Math.Round(velMin + t * (fixedVelocity - velMin)), 1, 127);
        }

        /// <summary>A hit's velocity from its level in dB: between <paramref name="velMin"/> and 127.</summary>
        public static int FromHit(double levelDb, int velMin, float softDb, float loudDb)
        {
            double t = Math.Clamp((levelDb - softDb) / (loudDb - softDb), 0.0, 1.0);
            return Math.Clamp((int)Math.Round(velMin + t * (127 - velMin)), 1, 127);
        }
    }

    internal sealed class PitchBendMap
    {
        public int Range;                  // semitones, 0 = off
        public bool Anchored;
        public float AnchorSemis;
        int last = 8192;
        long lastAt;
        bool outOfAnchor;

        public bool On => Range > 0;
        public int Last => last;
        public void ResetAnchor() { outOfAnchor = false; }

        /// <summary>The bend message to send now (true and its value), if the distance <paramref name="dev"/> (semitones from the note) asks for a new one.</summary>
        public bool Next(double dev, long t, int rate, out int value)
        {
            value = 8192;
            if (Range <= 0) return false;
            double d = dev;
            if (Anchored)
            {
                double a = AnchorSemis;
                double ad = Math.Abs(d);
                if (!outOfAnchor) { if (ad > a) outOfAnchor = true; }
                else if (ad < a * 0.5) outOfAnchor = false;
                d = outOfAnchor ? Math.Sign(d) * Math.Max(0, ad - a * 0.5) : 0;
            }
            int v = (int)Math.Round(8192 + Math.Clamp(d / Range, -1.0, 1.0) * 8191);
            if (Math.Abs(v - last) < 24 || t - lastAt < (long)(0.004 * rate)) return false;
            last = v; lastAt = t; value = Math.Clamp(v, 0, 16383);
            return true;
        }

        /// <summary>Back to the centre when it is not there (a note starts or ends): true and 8192 if a message is due.</summary>
        public bool Centre(long t, out int value)
        {
            value = 8192;
            if (Range <= 0 || last == 8192) return false;
            last = 8192; lastAt = t;
            return true;
        }
    }

    internal sealed class NoteOffQueue
    {
        readonly int[] note, channel, lag;
        readonly long[] due;
        int count;
        public NoteOffQueue(int capacity = 32) { note = new int[capacity]; channel = new int[capacity]; lag = new int[capacity]; due = new long[capacity]; }
        public int Count => count;

        /// <summary>A note on now and its off <paramref name="lengthSamples"/> later; the same note still sounding is ended first.</summary>
        public void Play(MidiBlock b, MidiStream stream, int ch, int n, int velocity, long at, int lengthSamples) => Play(b, stream, ch, n, velocity, at, lengthSamples, at);

        /// <summary>As above for a hit whose sound began at <paramref name="attack"/> (at or before <paramref name="at"/>): the note on and the off that ends the old one carry the lag, and so does this note's own off, so a recording keeps the note's length.</summary>
        public void Play(MidiBlock b, MidiStream stream, int ch, int n, int velocity, long at, int lengthSamples, long attack)
        {
            for (int i = 0; i < count; i++)
                if (note[i] == n && channel[i] == ch) { b.Emit(MidiKind.NoteOff, stream, ch, n, 0, at, attack); due[i] = long.MaxValue; }
            b.Emit(MidiKind.NoteOn, stream, ch, n, velocity, at, attack);
            if (count < note.Length) { note[count] = n; channel[count] = ch; lag[count] = (int)Math.Clamp(at - attack, 0, 1 << 20); due[count] = at + Math.Max(lengthSamples, 1); count++; }
        }

        /// <summary>The note offs that fall due by <paramref name="now"/>.</summary>
        public void Service(MidiBlock b, MidiStream stream, long now)
        {
            int keep = 0;
            for (int i = 0; i < count; i++)
            {
                if (due[i] == long.MaxValue) continue;
                if (due[i] <= now) { b.Emit(MidiKind.NoteOff, stream, channel[i], note[i], 0, due[i], due[i] - lag[i]); continue; } // sent a block late: the lag grows by the delay, so the recording keeps the note's length
                note[keep] = note[i]; channel[keep] = channel[i]; lag[keep] = lag[i]; due[keep] = due[i]; keep++;
            }
            count = keep;
        }

        /// <summary>Ends every note still sounding at <paramref name="at"/>.</summary>
        public void Flush(MidiBlock b, MidiStream stream, long at)
        {
            for (int i = 0; i < count; i++) if (due[i] != long.MaxValue) b.Emit(MidiKind.NoteOff, stream, channel[i], note[i], 0, at);
            count = 0;
        }

        public void Clear() { count = 0; }
    }

    internal sealed class OnsetTrainer
    {
        public const int Slots = 64;
        readonly float[][] feats = new float[Slots][];
        readonly int[] cls = new int[Slots];
        readonly int dim;
        int write, read, learned;
        /// <summary>The class being learned, -1 when no pass runs.</summary>
        public volatile int Learning = -1;
        /// <summary>The pass stops by itself after this many examples.</summary>
        public int Target = 20;
        public volatile bool Done;
        public int Learned => Volatile.Read(ref learned);

        public OnsetTrainer(int dimension)
        {
            dim = dimension;
            for (int i = 0; i < Slots; i++) feats[i] = new float[dim];
        }

        public void Start(int sound) { Volatile.Write(ref learned, 0); Done = false; Learning = sound; }
        public void Stop() { Learning = -1; }

        /// <summary>The audio thread: files one onset's features as an example of the class being learned. True when this one ended the pass.</summary>
        public bool Offer(int sound, float[] features)
        {
            int w = write;
            Array.Copy(features, feats[w % Slots], dim);
            cls[w % Slots] = sound;
            Volatile.Write(ref write, w + 1);
            int c = Interlocked.Increment(ref learned);
            if (c >= Target) { Learning = -1; Done = true; return true; }
            return false;
        }

        /// <summary>The consumer (the machine's pump): the next example, or false.</summary>
        public bool Take(out int sound, float[] features)
        {
            sound = -1;
            int w = Volatile.Read(ref write);
            if (read >= w) return false;
            if (w - read > Slots) read = w - Slots;
            int slot = read % Slots;
            sound = cls[slot];
            Array.Copy(feats[slot], features, dim);
            read++;
            return true;
        }
    }

    /// <summary>Onsets waiting for the audio their features need (a slice from <c>lead</c> before the onset for <c>length</c> samples).</summary>
    internal sealed class HitQueue
    {
        readonly OnsetEvent[] queue;
        int count;
        public HitQueue(int capacity = 16) { queue = new OnsetEvent[capacity]; }
        public int Count => count;
        public void Clear() { count = 0; }
        public bool Add(in OnsetEvent o) { if (count >= queue.Length) return false; queue[count++] = o; return true; }

        /// <summary>The oldest onset whose slice is complete by <paramref name="now"/> (removed), and the stream position at which it was complete.</summary>
        public bool TakeReady(long now, int lead, int length, out OnsetEvent o, out long readyAt)
        {
            for (int i = 0; i < count; i++)
            {
                long ready = queue[i].Sample - lead + length;
                if (ready > now) continue;
                o = queue[i]; readyAt = ready;
                for (int k = i + 1; k < count; k++) queue[k - 1] = queue[k];
                count--;
                return true;
            }
            o = default; readyAt = 0;
            return false;
        }
    }
}
