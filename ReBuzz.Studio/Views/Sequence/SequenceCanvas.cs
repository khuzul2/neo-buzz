// Fork (docs/specs/studio-mvp.md S4): the sequence editor's timeline, code-drawn as the WPF
// horizontal editor draws it (ReBuzzGUI/ModernSequenceEditorHorizontal, Default theme): the time
// ruler (TimelineElement), the track headers with mute, solo and disable (TrackHeaderControl),
// a row per sequence with its pattern blocks and their contents (PatternVisual, PatternElement).
// SequenceOverlay draws above it what moves often (cursor, selection, drag ghosts, markers, the
// play position), so playback redraws only that layer. The mouse: click, drag to move, edges to
// resize, rubber-band selection, double click, the ruler, the wheel and pinch for scroll and zoom.

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

namespace ReBuzz.Studio.Views.Sequence
{
    using Control = Avalonia.Controls.Control;

    internal sealed class SequenceCanvas : Control
    {
        /// <summary>Resize snap (HorizontalSequenceEditorSettings.ResizeSnap).</summary>
        public const int ResizeSnap = 16;

        readonly ReBuzzCore core;
        readonly SequenceSongState state;
        readonly SequenceGeometry geo = new SequenceGeometry();
        readonly SequenceSelection selection = new SequenceSelection();
        readonly Dictionary<IPattern, PreviewMark[]> previews = new Dictionary<IPattern, PreviewMark[]>();
        readonly HashSet<IPattern> watchedPatterns = new HashSet<IPattern>();
        readonly Dictionary<IPatternColumn, IPattern> watchedColumns = new Dictionary<IPatternColumn, IPattern>();
        readonly List<ISequence> watchedSequences = new List<ISequence>();
        readonly HashSet<IMachine> watchedMachines = new HashSet<IMachine>();
        ISong song;
        int cursorTime, cursorRow;
        bool active;

        enum Drag { None, PendingMove, Move, ResizeLeft, ResizeRight, Select }
        Drag drag;
        Point pressPoint;
        int dragRow, dragTime, grabOffset, ghostTime, ghostLength;
        SequenceEvent dragEvent;
        double wheelZoom;

        public SequenceCanvas(ReBuzzCore core, SequenceSongState state)
        {
            this.core = core;
            this.state = state;
            Focusable = true;
            ClipToBounds = true;
            geo.TickWidth = state.Zoom;
            Overlay = new SequenceOverlay(this) { IsHitTestVisible = false };
            PointerTouchPadGestureMagnify += OnPinch;
        }

        public SequenceOverlay Overlay { get; }
        public SequenceGeometry Geometry => geo;
        public SequenceSelection Selection => selection;
        public ISong Song => core.Song;
        public int Rows => core.Song.Sequences.Count;
        public int CursorTime => cursorTime;
        public int CursorRow => cursorRow;
        public bool IsActive => active;
        public SongMeter TimeSignatures => state.TimeSignatures;

        /// <summary>Follow the play position while playing (scroll a page when it leaves the view).</summary>
        public bool Follow { get; set; } = true;

        /// <summary>The pattern under the cursor goes to the pattern editor as the cursor moves (AutoSelectPattern, on by default in WPF).</summary>
        public bool AutoSelectPattern { get; set; } = true;

        /// <summary>The edits, on the song's undo history.</summary>
        public SequenceEditing Edits => new SequenceEditing(core.Song, core.SongCore.ActionStack, state.TimeSignatures, state.Clipboard);

        /// <summary>The cursor moved (the pattern list follows its track).</summary>
        public event Action CursorChanged;

        /// <summary>Scroll, zoom or extent changed (the scroll bars and the slider follow).</summary>
        public event Action ViewChanged;

        /// <summary>A double click on a pattern block.</summary>
        public event Action<IPattern> PatternDoubleClicked;

        /// <summary>A right click: the view opens its menu here.</summary>
        public event Action<Point> MenuRequested;

        /// <summary>The track headers or blocks changed (the pattern list may need a refresh).</summary>
        public event Action TracksChanged;

        public ISequence SequenceAt(int row) => row >= 0 && row < Rows ? Song.Sequences[row] : null;

        public ISequence CursorSequence => SequenceAt(cursorRow);

        public int CursorSpan => state.TimeSignatures.GetBarLengthAt(cursorTime);

        /// <summary>A new pattern's length (General settings' PatternLength).</summary>
        public static int NewPatternLength => Math.Max(1, BuzzGUI.Common.Global.GeneralSettings?.PatternLength ?? 16);

        /// <summary>The end of the cursor's bar (Set End marks there).</summary>
        public int CursorBottomTime => cursorTime + CursorSpan;

        /// <summary>The event playing at a time on a row (GetSequenceEventAt): its start and the event.</summary>
        public (int Time, SequenceEvent Event) EventAt(int time, int row)
        {
            var seq = SequenceAt(row);
            if (seq == null) return (-1, null);
            foreach (var e in seq.Events)
                if (time >= e.Key && time < e.Key + SequenceGeometry.SpanOf(e.Value)) return (e.Key, e.Value);
            return (-1, null);
        }

        public IPattern CursorPattern
        {
            get
            {
                var (_, e) = EventAt(cursorTime, cursorRow);
                return e?.Type == SequenceEventType.PlayPattern ? e.Pattern : null;
            }
        }

        #region Engine events

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
            ReBuzz.Core.Session.PatternColors.Changed += OnPatternColourChanged;
            WatchSequences();
            geo.TickWidth = state.Zoom;
            ClampCursor();
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
            UnwatchPatterns();      // patterns are watched again as they are drawn (a closed song's let go)
            selection.Kill();
            ClampCursor();
            Changed();
            CursorChanged?.Invoke();
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

        void OnPatternColourChanged(IPattern p) => OnUi(InvalidateVisual);

