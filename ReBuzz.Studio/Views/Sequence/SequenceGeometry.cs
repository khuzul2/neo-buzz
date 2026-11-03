// Fork (docs/specs/studio-mvp.md S4): the sequence editor's pure logic, from the WPF editors:
// the time signature list (BuzzGUI.SequenceEditor's, which the horizontal editor borrows), the
// selection (SelectionLayer), and the layout and hit tests of the timeline (tick ↔ pixel, track
// rows, pattern blocks and their edges, PatternResizeHelper's snapping).

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Views.Sequence
{
    /// <summary>
    /// Bar lengths along the song: (time, step) pairs, each step the length of the bars from that
    /// time on. The cursor, the selection and moves snap to these bars. Port of
    /// BuzzGUI.SequenceEditor.TimeSignatureList (same file format).
    /// </summary>
    internal sealed class TimeSignatureList
    {
        SortedDictionary<int, int> data = new SortedDictionary<int, int>();

        public event Action Changed;

        public TimeSignatureList()
        {
            data[0] = 16;
        }

        public TimeSignatureList(TimeSignatureList x)
        {
            Clone(x);
        }

        public IReadOnlyDictionary<int, int> Entries => data;

        public void Clone(TimeSignatureList tsl)
        {
            data.Clear();
            foreach (var x in tsl.data)
                data[x.Key] = x.Value;
            Changed?.Invoke();
        }

        /// <summary>Back to one step of 16 from the start (a new song).</summary>
        public void Reset()
        {
            data = new SortedDictionary<int, int> { [0] = 16 };
            Changed?.Invoke();
        }

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
            if (!read.ContainsKey(0)) read[0] = 16;
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
            return 16;
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
    }

    /// <summary>
    /// The selection: an anchor cell and a second cell (time, row); a rectangle of whole bars and
    /// tracks between them. Port of SelectionLayer's logic.
    /// </summary>
    internal sealed class SequenceSelection
    {
        (int Time, int Row) anchor = (-1, -1);
        (int Time, int Row) point = (-1, -1);

        public bool Selecting { get; private set; }

        public bool NotEmpty => point.Time >= 0;

        public void Begin(int time, int row)
        {
            anchor = (time, row);
            point = (-1, -1);
            Selecting = true;
        }

        public void Update(int time, int row)
        {
            if (!Selecting && !NotEmpty) Begin(time, row);
            point = (time, row);
        }

        public void End() => Selecting = false;

        public void Kill()
        {
            point = (-1, -1);
            Selecting = false;
        }

        /// <summary>Whole bars from the first cell's to the last's, and the tracks between: (start tick, first track, ticks, tracks).</summary>
        public SequenceRect Rect(TimeSignatureList tsl)
        {
            int start = tsl.Snap(Math.Min(point.Time, anchor.Time), int.MaxValue);
            int end = tsl.Snap(Math.Max(point.Time, anchor.Time), int.MaxValue);
            end += tsl.GetBarLengthAt(end);
            return new SequenceRect(start, Math.Min(point.Row, anchor.Row), end - start, Math.Abs(point.Row - anchor.Row) + 1);
        }
    }

    /// <summary>A block of the timeline: ticks from Time for Span, tracks from Row for Rows.</summary>
    internal readonly record struct SequenceRect(int Time, int Row, int Span, int Rows)
    {
        public int End => Time + Span;
        public int LastRow => Row + Rows - 1;
        public bool Contains(int time, int row) => time >= Time && time < End && row >= Row && row <= LastRow;
    }

    /// <summary>Where a point falls on a pattern block.</summary>
    internal enum BlockPart { None, Body, LeftEdge, RightEdge }

    /// <summary>
    /// The timeline's layout: the track headers on the left, the time ruler on top, the tracks
    /// (one row per sequence) below it; tick ↔ pixel at the zoom (TickWidth, pixels per tick) and
    /// the scroll offsets. Pixels are the control's.
    /// </summary>
    internal sealed class SequenceGeometry
    {
        /// <summary>The header column (WPF: 140) and the ruler (WPF's 34 px top row, less its margin).</summary>
        public const double HeaderWidth = 140;
        public const double RulerHeight = 30;

        /// <summary>The zoom slider's range and step (pixels per tick), as WPF's.</summary>
        public const double MinTickWidth = 0.25, MaxTickWidth = 7.75, TickWidthStep = 0.25, DefaultTickWidth = 2;

        /// <summary>Ticks a break, mute, thru or stop event is drawn over (ViewSettings.NonPlayPattenSpan).</summary>
        public const int NonPatternSpan = 10;

        /// <summary>A block is at least this wide (PatternElement: Math.Max(w, 10)).</summary>
        public const double MinBlockWidth = 10;

        /// <summary>The bottom strip of a header holds the M, S and D buttons (16 px each).</summary>
        public const double HeaderButtonSize = 16;

        public double TrackHeight { get; set; } = 80;   // TrackHeights.Normal
        public double TickWidth { get; set; } = DefaultTickWidth;
        public double ScrollX { get; set; }
        public double ScrollY { get; set; }

        public double XOf(int time) => HeaderWidth + time * TickWidth - ScrollX;
        public double YOf(int row) => RulerHeight + row * TrackHeight - ScrollY;

        public int TimeAt(double x) => (int)Math.Max(0, Math.Floor((x - HeaderWidth + ScrollX) / TickWidth));

        /// <summary>The track row at y, unclamped (may be past the last track).</summary>
        public int RawRowAt(double y) => (int)Math.Floor((y - RulerHeight + ScrollY) / TrackHeight);

        public int RowAt(double y, int rows) => Math.Max(0, Math.Min(rows - 1, RawRowAt(y)));

        public static bool InRuler(Point p) => p.Y < RulerHeight && p.X >= HeaderWidth;
        public static bool InHeaders(Point p) => p.X < HeaderWidth && p.Y >= RulerHeight;
        public static bool InTracks(Point p) => p.X >= HeaderWidth && p.Y >= RulerHeight;

        /// <summary>Ticks a block covers: the event's span, or <see cref="NonPatternSpan"/> for the other kinds.</summary>
        public static int SpanOf(SequenceEvent e) => e.Type == SequenceEventType.PlayPattern && e.Pattern != null ? Math.Max(1, e.Span) : NonPatternSpan;

        public Rect BlockRect(int time, int row, SequenceEvent e) =>
            new Rect(XOf(time), YOf(row), Math.Max(SpanOf(e) * TickWidth, MinBlockWidth), TrackHeight);

        /// <summary>The cell (one bar of one track) at a time and row.</summary>
        public Rect CellRect(int time, int row, int barLength) => new Rect(XOf(time), YOf(row), barLength * TickWidth + 1, TrackHeight + 1);

        /// <summary>
        /// Body or edge of a block. The edges are WPF's drag lines (8 px wide at each end), narrower
        /// on small blocks so they keep a body; only pattern blocks resize.
        /// </summary>
        public static BlockPart PartAt(Rect block, Point p, bool resizable)
        {
            if (!block.Contains(p)) return BlockPart.None;
            if (!resizable) return BlockPart.Body;
            double edge = Math.Min(8, block.Width / 4);
            if (p.X < block.X + edge) return BlockPart.LeftEdge;
            if (p.X >= block.Right - edge) return BlockPart.RightEdge;
            return BlockPart.Body;
        }

        /// <summary>
        /// The pattern length a right-edge drag gives (PatternResizeHelper, Right): the end goes to
        /// the snap line at or after the pointer, at least one snap past the start.
        /// </summary>
        public static int ResizeRightLength(int eventTime, int pointerTick, int snap)
        {
            snap = Math.Max(1, snap);
            int end = (int)(snap * Math.Ceiling(pointerTick / (double)snap));
            int length = end - eventTime;
            return length <= 0 ? snap : length;
        }

        /// <summary>
        /// Where a left-edge drag puts the event (PatternResizeHelper, Left): the start goes to the
        /// nearest snap line, at most one snap before the end; the end stays.
        /// </summary>
        public static (int Time, int Length) ResizeLeft(int eventTime, int span, int pointerTick, int snap)
        {
            snap = Math.Max(1, snap);
            int tick = (int)(snap * Math.Floor(pointerTick / (double)snap + 0.5));
            if (tick >= span + eventTime - snap) tick = span + eventTime - snap;
            tick = Math.Max(0, tick);
            return (tick, span + eventTime - tick);
        }

        /// <summary>The zoom's next step, kept in range.</summary>
        public static double ClampTickWidth(double w) =>
            Math.Max(MinTickWidth, Math.Min(MaxTickWidth, Math.Round(w / TickWidthStep) * TickWidthStep));
    }
}
