// Fork (docs/specs/daw-modes.md AR1, §4): the Sequence tab's Arrangement mode. The same timeline as
// the tracker mode's canvas (ruler, bars, zoom, play position) with a track for each machine and a
// lane for each of its sequences, and clips on the lanes (ReBuzz.Core.ClipEditor): draw (double
// click), move, copy (Ctrl/Cmd: an independent copy with a pattern of its own; Alt/Option: linked,
// the same pattern), trim by the edges, repeat by the right edge when the clip loops, split, join,
// make unique, the shared badge. The Master's tempo and markers are on top; lanes the Session
// View has taken over are greyed. Edits are steps of the song's history (ClipEditAction).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Sequence
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ArrangementCanvas : Control
    {
        /// <summary>A clip's edges: the left and right this many pixels (less on a narrow clip) trim.</summary>
        const double EdgeWidth = 7;
        const double ClipInset = 3;

        readonly ReBuzzCore core;
        readonly SequenceSongState state;
        readonly SequenceGeometry geo = new SequenceGeometry();
        readonly ClipEditor editor;
        readonly Dictionary<IPattern, PreviewMark[]> previews = new();
        readonly HashSet<IPattern> watchedPatterns = new();
        readonly List<ISequence> watchedSequences = new();
        readonly Dictionary<ISequence, List<ClipItem>> items = new();
        readonly Dictionary<IPattern, int> uses = new();
        List<ArrangementRow> rows = new();
        ISong song;
        bool active;
        ClipItem selected;
        ISequence cursorLane;
        int cursorTime;
        double wheelZoom;

        enum Drag { None, PendingMove, Move, TrimLeft, TrimRight }
        Drag drag;
        Point pressPoint;
        ClipItem dragItem;
        int grabOffset, ghostTime, ghostLength;
        ISequence ghostLane;
        ClipEditor.CopyMode ghostMode;

        public ArrangementCanvas(ReBuzzCore core, SequenceSongState state)
        {
            this.core = core;
            this.state = state;
            editor = new ClipEditor(core);
            Focusable = true;
            ClipToBounds = true;
            geo.TickWidth = state.Zoom;
            Overlay = new ArrangementOverlay(this) { IsHitTestVisible = false };
            PointerTouchPadGestureMagnify += OnPinch;
        }

        public ArrangementOverlay Overlay { get; }
        public SequenceGeometry Geometry => geo;
        public ClipEditor Editor => editor;
        public ISong Song => core.Song;
        public SongMeter TimeSignatures => state.TimeSignatures;
        public IReadOnlyList<ArrangementRow> Rows => rows;
        public bool IsActive => active;
        public bool Follow { get; set; } = true;
        public int CursorTime => cursorTime;
        public ISequence CursorLane => cursorLane;
        public ClipItem Selected => selected;

        /// <summary>The last edit's refusal, or null (the view shows it in its status line).</summary>
        public string Status { get; private set; }

        public event Action ViewChanged;
        public event Action SelectionChanged;
        public event Action<IPattern> PatternDoubleClicked;
        public event Action<Point> MenuRequested;
        public event Action StatusChanged;

        static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        int TicksPerBeat => Math.Max(1, core.TPB);

        // ------------------------------------------------------------------ what is where

        public IReadOnlyList<ClipItem> ItemsOf(ISequence lane) => lane != null && items.TryGetValue(lane, out var list) ? list : Array.Empty<ClipItem>();

        public ArrangementRow RowOf(ISequence lane) => rows.FirstOrDefault(r => r.Lane == lane);

        public double YOf(ArrangementRow r) => SequenceGeometry.RulerHeight + r.Top - geo.ScrollY;

        /// <summary>A clip's rectangle (the lane's row, inset), in the canvas's pixels.</summary>
        public Rect ClipRect(ClipItem item)
        {
            var row = RowOf(item.Sequence);
            if (row == null) return default;
            double x = geo.XOf(item.Time), w = Math.Max(SequenceGeometry.MinBlockWidth, (item.End - item.Time) * geo.TickWidth);
            return new Rect(x, YOf(row) + ClipInset, w, row.Height - 2 * ClipInset);
        }

        /// <summary>The number of places a pattern is used in (clips on the lanes and slots of the session), for the shared badge.</summary>
        public int UsesOf(IPattern pattern) => pattern != null && uses.TryGetValue(pattern, out int n) ? n : 0;

        (ClipItem Item, BlockPart Part) ClipAt(Point p)
        {
            if (p.X < SequenceGeometry.HeaderWidth || p.Y < SequenceGeometry.RulerHeight) return (null, BlockPart.None);
            foreach (var row in rows.Where(r => r.Kind == ArrangementRowKind.Lane && !r.Folded && r.Lane != null))
            {
                if (p.Y < YOf(row) || p.Y >= YOf(row) + row.Height) continue;
                foreach (var item in ItemsOf(row.Lane))
                {
                    var r = ClipRect(item);
                    var part = SequenceGeometry.PartAt(r, p, true);
                    if (part == BlockPart.None) continue;
                    // The edges are narrower on a short clip, and a clip that doesn't loop can't be made longer by its right edge past its pattern.
                    double edge = Math.Min(EdgeWidth, r.Width / 4);
                    part = p.X < r.X + edge ? BlockPart.LeftEdge : p.X >= r.Right - edge ? BlockPart.RightEdge : BlockPart.Body;
                    return (item, part);
                }
            }
            return (null, BlockPart.None);
        }

        /// <summary>The lane row at a y (not the folded ones), or null.</summary>
        ArrangementRow LaneRowAt(double y) => rows.FirstOrDefault(r => r.Kind == ArrangementRowKind.Lane && !r.Folded && y >= YOf(r) && y < YOf(r) + r.Height);

        /// <summary>The grid a clip snaps to: a beat, or a tick with Shift held.</summary>
        int Snap(int tick, bool fine) => fine ? Math.Max(0, tick) : Math.Max(0, (int)Math.Round(tick / (double)TicksPerBeat) * TicksPerBeat);

        // ------------------------------------------------------------------ engine events

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            song = core.Song;
            song.SequenceAdded += OnSequencesChanged;
            song.SequenceRemoved += OnSequencesChanged;
            song.SequenceChanged += OnSequencesChanged;
            song.PropertyChanged += OnSongChanged;
            core.PropertyChanged += OnBuzzChanged;
            state.Changed += OnStateChanged;
            if (core.Arrangement != null) core.Arrangement.Changed += OnStoreChanged;
            if (core.MarkerStore != null) core.MarkerStore.Changed += OnStoreChanged;
            if (core.SessionCore is { } session)
            {
                session.TrackStateChanged += OnTrackState;
                session.PropertyChanged += OnSessionChanged;
            }
            ReBuzz.Core.Session.PatternColors.Changed += OnPatternColourChanged;
            WatchSequences();
            geo.TickWidth = state.Zoom;
            Changed();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            if (song != null)
            {
                song.SequenceAdded -= OnSequencesChanged;
                song.SequenceRemoved -= OnSequencesChanged;
                song.SequenceChanged -= OnSequencesChanged;
                song.PropertyChanged -= OnSongChanged;
            }
            core.PropertyChanged -= OnBuzzChanged;
            state.Changed -= OnStateChanged;
            if (core.Arrangement != null) core.Arrangement.Changed -= OnStoreChanged;
            if (core.MarkerStore != null) core.MarkerStore.Changed -= OnStoreChanged;
            if (core.SessionCore is { } session)
            {
                session.TrackStateChanged -= OnTrackState;
                session.PropertyChanged -= OnSessionChanged;
            }
            ReBuzz.Core.Session.PatternColors.Changed -= OnPatternColourChanged;
            UnwatchSequences();
            UnwatchPatterns();
            song = null;
            base.OnDetachedFromVisualTree(e);
        }

        static void OnUi(Action a)
        {
            if (Dispatcher.UIThread.CheckAccess()) a();
            else Dispatcher.UIThread.Post(a);
        }

        void OnSequencesChanged(int index, ISequence s) => OnUi(() =>
        {
            WatchSequences();
            if (index < 0 || !Song.Sequences.Contains(s)) clipboard = null;
            UnwatchPatterns();
            Changed();
        });

        void OnSongChanged(object sender, PropertyChangedEventArgs e) => OnUi(() =>
        {
            switch (e.PropertyName)
            {
                case "PlayPosition":
                    FollowPlayPosition();
                    Overlay.InvalidateVisual();
                    break;
                case "SongEnd":
                case "LoopStart":
                case "LoopEnd":
                    Changed();
                    break;
            }
        });

        void OnBuzzChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "TPB" || e.PropertyName == "BPM") OnUi(Changed);
            else if (e.PropertyName == "Playing") OnUi(() => Overlay.InvalidateVisual());
        }

        void OnStateChanged() => OnUi(() =>
        {
            if (geo.TickWidth != state.Zoom) SetZoom(state.Zoom, null);
            Changed();
        });

        void OnStoreChanged() => OnUi(Changed);
        void OnTrackState(ISequence s) => OnUi(InvalidateVisual);
        void OnSessionChanged(object sender, PropertyChangedEventArgs e) => OnUi(InvalidateVisual);
        void OnPatternColourChanged(IPattern p) => OnUi(InvalidateVisual);

        void WatchSequences()
        {
            UnwatchSequences();
            foreach (var s in Song.Sequences)
            {
                watchedSequences.Add(s);
                s.EventChanged += OnEventChanged;
                s.SpanInserted += OnSpan;
                s.SpanDeleted += OnSpan;
                s.SpanCleared += OnSpan;
            }
        }

        void UnwatchSequences()
        {
            foreach (var s in watchedSequences)
            {
                s.EventChanged -= OnEventChanged;
                s.SpanInserted -= OnSpan;
                s.SpanDeleted -= OnSpan;
                s.SpanCleared -= OnSpan;
            }
            watchedSequences.Clear();
        }

        void OnEventChanged(int time) => OnUi(Changed);
        void OnSpan(int time, int span) => OnUi(Changed);

        void Watch(IPattern p)
        {
            if (!watchedPatterns.Add(p)) return;
            p.PatternChanged += OnPatternContentChanged;
            p.PropertyChanged += OnPatternPropertyChanged;
            p.ColumnAdded += OnPatternContentChanged;
            p.ColumnRemoved += OnPatternContentChanged;
        }

        void UnwatchPatterns()
        {
            foreach (var p in watchedPatterns)
            {
                p.PatternChanged -= OnPatternContentChanged;
                p.PropertyChanged -= OnPatternPropertyChanged;
                p.ColumnAdded -= OnPatternContentChanged;
                p.ColumnRemoved -= OnPatternContentChanged;
            }
            watchedPatterns.Clear();
            previews.Clear();
        }

        void OnPatternContentChanged(IPatternColumn c) => OnUi(() =>
        {
            previews.Clear();
            InvalidateVisual();
        });

        void OnPatternPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "Name" && e.PropertyName != "Length") return;
            OnUi(() =>
            {
                if (sender is IPattern p) previews.Remove(p);
                Changed();
            });
        }

        /// <summary>Reads the clips again and redraws both layers; the scroll bars follow the extent.</summary>
        public void Changed()
        {
            if (song == null) return;
            if (!IsVisible)
            {
                // The tracker mode is showing: hold nothing of the song (the mode switch reads it all again).
                items.Clear();
                uses.Clear();
                rows = new List<ArrangementRow>();
                selected = null;
                clipboard = null;
                UnwatchPatterns();
                return;
            }
            rows = ArrangementLayout.Build(Song, core.Arrangement);
            items.Clear();
            uses.Clear();
            foreach (var row in rows.Where(r => r.Kind == ArrangementRowKind.Lane))
                foreach (var lane in row.Lanes)
                    if (!items.ContainsKey(lane)) items[lane] = editor.Read(lane);
            foreach (var item in items.Values.SelectMany(l => l))
                uses[item.Pattern] = uses.GetValueOrDefault(item.Pattern) + 1;
            var doc = core.SessionLayout?.Document;
            if (doc != null)
                foreach (var track in doc.Tracks)
                    foreach (var name in track.Slots.Values)
                        if (Song.Machines.FirstOrDefault(m => m.Name == track.Machine)?.Patterns.FirstOrDefault(p => p.Name == name) is { } pattern)
                            uses[pattern] = uses.GetValueOrDefault(pattern) + 1;
            if (selected != null) selected = items.Values.SelectMany(l => l).FirstOrDefault(i => i.Equals(selected));
            if (cursorLane != null && !Song.Sequences.Contains(cursorLane)) cursorLane = null;
            cursorLane ??= Song.Sequences.FirstOrDefault();
            ClampScroll();
            InvalidateVisual();
            Overlay.InvalidateVisual();
            ViewChanged?.Invoke();
        }

        void SetStatus(string text)
        {
            Status = text;
            StatusChanged?.Invoke();
        }

        // ------------------------------------------------------------------ cursor, scroll and zoom

        public void Select(ClipItem item)
        {
            selected = item;
            if (item != null) { cursorLane = item.Sequence; cursorTime = item.Time; }
            Overlay.InvalidateVisual();
            InvalidateVisual();
            SelectionChanged?.Invoke();
        }

        public void MoveCursor(int time, ISequence lane)
        {
            cursorTime = Math.Max(0, time);
            if (lane != null) cursorLane = lane;
            BringIntoView(cursorTime);
            Overlay.InvalidateVisual();
            SelectionChanged?.Invoke();
        }

        Size Viewport => new Size(Math.Max(1, Bounds.Width - SequenceGeometry.HeaderWidth), Math.Max(1, Bounds.Height - SequenceGeometry.RulerHeight));

        /// <summary>The scrollable size of the tracks: past the song end and the clips by half a view.</summary>
        public Size Extent
        {
            get
            {
                int end = Song.SongEnd;
                foreach (var item in items.Values.SelectMany(l => l)) end = Math.Max(end, item.End);
                return new Size(end * geo.TickWidth + Viewport.Width / 2, ArrangementLayout.TotalHeight(rows) + 1);
            }
        }

        void ClampScroll()
        {
            var ext = Extent;
            var view = Viewport;
            geo.ScrollX = Math.Max(0, Math.Min(geo.ScrollX, ext.Width - view.Width));
            geo.ScrollY = Math.Max(0, Math.Min(geo.ScrollY, ext.Height - view.Height));
        }

        public void ScrollTo(double x, double y)
        {
            geo.ScrollX = x;
            geo.ScrollY = y;
            Changed();
        }

        void BringIntoView(int tick)
        {
            var view = Viewport;
            double x = tick * geo.TickWidth;
            if (x < geo.ScrollX) geo.ScrollX = x;
            else if (x > geo.ScrollX + view.Width - 8) geo.ScrollX = x - view.Width + 8;
            ClampScroll();
            InvalidateVisual();
            ViewChanged?.Invoke();
        }

        void FollowPlayPosition()
        {
            if (!Follow || !core.PlayRequested) return;
            double x = Song.PlayPosition * geo.TickWidth;
            var view = Viewport;
            if (x < geo.ScrollX || x > geo.ScrollX + view.Width - 2)
            {
                geo.ScrollX = Math.Max(0, x - view.Width * 0.05);
                ClampScroll();
                InvalidateVisual();
                ViewChanged?.Invoke();
            }
        }

        public void SetZoom(double tickWidth, double? anchorX)
        {
            tickWidth = SequenceGeometry.ClampTickWidth(tickWidth);
            double ax = anchorX ?? SequenceGeometry.HeaderWidth;
            double tick = (ax - SequenceGeometry.HeaderWidth + geo.ScrollX) / geo.TickWidth;
            geo.TickWidth = tickWidth;
            geo.ScrollX = tick * tickWidth - (ax - SequenceGeometry.HeaderWidth);
            state.Zoom = tickWidth;
            Changed();
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            ClampScroll();
            ViewChanged?.Invoke();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsKeyboardFocusWithinProperty)
            {
                active = IsKeyboardFocusWithin;
                Overlay.InvalidateVisual();
            }
        }

        // ------------------------------------------------------------------ drawing

        static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-us");
        static readonly Typeface Font = new Typeface(new FontFamily("Segoe UI, Arial"));
        static readonly Typeface BoldFont = new Typeface(new FontFamily("Segoe UI, Arial"), FontStyle.Normal, FontWeight.Bold);
        static SequenceLook Look => StudioTheme.Sequence;

        static FormattedText Text(string s, IBrush brush, double size = 12, bool bold = false, double maxWidth = 0) =>
            new FormattedText(s, Culture, FlowDirection.LeftToRight, bold ? BoldFont : Font, size, brush)
            { MaxTextWidth = maxWidth > 0 ? maxWidth : double.PositiveInfinity, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };

        static Color Blend(Color a, Color b, double f) => Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * f), (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

        /// <summary>A clip's fill: its pattern's colour, as the tracker mode's blocks (SequenceCanvas.BlockColour).</summary>
        internal Color ColourOf(IPattern pattern)
        {
            var palette = SequenceSongState.Palette;
            int ci = pattern != null ? state.ColourIndexOf(pattern) : -1;
            bool hasOwn = pattern != null && state.TryGet(pattern, out int saved) && saved >= 0;
            if (!hasOwn && !Look.IsClassic && pattern?.Machine != null && MachineIdentity.ColourOf(pattern.Machine) is { } identity) return identity;
            return ci >= 0 && ci < palette.Count
                ? Color.FromUInt32(palette[ci].Argb)
                : Look.BoxColours is { } own ? own[0] : (Color)StudioTokens.Accent;
        }

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            ctx.FillRectangle(Look.Background, new Rect(size));
            if (song == null) return;
            double hw = SequenceGeometry.HeaderWidth, rh = SequenceGeometry.RulerHeight;
            var tracksArea = new Rect(hw, rh, Math.Max(0, size.Width - hw), Math.Max(0, size.Height - rh));
            var rulerArea = new Rect(hw, 0, Math.Max(0, size.Width - hw), rh);
            var tsl = state.TimeSignatures;
            int lastTick = geo.TimeAt(size.Width) + 1;
            var bars = tsl.GetBars(Math.Max(lastTick, 1) + 1).Where(b => b.Start + b.Length >= geo.TimeAt(hw)).ToList();

            DrawRuler(ctx, rulerArea, bars);
            DrawTracks(ctx, tracksArea, bars);
            ctx.DrawRectangle(null, Look.Border, rulerArea.Deflate(0.5));
            ctx.DrawRectangle(null, Look.Border, tracksArea.Deflate(0.5));
        }

        /// <summary>The 1-based bar a tick is in, counted over the song's time signatures.</summary>
        internal int BarNumberAt(int tick)
        {
            var tsl = state.TimeSignatures;
            int bar = 1;
            for (int t = 0; t + tsl.GetBarLengthAt(t) <= tick && bar < 1_000_000; t += Math.Max(1, tsl.GetBarLengthAt(t))) bar++;
            return bar;
        }

        /// <summary>"Bar 3, beat 2" for a tick (the tooltips' time).</summary>
        internal string BarBeat(int tick)
        {
            var tsl = state.TimeSignatures;
            int start = tsl.Snap(tick, int.MaxValue);
            return $"bar {BarNumberAt(tick)}.{(tick - start) / TicksPerBeat + 1}";
        }

        void DrawRuler(DrawingContext ctx, Rect area, List<(int Start, int Length)> bars)
        {
            using var clip = ctx.PushClip(area);
            ctx.FillRectangle(Look.Ruler, area);
            var tsl = state.TimeSignatures;
            double lastLabelEnd = double.MinValue;
            int number = bars.Count > 0 ? BarNumberAt(bars[0].Start) - 1 : 0;
            foreach (var bar in bars)
            {
                number++;
                double x = geo.XOf(bar.Start);
                if (bar.Start > 0) ctx.FillRectangle(Look.Line, new Rect(Math.Floor(x), 0, 1, area.Height));
                if (x + 3 < lastLabelEnd) continue;
                var ft = Text(number.ToString(CultureInfo.InvariantCulture), Look.RulerText, 12, tsl.TimeSignatureChangesAt(bar.Start));
                ctx.DrawText(ft, new Point(x + 3, 3));
                lastLabelEnd = x + 3 + ft.Width + 4;
            }
        }

        void DrawTracks(DrawingContext ctx, Rect area, List<(int Start, int Length)> bars)
        {
            using var clip = ctx.PushClip(area);
            ctx.FillRectangle(Look.TrackView, area);

            // The meter grid: a line at each bar, a fainter one at each beat when a beat is wide enough to tell apart.
            double beatWidth = TicksPerBeat * geo.TickWidth;
            foreach (var bar in bars)
            {
                if (beatWidth >= 14)
                {
                    using var faint = ctx.PushOpacity(0.45);
                    for (int t = bar.Start + TicksPerBeat; t < bar.Start + bar.Length; t += TicksPerBeat)
                        ctx.FillRectangle(Look.BarLine, new Rect(Math.Floor(geo.XOf(t)), area.Y, 1, area.Height));
                }
                if (bar.Start > 0) ctx.FillRectangle(Look.BarLine, new Rect(Math.Floor(geo.XOf(bar.Start)), area.Y, 1, area.Height));
            }

            var session = core.SessionCore;
            foreach (var row in rows)
            {
                double y = YOf(row);
                if (y + row.Height < area.Y || y > area.Bottom) continue;
                switch (row.Kind)
                {
                    case ArrangementRowKind.Tempo: DrawTempo(ctx, area, row, y); break;
                    case ArrangementRowKind.Markers: DrawMarkers(ctx, area, row, y); break;
                    default:
                        if (row.Folded)
                        {
                            foreach (var lane in row.Lanes)
                                foreach (var item in ItemsOf(lane)) DrawFolded(ctx, area, item, y, row.Height);
                        }
                        else
                        {
                            foreach (var item in ItemsOf(row.Lane))
                            {
                                var r = ClipRect(item);
                                if (r.Right < area.X || r.X > area.Right) continue;
                                DrawClip(ctx, r, item);
                            }
                            DrawLoose(ctx, area, row, y);
                            if (session != null && session.IsArrangementOverridden(row.Lane))
                            {
                                using var dim = ctx.PushOpacity(0.55);
                                ctx.FillRectangle(Look.Background, new Rect(area.X, y, area.Width, row.Height));
                            }
                        }
                        break;
                }
                ctx.FillRectangle(Look.TrackLine, new Rect(area.X, Math.Floor(y + row.Height), area.Width, 1));
            }
        }

        /// <summary>Break, mute, thru and stop events of a lane: small marks, as the tracker mode keeps them.</summary>
        void DrawLoose(DrawingContext ctx, Rect area, ArrangementRow row, double y)
        {
            foreach (var kv in row.Lane.Events)
            {
                if (kv.Value.Type == SequenceEventType.PlayPattern) continue;
                double x = geo.XOf(kv.Key);
                if (x < area.X - 8 || x > area.Right) continue;
                ctx.FillRectangle(Look.Off, new Rect(Math.Floor(x), y + ClipInset, 3, row.Height - 2 * ClipInset));
            }
        }

        void DrawTempo(DrawingContext ctx, Rect area, ArrangementRow row, double y)
        {
            var changes = TempoLane.Changes(Song);
            int bpm0 = changes.Count > 0 && changes[0].Tick == 0 ? changes[0].Bpm : core.BPM;
            var steps = new List<(int Tick, int Bpm)>();
            if (changes.Count == 0 || changes[0].Tick > 0) steps.Add((0, bpm0));
            steps.AddRange(changes);
            int lo = steps.Min(s => s.Bpm), hi = steps.Max(s => s.Bpm);
            if (hi - lo < 20) { lo -= 10; hi += 10; }
            double Y(int bpm) => y + 6 + (row.Height - 12) * (1 - (bpm - lo) / (double)(hi - lo));
            var pen = new Pen(Look.Param, 1.5);
            for (int i = 0; i < steps.Count; i++)
            {
                double x0 = geo.XOf(steps[i].Tick), x1 = i + 1 < steps.Count ? geo.XOf(steps[i + 1].Tick) : area.Right;
                ctx.DrawLine(pen, new Point(x0, Y(steps[i].Bpm)), new Point(x1, Y(steps[i].Bpm)));
                if (i + 1 < steps.Count) ctx.DrawLine(pen, new Point(x1, Y(steps[i].Bpm)), new Point(x1, Y(steps[i + 1].Bpm)));
                if (x0 + 40 > area.X && x0 < area.Right)
                    ctx.DrawText(Text(steps[i].Bpm.ToString(CultureInfo.InvariantCulture), Look.HeaderText, 11), new Point(x0 + 3, Y(steps[i].Bpm) - 15));
            }
        }

        void DrawMarkers(DrawingContext ctx, Rect area, ArrangementRow row, double y)
        {
            var brush = Look.HeaderText;
            foreach (var (name, tick) in core.MarkerStore?.All ?? Array.Empty<(string, int)>())
            {
                double x = Math.Floor(geo.XOf(tick));
                if (x > area.Right || x + 200 < area.X) continue;
                ctx.FillRectangle(Look.Param, new Rect(x, y + 2, 2, row.Height - 4));
                ctx.DrawText(Text(name, brush, 11), new Point(x + 5, y + 4));
            }
            DrawFlag(ctx, area, geo.XOf(Song.LoopStart), y, row.Height, Look.Loop, "loop", Song.LoopStart != 0);
            DrawFlag(ctx, area, geo.XOf(Song.LoopEnd), y, row.Height, Look.Loop, "", true);
            DrawFlag(ctx, area, geo.XOf(Song.SongEnd), y, row.Height, Look.SongEnd, "end", true);
        }

        void DrawFlag(DrawingContext ctx, Rect area, double x, double y, double h, IBrush brush, string label, bool show)
        {
            if (!show || x < area.X || x > area.Right) return;
            ctx.FillRectangle(brush, new Rect(Math.Floor(x), y, 2, h));
            if (label.Length > 0) ctx.DrawText(Text(label, Look.HeaderText, 10), new Point(x + 4, y + 6));
        }

        void DrawFolded(DrawingContext ctx, Rect area, ClipItem item, double y, double h)
        {
            double x0 = geo.XOf(item.Time), x1 = geo.XOf(item.End);
            if (x1 < area.X || x0 > area.Right) return;
            ctx.FillRectangle(new SolidColorBrush(ColourOf(item.Pattern)), new Rect(x0, y + 8, Math.Max(2, x1 - x0), h - 16));
        }

        void DrawClip(DrawingContext ctx, Rect r, ClipItem item)
        {
            var spec = item.Spec;
            var colour = ColourOf(spec.Pattern);
            var fill = new SolidColorBrush(colour);
            var highlight = new SolidColorBrush(Blend(colour, Colors.White, 0.5));
            var shadow = new SolidColorBrush(Blend(colour, Colors.Black, 0.66));
            double w = r.Width, h = r.Height;
            bool isSelected = selected != null && selected.Equals(item);
            using var clip = ctx.PushClip(r.Inflate(1));
            using var at = ctx.PushTransform(Matrix.CreateTranslation(r.X, r.Y));
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, w + 1, h + 1));
            if (h > 1) ctx.FillRectangle(fill, new Rect(1, 1, w - 1, h - 1));
            ctx.FillRectangle(highlight, new Rect(1, 1, w - 2, 1));
            if (h > 3) ctx.FillRectangle(highlight, new Rect(1, 2, 1, h - 3));
            ctx.FillRectangle(shadow, new Rect(2, h - 1, w - 2, 1));
            if (h > 3) ctx.FillRectangle(shadow, new Rect(w - 1, 2, 1, h - 3));

            // The previews and the loop's repeats, a part at a time: a repeat starts with a dashed line.
            var segments = item.Actual;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                double sx = (s.Time - item.Time) * geo.TickWidth, sw = s.Length * geo.TickWidth;
                if (r.X + sx > Bounds.Width || r.X + sx + sw < 0) continue;
                if (i > 0) ctx.DrawLine(new Pen(shadow, 1, DashStyle.Dash), new Point(sx, 1), new Point(sx, h - 1));
                DrawPreview(ctx, spec.Pattern, s, sx, sw, h);
            }

            // The name, the loop and the shared badge, in a strip above the preview.
            var textBrush = Look.BlockText ?? Brushes.Black;
            string name = spec.Pattern.Name + (segments.Count > 1 ? $" ×{segments.Count}" : "") + (spec.Gain < 1.0 ? $" {Math.Round(spec.Gain * 100)}%" : "");
            double badges = 0;
            int n = UsesOf(spec.Pattern);
            if (n > 1) badges += DrawBadge(ctx, w - 4 - badges, 3, $"{n}", Look.Param, textBrush);
            if (spec.Loop) badges += DrawBadge(ctx, w - 4 - badges, 3, "loop", Look.Note, textBrush);
            ctx.DrawRectangle(Look.NameBack, Look.NameBackPen, new Rect(4.5, 2.5, Math.Max(0, w - 8 - badges), 15));
            ctx.DrawText(Text(name, textBrush, 12, maxWidth: Math.Max(1, w - 12 - badges)), new Point(6, 1));

            if (isSelected) ctx.DrawRectangle(null, new Pen(Look.SelectionBorder.Brush, 2), new Rect(1, 1, Math.Max(1, w - 1), Math.Max(1, h - 1)));
        }

        /// <summary>A small pill at the right of a clip's name strip; returns its width with its gap.</summary>
        static double DrawBadge(DrawingContext ctx, double right, double top, string text, IBrush back, IBrush fore)
        {
            var ft = Text(text, fore, 10, true);
            double w = ft.Width + 8;
            if (right - w < 14) return 0;
            ctx.DrawRectangle(back, Look.EventBorder, new Rect(right - w + 0.5, top + 0.5, w, 13), 3, 3);
            ctx.DrawText(ft, new Point(right - w + 4.5, top));
            return w + 3;
        }

        void DrawPreview(DrawingContext ctx, IPattern p, ClipSegment s, double sx, double sw, double h)
        {
            if (p.Length <= 0) return;
            if (!previews.TryGetValue(p, out var marks))
            {
                Watch(p);
                previews[p] = marks = PatternPreview.Of(p);
            }
            if (marks.Length == 0) return;
            var area = new Rect(sx + 1, 19, Math.Max(0, sw - 2), Math.Max(0, h - 21));
            using var clip = ctx.PushClip(area);
            double drawWidth = p.Length * geo.TickWidth;
            double drawHeight = area.Height * 0.9, marginTop = area.Height * 0.05;
            double lineWidth = Math.Max(drawWidth / p.Length, 6);
            double shift = -s.Offset * geo.TickWidth;
            foreach (var m in marks)
            {
                double x = area.X + shift + m.X * drawWidth - 1;
                if (x > area.Right || x + Math.Max(lineWidth, m.Length * drawWidth) < area.X) continue;
                double laneHeight, yy;
                if (m.FixedHeight) { laneHeight = 4; yy = marginTop + m.Lane / (double)m.Lanes * drawHeight; }
                else { double lh = drawHeight / m.Lanes; laneHeight = lh * 0.9; yy = marginTop + 0.1 * lh / 2 + m.Lane / (double)m.Lanes * drawHeight; }
                var brush = m.Kind switch { PreviewKind.Off => Look.Off, PreviewKind.Parameter => Look.Param, _ => Look.Note };
                double width = m.Length > 0 ? Math.Max(3, m.Length * drawWidth) : lineWidth;
                ctx.DrawRectangle(brush, Look.EventBorder, new Rect(Math.Round(x) + 0.5, Math.Round(area.Y + yy) + 0.5, width, Math.Max(1, laneHeight)));
            }
        }

        // ------------------------------------------------------------------ the ghost (what a drag would make)

        internal (Rect Rect, string Label)? Ghost
        {
            get
            {
                if (drag is Drag.None or Drag.PendingMove || dragItem == null) return null;
                var row = RowOf(ghostLane ?? dragItem.Sequence);
                if (row == null) return null;
                var r = new Rect(geo.XOf(ghostTime), YOf(row) + ClipInset, Math.Max(SequenceGeometry.MinBlockWidth, ghostLength * geo.TickWidth), row.Height - 2 * ClipInset);
                string label = drag switch
                {
                    Drag.Move => ghostMode switch { ClipEditor.CopyMode.Independent => "copy", ClipEditor.CopyMode.Linked => "linked copy", _ => "" },
                    _ => "",
                };
                return (r, label);
            }
        }

        public double PlayMarkerX => geo.XOf(Song.PlayPosition);

        // ------------------------------------------------------------------ the mouse

        internal string TipAt(Point p)
        {
            var (item, part) = ClipAt(p);
            if (item == null) return SequenceGeometry.InRuler(p) ? $"{BarBeat(geo.TimeAt(p.X))}\nClick: move the play position here" : null;
            var s = item.Spec;
            string text = $"{s.Pattern.Name} · {BarBeat(item.Time)} · {item.End - item.Time} ticks";
            if (UsesOf(s.Pattern) > 1) text += $"\nShared by {UsesOf(s.Pattern)} places: Make unique gives this clip a pattern of its own";
            text += part switch
            {
                BlockPart.LeftEdge => "\nDrag: trim the start",
                BlockPart.RightEdge => s.Loop ? "\nDrag: trim the end, or repeat the loop" : "\nDrag: trim the end",
                _ => "\nDrag: move · Ctrl/Cmd-drag: copy · Alt-drag: linked copy · Shift: no snap",
            };
            return text;
        }

        string shownTip;

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            var pt = e.GetCurrentPoint(this);
            var p = pt.Position;
            var kind = pt.Properties.PointerUpdateKind;
            var mods = e.KeyModifiers;
            e.Handled = true;
            if (drag != Drag.None) return;
            if (OperatingSystem.IsMacOS() && kind == PointerUpdateKind.LeftButtonPressed && mods.HasFlag(KeyModifiers.Control))
                kind = PointerUpdateKind.RightButtonPressed;

            if (kind == PointerUpdateKind.RightButtonPressed)
            {
                var (hit, _) = ClipAt(p);
                if (hit != null)
                {
                    Select(hit);
                    MoveCursor(Snap(geo.TimeAt(p.X), mods.HasFlag(KeyModifiers.Shift)), hit.Sequence);
                }
                else if (LaneRowAt(p.Y) is { } lr) MoveCursor(Snap(geo.TimeAt(p.X), true), lr.Lane);
                return;
            }
            if (kind != PointerUpdateKind.LeftButtonPressed) return;

            if (SequenceGeometry.InRuler(p))
            {
                Song.PlayPosition = state.TimeSignatures.Snap(geo.TimeAt(p.X), int.MaxValue);
                return;
            }
            if (!SequenceGeometry.InTracks(p)) return;

            var (item, part) = ClipAt(p);
            var laneRow = LaneRowAt(p.Y);
            bool fine = mods.HasFlag(KeyModifiers.Shift);
            if (e.ClickCount == 2 && !mods.HasFlag(CommandKey) && !mods.HasFlag(KeyModifiers.Alt))
            {
                if (item != null)
                {
                    Select(item);
                    PatternDoubleClicked?.Invoke(item.Pattern);
                }
                else if (laneRow?.Lane != null)
                {
                    DrawAt(laneRow.Lane, Snap(geo.TimeAt(p.X), fine));
                }
                return;
            }

            pressPoint = p;
            if (item != null)
            {
                Select(item);
                MoveCursor(Snap(geo.TimeAt(p.X), fine), item.Sequence);     // where the click was: what Split cuts at
                dragItem = item;
                ghostTime = item.Time;
                ghostLength = item.End - item.Time;
                ghostLane = item.Sequence;
                ghostMode = ClipEditor.CopyMode.Move;
                grabOffset = geo.TimeAt(p.X) - item.Time;
                drag = part switch { BlockPart.LeftEdge => Drag.TrimLeft, BlockPart.RightEdge => Drag.TrimRight, _ => Drag.PendingMove };
                e.Pointer.Capture(this);
                Overlay.InvalidateVisual();
                return;
            }
            Select(null);
            if (laneRow?.Lane != null) MoveCursor(Snap(geo.TimeAt(p.X), true), laneRow.Lane);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            var mods = e.KeyModifiers;
            if (drag == Drag.None)
            {
                string tip = TipAt(p);
                if (tip != shownTip)
                {
                    shownTip = tip;
                    ToolTip.SetTip(this, tip);
                }
                var (_, part) = ClipAt(p);
                Cursor = part is BlockPart.LeftEdge or BlockPart.RightEdge ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
                return;
            }
            bool fine = mods.HasFlag(KeyModifiers.Shift);
            switch (drag)
            {
                case Drag.PendingMove:
                    if (Math.Abs(p.X - pressPoint.X) < 3 && Math.Abs(p.Y - pressPoint.Y) < 3) return;
                    drag = Drag.Move;
                    goto case Drag.Move;
                case Drag.Move:
                    ghostTime = Snap(geo.TimeAt(p.X) - grabOffset, fine);
                    if (LaneRowAt(p.Y)?.Lane is { } lane && lane.Machine == dragItem.Sequence.Machine) ghostLane = lane;
                    ghostMode = mods.HasFlag(CommandKey) ? ClipEditor.CopyMode.Independent : mods.HasFlag(KeyModifiers.Alt) ? ClipEditor.CopyMode.Linked : ClipEditor.CopyMode.Move;
                    ghostLength = dragItem.Spec.Segments().Sum(s => s.Length);
                    break;
                case Drag.TrimLeft:
                    {
                        int t = Snap(geo.TimeAt(p.X), fine);
                        t = Math.Min(t, dragItem.End - 1);
                        if (t < dragItem.Time) t = Math.Max(t, dragItem.Time - dragItem.Spec.Start);
                        ghostTime = Math.Max(0, t);
                        ghostLength = dragItem.End - ghostTime;
                        break;
                    }
                case Drag.TrimRight:
                    {
                        int t = Math.Max(dragItem.Time + 1, Snap(geo.TimeAt(p.X), fine));
                        ghostTime = dragItem.Time;
                        ghostLength = Math.Min(t - dragItem.Time, dragItem.Spec.MaxLength);
                        break;
                    }
            }
            Overlay.InvalidateVisual();
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            var p = e.GetPosition(this);
            var button = e.InitialPressMouseButton;
            bool macRight = OperatingSystem.IsMacOS() && button == MouseButton.Left && e.KeyModifiers.HasFlag(KeyModifiers.Control) && drag == Drag.None;
            if (drag != Drag.None)
            {
                if (button != MouseButton.Left) return;
                EndDrag(true);
                e.Pointer.Capture(null);
                e.Handled = true;
                return;
            }
            if (button == MouseButton.Right || macRight)
            {
                MenuRequested?.Invoke(p);
                e.Handled = true;
            }
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (drag != Drag.None) EndDrag(false);
        }

        void EndDrag(bool commit)
        {
            var d = drag;
            drag = Drag.None;
            var item = dragItem;
            dragItem = null;
            if (commit && item != null)
            {
                string why = d switch
                {
                    Drag.Move => ghostTime != item.Time || ghostLane != item.Sequence || ghostMode != ClipEditor.CopyMode.Move
                        ? editor.Move(item, ghostLane ?? item.Sequence, ghostTime, ghostMode) : null,
                    Drag.TrimLeft => editor.TrimLeft(item, ghostTime),
                    Drag.TrimRight => editor.TrimRight(item, ghostTime + ghostLength),
                    _ => null,
                };
                SetStatus(why);
                if (why == null && d == Drag.Move)
                {
                    Changed();
                    var moved = ItemsOf(ghostLane ?? item.Sequence).FirstOrDefault(i => i.Time == ghostTime);
                    if (moved != null) Select(moved);
                }
            }
            Changed();
        }

        /// <summary>A double click on an empty place: a pattern and its clip (the session's clip length, shorter where the next clip starts).</summary>
        public void DrawAt(ISequence lane, int time)
        {
            int length = core.SessionLayout?.NewClipLength ?? TicksPerBeat * 4;
            SetStatus(editor.Draw(lane, time, length, out var made));
            Changed();
            if (made != null)
            {
                var item = ItemsOf(lane).FirstOrDefault(i => ReferenceEquals(i.Pattern, made));
                if (item != null) Select(item);
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var p = e.GetPosition(this);
            if (e.KeyModifiers.HasFlag(CommandKey) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                wheelZoom += e.Delta.Y / 2;
                int steps = (int)Math.Truncate(wheelZoom / SequenceGeometry.TickWidthStep);
                if (steps != 0)
                {
                    wheelZoom -= steps * SequenceGeometry.TickWidthStep;
                    SetZoom(geo.TickWidth + steps * SequenceGeometry.TickWidthStep, Math.Max(SequenceGeometry.HeaderWidth, p.X));
                }
            }
            else
            {
                var d = e.Delta;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && d.X == 0) d = new Vector(d.Y, 0);
                ScrollTo(geo.ScrollX - d.X * 50, geo.ScrollY - d.Y * ArrangementLayout.LaneHeight / 2);
            }
            e.Handled = true;
        }

        void OnPinch(object sender, PointerDeltaEventArgs e)
        {
            SetZoom(geo.TickWidth * (1 + e.Delta.X), Math.Max(SequenceGeometry.HeaderWidth, e.GetPosition(this).X));
            e.Handled = true;
        }

        // ------------------------------------------------------------------ the clip commands (menu and keys)

        ClipCopy clipboard;

        public bool HasClipboard => clipboard != null;

        bool Run(string why)
        {
            SetStatus(why);
            Changed();
            return why == null;
        }

        /// <summary>Re-selects the clip that starts at a time on a lane after an edit.</summary>
        void Reselect(ISequence lane, int time)
        {
            var item = ItemsOf(lane).FirstOrDefault(i => i.Time == time) ?? ItemsOf(lane).FirstOrDefault(i => i.Time < time && i.End > time);
            Select(item);
        }

        public bool SplitAtCursor()
        {
            if (selected == null || cursorTime <= selected.Time || cursorTime >= selected.End)
            {
                SetStatus("Put the cursor inside the clip to split it there (click in the lane, or the ruler).");
                return false;
            }
            var lane = selected.Sequence;
            int at = cursorTime;
            bool ok = Run(editor.Split(selected, at));
            if (ok) Reselect(lane, at);
            return ok;
        }

        public bool JoinWithNext()
        {
            if (selected == null) return false;
            var next = ItemsOf(selected.Sequence).FirstOrDefault(i => i.Time == selected.End);
            if (next == null) { SetStatus("There's no clip right after this one."); return false; }
            var lane = selected.Sequence;
            int time = selected.Time;
            bool ok = Run(editor.Join(selected, next));
            if (ok) Reselect(lane, time);
            return ok;
        }

        public bool ToggleLoop()
        {
            if (selected == null) return false;
            var lane = selected.Sequence;
            int time = selected.Time;
            bool ok = Run(editor.SetLoop(selected, !selected.Spec.Loop));
            if (ok) Reselect(lane, time);
            return ok;
        }

        public bool MakeUnique()
        {
            if (selected == null) return false;
            var lane = selected.Sequence;
            int time = selected.Time;
            bool ok = Run(editor.MakeUnique(selected));
            if (ok) Reselect(lane, time);
            return ok;
        }

        public bool DeleteSelected()
        {
            if (selected == null) return false;
            bool ok = Run(editor.Delete(selected));
            if (ok) Select(null);
            return ok;
        }

        public bool DuplicateSelected()
        {
            if (selected == null) return false;
            var lane = selected.Sequence;
            int at = selected.End;
            bool ok = Run(editor.Duplicate(selected));
            if (ok) Reselect(lane, at);
            return ok;
        }

        public bool CopySelected()
        {
            if (selected == null) return false;
            clipboard = editor.CopyOf(selected);
            return true;
        }

        /// <summary>Paste: an independent copy at the cursor, on the cursor's lane (or the clip's machine's first one).</summary>
        public bool PasteAtCursor()
        {
            if (clipboard == null) return false;
            var lane = cursorLane?.Machine == clipboard.Pattern.Machine ? cursorLane : Song.Sequences.FirstOrDefault(s => s.Machine == clipboard.Pattern.Machine);
            if (lane == null) { SetStatus("The clip's machine has no lane."); return false; }
            int at = Snap(cursorTime, false);
            bool ok = Run(editor.Paste(clipboard, lane, at));
            if (ok) Reselect(lane, at);
            return ok;
        }

        /// <summary>Puts an existing pattern at the cursor as a clip (shared with its other places).</summary>
        public bool PlaceAtCursor(IPattern pattern)
        {
            var lane = cursorLane?.Machine == pattern?.Machine ? cursorLane : Song.Sequences.FirstOrDefault(s => s.Machine == pattern?.Machine);
            if (lane == null) { SetStatus("That pattern's machine has no lane."); return false; }
            int at = Snap(cursorTime, false);
            bool ok = Run(editor.Place(lane, pattern, at));
            if (ok) Reselect(lane, at);
            return ok;
        }

        public void ClearSelection()
        {
            if (selected != null) Select(null);
        }

        public bool HandleKey(Key key, KeyModifiers mods)
        {
            bool none = mods == KeyModifiers.None, ctrl = mods == KeyModifiers.Control;
            if (none)
            {
                switch (key)
                {
                    case Key.Delete:
                    case Key.Back: return DeleteSelected();
                    case Key.S: return SplitAtCursor();
                    case Key.J: return JoinWithNext();
                    case Key.L: return ToggleLoop();
                    case Key.U: return MakeUnique();
                    case Key.Return when selected != null: PatternDoubleClicked?.Invoke(selected.Pattern); return true;
                    case Key.Escape: ClearSelection(); return true;
                    case Key.Right: MoveCursor(cursorTime + TicksPerBeat, null); return true;
                    case Key.Left: MoveCursor(cursorTime - TicksPerBeat, null); return true;
                    case Key.Down: return StepLane(1);
                    case Key.Up: return StepLane(-1);
                }
            }
            else if (ctrl)
            {
                switch (key)
                {
                    case Key.D: return DuplicateSelected();
                    case Key.C: return CopySelected();
                    case Key.V: return PasteAtCursor();
                    case Key.X: return CopySelected() && DeleteSelected();
                }
            }
            return false;
        }

        bool StepLane(int delta)
        {
            var lanes = rows.Where(r => r.Lane != null).Select(r => r.Lane).ToList();
            int i = lanes.IndexOf(cursorLane);
            if (lanes.Count == 0) return false;
            cursorLane = lanes[Math.Clamp(i + delta, 0, lanes.Count - 1)];
            if (selected != null) Select(null);
            Overlay.InvalidateVisual();
            SelectionChanged?.Invoke();
            return true;
        }
    }

    /// <summary>The layer above the arrangement: the cursor, a drag's ghost, the loop and song end lines and the play position.</summary>
    internal sealed class ArrangementOverlay : Control
    {
        readonly ArrangementCanvas canvas;

        static SequenceLook Look => StudioTheme.Sequence;

        public ArrangementOverlay(ArrangementCanvas canvas)
        {
            this.canvas = canvas;
            ClipToBounds = true;
        }

        public override void Render(DrawingContext ctx)
        {
            var song = canvas.Song;
            if (song == null) return;
            var geo = canvas.Geometry;
            var size = Bounds.Size;
            double hw = SequenceGeometry.HeaderWidth, rh = SequenceGeometry.RulerHeight;
            var tracks = new Rect(hw, rh, Math.Max(0, size.Width - hw), Math.Max(0, size.Height - rh));
            var timeline = new Rect(hw, 0, Math.Max(0, size.Width - hw), size.Height);

            using (ctx.PushClip(tracks))
            {
                if (canvas.CursorLane != null && canvas.RowOf(canvas.CursorLane) is { } row)
                {
                    double y = canvas.YOf(row);
                    ctx.FillRectangle(canvas.IsActive ? Look.Cursor : Look.CursorInactive, new Rect(tracks.X, y, tracks.Width, row.Height));
                    ctx.FillRectangle(Look.Param, new Rect(Math.Floor(geo.XOf(canvas.CursorTime)), y, 1, row.Height));
                }
                if (canvas.Ghost is { } g)
                {
                    ctx.DrawRectangle(Look.ResizeFill, Look.ResizePen, g.Rect.Deflate(0.5));
                    if (g.Label.Length > 0)
                        ctx.DrawText(new FormattedText(g.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI, Arial")), 12, Look.HeaderText), new Point(g.Rect.X + 4, g.Rect.Y + 2));
                }
            }

            using (ctx.PushClip(timeline))
            {
                if (song.LoopStart != 0) Line(ctx, geo.XOf(song.LoopStart), Look.Loop, size.Height);
                Line(ctx, geo.XOf(song.LoopEnd), Look.Loop, size.Height);
                Line(ctx, geo.XOf(song.SongEnd), Look.SongEnd, size.Height);
                double x = Math.Floor(canvas.PlayMarkerX);
                ctx.FillRectangle(Look.PlayEdge, new Rect(x - 1, 0, 4, size.Height));
                ctx.FillRectangle(Look.Play, new Rect(x, 0, 2, size.Height));
            }
        }

        static void Line(DrawingContext ctx, double x, IBrush brush, double height) => ctx.FillRectangle(brush, new Rect(Math.Floor(x), 0, 1, height));
    }
}