        void WatchSequences()
        {
            UnwatchSequences();
            foreach (var s in Song.Sequences)
            {
                watchedSequences.Add(s);
                s.PropertyChanged += OnSequenceChanged;
                s.EventChanged += OnEventChanged;
                s.SpanInserted += OnSpan;
                s.SpanDeleted += OnSpan;
                s.SpanCleared += OnSpan;
                if (s.Machine != null && watchedMachines.Add(s.Machine))
                    s.Machine.PropertyChanged += OnMachineChanged;
            }
        }

        void UnwatchSequences()
        {
            foreach (var s in watchedSequences)
            {
                s.PropertyChanged -= OnSequenceChanged;
                s.EventChanged -= OnEventChanged;
                s.SpanInserted -= OnSpan;
                s.SpanDeleted -= OnSpan;
                s.SpanCleared -= OnSpan;
            }
            watchedSequences.Clear();
            foreach (var m in watchedMachines) m.PropertyChanged -= OnMachineChanged;
            watchedMachines.Clear();
        }

        void OnSequenceChanged(object sender, PropertyChangedEventArgs e) => OnUi(Changed);
        void OnEventChanged(int time) => OnUi(Changed);
        void OnSpan(int time, int span) => OnUi(Changed);

        void OnMachineChanged(object sender, PropertyChangedEventArgs e)
        {
            // What the headers and blocks show (not IsActive, Position…, which change often).
            switch (e.PropertyName)
            {
                case "Name":
                case "IsMuted":
                case "IsSoloed":
                case "Patterns":
                    OnUi(() =>
                    {
                        if (e.PropertyName == "Patterns") previews.Clear();
                        Changed();
                    });
                    break;
            }
        }

        void Watch(IPattern p)
        {
            if (!watchedPatterns.Add(p)) return;
            p.PatternChanged += OnPatternContentChanged;
            p.PropertyChanged += OnPatternPropertyChanged;
            p.ColumnAdded += OnPatternContentChanged;
            p.ColumnRemoved += OnPatternContentChanged;
            foreach (var c in p.Columns)
                if (watchedColumns.TryAdd(c, p)) c.EventsChanged += OnColumnEventsChanged;
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
            foreach (var c in watchedColumns.Keys) c.EventsChanged -= OnColumnEventsChanged;
            watchedColumns.Clear();
            previews.Clear();
        }

