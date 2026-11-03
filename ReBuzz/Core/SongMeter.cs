// Fork (docs/specs/daw-modes.md S1): the song's bar list in the engine. Buzz keeps it in the
// sequence editor (BuzzGUI.SequenceEditor's TimeSignatureList, saved in the song's
// "SequenceEditor" subsection), so only that editor knew where bars fall. As PatternColours gives
// the engine the editor's colours (ViewState.cs), this gives the engine the bars, but here the
// engine owns them: the studio's sequence editor uses this list, the WPF editors' list mirrors it
// (ReBuzz/AppViews/SequenceEditorMeter.cs), and the Session View and the agent read it. The
// sequence editors still save it in their subsection, so songs go back and forth with upstream.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    /// <summary>
    /// Bar lengths along the song: (tick, ticks per bar) pairs, each the length of the bars from
    /// that tick on. The same rules and file format as BuzzGUI.SequenceEditor.TimeSignatureList
    /// (the studio's sequence editor had a port of it, now this).
    /// </summary>
    internal sealed class SongMeter
    {
        /// <summary>Buzz's bar: 16 ticks (4 beats at 4 ticks a beat).</summary>
        public const int DefaultBar = 16;

        SortedDictionary<int, int> data = new SortedDictionary<int, int> { [0] = DefaultBar };

        /// <summary>The bars changed (an edit, a song opened, a new song).</summary>
        public event Action Changed;

        public SongMeter()
        {
        }

        /// <summary>A copy (undo keeps one).</summary>
        public SongMeter(SongMeter x)
        {
            Clone(x);
        }

        /// <summary>The (tick, ticks per bar) pairs, from tick 0 on.</summary>
        public IReadOnlyDictionary<int, int> Entries => data;

        /// <summary>Whether the bars are Buzz's default: 16 ticks from the start, never changed.</summary>
        public bool IsDefault => data.Count == 1 && data.TryGetValue(0, out int bar) && bar == DefaultBar;

        /// <summary>Whether this has the same bars as another list.</summary>
        public bool SameAs(SongMeter other) => other != null && data.Count == other.data.Count && data.SequenceEqual(other.data);

        public void Clone(SongMeter tsl)
        {
            data.Clear();
            foreach (var x in tsl.data)
                data[x.Key] = x.Value;
            Changed?.Invoke();
        }

        /// <summary>Back to one bar of 16 ticks from the start (a new song).</summary>
        public void Reset()
        {
            data = new SortedDictionary<int, int> { [0] = DefaultBar };
            Changed?.Invoke();
        }

        /// <summary>Reads the list as TimeSignatureList writes it (a count, then the pairs).</summary>
        public void Read(BinaryReader br)
        {
            var read = new SortedDictionary<int, int>();
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                int key = br.ReadInt32();
                int value = br.ReadInt32();
                if (key >= 0 && value > 0) read[key] = value;
            }
            if (!read.ContainsKey(0)) read[0] = DefaultBar;
            data = read;
            Changed?.Invoke();
        }

        public void Write(BinaryWriter bw)
        {
            bw.Write(data.Count);
            foreach (var e in data)
            {
                bw.Write(e.Key);
                bw.Write(e.Value);
            }
        }

        /// <summary>The list in TimeSignatureList's format.</summary>
        public byte[] ToBytes()
        {
            var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms)) Write(bw);
            return ms.ToArray();
        }

        /// <summary>Reads the list from TimeSignatureList's format.</summary>
        public void ReadBytes(byte[] bytes)
        {
            using var br = new BinaryReader(new MemoryStream(bytes));
            Read(br);
        }

        public void Set(int time, int step)
        {
            data[time] = step;
            Cleanup();
            Changed?.Invoke();
        }

        public void Insert(int time, int span)
        {
            var newdata = new SortedDictionary<int, int>();
            foreach (var e in data)
            {
                if (e.Key <= time)
                    newdata[e.Key] = e.Value;
                else
                    newdata[e.Key + span] = e.Value;
            }
            data = newdata;
            Changed?.Invoke();
        }

        public void Delete(int time, int span)
        {
            var newdata = new SortedDictionary<int, int>();
            foreach (var e in data)
            {
                if (e.Key <= time)
                    newdata[e.Key] = e.Value;
                else if (e.Key >= time + span)
                    newdata[e.Key - span] = e.Value;
            }
            data = newdata;
            Cleanup();
            Changed?.Invoke();
        }

        void Cleanup()
        {
            var newdata = new SortedDictionary<int, int>();
            newdata[0] = data[0];
            int t = 0;
            int step = data[0];
            foreach (var e in data)
            {
                if (e.Key > 0)
                {
                    int lastspan = (e.Key - t) % step;
                    if (lastspan != 0)
                    {
                        t = e.Key - lastspan;
                        step = lastspan;
                        newdata[t] = step;
                    }
                    if (e.Value != step)
                    {
                        t = e.Key;
                        step = e.Value;
                        newdata[t] = step;
                    }
                }
            }
            data = newdata;
        }

        public bool TimeSignatureChangesAt(int time) => data.ContainsKey(time);

        /// <summary>The bars up to <paramref name="end"/>: (start, length).</summary>
        public IEnumerable<(int Start, int Length)> GetBars(int end)
        {
            var e = data.GetEnumerator();
            e.MoveNext();
            int nextt;
            do
            {
                var current = e.Current;
                nextt = e.MoveNext() ? e.Current.Key : end;
                for (long t = current.Key; t < nextt; t += current.Value)
                    yield return ((int)t, (int)Math.Min(current.Value, nextt - t));
            } while (nextt < end);
        }

        public int GetBarLengthAt(int time)
        {
            foreach (var x in GetBars(int.MaxValue))
            {
                if (time >= x.Start && time < x.Start + x.Length)
                    return x.Length;
            }
            return DefaultBar;
        }

        /// <summary>The start of the bar holding <paramref name="time"/> (the last bar before <paramref name="end"/> past it).</summary>
        public int Snap(long time, int end)
        {
            if (time < 0) return 0;
            int last = 0;
            foreach (var x in GetBars(end))
            {
                if (time >= x.Start && time < x.Start + x.Length)
                    return x.Start;
                last = x.Start;
            }
            return last;
        }

        /// <summary>
        /// The Session View's bar where its clock starts, in beats: the first bar, when it is whole
        /// beats (1 to 64) and the list isn't Buzz's untouched default; otherwise 4, as before.
        /// </summary>
        public int SessionBeatsPerBar(int ticksPerBeat)
        {
            if (IsDefault || ticksPerBeat <= 0) return 4;
            int bar = data[0];
            return bar % ticksPerBeat == 0 && bar / ticksPerBeat is >= 1 and <= 64 ? bar / ticksPerBeat : 4;
        }
    }

    /// <summary>
    /// Reads the song's bar list from the classic sequence editor's subsection when a song opens,
    /// and writes that subsection when the song is saved. A sequence editor that saves its own
    /// (the WPF editor, the studio's) subscribed later, so its subsection replaces this one; this
    /// one stays where none does (rebuzz-headless), keeping the bars, the editor's zoom and the
    /// pattern colours the song had.
    /// </summary>
    internal sealed class SongMeterStore
    {
        public const string SubSectionName = "SequenceEditor";
        const int ClassicVersion = 2;

        readonly ReBuzzCore buzz;
        double zoom = 2;                                                 // the classic editor's
        readonly List<(string Machine, string Pattern, int Colour)> colours = new List<(string, string, int)>();

        internal SongMeterStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.OpenSong += OnOpenSong;
            buzz.SaveSong += OnSaveSong;
        }

        void OnOpenSong(IOpenSong open)
        {
            zoom = 2;
            colours.Clear();
            var meter = buzz.SongMeter;
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream == null)
                {
                    meter.Reset();
                    return;
                }
                var br = new BinaryReader(stream);
                int version = br.ReadInt32();
                if (version > ClassicVersion)
                {
                    meter.Reset();
                    return;
                }
                zoom = br.ReadDouble();
                meter.Read(br);
                if (version < 2) return;
                int count = br.ReadInt32();
                for (int i = 0; i < count; i++)
                    colours.Add((br.ReadString(), br.ReadString(), br.ReadInt32()));
            }
            catch (Exception e) when (e is IOException || e is EndOfStreamException)
            {
                buzz.DCWriteLine("Song meter: " + e.Message, DCLogLevel.Warning);
            }
        }

        void OnSaveSong(ISaveSong save)
        {
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var bw = new BinaryWriter(stream);
            bw.Write(ClassicVersion);
            bw.Write(zoom);
            buzz.SongMeter.Write(bw);
            // The colours: the sequence editor's when one is up, else those the song had, of patterns still in it.
            var live = new List<(string, string, int)>();
            var view = PatternColours.Current;
            if (view != null)
            {
                foreach (var p in view.Patterns)
                    if (p?.Machine != null && save.Song.Machines.Contains(p.Machine) && p.Machine.Patterns.Contains(p) && view.TryGet(p, out int ci))
                        live.Add((p.Machine.Name, p.Name, ci));
            }
            else
            {
                foreach (var c in colours)
                    if (save.Song.Machines.FirstOrDefault(m => m.Name == c.Machine)?.Patterns.Any(p => p.Name == c.Pattern) == true)
                        live.Add(c);
            }
            bw.Write(live.Count);
            foreach (var (machine, pattern, colour) in live)
            {
                bw.Write(machine);
                bw.Write(pattern);
                bw.Write(colour);
            }
            bw.Flush();
        }
    }
}
