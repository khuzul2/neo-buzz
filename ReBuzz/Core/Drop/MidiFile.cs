// Fork (docs/specs/drag-drop.md, DD1): a small Standard MIDI File reader (our own code, no dependency). A dropped .mid file becomes the notes of a
// pattern: every track and channel merged, note-on to note-off in beats of the file's quarter note. The tempo and the meter are read but
// informative only: the song's own stay as they are.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReBuzz.Core.Drop
{
    /// <summary>A note of a MIDI file: where it starts and how long it lasts, in quarter notes.</summary>
    internal readonly record struct MidiNote(double Start, double Length, int Pitch, int Velocity);

    /// <summary>What a .mid file holds that a drop uses.</summary>
    internal sealed class MidiFile
    {
        public IReadOnlyList<MidiNote> Notes { get; init; } = Array.Empty<MidiNote>();

        /// <summary>The first tempo, in beats per minute, or null (informative only).</summary>
        public double? Bpm { get; init; }

        /// <summary>The first time signature (beats, beat unit), or null (informative only).</summary>
        public (int Beats, int Unit)? Meter { get; init; }

        /// <summary>The end of the last note, in quarter notes.</summary>
        public double LengthBeats => Notes.Count == 0 ? 0 : Notes.Max(n => n.Start + n.Length);

        /// <summary>The most notes a file is read for (a bound against a damaged or enormous file).</summary>
        public const int MaxNotes = 200_000;

        public static bool IsMidiFile(string path) =>
            path != null && (path.EndsWith(".mid", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".midi", StringComparison.OrdinalIgnoreCase));

        public static MidiFile Read(string path) => Parse(File.ReadAllBytes(path));

        /// <summary>Reads a format 0, 1 or 2 file. Throws InvalidDataException with the reason when it isn't one.</summary>
        public static MidiFile Parse(byte[] data)
        {
            int p = 0;
            if (data == null || data.Length < 14 || data[0] != 'M' || data[1] != 'T' || data[2] != 'h' || data[3] != 'd')
                throw new InvalidDataException("it isn't a MIDI file");
            int headerLength = Be32(data, 4);
            if (headerLength < 6 || 8 + headerLength > data.Length) throw new InvalidDataException("its header is damaged");
            int tracks = Be16(data, 10);
            int division = Be16(data, 12);
            if ((division & 0x8000) != 0) throw new InvalidDataException("it counts time in SMPTE frames, not beats");
            if (division == 0) throw new InvalidDataException("its resolution is zero");
            p = 8 + headerLength;

            var notes = new List<MidiNote>();
            double? bpm = null;
            (int, int)? meter = null;
            for (int t = 0; t < tracks && p + 8 <= data.Length; t++)
            {
                int length = Be32(data, p + 4);
                bool isTrack = data[p] == 'M' && data[p + 1] == 'T' && data[p + 2] == 'r' && data[p + 3] == 'k';
                int start = p + 8;
                int end = (int)Math.Min((long)data.Length, (long)start + Math.Max(0, length));
                p = end;
                if (!isTrack) continue;
                ReadTrack(data, start, end, division, notes, ref bpm, ref meter);
                if (notes.Count > MaxNotes) break;
            }
            notes.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Pitch.CompareTo(b.Pitch));
            return new MidiFile { Notes = notes, Bpm = bpm, Meter = meter };
        }

        static void ReadTrack(byte[] d, int p, int end, int division, List<MidiNote> notes, ref double? bpm, ref (int, int)? meter)
        {
            long tick = 0;
            int running = 0;
            // channel * 128 + pitch -> the on-notes that sound, oldest first (tick, velocity)
            var open = new Dictionary<int, Queue<(long Tick, int Velocity)>>();
            void Close(int channel, int pitch, long at)
            {
                if (!open.TryGetValue(channel * 128 + pitch, out var q) || q.Count == 0) return;
                var (from, velocity) = q.Dequeue();
                notes.Add(new MidiNote(from / (double)division, Math.Max(1, at - from) / (double)division, pitch, velocity));
            }
            while (p < end)
            {
                long delta = 0;
                int count = 0;
                while (p < end)
                {
                    byte b = d[p++];
                    delta = (delta << 7) | (uint)(b & 0x7F);
                    if ((b & 0x80) == 0 || ++count >= 4) break;
                }
                tick += delta;
                if (p >= end) break;
                int status = d[p];
                if (status < 0x80)
                {
                    if (running == 0) break;
                    status = running;
                }
                else p++;
                if (status == 0xFF)
                {
                    if (p >= end) break;
                    int type = d[p++];
                    int len = 0, n = 0;
                    while (p < end)
                    {
                        byte b = d[p++];
                        len = (len << 7) | (b & 0x7F);
                        if ((b & 0x80) == 0 || ++n >= 4) break;
                    }
                    if (p + len > end) break;
                    if (type == 0x51 && len == 3 && bpm == null)
                    {
                        int micros = (d[p] << 16) | (d[p + 1] << 8) | d[p + 2];
                        if (micros > 0) bpm = Math.Round(60_000_000.0 / micros, 2);
                    }
                    else if (type == 0x58 && len >= 2 && meter == null)
                        meter = (Math.Max(1, (int)d[p]), 1 << Math.Min(6, (int)d[p + 1]));
                    else if (type == 0x2F) { p += len; break; }
                    p += len;
                    continue;
                }
                if (status == 0xF0 || status == 0xF7)
                {
                    int len = 0, n = 0;
                    while (p < end)
                    {
                        byte b = d[p++];
                        len = (len << 7) | (b & 0x7F);
                        if ((b & 0x80) == 0 || ++n >= 4) break;
                    }
                    p += len;
                    continue;
                }
                running = status;
                int kind = status & 0xF0, channel = status & 0x0F;
                int size = kind == 0xC0 || kind == 0xD0 ? 1 : 2;
                if (p + size > end) break;
                int a = d[p] & 0x7F, v = size == 2 ? d[p + 1] & 0x7F : 0;
                p += size;
                if (kind == 0x90 && v > 0)
                {
                    if (!open.TryGetValue(channel * 128 + a, out var q)) open[channel * 128 + a] = q = new Queue<(long, int)>();
                    q.Enqueue((tick, v));
                }
                else if (kind == 0x80 || kind == 0x90) Close(channel, a, tick);
                if (notes.Count > MaxNotes) return;
            }
            // Notes never switched off end where the track does.
            foreach (var (key, q) in open)
                while (q.Count > 0) Close(key / 128, key % 128, Math.Max(tick, q.Peek().Tick + division));
        }

        static int Be16(byte[] d, int p) => (d[p] << 8) | d[p + 1];
        static int Be32(byte[] d, int p) => (d[p] << 24) | (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
    }
}