        void OnPatternContentChanged(IPatternColumn c) => OnUi(() =>
        {
            // A column came or went: watch the pattern's columns afresh.
            previews.Clear();
            UnwatchPatterns();
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

        void OnColumnEventsChanged(IEnumerable<PatternEvent> events, bool set) => OnUi(() =>
        {
            previews.Clear();
            InvalidateVisual();
        });

        /// <summary>Redraws both layers, and the scroll bars follow the extent.</summary>
        public void Changed()
        {
            ClampScroll();
            InvalidateVisual();
            Overlay.InvalidateVisual();
            ViewChanged?.Invoke();
            TracksChanged?.Invoke();
        }

        #endregion

        #region Cursor, scroll and zoom

        void ClampCursor()
        {
            if (cursorRow >= Rows) cursorRow = Math.Max(0, Rows - 1);
        }

        /// <summary>Moves the cursor to a cell (MoveCursor): snapped to its bar, kept in the tracks, brought into view.</summary>
        public void MoveCursor(int time, int row)
        {
            int t = state.TimeSignatures.Snap(Math.Max(0, time), int.MaxValue);
            int r = Math.Max(0, Math.Min(row, Rows - 1));
            if (t == cursorTime && r == cursorRow) return;
            cursorTime = t;
            cursorRow = r;
            CursorMoved();
        }

        /// <summary>Arrow keys (MoveCursorDelta): by bars and tracks; with <paramref name="select"/> the selection follows.</summary>
        public void MoveCursorDelta(int dx, int dy, int maxx, bool select)
        {
            if (select)
            {
                if (!selection.Selecting && !selection.NotEmpty) selection.Begin(cursorTime, cursorRow);
            }
            else selection.Kill();

            var tsl = state.TimeSignatures;
            if (dx != 0)
            {
                long t = cursorTime + (long)dx * tsl.GetBarLengthAt(cursorTime);
                cursorTime = tsl.Snap(Math.Max(0, t), maxx);
            }
            if (dy != 0)
                cursorRow = (int)Math.Max(0, Math.Min(Rows - 1, cursorRow + (long)dy));
            if (select) selection.Update(cursorTime, cursorRow);
            CursorMoved();
        }

        void CursorMoved()
        {
            BringCursorIntoView();
            Changed();
            CursorChanged?.Invoke();
            var seq = CursorSequence;
            if (seq?.Machine == null) return;
            // UpdateSelectedRow: the pattern under the cursor to the editor; a generator takes MIDI.
            if (AutoSelectPattern && CursorPattern is IPattern p)
            {
                try
                {
                    core.SetPatternEditorPattern(p);
                }
                catch (Exception e)
                {
                    core.DCWriteLine("Sequence editor: " + e.Message);
                }
            }
            var m = seq.Machine;
            if (m.DLL?.Info.Type == MachineType.Generator || m.IsControlMachine)
                core.MIDIFocusMachine = m;
        }

        Size Viewport => new Size(Math.Max(1, Bounds.Width - SequenceGeometry.HeaderWidth), Math.Max(1, Bounds.Height - SequenceGeometry.RulerHeight));

        /// <summary>The scrollable size of the tracks: past the song end (and the cursor and blocks) by half a view.</summary>
        public Size Extent
        {
            get
            {
                int end = Math.Max(Song.SongEnd, CursorBottomTime);
                foreach (var s in Song.Sequences)
                {
                    var last = s.Events.LastOrDefault();
                    if (last.Value != null) end = Math.Max(end, last.Key + SequenceGeometry.SpanOf(last.Value));
                }
                return new Size(end * geo.TickWidth + Viewport.Width / 2, Rows * geo.TrackHeight + 1);
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

        void BringCursorIntoView()
        {
            var view = Viewport;
            double x = cursorTime * geo.TickWidth, w = CursorSpan * geo.TickWidth;
            double y = cursorRow * geo.TrackHeight;
            if (x < geo.ScrollX) geo.ScrollX = x;
            else if (x + w > geo.ScrollX + view.Width) geo.ScrollX = x + w - view.Width;
            if (y < geo.ScrollY) geo.ScrollY = y;
            else if (y + geo.TrackHeight > geo.ScrollY + view.Height) geo.ScrollY = y + geo.TrackHeight - view.Height;
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

        /// <summary>Sets the zoom (pixels per tick), keeping the tick under <paramref name="anchorX"/> (or the view's left edge) in place.</summary>
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

        #endregion

        #region Drawing

        static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-us");
        static readonly Typeface Font = new Typeface(new FontFamily("Segoe UI, Arial"));
        static readonly Typeface BoldFont = new Typeface(new FontFamily("Segoe UI, Arial"), FontStyle.Normal, FontWeight.Bold);

        // The Default theme's SequenceEditor.xaml.
        internal static readonly IBrush BackgroundBrush = Gradient(Color.Parse("#E5E5D7"), Color.Parse("#F7F7F3"), new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative));
        static readonly IBrush TrackViewBrush = Gradient(Color.Parse("#BA9"), Color.Parse("#765"), new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 1, RelativeUnit.Relative));
        static readonly IBrush RulerBrush = new SolidColorBrush(Color.Parse("#BA9"));
        static readonly IBrush HeaderViewBrush = new SolidColorBrush(Color.Parse("#BA9"));
        static readonly IBrush HeaderSelectedBrush = new SolidColorBrush(Color.Parse("#3000"));
        static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.Parse("#8000")), 1);
        static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#8000"));
        static readonly IBrush TrackLineBrush = new SolidColorBrush(Color.Parse("#5000"));    // SeqEdBackgroundMarkerLineBrush #55000000
        static readonly IBrush BarLineBrush = new SolidColorBrush(Color.Parse("#2000"));
        static readonly IBrush NameBackBrush = new SolidColorBrush(Color.Parse("#90e0e0ff"));
        static readonly IPen NameBackPen = new Pen(new SolidColorBrush(Color.Parse("#40333333")), 1);
        static readonly IBrush NoteBrush = Brushes.LightGreen;
        static readonly IBrush OffBrush = Brushes.Gray;
        static readonly IBrush ParamBrush = Brushes.Orange;
        static readonly IPen EventBorderPen = new Pen(Brushes.Black, 1);
        static readonly IBrush MuteOnBrush = new SolidColorBrush(Color.Parse("#E8907A"));
        static readonly IBrush SoloOnBrush = new SolidColorBrush(Color.Parse("#9EE08C"));
        static readonly IBrush ButtonOffBrush = new SolidColorBrush(Color.Parse("#E4E4E4"));
        static readonly IPen ButtonPen = new Pen(new SolidColorBrush(Color.Parse("#808080")), 1);

        static IBrush Gradient(Color a, Color b, RelativePoint start, RelativePoint end) => new LinearGradientBrush
        {
            StartPoint = start,
            EndPoint = end,
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };

        Color Theme(string name)
        {
            var c = core.GetThemeColour(name);
            return Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        static Color Blend(Color a, Color b, double f) => Color.FromArgb(
            (byte)(a.A + (b.A - a.A) * f), (byte)(a.R + (b.R - a.R) * f), (byte)(a.G + (b.G - a.G) * f), (byte)(a.B + (b.B - a.B) * f));

        /// <summary>A block's fill and its 3D edges (BrushSet: the colour blended 50% to white and 66% to black).</summary>
        readonly record struct BoxBrushes(IBrush Fill, IBrush Highlight, IBrush Shadow)
        {
            public static BoxBrushes Of(Color c) => new BoxBrushes(new SolidColorBrush(c), new SolidColorBrush(Blend(c, Colors.White, 0.5)), new SolidColorBrush(Blend(c, Colors.Black, 0.66)));
        }

        public override void Render(DrawingContext ctx)
        {
            var size = Bounds.Size;
            ctx.FillRectangle(BackgroundBrush, new Rect(size));
            if (song == null) return;
            double hw = SequenceGeometry.HeaderWidth, rh = SequenceGeometry.RulerHeight;
            var tracksArea = new Rect(hw, rh, Math.Max(0, size.Width - hw), Math.Max(0, size.Height - rh));
            var rulerArea = new Rect(hw, 0, Math.Max(0, size.Width - hw), rh);
            var headerArea = new Rect(0, rh, hw, Math.Max(0, size.Height - rh));

            var tsl = state.TimeSignatures;
            int firstTick = geo.TimeAt(hw);
            int lastTick = geo.TimeAt(size.Width) + 1;
            var bars = tsl.GetBars(Math.Max(lastTick, 1) + 1).Where(b => b.Start + b.Length >= firstTick).ToList();

            DrawRuler(ctx, rulerArea, bars);
            DrawTracks(ctx, tracksArea, bars);
            DrawHeaders(ctx, headerArea);

            ctx.DrawRectangle(null, BorderPen, rulerArea.Deflate(0.5));
            ctx.DrawRectangle(null, BorderPen, headerArea.Deflate(0.5));
            ctx.DrawRectangle(null, BorderPen, tracksArea.Deflate(0.5));
        }

        /// <summary>The 1-based bar a tick is in, counted over the song's time signatures from its start.</summary>
        internal int BarNumberAt(int tick)
        {
            var tsl = state.TimeSignatures;
            int bar = 1;
            for (int t = 0; t + tsl.GetBarLengthAt(t) <= tick && bar < 1_000_000; t += Math.Max(1, tsl.GetBarLengthAt(t))) bar++;
            return bar;
        }

        /// <summary>The ruler's tooltip at a tick: "Bar 9 · tick 128".</summary>
        internal string RulerTip(int tick) => $"Bar {BarNumberAt(tick)} · tick {tick}\nClick: move the play position here";

        void DrawRuler(DrawingContext ctx, Rect area, List<(int Start, int Length)> bars)
        {
            using var clip = ctx.PushClip(area);
            ctx.FillRectangle(RulerBrush, area);
            var tsl = state.TimeSignatures;
            double lastLabelEnd = double.MinValue;
            // Fork (ux-overhaul.md §2, one time unit): bar numbers from 1, as the transport counts ("9" where it says 9.1.1;
            // the ruler counted ticks, 128). Ticks are in the ruler's tooltip (RulerTip). Bold where the time signature
            // changes; none that would overlap.
            int number = bars.Count > 0 ? BarNumberAt(bars[0].Start) - 1 : 0;      // the bars are consecutive: count on from the first
            foreach (var bar in bars)
            {
                number++;
                double x = geo.XOf(bar.Start);
                if (bar.Start > 0) ctx.FillRectangle(LineBrush, new Rect(Math.Floor(x), 0, 1, area.Height));
                if (x + 3 < lastLabelEnd) continue;
                var ft = new FormattedText(number.ToString(CultureInfo.InvariantCulture), Culture, FlowDirection.LeftToRight,
                    tsl.TimeSignatureChangesAt(bar.Start) ? BoldFont : Font, 12, Brushes.Black);
                ctx.DrawText(ft, new Point(x + 3, 3));
                lastLabelEnd = x + 3 + ft.Width + 4;
            }
            double end = Math.Floor(geo.XOf(Song.SongEnd));
            ctx.FillRectangle(LineBrush, new Rect(end, 0, 1, area.Height));
        }

        void DrawTracks(DrawingContext ctx, Rect area, List<(int Start, int Length)> bars)
        {
            using var clip = ctx.PushClip(area);
            ctx.FillRectangle(TrackViewBrush, area);

            foreach (var bar in bars)
            {
                double x = Math.Floor(geo.XOf(bar.Start));
                if (bar.Start > 0) ctx.FillRectangle(BarLineBrush, new Rect(x, area.Y, 1, area.Height));
            }

            int rows = Rows;
            int first = Math.Max(0, geo.RawRowAt(area.Y));
            int last = Math.Min(rows - 1, geo.RawRowAt(area.Bottom));
            for (int row = first; row <= last; row++)
            {
                var seq = Song.Sequences[row];
                foreach (var e in seq.Events)
                {
                    var r = geo.BlockRect(e.Key, row, e.Value);
                    if (r.Right < area.X || r.X > area.Right) continue;
                    DrawBlock(ctx, r, e.Key, seq, e.Value);
                }
            }

            // Lines between the tracks (VerticalBackgroundMarker).
            for (int row = first; row <= last + 1; row++)
            {
                double y = Math.Floor(geo.YOf(row));
                if (row > 0) ctx.FillRectangle(TrackLineBrush, new Rect(area.X, y, area.Width, 1));
            }
        }

        void DrawBlock(DrawingContext ctx, Rect r, int time, ISequence seq, SequenceEvent e)
        {
            double w = r.Width, h = r.Height;
            using var clip = ctx.PushClip(r);
            using var at = ctx.PushTransform(Matrix.CreateTranslation(r.X, r.Y));

            int kind = e.Type switch
            {
                SequenceEventType.Break => 1,
                SequenceEventType.Mute or SequenceEventType.Thru => 2,
                SequenceEventType.Off => 3,
                _ => 0,
            };
            string text = e.Type switch
            {
                SequenceEventType.PlayPattern => e.Describe(),      // fork (0.5.1): with the part played and the volume
                SequenceEventType.Break => "<break>",
                SequenceEventType.Mute => "<mute>",
                SequenceEventType.Thru => "<thru>",
                _ => "<stop>",
            };
            int ci = kind == 0 && e.Pattern != null ? state.ColourIndexOf(e.Pattern) : -1;
            var palette = SequenceSongState.Palette;
            var colour = ci >= 0 && ci < palette.Count
                ? Color.FromUInt32(palette[ci].Argb)
                : Theme(kind switch { 1 => "SE Break Box", 2 => "SE Mute Box", 3 => "SE Off Box", _ => "SE Pattern Box" });
            var box = BoxBrushes.Of(colour);

            // PatternVisual: border, fill, then the 3D highlight (top, left) and shadow (bottom, right).
            ctx.FillRectangle(Brushes.Black, new Rect(0, 0, w + 1, h + 1));
            if (h > 1) ctx.FillRectangle(box.Fill, new Rect(1, 1, w - 1, h - 1));
            ctx.FillRectangle(box.Highlight, new Rect(1, 1, w - 2, 1));
            if (h > 3) ctx.FillRectangle(box.Highlight, new Rect(1, 2, 1, h - 3));
            ctx.FillRectangle(box.Shadow, new Rect(2, h - 1, w - 2, 1));
            if (h > 3) ctx.FillRectangle(box.Shadow, new Rect(w - 1, 2, 1, h - 3));

            var textBrush = new SolidColorBrush(Theme("SE Text"));
            if (kind == 0)
            {
                DrawPreview(ctx, e, w, h);
                ctx.DrawRectangle(NameBackBrush, NameBackPen, new Rect(4.5, 2.5, Math.Max(0, w - 8), 15));
                var ft = new FormattedText(text, Culture, FlowDirection.LeftToRight, Font, 12, textBrush) { MaxTextWidth = Math.Max(1, w - 10), MaxLineCount = 1, Trimming = TextTrimming.None };
                ctx.DrawText(ft, new Point(6, 1));
            }
            else
            {
                // The other kinds: their name turned upright in a strip on the left.
                ctx.DrawRectangle(NameBackBrush, NameBackPen, new Rect(2.5, 2.5, 16, Math.Max(0, h - 4)));
                var ft = new FormattedText(text, Culture, FlowDirection.LeftToRight, Font, 12, textBrush);
                using (ctx.PushTransform(Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(2 + (16 - ft.Height) / 2, h - 4)))
                    ctx.DrawText(ft, new Point(0, 0));
            }
        }

        /// <summary>The pattern's contents in the block, below the name (PatternElement's event hint canvas, DrawPatternEvents).</summary>
        void DrawPreview(DrawingContext ctx, SequenceEvent e, double w, double h)
        {
            var p = e.Pattern;
            if (p == null || p.Length <= 0) return;
            if (!previews.TryGetValue(p, out var marks))
            {
                Watch(p);
                previews[p] = marks = PatternPreview.Of(p);
            }
            if (marks.Length == 0) return;

            var area = new Rect(1, 17, Math.Max(0, w - 2), Math.Max(0, h - 18));
            using var clip = ctx.PushClip(area);
            double drawWidth = p.Length * geo.TickWidth;
            // WPF sizes the lanes to the whole track height and clips the lowest; here they fit the area below the name.
            double drawHeight = area.Height * 0.9;
            double marginTop = area.Height * 0.05;
            double lineWidth = Math.Max(drawWidth / p.Length, 6);       // PatternEventWidth
            double shift = -e.PlayedOffset * geo.TickWidth;              // fork (0.5.1): an event that starts partway
            foreach (var m in marks)
            {
                double x = area.X + shift + m.X * drawWidth - 1;
                if (x > area.Right || x + Math.Max(lineWidth, m.Length * drawWidth) < area.X) continue;
                double laneHeight, y;
                if (m.FixedHeight)
                {
                    laneHeight = 4;
                    y = marginTop + m.Lane / (double)m.Lanes * drawHeight;
                }
                else
                {
                    double lh = drawHeight / m.Lanes;
                    laneHeight = lh * 0.9;
                    y = marginTop + 0.1 * lh / 2 + m.Lane / (double)m.Lanes * drawHeight;
                }
                var brush = m.Kind switch { PreviewKind.Off => OffBrush, PreviewKind.Parameter => ParamBrush, _ => NoteBrush };
                double width = m.Length > 0 ? Math.Max(3, m.Length * drawWidth) : lineWidth;
                ctx.DrawRectangle(brush, EventBorderPen, new Rect(Math.Round(x) + 0.5, Math.Round(area.Y + y) + 0.5, width, Math.Max(1, laneHeight)));
            }
        }

        void DrawHeaders(DrawingContext ctx, Rect area)
        {
            using var clip = ctx.PushClip(area);
            ctx.FillRectangle(HeaderViewBrush, area);
            int rows = Rows;
            int first = Math.Max(0, geo.RawRowAt(area.Y));
            int last = Math.Min(rows - 1, geo.RawRowAt(area.Bottom));
            for (int row = first; row <= last; row++)
            {
                var seq = Song.Sequences[row];
                double y = geo.YOf(row);
                var r = new Rect(0, y, area.Width, geo.TrackHeight);
                if (row == cursorRow) ctx.FillRectangle(HeaderSelectedBrush, r);
                // The machine's name, centred above the buttons.
                var name = new FormattedText(seq.Machine?.Name ?? "", Culture, FlowDirection.LeftToRight, Font, 12, Brushes.Black)
                { MaxTextWidth = area.Width - 8, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
                ctx.DrawText(name, new Point(4 + (area.Width - 8 - name.Width) / 2, y + 4 + (geo.TrackHeight - SequenceGeometry.HeaderButtonSize - 4 - name.Height) / 2));

                DrawMute(ctx, HeaderButton(row, 0), seq.Machine?.IsMuted == true);
                DrawSolo(ctx, HeaderButton(row, 1), seq.Machine?.IsSoloed == true);
                DrawDisable(ctx, HeaderButton(row, 2), seq.IsDisabled);
                if (row > 0) ctx.FillRectangle(TrackLineBrush, new Rect(0, Math.Floor(y), area.Width, 1));
            }
        }

        /// <summary>A header's M, S or D button (TrackHeaderControl's bottom row: *, 16, 16, 16; 20 pt each, ux-overhaul.md §3.2).</summary>
        public Rect HeaderButton(int row, int index)
        {
            double s = SequenceGeometry.HeaderButtonSize;
            return new Rect(SequenceGeometry.HeaderWidth - 6 - (3 - index) * (s + 2), geo.YOf(row) + geo.TrackHeight - s - 2, s, s);
        }

        /// <summary>The top left of a button's 16-unit icon, centred in the button.</summary>
        static Point IconAt(Rect r) => new Point(r.X + (r.Width - 16) / 2, r.Y + (r.Height - 16) / 2);

        static void ButtonBox(DrawingContext ctx, Rect r, IBrush fill) =>
            ctx.DrawRectangle(fill, ButtonPen, r.Deflate(1.5), 2, 2);

        /// <summary>mute_on/off_icon.png: a speaker, struck through when on.</summary>
        static void DrawMute(DrawingContext ctx, Rect r, bool on)
        {
            ButtonBox(ctx, r, on ? MuteOnBrush : ButtonOffBrush);
            var o = IconAt(r);
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                gc.BeginFigure(new Point(o.X + 4, o.Y + 6.5), true);
                gc.LineTo(new Point(o.X + 6, o.Y + 6.5));
                gc.LineTo(new Point(o.X + 10, o.Y + 3.5));
                gc.LineTo(new Point(o.X + 10, o.Y + 12.5));
                gc.LineTo(new Point(o.X + 6, o.Y + 9.5));
                gc.LineTo(new Point(o.X + 4, o.Y + 9.5));
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(on ? new SolidColorBrush(Color.Parse("#5A3A30")) : new SolidColorBrush(Color.Parse("#404040")), null, g);
            if (on) ctx.DrawLine(new Pen(Brushes.Black, 1.2), new Point(o.X + 4, o.Y + 4), new Point(o.X + 12, o.Y + 12));
        }

        /// <summary>solo_on/off_icon.png: an S, on green when on.</summary>
        static void DrawSolo(DrawingContext ctx, Rect r, bool on)
        {
            ButtonBox(ctx, r, on ? SoloOnBrush : ButtonOffBrush);
            var ft = new FormattedText("S", Culture, FlowDirection.LeftToRight, BoldFont, 11, on ? new SolidColorBrush(Color.Parse("#1F5A18")) : new SolidColorBrush(Color.Parse("#3A3A3A")));
            ctx.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
        }

        /// <summary>The D toggle (DisableColumnButtonStyle: a plain button with "D").</summary>
        static void DrawDisable(DrawingContext ctx, Rect r, bool on)
        {
            ButtonBox(ctx, r, on ? Brushes.DimGray : ButtonOffBrush);
            var ft = new FormattedText("D", Culture, FlowDirection.LeftToRight, Font, 11, on ? Brushes.White : new SolidColorBrush(Color.Parse("#111")));
            ctx.DrawText(ft, new Point(r.X + (r.Width - ft.Width) / 2, r.Y + (r.Height - ft.Height) / 2));
        }

        #endregion

        #region Overlay state

        /// <summary>What the overlay draws for a drag in progress: a block's new place, or a resize's rectangle.</summary>
        internal (Rect Rect, bool Dashed, SequenceEvent Event)? Ghost
        {
            get
            {
                switch (drag)
                {
                    case Drag.Move:
                        return (geo.BlockRect(ghostTime, dragRow, dragEvent), false, dragEvent);
                    case Drag.ResizeLeft:
                    case Drag.ResizeRight:
                        return (new Rect(geo.XOf(ghostTime), geo.YOf(dragRow), ghostLength * geo.TickWidth, geo.TrackHeight), true, null);
                    default:
                        return null;
                }
            }
        }

        /// <summary>The play position's x (tests check it follows playback).</summary>
        public double PlayMarkerX => geo.XOf(Song.PlayPosition);

        internal void DrawGhostBlock(DrawingContext ctx, Rect r, SequenceEvent e)
        {
            using (ctx.PushOpacity(0.6))
                DrawBlock(ctx, r, ghostTime, SequenceAt(dragRow), e);
        }

        #endregion

        #region Mouse

        static KeyModifiers CommandKey => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        static readonly Cursor ResizeCursor = new Cursor(StandardCursorType.SizeWestEast);
        static readonly Cursor MoveCursorShape = new Cursor(StandardCursorType.Hand);

        /// <summary>The block and its part under a point: its row, start, event and part.</summary>
        public (int Row, int Time, SequenceEvent Event, BlockPart Part) BlockAt(Point p)
        {
            if (!SequenceGeometry.InTracks(p)) return (-1, -1, null, BlockPart.None);
            int row = geo.RawRowAt(p.Y);
            var seq = SequenceAt(row);
            if (seq == null) return (-1, -1, null, BlockPart.None);
            // Later blocks are drawn over earlier ones: the last that holds the point.
            foreach (var e in seq.Events.Reverse())
            {
                var r = geo.BlockRect(e.Key, row, e.Value);
                var part = SequenceGeometry.PartAt(r, p, e.Value.Type == SequenceEventType.PlayPattern && e.Value.Pattern != null);
                if (part != BlockPart.None) return (row, e.Key, e.Value, part);
            }
            return (row, -1, null, BlockPart.None);
        }

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
                kind = PointerUpdateKind.RightButtonPressed;      // Control-click is a right click on a Mac

            if (kind == PointerUpdateKind.RightButtonPressed)
            {
                // The cursor goes where the menu is for (the ruler: the time only); the menu opens on release.
                if (SequenceGeometry.InTracks(p)) MoveCursor(geo.TimeAt(p.X), geo.RowAt(p.Y, Rows));
                else if (SequenceGeometry.InHeaders(p)) MoveCursor(cursorTime, geo.RowAt(p.Y, Rows));
                else if (SequenceGeometry.InRuler(p)) MoveCursor(geo.TimeAt(p.X), cursorRow);
                return;
            }
            if (kind != PointerUpdateKind.LeftButtonPressed) return;

            if (SequenceGeometry.InRuler(p))
            {
                // A click on the ruler moves the play position there.
                Song.PlayPosition = state.TimeSignatures.Snap(geo.TimeAt(p.X), int.MaxValue);
                return;
            }
            if (SequenceGeometry.InHeaders(p))
            {
                PressHeader(p, e.ClickCount);
                return;
            }
            if (!SequenceGeometry.InTracks(p) || Rows == 0) return;

            int time = geo.TimeAt(p.X);
            int row = geo.RowAt(p.Y, Rows);
            var (brow, btime, ev, part) = BlockAt(p);

            if (e.ClickCount == 2 && !mods.HasFlag(KeyModifiers.Shift) && !mods.HasFlag(CommandKey))
            {
                // A double click: on a pattern, choose it for editing; on an empty cell, make a pattern there.
                if (ev?.Type == SequenceEventType.PlayPattern && ev.Pattern != null)
                {
                    MoveCursor(btime, brow);
                    PatternDoubleClicked?.Invoke(ev.Pattern);
                }
                else if (ev == null)
                {
                    MoveCursor(time, row);
                    Edits.NewPatternAt(cursorRow, cursorTime, NewPatternLength);
                    CursorMoved();
                }
                return;
            }

            pressPoint = p;
            if (mods.HasFlag(KeyModifiers.Shift))
            {
                // Shift extends the selection from the cursor.
                if (!selection.NotEmpty) selection.Begin(cursorTime, cursorRow);
                selection.Update(time, row);
                drag = Drag.Select;
                e.Pointer.Capture(this);
                Changed();
                return;
            }

            if (ev != null && (part == BlockPart.LeftEdge || part == BlockPart.RightEdge))
            {
                MoveCursor(btime, brow);
                SelectBlock(brow, btime, ev);
                dragRow = brow;
                dragTime = btime;
                dragEvent = ev;
                ghostTime = btime;
                ghostLength = SequenceGeometry.SpanOf(ev);
                drag = part == BlockPart.LeftEdge ? Drag.ResizeLeft : Drag.ResizeRight;
                e.Pointer.Capture(this);
                Changed();
                return;
            }

            if (ev != null)
            {
                // A click on a block puts the cursor on it and selects it; a drag moves it.
                MoveCursor(btime, brow);
                SelectBlock(brow, btime, ev);
                dragRow = brow;
                dragTime = btime;
                dragEvent = ev;
                grabOffset = time - btime;
                ghostTime = btime;
                drag = Drag.PendingMove;
                e.Pointer.Capture(this);
                Changed();
                return;
            }

            // Empty space: the cursor goes there, and a drag selects (the WPF Dragger's selection).
            MoveCursor(time, row);
            selection.Kill();
            selection.Begin(cursorTime, cursorRow);
            drag = Drag.Select;
            e.Pointer.Capture(this);
            Changed();
        }

        /// <summary>The selection is a block's bars on its track.</summary>
        public void SelectBlock(int row, int time, SequenceEvent e)
        {
            selection.Begin(time, row);
            selection.Update(time + SequenceGeometry.SpanOf(e) - 1, row);
            selection.End();
        }

        void PressHeader(Point p, int clicks)
        {
            int row = geo.RawRowAt(p.Y);
            var seq = SequenceAt(row);
            if (seq == null) return;
            for (int i = 0; i < 3; i++)
            {
                if (!HeaderButton(row, i).Contains(p)) continue;
                // M, S and D are the machine's and the sequence's own switches (bound in WPF; not undo steps there either).
                if (i == 0 && seq.Machine != null) seq.Machine.IsMuted ^= true;
                else if (i == 1 && seq.Machine != null) seq.Machine.IsSoloed ^= true;
                else if (i == 2) seq.IsDisabled ^= true;
                Changed();
                return;
            }
            if (clicks == 2)
            {
                seq.Machine?.DoubleClick();      // the machine's window
                return;
            }
            MoveCursor(cursorTime, row);
        }

        /// <summary>What the pointer's tooltip says where it is (ux-overhaul.md P1): the ruler's bar and tick, the M, S, D buttons.</summary>
        internal string TipAt(Point p)
        {
            if (SequenceGeometry.InRuler(p)) return RulerTip(Math.Max(0, geo.TimeAt(p.X)));
            if (!SequenceGeometry.InHeaders(p)) return null;
            int row = geo.RawRowAt(p.Y);
            if (row < 0 || row >= Rows) return null;
            for (int i = 0; i < 3; i++)
                if (HeaderButton(row, i).Contains(p))
                    return i switch
                    {
                        0 => "Mute the track's machine",
                        1 => "Solo the track's machine",
                        _ => "Disable the track: its patterns don't play (the machine still sounds)",
                    };
            return null;
        }

        string shownTip;

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            if (drag == Drag.None)
            {
                string tip = TipAt(p);
                if (tip != shownTip)
                {
                    shownTip = tip;
                    ToolTip.SetTip(this, tip);
                }
            }
            switch (drag)
            {
                case Drag.PendingMove:
                    if (Math.Abs(p.X - pressPoint.X) < 3 && Math.Abs(p.Y - pressPoint.Y) < 3) return;
                    drag = Drag.Move;
                    goto case Drag.Move;
                case Drag.Move:
                    AutoScroll(p);
                    ghostTime = state.TimeSignatures.Snap(Math.Max(0, geo.TimeAt(p.X) - grabOffset), int.MaxValue);
                    Overlay.InvalidateVisual();
                    return;
                case Drag.ResizeRight:
                    AutoScroll(p);
                    ghostTime = dragTime;
                    ghostLength = SequenceGeometry.ResizeRightLength(dragTime, geo.TimeAt(p.X), ResizeSnap);
                    Overlay.InvalidateVisual();
                    return;
                case Drag.ResizeLeft:
                    AutoScroll(p);
                    (ghostTime, ghostLength) = SequenceGeometry.ResizeLeft(dragTime, SequenceGeometry.SpanOf(dragEvent), geo.TimeAt(p.X), ResizeSnap);
                    Overlay.InvalidateVisual();
                    return;
                case Drag.Select:
                    AutoScroll(p);
                    int t = geo.TimeAt(p.X), r = geo.RowAt(p.Y, Rows);
                    if (selection.NotEmpty || Math.Abs(p.X - pressPoint.X) >= 3 || Math.Abs(p.Y - pressPoint.Y) >= 3)
                    {
                        selection.Update(t, r);
                        Overlay.InvalidateVisual();
                    }
                    return;
            }
            // Hovering: the edges of pattern blocks resize.
            var (_, _, ev, part) = BlockAt(p);
            Cursor = part == BlockPart.LeftEdge || part == BlockPart.RightEdge ? ResizeCursor : ev != null ? MoveCursorShape : Cursor.Default;
        }

        /// <summary>A drag past the tracks' edges scrolls them.</summary>
        void AutoScroll(Point p)
        {
            double dx = 0, dy = 0;
            if (p.X < SequenceGeometry.HeaderWidth) dx = -20;
            else if (p.X > Bounds.Width) dx = 20;
            if (p.Y < SequenceGeometry.RulerHeight) dy = -geo.TrackHeight / 4;
            else if (p.Y > Bounds.Height) dy = geo.TrackHeight / 4;
            if (dx != 0 || dy != 0) ScrollTo(geo.ScrollX + dx, geo.ScrollY + dy);
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
                var mods = e.KeyModifiers;
                EndDrag(true, mods.HasFlag(CommandKey) || mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt));
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
            if (drag != Drag.None) EndDrag(false, false);
        }

