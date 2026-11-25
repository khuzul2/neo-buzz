// Fork (docs/specs/studio-mvp.md S4): the sequence editor's pure logic, from the WPF editors:
// the selection (SelectionLayer), and the layout and hit tests of the timeline (tick ↔ pixel, track
// rows, pattern blocks and their edges, PatternResizeHelper's snapping). The time signature list
// (BuzzGUI.SequenceEditor's) that was ported here is the engine's song meter now
// (ReBuzz/Core/SongMeter.cs, daw-modes.md S1).

using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Sequence
{
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
        public SequenceRect Rect(SongMeter tsl)
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
        public const int NonClipSpan = 10;

        /// <summary>A block is at least this wide (PatternElement: Math.Max(w, 10)).</summary>
        public const double MinBlockWidth = 10;

        /// <summary>The bottom strip of a header holds the M, S and D buttons (16 px each).</summary>
        public const double HeaderButtonSize = 20;      // fork (ux-overhaul.md §3.2): 20 pt targets (Buzz's were 16, gallery mac-030)

        public double TrackHeight { get; set; } = 80;   // TrackHeights.Normal
        public double TickWidth { get; set; } = DefaultTickWidth;
        public double ScrollX { get; set; }
        public double ScrollY { get; set; }

        // ---- Fork (daw-modes.md AR6): the vertical (Buzz's default) orientation. Time runs down, tracks are columns, the ruler is on the
        // left and the headers on top. ScrollX and ScrollY stay the screen's: across (tracks, in the vertical) and down (time).

        /// <summary>Time runs down the page and the tracks are columns (Buzz's default sequence view).</summary>
        public bool Vertical { get; set; }

        /// <summary>The vertical's ruler column and header row.</summary>
        public const double VerticalRulerWidth = 44, VerticalHeaderHeight = 56;

        /// <summary>A track column's width in the vertical.</summary>
        public double TrackWidth { get; set; } = 104;

        /// <summary>The vertical draws a tick this many times taller than the horizontal draws it wide (the page is long, the bars short).</summary>
        public const double VerticalScale = 2;

        /// <summary>Where the tracks start from the control's left and top.</summary>
        public double LeadX => Vertical ? VerticalRulerWidth : HeaderWidth;
        public double LeadY => Vertical ? VerticalHeaderHeight : RulerHeight;

        /// <summary>Pixels a tick takes along the time axis.</summary>
        public double PxPerTick => TickWidth * (Vertical ? VerticalScale : 1);

        /// <summary>A track's size across the time axis (a row's height, a column's width).</summary>
        public double RowSize => Vertical ? TrackWidth : TrackHeight;

        /// <summary>The scroll along the time axis and along the tracks.</summary>
        public double TimeScroll => Vertical ? ScrollY : ScrollX;
        public double RowScroll => Vertical ? ScrollX : ScrollY;

        public void SetScroll(double time, double rows)
        {
            if (Vertical) { ScrollY = time; ScrollX = rows; }
            else { ScrollX = time; ScrollY = rows; }
        }

        /// <summary>A tick's place along the time axis (x in the horizontal, y in the vertical).</summary>
        public double TimePos(int time) => (Vertical ? LeadY : LeadX) + time * PxPerTick - TimeScroll;

        /// <summary>A track's place along the other axis (y in the horizontal, x in the vertical).</summary>
        public double RowPos(int row) => (Vertical ? LeadX : LeadY) + row * RowSize - RowScroll;

        // The horizontal's, as they were: the vertical goes through TimePos, RowPos, TimeAtPoint and RawRowAtPoint.
        public double XOf(int time) => HeaderWidth + time * TickWidth - ScrollX;
        public double YOf(int row) => RulerHeight + row * TrackHeight - ScrollY;

        public int TimeAt(double x) => (int)Math.Max(0, Math.Floor((x - HeaderWidth + ScrollX) / TickWidth));

        /// <summary>The track row at y, unclamped (may be past the last track).</summary>
        public int RawRowAt(double y) => (int)Math.Floor((y - RulerHeight + ScrollY) / TrackHeight);

        public int RowAt(double y, int rows) => Math.Max(0, Math.Min(rows - 1, RawRowAt(y)));

        /// <summary>The tick under a point, whichever way the timeline runs.</summary>
        public int TimeAtPoint(Point p) => (int)Math.Max(0, Math.Floor(((Vertical ? p.Y - LeadY : p.X - LeadX) + TimeScroll) / PxPerTick));

        /// <summary>The track under a point, unclamped.</summary>
        public int RawRowAtPoint(Point p) => (int)Math.Floor(((Vertical ? p.X - LeadX : p.Y - LeadY) + RowScroll) / RowSize);

        public int RowAtPoint(Point p, int rows) => Math.Max(0, Math.Min(rows - 1, RawRowAtPoint(p)));

        public static bool InRuler(Point p) => p.Y < RulerHeight && p.X >= HeaderWidth;
        public static bool InHeaders(Point p) => p.X < HeaderWidth && p.Y >= RulerHeight;
        public static bool InTracks(Point p) => p.X >= HeaderWidth && p.Y >= RulerHeight;

        // The same three, for either orientation.
        public bool OverRuler(Point p) => Vertical ? p.X < LeadX && p.Y >= LeadY : p.Y < LeadY && p.X >= LeadX;
        public bool OverHeaders(Point p) => Vertical ? p.Y < LeadY && p.X >= LeadX : p.X < LeadX && p.Y >= LeadY;
        public bool OverTracks(Point p) => p.X >= LeadX && p.Y >= LeadY;

        /// <summary>The ruler, the headers and the tracks inside a control of this size.</summary>
        public Rect RulerArea(Size s) => Vertical ? new Rect(0, LeadY, LeadX, Math.Max(0, s.Height - LeadY)) : new Rect(LeadX, 0, Math.Max(0, s.Width - LeadX), LeadY);
        public Rect HeaderArea(Size s) => Vertical ? new Rect(LeadX, 0, Math.Max(0, s.Width - LeadX), LeadY) : new Rect(0, LeadY, LeadX, Math.Max(0, s.Height - LeadY));
        public Rect TracksArea(Size s) => new Rect(LeadX, LeadY, Math.Max(0, s.Width - LeadX), Math.Max(0, s.Height - LeadY));

        /// <summary>Ticks a block covers: the event's span, or <see cref="NonPatternSpan"/> for the other kinds.</summary>
        public static int SpanOf(SequenceEvent e) => e.Type == SequenceEventType.PlayClip && e.Clip != null ? Math.Max(1, e.Span) : NonClipSpan;

        public Rect BlockRect(int time, int row, SequenceEvent e) => Span(time, Math.Max(SpanOf(e) * PxPerTick, MinBlockWidth), row, 1, 0);

        /// <summary>The cell (one bar of one track) at a time and row.</summary>
        public Rect CellRect(int time, int row, int barLength) => Span(time, barLength * PxPerTick + 1, row, 1, 1);

        /// <summary>A track's stretch of ticks from <paramref name="time"/> (a resize's rectangle).</summary>
        public Rect TickRect(int time, int ticks, int row) => Span(time, ticks * PxPerTick, row, 1, 0);

        /// <summary>A block of ticks and tracks: <paramref name="timeLength"/> pixels along the time axis, whole tracks across it.</summary>
        public Rect SpanRect(int time, int ticks, int row, int rows) => Span(time, ticks * PxPerTick + 1, row, rows, 1);

        Rect Span(int time, double timeLength, int row, int rows, double pad) => Vertical
            ? new Rect(RowPos(row), TimePos(time), rows * RowSize + pad, timeLength)
            : new Rect(TimePos(time), RowPos(row), timeLength, rows * RowSize + pad);

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

        /// <summary>Body or edge, either orientation: the start edge is the left (the vertical's top), the end edge the right (the bottom).</summary>
        public BlockPart PartAtBlock(Rect block, Point p, bool resizable)
        {
            if (!Vertical) return PartAt(block, p, resizable);
            if (!block.Contains(p)) return BlockPart.None;
            if (!resizable) return BlockPart.Body;
            double edge = Math.Min(8, block.Height / 4);
            if (p.Y < block.Y + edge) return BlockPart.LeftEdge;
            if (p.Y >= block.Bottom - edge) return BlockPart.RightEdge;
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
