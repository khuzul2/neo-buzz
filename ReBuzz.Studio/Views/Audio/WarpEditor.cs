// Fork (docs/specs/neo-audio.md, NA3): the audio clip page's waveform, which is also the warp marker editor (a port of the Session Clip Player's WarpMarkerWindow).
// The audio is drawn along the clip's beats as it plays (through the warp map, times the gain, backwards when reversed), with the beat and bar grid, the
// transients AudioAnalysis found, the clip's region (start, loop start and end) and the playing position. A warp marker pins a moment of the audio to a beat;
// the audio between two markers is stretched to fit. Double-click: add a marker (on the nearest transient when one is close), double-click a marker or Del:
// delete it, drag: warp (snapped to the beat grid, Shift for none). Each gesture ends in one callback, which the page makes one undo step.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Audio
{
    internal sealed class WarpEditor : Avalonia.Controls.Control
    {
        const double Grab = 6;
        const double TopBand = 18;
        const double BottomBand = 16;
        const double MinGap = 1.0 / 64;

        static readonly IBrush Back = SessionPalette.Empty;
        static readonly IBrush WaveFill = SessionPalette.Fader;
        static readonly IBrush BarBrush = SessionPalette.DimText;
        static readonly IBrush BeatBrush = SessionPalette.Header;
        static readonly IBrush MarkerBrush = SessionPalette.Selection;
        static readonly IBrush TransientBrush = SessionPalette.DimText;
        static readonly IBrush EndBrush = SessionPalette.Learn;
        static readonly IBrush Shade = StudioTokens.Themed(() => Avalonia.Media.Color.FromArgb(NeoBuzz.Editor.Tokens.IsLight ? (byte)0x55 : (byte)0x88, 0x00, 0x00, 0x00));
        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI, Arial"));
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        static readonly Cursor Arrow = new Cursor(StandardCursorType.Arrow), SizeWE = new Cursor(StandardCursorType.SizeWestEast);

        /// <summary>The region's handles at the bottom: where the clip starts, and where its loop starts and ends.</summary>
        internal enum Handle { None, Start, LoopStart, LoopEnd }

        AudioClipView view;
        AudioSource source;
        IReadOnlyList<double> transients = Array.Empty<double>();
        List<(double Sample, double Beat)> markers = new();
        WarpMap map;
        bool reverse;
        double baseBeat;                       // the mirror of a reversed clip: the audio's end, in beats, when it was loaded
        double gain = 1;
        bool warpOff;
        int ticksPerBeat = 4, beatsPerBar = 4, patternTicks = 16;
        (int Start, int LoopStart, int LoopEnd, bool Loop) region;
        double pxPerBeat = 40, offset;
        bool fitted;
        double playhead = double.NaN;
        int selected = -1;

        // The gesture in progress.
        int dragMarker = -1;
        Handle dragHandle;
        bool panning, moved;
        double panFrom, offsetFrom;
        (int Start, int LoopStart, int LoopEnd) dragRegion;
        double[] mins = Array.Empty<double>(), maxs = Array.Empty<double>();

        public WarpEditor()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Arrow;
            Avalonia.Automation.AutomationProperties.SetName(this, "Waveform");
            ToolTip.SetTip(this,
                "The audio along the clip's beats.\n" +
                "Double-click: add a warp marker (on a transient when one is near) · double-click a marker or Del: remove it\n" +
                "Drag a marker: warp (snaps to the beat grid; Shift: free) · bottom handles: start and loop · wheel: zoom · drag the background: scroll");
        }

        /// <summary>A gesture that changed the markers: the new list (fewer than two is straight). Raised once, when the gesture ends.</summary>
        public event Action<IReadOnlyList<(double Sample, double Beat)>> MarkersCommitted;

        /// <summary>A gesture that moved the region's handles (ticks into the clip; null leaves one as it is). Raised when it ends.</summary>
        public event Action<int?, int?, int?> RegionCommitted;

        // ------------------------------------------------------------------ the data

        public bool HasClip => view != null;
        public IReadOnlyList<(double Sample, double Beat)> Markers => markers;
        public int Selected => selected;
        public bool IsDragging => dragMarker >= 0 || dragHandle != Handle.None;
        public double PixelsPerBeat => pxPerBeat;
        public double ClipBeats => map == null || source == null ? 0 : Math.Max(map.BeatAt(source.Length), patternTicks / (double)ticksPerBeat);

        public void Load(AudioClipView clip, IReadOnlyList<double> found, int tpb, int bar, int length, (int Start, int LoopStart, int LoopEnd, bool Loop) clipRegion)
        {
            if (IsDragging) return;                  // a gesture in progress isn't disturbed
            view = clip;
            ticksPerBeat = Math.Max(1, tpb);
            beatsPerBar = Math.Max(1, bar);
            patternTicks = Math.Max(1, length);
            region = clipRegion;
            if (clip == null)
            {
                source = null;
                map = null;
                markers = new();
                selected = -1;
                InvalidateVisual();
                return;
            }
            source = clip.Source;
            transients = found ?? Array.Empty<double>();
            reverse = clip.Reverse;
            gain = clip.Audio.Gain;
            warpOff = clip.Audio.Warp == "Off";
            double? keep = selected >= 0 && selected < markers.Count ? markers[selected].Sample : null;
            markers = (clip.Audio.Markers ?? new()).Count >= 2 ? clip.Audio.Markers.OrderBy(m => m.Beat).ToList() : new();
            map = MapNow();
            baseBeat = map.BeatAt(source.Length);
            selected = keep.HasValue ? markers.FindIndex(m => Math.Abs(m.Sample - keep.Value) < 1) : -1;
            if (!fitted && Bounds.Width > 0) Fit();
            InvalidateVisual();
        }

        WarpMap MapNow() => markers.Count >= 2
            ? new WarpMap(markers.Select(m => new WarpMarker(m.Sample, m.Beat)))
            : WarpMap.Straight(source.Length, source.Seconds * Math.Max(1, view.Audio.Bpm) / 60);

        public void Fit()
        {
            if (source == null || Bounds.Width <= 0) return;
            fitted = true;
            pxPerBeat = Math.Clamp((Bounds.Width - 24) / Math.Max(1, ClipBeats), 2, 4000);
            offset = -12;
            InvalidateVisual();
        }

        /// <summary>The beat of the clip playing now (NaN when it isn't).</summary>
        public void ShowPlayhead(double beat)
        {
            if (beat.Equals(playhead) || double.IsNaN(beat) && double.IsNaN(playhead)) return;
            playhead = beat;
            InvalidateVisual();
        }

        // ------------------------------------------------------------------ geometry

        // The display's beat (the clip's time; backwards audio runs the other way) and the audio's own beat (what the markers hold).
        double Display(double audioBeat) => reverse ? baseBeat - audioBeat : audioBeat;
        double AudioBeat(double displayBeat) => reverse ? baseBeat - displayBeat : displayBeat;

        /// <summary>The audio sample heard at a beat of the clip.</summary>
        double SampleAt(double displayBeat) => map.SampleAt(AudioBeat(displayBeat));

        public double X(double beat) => beat * pxPerBeat - offset;
        public double BeatAt(double x) => (x + offset) / pxPerBeat;

        /// <summary>Where a warp marker is drawn (its beat, mirrored when the clip is reversed).</summary>
        public double MarkerX(int index) => X(Display(markers[index].Beat));

        double HandleBeat(Handle h)
        {
            var r = dragHandle != Handle.None ? (dragRegion.Start, dragRegion.LoopStart, dragRegion.LoopEnd) : (region.Start, region.LoopStart, region.LoopEnd);
            int end = r.LoopEnd > 0 && r.LoopEnd < patternTicks ? r.LoopEnd : patternTicks;
            return h switch { Handle.Start => r.Start, Handle.LoopStart => r.LoopStart, _ => end } / (double)ticksPerBeat;
        }

        /// <summary>Where a region handle is drawn.</summary>
        public double HandleX(Handle h) => X(HandleBeat(h));

        /// <summary>The beat grid's step a drag snaps to at this zoom (the least that is at least 12 pixels wide, down to a sixteenth).</summary>
        public double SnapStep => pxPerBeat >= 192 ? 1.0 / 16 : pxPerBeat >= 48 ? 0.25 : pxPerBeat >= 24 ? 0.5 : 1;

        double Snap(double beat, bool free) => free ? beat : Math.Round(beat / SnapStep) * SnapStep;

        bool InBottomBand(double y) => y >= Bounds.Height - BottomBand;

        Handle HandleAtPoint(Point p)
        {
            if (!InBottomBand(p.Y) || source == null) return Handle.None;
            var best = Handle.None;
            double distance = Grab;
            foreach (var h in new[] { Handle.Start, Handle.LoopStart, Handle.LoopEnd })
            {
                if (h == Handle.LoopStart && !region.Loop && region.LoopStart == 0) continue;
                double d = Math.Abs(HandleX(h) - p.X);
                if (d < distance || (best == Handle.None && d <= distance)) { distance = d; best = h; }
            }
            return best;
        }

        int MarkerAtX(double x)
        {
            int best = -1;
            double distance = Grab;
            for (int i = 0; i < markers.Count; i++)
            {
                double d = Math.Abs(MarkerX(i) - x);
                if (d <= distance) { distance = d; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------ drawing

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height;
            ctx.FillRectangle(Back, new Rect(0, 0, w, h));
            if (source == null || map == null || w <= 0 || h <= TopBand + BottomBand + 4)
            {
                if (source == null) DrawText(ctx, "Select an audio clip", 12, 12, 12, SessionPalette.DimText);
                return;
            }
            double waveTop = TopBand, waveBottom = h - BottomBand;

            // Beats and bars, bar numbers along the top.
            int step = pxPerBeat >= 6 ? 1 : pxPerBeat * beatsPerBar >= 6 ? beatsPerBar : beatsPerBar * 4;
            var barPen = new Pen(BarBrush, 1);
            var beatPen = new Pen(BeatBrush, 1);
            for (long b = (long)Math.Floor(BeatAt(0) / step) * step; b <= BeatAt(w); b += step)
            {
                double x = Math.Round(X(b)) + 0.5;
                bool isBar = b % beatsPerBar == 0;
                ctx.DrawLine(isBar ? barPen : beatPen, new Point(x, isBar ? 0 : TopBand), new Point(x, waveBottom));
                if (isBar && pxPerBeat * beatsPerBar >= 24)
                    DrawText(ctx, (b / beatsPerBar + 1).ToString(Culture), x + 3, 1, 10, SessionPalette.DimText);
            }

            // The audio as it plays: each column the extremes of what plays in its beats, through the gain.
            double mid = waveTop + (waveBottom - waveTop) / 2, half = (waveBottom - waveTop) / 2 * 0.92;
            int columns = Math.Max(0, (int)Math.Ceiling(w));
            if (mins.Length < columns) { mins = new double[columns]; maxs = new double[columns]; }
            var peaks = view.Asset.PeaksOrBuild(() => Dispatcher.UIThread.Post(InvalidateVisual));
            if (peaks == null)
            {
                ctx.FillRectangle(WaveFill, new Rect(0, mid, w, 1));
            }
            else
            {
                int first = -1, last = -1;
                for (int x = 0; x < columns; x++)
                {
                    double s0 = SampleAt(BeatAt(x)), s1 = SampleAt(BeatAt(x + 1));
                    if (Math.Max(s0, s1) < 0 || Math.Min(s0, s1) >= source.Length) { maxs[x] = double.NaN; continue; }
                    peaks.Range(s0, s1, out float lo, out float hi);
                    mins[x] = Math.Clamp(lo * gain, -1, 1);
                    maxs[x] = Math.Clamp(hi * gain, -1, 1);
                    if (first < 0) first = x;
                    last = x;
                }
                if (first >= 0)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        // Runs of columns that hold audio, each a closed shape.
                        int x = first;
                        while (x <= last)
                        {
                            if (double.IsNaN(maxs[x])) { x++; continue; }
                            int end = x;
                            while (end + 1 <= last && !double.IsNaN(maxs[end + 1])) end++;
                            c.BeginFigure(new Point(x, mid - maxs[x] * half), true);
                            for (int i = x + 1; i <= end; i++) c.LineTo(new Point(i, mid - maxs[i] * half));
                            for (int i = end; i >= x; i--) c.LineTo(new Point(i + 1, mid - Math.Min(mins[i], maxs[i] - 0.01) * half));
                            c.EndFigure(true);
                            x = end + 1;
                        }
                    }
                    ctx.DrawGeometry(WaveFill, null, g);
                }
            }

            // The region: what lies outside the start and the loop's end is shaded.
            double xs = X(HandleBeat(Handle.Start)), xe = X(HandleBeat(Handle.LoopEnd));
            if (xs > 0) ctx.FillRectangle(Shade, new Rect(0, waveTop, Math.Min(w, xs), waveBottom - waveTop));
            if (xe < w) ctx.FillRectangle(Shade, new Rect(Math.Max(0, xe), waveTop, w - Math.Max(0, xe), waveBottom - waveTop));

            // Transients (where Beats mode cuts), above the bottom band.
            var transientPen = new Pen(TransientBrush, 1);
            foreach (double t in transients)
            {
                double x = X(Display(map.BeatAt(t)));
                if (x < -1 || x > w + 1) continue;
                ctx.DrawLine(transientPen, new Point(x, waveBottom - 8), new Point(x, waveBottom));
            }

            // The audio's end: the clip's.
            double endX = X(Display(map.BeatAt(source.Length)));
            ctx.DrawLine(new Pen(EndBrush, 1, DashStyle.Dash), new Point(endX, waveTop), new Point(endX, waveBottom));

            // Warp markers: a line and a handle in the top band.
            var markerPen = new Pen(MarkerBrush, 1.5);
            for (int i = 0; i < markers.Count; i++)
            {
                double x = MarkerX(i);
                if (x < -Grab || x > w + Grab) continue;
                bool on = i == selected;
                ctx.DrawLine(on ? new Pen(MarkerBrush, 2.5) : markerPen, new Point(x, TopBand), new Point(x, waveBottom));
                var handle = new StreamGeometry();
                using (var c = handle.Open())
                {
                    c.BeginFigure(new Point(x - 5, 2), true);
                    c.LineTo(new Point(x + 5, 2));
                    c.LineTo(new Point(x, TopBand - 1));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(MarkerBrush, on ? new Pen(StudioTokens.TextPrimaryBrush, 1) : null, handle);
            }

            // The region's handles: the start, and the loop's brackets.
            DrawHandle(ctx, Handle.Start, h);
            if (region.Loop || region.LoopStart > 0 || dragHandle == Handle.LoopStart) DrawHandle(ctx, Handle.LoopStart, h);
            DrawHandle(ctx, Handle.LoopEnd, h);

            if (!double.IsNaN(playhead))
            {
                double x = X(playhead);
                ctx.DrawLine(new Pen(StudioTokens.PlayBrush, 1.5), new Point(x, 0), new Point(x, waveBottom));
            }
            if (warpOff) DrawText(ctx, "Warp is off: the audio plays at its own speed, so warp markers don't apply.", 8, waveTop + 4, 11, SessionPalette.DimText);
            else if (markers.Count < 2) DrawText(ctx, "Straight at the clip's tempo · double-click to add a warp marker", 8, waveTop + 4, 11, SessionPalette.DimText);
            if (reverse) DrawText(ctx, "reversed", Math.Max(8, w - 62), waveTop + 4, 11, SessionPalette.DimText);
        }

        void DrawHandle(DrawingContext ctx, Handle which, double h)
        {
            double x = Math.Round(HandleX(which)) + 0.5;
            if (x < -Grab || x > Bounds.Width + Grab) return;
            double top = h - BottomBand;
            var pen = new Pen(StudioTokens.AccentBrush, 1.5);
            ctx.DrawLine(pen, new Point(x, top), new Point(x, h));
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                switch (which)
                {
                    case Handle.Start:           // a flag to the right: where it starts playing
                        c.BeginFigure(new Point(x, top), true);
                        c.LineTo(new Point(x + 8, top + 4));
                        c.LineTo(new Point(x, top + 8));
                        break;
                    case Handle.LoopStart:       // [
                        c.BeginFigure(new Point(x, top + 1), true);
                        c.LineTo(new Point(x + 6, top + 1));
                        c.LineTo(new Point(x + 6, top + 4));
                        c.LineTo(new Point(x + 3, top + 4));
                        c.LineTo(new Point(x + 3, h - 5));
                        c.LineTo(new Point(x + 6, h - 5));
                        c.LineTo(new Point(x + 6, h - 2));
                        c.LineTo(new Point(x, h - 2));
                        break;
                    default:                     // ]
                        c.BeginFigure(new Point(x, top + 1), true);
                        c.LineTo(new Point(x - 6, top + 1));
                        c.LineTo(new Point(x - 6, top + 4));
                        c.LineTo(new Point(x - 3, top + 4));
                        c.LineTo(new Point(x - 3, h - 5));
                        c.LineTo(new Point(x - 6, h - 5));
                        c.LineTo(new Point(x - 6, h - 2));
                        c.LineTo(new Point(x, h - 2));
                        break;
                }
                c.EndFigure(true);
            }
            ctx.DrawGeometry(StudioTokens.AccentBrush, null, g);
        }

        static void DrawText(DrawingContext ctx, string text, double x, double y, double size, IBrush brush) =>
            ctx.DrawText(new FormattedText(text, Culture, FlowDirection.LeftToRight, Face, size, brush), new Point(x, y));

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            base.OnSizeChanged(e);
            if (!fitted) Fit();
        }

        // ------------------------------------------------------------------ the markers' edits

        /// <summary>Markers to start from when there are none: the audio's ends, as it plays now.</summary>
        void EnsureEnds()
        {
            if (markers.Count >= 2) return;
            markers = new() { (0, 0), (source.Length, map.BeatAt(source.Length)) };
        }

        /// <summary>
        /// Adds a marker at a beat of the clip where the audio already is (nothing moves until it is dragged): on the nearest transient when one is
        /// within a grab's width, else where the pointer is. Returns whether a marker was added.
        /// </summary>
        public bool AddMarkerAt(double x)
        {
            if (source == null || warpOff) return false;
            double beat = AudioBeat(BeatAt(x));
            double sample = Math.Clamp(map.SampleAt(beat), 0, source.Length);
            double near = Grab / pxPerBeat;
            double best = double.NaN, bestDistance = double.MaxValue;
            foreach (double t in transients)
            {
                double d = Math.Abs(map.BeatAt(t) - beat);
                if (d <= near && d < bestDistance) { best = t; bestDistance = d; }
            }
            if (!double.IsNaN(best)) sample = best;
            EnsureEnds();
            double at = map.BeatAt(sample);
            if (markers.Any(m => Math.Abs(m.Sample - sample) < 1 || Math.Abs(m.Beat - at) < 1e-6)) return false;
            markers.Add((sample, at));
            markers.Sort((a, b) => a.Beat.CompareTo(b.Beat));
            selected = markers.FindIndex(m => m.Sample == sample);
            map = MapNow();
            InvalidateVisual();
            Commit();
            return true;
        }

        public bool RemoveMarker(int index)
        {
            if (index < 0 || index >= markers.Count) return false;
            markers.RemoveAt(index);
            selected = -1;
            if (markers.Count < 2) markers = new();
            map = MapNow();
            InvalidateVisual();
            Commit();
            return true;
        }

        void MoveMarker(int index, double displayBeat, bool free)
        {
            if (index < 0 || index >= markers.Count) return;
            double beat = Snap(displayBeat, free);
            beat = AudioBeat(beat);
            double lo = index > 0 ? markers[index - 1].Beat + MinGap : double.NegativeInfinity;
            double hi = index + 1 < markers.Count ? markers[index + 1].Beat - MinGap : double.PositiveInfinity;
            markers[index] = (markers[index].Sample, Math.Clamp(beat, lo, hi));
            map = MapNow();
            InvalidateVisual();
        }

        void Commit() => MarkersCommitted?.Invoke(markers.ToList());

        // ------------------------------------------------------------------ the mouse and keys

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            if (source == null) return;
            var pt = e.GetCurrentPoint(this);
            var p = pt.Position;
            if (pt.Properties.IsRightButtonPressed)
            {
                int hitRight = MarkerAtX(p.X);
                if (hitRight >= 0) RemoveMarker(hitRight);
                e.Handled = true;
                return;
            }
            if (!pt.Properties.IsLeftButtonPressed) return;
            var handle = HandleAtPoint(p);
            int hit = handle == Handle.None ? MarkerAtX(p.X) : -1;
            if (e.ClickCount == 2 && handle == Handle.None)
            {
                // Double-click: remove the marker it is on, else add one.
                if (hit >= 0) RemoveMarker(hit);
                else AddMarkerAt(p.X);
                e.Handled = true;
                return;
            }
            moved = false;
            if (handle != Handle.None)
            {
                dragHandle = handle;
                dragRegion = (region.Start, region.LoopStart, region.LoopEnd);
            }
            else if (hit >= 0)
            {
                dragMarker = hit;
                selected = hit;
                InvalidateVisual();
            }
            else
            {
                selected = -1;
                panning = true;
                panFrom = p.X;
                offsetFrom = offset;
                InvalidateVisual();
            }
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            if (dragMarker >= 0)
            {
                moved = true;
                MoveMarker(dragMarker, BeatAt(p.X), e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            }
            else if (dragHandle != Handle.None)
            {
                moved = true;
                bool free = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                int tick = (int)Math.Round(Snap(BeatAt(p.X), free) * ticksPerBeat);
                MoveHandle(dragHandle, tick);
                InvalidateVisual();
            }
            else if (panning)
            {
                offset = offsetFrom - (p.X - panFrom);
                InvalidateVisual();
            }
            else
            {
                Cursor = HandleAtPoint(p) != Handle.None || MarkerAtX(p.X) >= 0 ? SizeWE : Arrow;
            }
        }

        /// <summary>A region handle's drag to a tick of the clip, kept in order and inside the clip.</summary>
        void MoveHandle(Handle which, int tick)
        {
            var r = dragRegion;
            int end = r.LoopEnd > 0 && r.LoopEnd < patternTicks ? r.LoopEnd : patternTicks;
            switch (which)
            {
                case Handle.Start:
                    r.Start = Math.Clamp(tick, 0, end - 1);
                    break;
                case Handle.LoopStart:
                    r.LoopStart = Math.Clamp(tick, 0, end - 1);
                    break;
                default:
                    end = Math.Clamp(tick, Math.Max(r.Start, r.LoopStart) + 1, patternTicks);
                    r.LoopEnd = end >= patternTicks ? 0 : end;
                    break;
            }
            dragRegion = r;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            e.Pointer.Capture(null);
            Finish();
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            Finish();
        }

        void Finish()
        {
            bool marker = dragMarker >= 0, handle = dragHandle != Handle.None, didMove = moved;
            var which = dragHandle;
            var r = dragRegion;
            dragMarker = -1;
            dragHandle = Handle.None;
            panning = false;
            moved = false;
            if (!didMove) return;
            if (marker) Commit();
            else if (handle)
            {
                region = (r.Start, r.LoopStart, r.LoopEnd, region.Loop);
                RegionCommitted?.Invoke(which == Handle.Start ? r.Start : null, which == Handle.LoopStart ? r.LoopStart : null, which == Handle.LoopEnd ? r.LoopEnd : null);
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (source == null) return;
            double x = e.GetPosition(this).X;
            double beat = BeatAt(x);
            pxPerBeat = Math.Clamp(pxPerBeat * (e.Delta.Y > 0 ? 1.25 : 0.8), 2, 4000);
            offset = beat * pxPerBeat - x;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if ((e.Key == Key.Delete || e.Key == Key.Back) && selected >= 0)
            {
                RemoveMarker(selected);
                e.Handled = true;
            }
        }

        /// <summary>Zooms to a pixels-per-beat (tests, and the page's zoom buttons); the view keeps the clip's start where it was.</summary>
        public void SetZoom(double pixelsPerBeat)
        {
            pxPerBeat = Math.Clamp(pixelsPerBeat, 2, 4000);
            InvalidateVisual();
        }
    }
}