        void EndDrag(bool commit, bool clone)
        {
            var d = drag;
            drag = Drag.None;
            if (!commit)
            {
                Changed();
                return;
            }
            switch (d)
            {
                case Drag.Move:
                    if (ghostTime != dragTime && Edits.MoveEvent(dragRow, dragTime, ghostTime, clone))
                    {
                        MoveCursor(ghostTime, dragRow);
                        var seq = SequenceAt(dragRow);
                        if (seq != null && seq.Events.TryGetValue(ghostTime, out var moved)) SelectBlock(dragRow, ghostTime, moved);
                    }
                    break;
                case Drag.ResizeRight:
                    Edits.SetPatternLength(dragEvent.Pattern, ghostLength + dragEvent.PlayedOffset);
                    break;
                case Drag.ResizeLeft:
                    Edits.ResizeLeft(dragRow, dragTime, ghostTime, ghostLength + dragEvent.PlayedOffset);
                    MoveCursor(ghostTime, dragRow);
                    break;
                case Drag.Select:
                    selection.End();
                    break;
            }
            dragEvent = null;
            Changed();
            CursorChanged?.Invoke();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            var p = e.GetPosition(this);
            if (e.KeyModifiers.HasFlag(CommandKey) || e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                // Ctrl (Command) + wheel zooms, around the pointer (WPF: zoomSlider += delta / 240, a notch is 120).
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
                // The wheel scrolls the tracks; Shift turns it sideways; a trackpad scrolls both ways.
                var d = e.Delta;
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && d.X == 0) d = new Vector(d.Y, 0);
                ScrollTo(geo.ScrollX - d.X * 50, geo.ScrollY - d.Y * geo.TrackHeight / 2);
            }
            e.Handled = true;
        }

        /// <summary>A trackpad pinch zooms around the pointer.</summary>
        void OnPinch(object sender, PointerDeltaEventArgs e)
        {
            SetZoom(geo.TickWidth * (1 + e.Delta.X), Math.Max(SequenceGeometry.HeaderWidth, e.GetPosition(this).X));
            e.Handled = true;
        }

        #endregion
    }

    /// <summary>
    /// The layer above the timeline: the selection, the cursor, a drag's ghost, the loop and song
    /// end markers and the play position (MarkerControl, CursorElement, SelectionLayer, resizeRect).
    /// </summary>
    internal sealed class SequenceOverlay : Control
    {
        readonly SequenceCanvas canvas;

        static readonly IBrush CursorBrush = new SolidColorBrush(Color.Parse("#2000"));
        static readonly IBrush CursorInactiveBrush = new SolidColorBrush(Color.Parse("#2005"));
        static readonly IBrush SelectionBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse("#100000FF"), 0), new GradientStop(Color.Parse("#200000FF"), 1) },
        };
        static readonly IPen SelectionPen = new Pen(new SolidColorBrush(Color.Parse("#600000FF")), 1);
        static readonly IBrush ResizeFill = new SolidColorBrush(Color.Parse("#33000000"));
        static readonly IPen ResizePen = new Pen(new SolidColorBrush(Color.Parse("#aa000000")), 1, new DashStyle(new double[] { 4, 4 }, 0));
        static readonly IBrush LoopBrush = Brushes.Tomato;
        static readonly IBrush SongEndBrush = Brushes.Black;
        static readonly IBrush PlayBrush = Brushes.Yellow;

        public SequenceOverlay(SequenceCanvas canvas)
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
                var tsl = canvas.TimeSignatures;
                if (canvas.Selection.NotEmpty)
                {
                    var s = canvas.Selection.Rect(tsl);
                    var r = new Rect(geo.XOf(s.Time), geo.YOf(s.Row), s.Span * geo.TickWidth + 1, s.Rows * geo.TrackHeight + 1);
                    ctx.DrawRectangle(SelectionBrush, SelectionPen, r.Deflate(0.5));
                }
                if (canvas.Rows > 0)
                    ctx.FillRectangle(canvas.IsActive ? CursorBrush : CursorInactiveBrush, geo.CellRect(canvas.CursorTime, canvas.CursorRow, canvas.CursorSpan));
                if (canvas.Ghost is { } g)
                {
                    if (g.Dashed) ctx.DrawRectangle(ResizeFill, ResizePen, g.Rect.Deflate(0.5));
                    else canvas.DrawGhostBlock(ctx, g.Rect, g.Event);
                }
            }

            using (ctx.PushClip(timeline))
            {
                // MarkerControl: 1 px lines over the ruler and the tracks; small flags in the ruler mark them.
                if (song.LoopStart != 0) Marker(ctx, geo.XOf(song.LoopStart), LoopBrush, size.Height, +1);
                Marker(ctx, geo.XOf(song.LoopEnd), LoopBrush, size.Height, -1);
                Marker(ctx, geo.XOf(song.SongEnd), SongEndBrush, size.Height, 0);
                Marker(ctx, canvas.PlayMarkerX, PlayBrush, size.Height, 0, flag: false);
            }
        }

        static readonly IBrush PlayEdge = new SolidColorBrush(Color.Parse("#B0000000"));

        static void Marker(DrawingContext ctx, double x, IBrush brush, double height, int side, bool flag = true)
        {
            x = Math.Floor(x);
            if (ReferenceEquals(brush, PlayBrush))
            {
                // Fork (ux-overhaul.md §3.2): the playhead 2 pt wide with a dark edge, readable on any lane (1 px yellow on
                // tan was 2.76:1, gallery mac-071).
                ctx.FillRectangle(PlayEdge, new Rect(x - 1, 0, 4, height));
                ctx.FillRectangle(brush, new Rect(x, 0, 2, height));
            }
            else
            {
                ctx.FillRectangle(brush, new Rect(x, 0, 1, height));
            }
            if (!flag) return;
            var g = new StreamGeometry();
            using (var gc = g.Open())
            {
                double y = SequenceGeometry.RulerHeight;
                if (side == 0)
                {
                    gc.BeginFigure(new Point(x - 4, y - 7), true);
                    gc.LineTo(new Point(x + 5, y - 7));
                    gc.LineTo(new Point(x + 0.5, y - 1));
                }
                else
                {
                    gc.BeginFigure(new Point(x + 0.5, y - 9), true);
                    gc.LineTo(new Point(x + 0.5 + side * 7, y - 5));
                    gc.LineTo(new Point(x + 0.5, y - 1));
                }
                gc.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, g);
        }
    }
}
