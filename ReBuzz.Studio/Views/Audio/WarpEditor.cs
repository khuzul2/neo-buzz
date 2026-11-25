// Fork (docs/specs/neo-audio.md, NA3 and WM1): the audio clip page's waveform, which is also the warp marker editor (a port of the Session Clip Player's WarpMarkerWindow,
// grown into a clip editor like a modern DAW's). The audio is drawn along the clip's beats as it plays (through the warp map, times the gain, backwards when reversed), with
// the beat and bar grid, the transients AudioAnalysis found, the clip's region (start, loop start and end) and the playing position. A warp marker pins a moment of the audio
// to a beat; the audio between two markers is stretched to fit.
//   Transients: hover one and a ghost marker shows; drag it to make a real marker there and warp, in one gesture.
//   Markers: click selects, Shift- or Ctrl/Cmd-click extends; dragging empty waveform selects a time range (and the markers in it); Ctrl/Cmd+A selects all, Esc clears.
//   Dragging a selected marker moves the whole selection, keeping its spacing; the audio before and after stretches. Alt (Option) + drag moves the marker along the audio, keeping its beat.
//   Double-click: add a marker (on a transient when one is near) or remove the one under it. Del removes the selected ones. The right-click menu and the keys below are the page's
//   other edits: each is one request, which the page hands to SessionLayout.WarpEdit (the call the agent's audio_warp_markers makes), so each is one undo step.
//   Wheel or pinch: zoom around the pointer; Shift+wheel (or a sideways wheel): scroll; middle-button drag: scroll.

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
using ReBuzz.Studio.Shell.Commands;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Audio
{
    internal sealed class WarpEditor : Avalonia.Controls.Control
    {
        const double Grab = 6;
        const double TopBand = 18;
        const double BottomBand = 16;
        const double MinGap = WarpMath.MinGap;
        const double DragStart = 3;

        static readonly IBrush Back = SessionPalette.Empty;
        static readonly IBrush WaveFill = SessionPalette.Fader;
        static readonly IBrush BarBrush = SessionPalette.DimText;
        static readonly IBrush BeatBrush = SessionPalette.Header;
        static readonly IBrush MarkerBrush = SessionPalette.Selection;
        static readonly IBrush TransientBrush = SessionPalette.DimText;
        static readonly IBrush EndBrush = SessionPalette.Learn;
        static readonly IBrush Shade = StudioTokens.Themed(() => Avalonia.Media.Color.FromArgb(NeoBuzz.Editor.Tokens.IsLight ? (byte)0x55 : (byte)0x88, 0x00, 0x00, 0x00));
        static readonly IBrush RangeFill = StudioTokens.Themed(() => Avalonia.Media.Color.FromArgb(NeoBuzz.Editor.Tokens.IsLight ? (byte)0x30 : (byte)0x40, 0x40, 0x80, 0xff));
        static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI, Arial"));
        static readonly CultureInfo Culture = CultureInfo.InvariantCulture;
        static readonly Cursor Arrow = new Cursor(StandardCursorType.Arrow), SizeWE = new Cursor(StandardCursorType.SizeWestEast), Hand = new Cursor(StandardCursorType.Hand);

        /// <summary>The region's handles at the bottom: where the clip starts, and where its loop starts and ends.</summary>
        internal enum Handle { None, Start, LoopStart, LoopEnd }

        enum Gesture { None, Marker, Ghost, Range, Handle, Pan }

        /// <summary>The key that moves a marker along the audio: Option on a Mac, Alt elsewhere.</summary>
        internal static string AltName => KeyText.IsMac ? "Option" : "Alt";

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

        // The selection: markers by their audio sample (they survive a reload), the one clicked last, and a range of the clip's beats.
        readonly HashSet<double> selection = new();
        double primary = double.NaN;
        (double A, double B)? range;

        // What is under the pointer.
        double ghost = double.NaN;             // the sample of the transient a ghost marker shows on
        double hoverX = double.NaN;
        string hint = "";

        // The gesture in progress.
        Gesture gesture;
        bool moved, collapseOnRelease, toggleOffOnRelease;
        double grabSample;
        double pressX, panFrom, offsetFrom;
        List<(double Sample, double Beat)> startMarkers;
        double grabBeat, rangeFrom;
        double ghostSample;
        Handle dragHandle;
        (int Start, int LoopStart, int LoopEnd) dragRegion;
        double[] mins = Array.Empty<double>(), maxs = Array.Empty<double>();
        ContextMenu menu;
        double menuX = double.NaN;

        public WarpEditor()
        {
            Focusable = true;
            ClipToBounds = true;
            Cursor = Arrow;
            Avalonia.Automation.AutomationProperties.SetName(this, "Waveform");
            ToolTip.SetTip(this,
                "The audio along the clip's beats.\n" +
                "Click a marker to select it (Shift- or " + (KeyText.IsMac ? "⌘" : "Ctrl") + "-click adds); drag empty waveform to select a time range; " + (KeyText.IsMac ? "⌘" : "Ctrl") + "+A selects all, Esc clears\n" +
                "Drag a marker: warp (the selection moves together; snaps to the beat grid; Shift: free) · " + AltName + "+drag: move it along the audio, keeping its beat\n" +
                "Hover a transient and drag: make a marker there and warp · double-click: add or remove a marker · Del: remove the selected ones\n" +
                "Wheel or pinch: zoom · Shift+wheel: scroll · right-click: more");
            PointerTouchPadGestureMagnify += OnPinch;
            SetHint();
        }

        /// <summary>A gesture that changed the markers: the new list (fewer than two is straight). Raised once, when the gesture ends.</summary>
        public event Action<IReadOnlyList<(double Sample, double Beat)>> MarkersCommitted;

        /// <summary>A gesture that moved the region's handles (ticks into the clip; null leaves one as it is). Raised when it ends.</summary>
        public event Action<int?, int?, int?> RegionCommitted;

        /// <summary>An edit the page makes through SessionLayout.WarpEdit (and a name for its undo step).</summary>
        public event Action<WarpRequest, string> ActionRequested;

        /// <summary>The selection (markers or range) changed.</summary>
        public event Action SelectionChanged;

        /// <summary>What the pointer is over and what a gesture there does (the page's status line).</summary>
        public event Action<string> HintChanged;

        // ------------------------------------------------------------------ the data

        public bool HasClip => view != null;
        public IReadOnlyList<(double Sample, double Beat)> Markers => markers;
        /// <summary>The marker clicked last, or -1.</summary>
        public int Selected => IndexOfSample(primary);
        /// <summary>The selected markers, as numbers in beat order.</summary>
        public IReadOnlyList<int> SelectedIndices => markers.Select((m, i) => (m, i)).Where(t => selection.Contains(t.m.Sample)).Select(t => t.i).ToList();
        /// <summary>The time range selected, in beats of the clip as shown (null for none).</summary>
        public (double From, double To)? Range => range is { } r ? (r.A, r.B) : null;
        public bool IsDragging => gesture is Gesture.Marker or Gesture.Handle or Gesture.Range or Gesture.Ghost;
        public double PixelsPerBeat => pxPerBeat;
        public double ClipBeats => map == null || source == null ? 0 : Math.Max(map.BeatAt(source.Length), patternTicks / (double)ticksPerBeat);
        /// <summary>Whether the view follows the playing position.</summary>
        public bool Follow { get; set; }
        public string Hint => hint;
        /// <summary>The sample of the transient a ghost marker shows on now, or NaN.</summary>
        public double Ghost => ghost;
        /// <summary>The context menu (built when it is opened).</summary>
        internal ContextMenu LastMenu => menu;

        /// <summary>The page's Auto-warp to grid (the waveform's right-click menu offers it): auto-warp, then every transient pulled to the grid.</summary>
        public Action ToGridRequested { get; set; }

        /// <summary>The selected marker latest in the clip (what the segment tempos are shown for when the one clicked last is gone), or NaN.</summary>
        double LastSelected() => markers.Where(m => selection.Contains(m.Sample)).Select(m => m.Sample).DefaultIfEmpty(double.NaN).Last();

        int IndexOfSample(double sample) => double.IsNaN(sample) ? -1 : markers.FindIndex(m => Math.Abs(m.Sample - sample) < 0.5);

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
                selection.Clear();
                primary = double.NaN;
                range = null;
                InvalidateVisual();
                return;
            }
            source = clip.Source;
            transients = found ?? Array.Empty<double>();
            reverse = clip.Reverse;
            gain = clip.Audio.Gain;
            warpOff = clip.Audio.Warp == "Off";
            markers = (clip.Audio.Markers ?? new()).Count >= 2 ? clip.Audio.Markers.OrderBy(m => m.Beat).ToList() : new();
            map = MapNow();
            baseBeat = map.BeatAt(source.Length);
            selection.RemoveWhere(s => IndexOfSample(s) < 0);
            if (IndexOfSample(primary) < 0) primary = LastSelected();
            if (!fitted && Bounds.Width > 0) Fit();
            InvalidateVisual();
        }

        WarpMap MapNow() => markers.Count >= 2
            ? new WarpMap(markers.Select(m => new WarpMarker(m.Sample, m.Beat)))
            : WarpMap.Straight(source.Length, source.Seconds * Math.Max(1, view.Audio.Bpm) / 60);

        /// <summary>The markers as the clip plays them: its own, or the two ends of its straight map.</summary>
        List<(double Sample, double Beat)> Effective() =>
            markers.Count >= 2 ? markers.ToList() : WarpMath.Effective(null, source.Length, source.SampleRate, view.Audio.Bpm);

        public void Fit()
        {
            if (source == null || Bounds.Width <= 0) return;
            fitted = true;
            pxPerBeat = Math.Clamp((Bounds.Width - 24) / Math.Max(1, ClipBeats), 2, 4000);
            offset = -12;
            InvalidateVisual();
        }

        /// <summary>The beat of the clip playing now (NaN when it isn't). With Follow on, the view scrolls to keep it in sight.</summary>
        public void ShowPlayhead(double beat)
        {
            if (beat.Equals(playhead) || double.IsNaN(beat) && double.IsNaN(playhead)) return;
            playhead = beat;
            if (Follow && !double.IsNaN(beat) && Bounds.Width > 0 && gesture == Gesture.None)
            {
                double x = X(beat);
                if (x < 20 || x > Bounds.Width - 40) offset = beat * pxPerBeat - Bounds.Width * 0.2;
            }
            InvalidateVisual();
        }

        // ------------------------------------------------------------------ selection

        void Changed()
        {
            InvalidateVisual();
            SelectionChanged?.Invoke();
        }

        public void SelectIndices(IEnumerable<int> indices)
        {
            selection.Clear();
            foreach (int i in indices) if (i >= 0 && i < markers.Count) selection.Add(markers[i].Sample);
            primary = LastSelected();
            Changed();
        }

        public void SelectAll()
        {
            selection.Clear();
            foreach (var m in markers) selection.Add(m.Sample);
            if (markers.Count > 0) primary = markers[^1].Sample;
            range = null;
            Changed();
        }

        public void ClearSelection()
        {
            if (selection.Count == 0 && range == null) return;
            selection.Clear();
            primary = double.NaN;
            range = null;
            Changed();
        }

        /// <summary>Selects a time range (beats of the clip as shown) and the markers inside it.</summary>
        public void SelectRange(double from, double to, bool extend = false)
        {
            if (map == null) return;
            range = (Math.Min(from, to), Math.Max(from, to));
            if (!extend) selection.Clear();
            foreach (var m in markers)
            {
                double b = Display(m.Beat);
                if (b >= range.Value.A - 1e-9 && b <= range.Value.B + 1e-9) selection.Add(m.Sample);
            }
            primary = LastSelected();
            Changed();
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

        /// <summary>Where a transient is drawn.</summary>
        public double TransientX(double sample) => X(Display(map.BeatAt(sample)));

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

        /// <summary>The transient within a grab's width of an x, when no marker is there: the one a ghost marker shows on. NaN for none.</summary>
        double TransientNear(double x)
        {
            if (source == null || warpOff || map == null || MarkerAtX(x) >= 0) return double.NaN;
            double best = double.NaN, bestD = Grab + 1e-9;
            foreach (double t in transients)
            {
                double d = Math.Abs(TransientX(t) - x);
                if (d <= bestD && t > 0 && t < source.Length - 1) { best = t; bestD = d; }
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

            // The selected time range, under the audio.
            if (range is { } rg)
            {
                double x0 = Math.Max(0, X(rg.A)), x1 = Math.Min(w, X(rg.B));
                if (x1 > x0) ctx.FillRectangle(RangeFill, new Rect(x0, TopBand, x1 - x0, waveBottom - TopBand));
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
                double x = TransientX(t);
                if (x < -1 || x > w + 1) continue;
                ctx.DrawLine(transientPen, new Point(x, waveBottom - 8), new Point(x, waveBottom));
            }

            // The audio's end: the clip's.
            double endX = X(Display(map.BeatAt(source.Length)));
            ctx.DrawLine(new Pen(EndBrush, 1, DashStyle.Dash), new Point(endX, waveTop), new Point(endX, waveBottom));

            // A ghost marker on the transient under the pointer: where a drag would make one.
            if (!double.IsNaN(ghost))
            {
                double x = TransientX(ghost);
                var ghostPen = new Pen(MarkerBrush, 1.5, DashStyle.Dash) { };
                ctx.DrawLine(ghostPen, new Point(x, TopBand), new Point(x, waveBottom));
                var gg = new StreamGeometry();
                using (var c = gg.Open())
                {
                    c.BeginFigure(new Point(x - 5, 2), true);
                    c.LineTo(new Point(x + 5, 2));
                    c.LineTo(new Point(x, TopBand - 1));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(null, new Pen(MarkerBrush, 1.5), gg);
            }

            // Warp markers: a line and a handle in the top band.
            var markerPen = new Pen(MarkerBrush, 1.5);
            for (int i = 0; i < markers.Count; i++)
            {
                double x = MarkerX(i);
                if (x < -Grab || x > w + Grab) continue;
                bool on = selection.Contains(markers[i].Sample);
                if (on) ctx.DrawLine(new Pen(new SolidColorBrush(Colors.White, 0.35), 8), new Point(x, TopBand), new Point(x, waveBottom));   // a halo: the selection shows at a glance
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

            // The tempo of the segments either side of the marker clicked last.
            int p = Selected;
            if (p >= 0 && markers.Count >= 2)
            {
                double x = MarkerX(p);
                double rate = source.SampleRate;
                double after = WarpMath.SegmentTempo(markers, p, rate), before = WarpMath.SegmentTempo(markers, p - 1, rate);
                // A reversed clip shows its segments the other way round.
                double left = reverse ? after : before, right = reverse ? before : after;
                if (!double.IsNaN(left)) DrawTempo(ctx, left.ToString("0.##", Culture) + " BPM ◂", x - 6, waveTop + 2, true);
                if (!double.IsNaN(right)) DrawTempo(ctx, "▸ " + right.ToString("0.##", Culture) + " BPM", x + 6, waveTop + 2, false);
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
            else if (markers.Count < 2 && selection.Count == 0) DrawText(ctx, "Straight at the clip's tempo · double-click, or drag a transient, to add a warp marker", 8, waveTop + 4, 11, SessionPalette.DimText);
            if (reverse) DrawText(ctx, "reversed", Math.Max(8, w - 62), waveTop + 4, 11, SessionPalette.DimText);
        }

        static void DrawTempo(DrawingContext ctx, string text, double x, double y, bool rightAligned)
        {
            var f = new FormattedText(text, Culture, FlowDirection.LeftToRight, Face, 11, StudioTokens.TextPrimaryBrush);
            double left = rightAligned ? x - f.Width : x;
            ctx.FillRectangle(StudioTokens.SurfacePanelBrush, new Rect(left - 3, y - 1, f.Width + 6, f.Height + 2), 3);
            ctx.DrawText(f, new Point(left, y));
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

        // ------------------------------------------------------------------ requests

        void Ask(WarpRequest request) => ActionRequested?.Invoke(request, request.Label);

        /// <summary>The sample the pointer is at (on a transient when one is within a grab's width), for "here".</summary>
        double HereSample(out bool ok)
        {
            ok = true;
            double x = !double.IsNaN(menuX) ? menuX : hoverX;
            if (double.IsNaN(x))
            {
                int p = Selected;
                if (p >= 0) return markers[p].Sample;
                ok = false;
                return 0;
            }
            int hit = MarkerAtX(x);
            if (hit >= 0) return markers[hit].Sample;
            double near = double.NaN, bestD = Grab;
            foreach (double t in transients)
            {
                double d = Math.Abs(TransientX(t) - x);
                if (d <= bestD) { near = t; bestD = d; }
            }
            return !double.IsNaN(near) ? near : Math.Clamp(SampleAt(BeatAt(x)), 0, source.Length);
        }

        /// <summary>Adds a marker at the pointer's x, on the nearest transient when one is close, where the audio is.</summary>
        public bool AddMarkerAt(double x)
        {
            if (source == null || warpOff) return false;
            double near = TransientNear(x);
            double sample = !double.IsNaN(near) ? near : Math.Clamp(SampleAt(BeatAt(x)), 0, source.Length);
            Ask(new WarpRequest { Action = WarpRequest.Add, Sample = sample });
            return true;
        }

        public bool RemoveMarker(int index)
        {
            if (index < 0 || index >= markers.Count) return false;
            Ask(new WarpRequest { Action = WarpRequest.Delete, Markers = new[] { index } });
            return true;
        }

        /// <summary>Deletes the selected markers (the page's Delete).</summary>
        public void DeleteSelected()
        {
            var sel = SelectedIndices;
            if (sel.Count > 0) Ask(new WarpRequest { Action = WarpRequest.Delete, Markers = sel.ToArray() });
        }

        public void SetBarOneHere()
        {
            double s = HereSample(out bool ok);
            if (!ok) { SetHint("Point at the moment first, then set bar 1 there."); return; }
            Ask(new WarpRequest { Action = WarpRequest.SetBarOne, Sample = s });
        }

        public void WarpStraightHere()
        {
            double s = HereSample(out bool ok);
            if (!ok) { SetHint("Point at the moment first, then warp from there."); return; }
            Ask(new WarpRequest { Action = WarpRequest.WarpStraight, Sample = s });
        }

        /// <summary>Warp from here, following the tempo changes: auto-warp of the audio from this moment on (the markers before it stay).</summary>
        public void WarpFollowHere()
        {
            double s = HereSample(out bool ok);
            if (!ok) { SetHint("Point at the moment first, then warp from there."); return; }
            Ask(new WarpRequest { Action = WarpRequest.WarpFollow, Sample = s });
        }

        /// <summary>The range the next edit is about: the selected time range, else the span of the selected markers (two or more), else none (the clip's own region, or the whole clip).</summary>
        void FillRange(WarpRequest r)
        {
            if (range is { } rg) { r.FromBeat = rg.A; r.ToBeat = rg.B; return; }
            var sel = SelectedIndices;
            if (sel.Count >= 2) { r.FromSample = markers[sel[0]].Sample; r.ToSample = markers[sel[^1]].Sample; }
        }

        public void WarpAsLoop()
        {
            var r = new WarpRequest { Action = WarpRequest.WarpLoop };
            FillRange(r);
            Ask(r);
        }

        /// <summary>The number of bars Warp as a loop would make of the selection or region now, for the menu's words.</summary>
        int LoopBarsNow()
        {
            double a, b;
            if (range is { } rg) { a = rg.A; b = rg.B; }
            else
            {
                var sel = SelectedIndices;
                if (sel.Count >= 2) { a = Display(markers[sel[0]].Beat); b = Display(markers[sel[^1]].Beat); if (a > b) (a, b) = (b, a); }
                else
                {
                    int end = region.LoopEnd > 0 && region.LoopEnd < patternTicks ? region.LoopEnd : patternTicks;
                    a = region.Start / (double)ticksPerBeat; b = end / (double)ticksPerBeat;
                }
            }
            return WarpMath.NearestLoopBars((b - a) / beatsPerBar);
        }

        public void InsertAtTransients()
        {
            var r = new WarpRequest { Action = WarpRequest.InsertAtTransients };
            FillRange(r);
            Ask(r);
        }

        public void InsertEveryBar()
        {
            var r = new WarpRequest { Action = WarpRequest.InsertEveryBar };
            FillRange(r);
            Ask(r);
        }

        public void RemoveAll() => Ask(new WarpRequest { Action = WarpRequest.RemoveAll });

        /// <summary>Quantize: the selected markers, and the markers (or transients) in the selected range, toward the nearest line of the grid shown (the page's strength).</summary>
        public void Quantize()
        {
            var r = new WarpRequest { Action = WarpRequest.Quantize, Markers = SelectedIndices.ToArray(), GridBeats = SnapStep };
            if (range is { } rg) { r.FromBeat = rg.A; r.ToBeat = rg.B; }
            Ask(r);
        }

        // ------------------------------------------------------------------ the context menu

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            bool any = source != null && !warpOff;
            var sel = SelectedIndices;
            bool deletable = sel.Any(i => i > 0 && i < markers.Count - 1);
            bool hasRange = range != null || sel.Count > 0;
            MenuItem Item(string header, string keys, Action run, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = keys == null ? null : KeyText.Parse(keys) };
                item.Click += (s, e) => run();
                return item;
            }
            m.Items.Add(Item("Set bar 1 here", "Cmd+B", SetBarOneHere, any && !reverse));
            m.Items.Add(Item("Warp from here", null, WarpFollowHere, any && !reverse));
            m.Items.Add(Item("Warp from here, straight", null, WarpStraightHere, any && !reverse));
            m.Items.Add(Item("Auto-warp to grid", null, () => ToGridRequested?.Invoke(), any && !reverse && ToGridRequested != null));
            m.Items.Add(Item($"Warp as a loop of {(any ? LoopBarsNow() : 1)} bar{(any && LoopBarsNow() == 1 ? "" : "s")}", "Cmd+L", WarpAsLoop, any));
            m.Items.Add(new Separator());
            m.Items.Add(Item(hasRange ? "Insert markers at the transients in the selection" : "Insert markers at the transients", "Cmd+T", InsertAtTransients, any));
            m.Items.Add(Item("Insert a marker on every bar", "Shift+Cmd+B", InsertEveryBar, any));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Delete the selected markers", "Delete", DeleteSelected, deletable));
            m.Items.Add(Item("Remove all markers (straight)", null, RemoveAll, any && markers.Count >= 2));
            m.Items.Add(new Separator());
            m.Items.Add(Item("Quantize to the grid", "Cmd+U", Quantize, any && hasRange));
            return m;
        }

        void OpenMenu(double x)
        {
            menuX = x;
            menu = BuildMenu();
            menu.Closed += (s, e) => menuX = double.NaN;
            menu.Open(this);
        }

        // ------------------------------------------------------------------ the hint

        void SetHint(string text = null)
        {
            text ??= "Drag: select a time range · wheel: zoom · Shift+wheel: scroll · double-click: add a marker";
            if (text == hint) return;
            hint = text;
            HintChanged?.Invoke(hint);
        }

        string HintFor(Point p, KeyModifiers mods)
        {
            if (source == null) return "";
            if (HandleAtPoint(p) != Handle.None) return "Drag: where the clip starts, or its loop starts and ends";
            int hit = MarkerAtX(p.X);
            string cmd = KeyText.IsMac ? "⌘" : "Ctrl";
            if (hit >= 0)
                return mods.HasFlag(KeyModifiers.Alt)
                    ? $"{AltName}+drag: move the marker along the audio, keeping its beat (Shift: no snap to transients)"
                    : $"Drag: warp (Shift: no snap) · {AltName}+drag: move it along the audio, keeping its beat · Shift- or {cmd}-click: add to the selection · Del: remove";
            if (!double.IsNaN(TransientNear(p.X))) return "Drag: make a marker on this transient and warp (Shift: no snap)";
            return range != null || selection.Count > 0 ? $"Right-click: quantize, loop, bars… · {cmd}+U quantize · Esc: clear the selection" : "Drag: select a time range · double-click: add a marker · wheel: zoom · Shift+wheel: scroll";
        }

        // ------------------------------------------------------------------ the mouse and keys

        static bool Extends(KeyModifiers m) => m.HasFlag(KeyModifiers.Shift) || m.HasFlag(KeyModifiers.Control) || m.HasFlag(KeyModifiers.Meta);

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            Focus();
            if (source == null) return;
            var pt = e.GetCurrentPoint(this);
            var p = pt.Position;
            hoverX = p.X;
            if (pt.Properties.IsMiddleButtonPressed)
            {
                gesture = Gesture.Pan;
                panFrom = p.X;
                offsetFrom = offset;
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
            if (pt.Properties.IsRightButtonPressed)
            {
                int hitRight = MarkerAtX(p.X);
                // A marker clicked with the right button is selected (unless it already is part of the selection), then the menu opens.
                if (hitRight >= 0 && !selection.Contains(markers[hitRight].Sample))
                {
                    selection.Clear();
                    selection.Add(markers[hitRight].Sample);
                    primary = markers[hitRight].Sample;
                    Changed();
                }
                OpenMenu(p.X);
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
            pressX = p.X;
            collapseOnRelease = false;
            toggleOffOnRelease = false;
            bool extend = Extends(e.KeyModifiers);
            if (handle != Handle.None)
            {
                gesture = Gesture.Handle;
                dragHandle = handle;
                dragRegion = (region.Start, region.LoopStart, region.LoopEnd);
            }
            else if (hit >= 0)
            {
                double s = markers[hit].Sample;
                bool wasIn = selection.Contains(s);
                grabSample = s;
                if (extend)
                {
                    // Shift- or Ctrl/Cmd-click adds a marker, and takes a selected one out, but only when it is a click: a drag (Shift: no snap) moves the selection.
                    if (wasIn) toggleOffOnRelease = true;
                    else { selection.Add(s); primary = s; }
                }
                else
                {
                    if (!wasIn) { selection.Clear(); selection.Add(s); }
                    else collapseOnRelease = selection.Count > 1;        // a click on a selected marker of several: select it alone, unless it is dragged
                    primary = s;
                    range = null;
                }
                Changed();
                gesture = Gesture.Marker;
                startMarkers = markers.ToList();
                grabBeat = markers[hit].Beat;
            }
            else
            {
                double near = TransientNear(p.X);
                if (!extend) { selection.Clear(); primary = double.NaN; range = null; }
                if (!double.IsNaN(near))
                {
                    gesture = Gesture.Ghost;
                    ghostSample = near;
                }
                else
                {
                    gesture = Gesture.Range;
                    rangeFrom = Snap(BeatAt(p.X), e.KeyModifiers.HasFlag(KeyModifiers.Shift) && !extend);
                }
                Changed();
            }
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var p = e.GetPosition(this);
            hoverX = p.X;
            bool free = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            bool along = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            switch (gesture)
            {
                case Gesture.Ghost:
                    if (Math.Abs(p.X - pressX) < DragStart) return;
                    // The drag begins: the marker is made on the transient (where the audio already is), then it warps with the pointer.
                    {
                        var eff = Effective();
                        var made = WarpMath.Add(eff, ghostSample);
                        if (!made.Ok) { gesture = Gesture.None; e.Pointer.Capture(null); SetHint(made.Error); return; }
                        startMarkers = made.Markers;
                        markers = made.Markers.ToList();
                        int at = markers.FindIndex(m => Math.Abs(m.Sample - ghostSample) < 0.5);
                        selection.Clear();
                        selection.Add(markers[at].Sample);
                        primary = markers[at].Sample;
                        grabBeat = markers[at].Beat;
                        ghost = double.NaN;
                        gesture = Gesture.Marker;
                        map = MapNow();
                        Changed();
                    }
                    goto case Gesture.Marker;
                case Gesture.Marker:
                    if (Math.Abs(p.X - pressX) >= DragStart || moved)
                    {
                        moved = true;
                        collapseOnRelease = false;
                        DragMarkers(p.X, free, along);
                    }
                    break;
                case Gesture.Handle:
                {
                    moved = true;
                    int tick = (int)Math.Round(Snap(BeatAt(p.X), free) * ticksPerBeat);
                    MoveHandle(dragHandle, tick);
                    InvalidateVisual();
                    break;
                }
                case Gesture.Range:
                    if (Math.Abs(p.X - pressX) >= DragStart || moved)
                    {
                        moved = true;
                        SelectRange(rangeFrom, Snap(BeatAt(p.X), free), extend: Extends(e.KeyModifiers));
                    }
                    break;
                case Gesture.Pan:
                    offset = offsetFrom - (p.X - panFrom);
                    InvalidateVisual();
                    break;
                default:
                {
                    double was = ghost;
                    ghost = HandleAtPoint(p) != Handle.None ? double.NaN : TransientNear(p.X);
                    bool over = HandleAtPoint(p) != Handle.None || MarkerAtX(p.X) >= 0 || !double.IsNaN(ghost);
                    Cursor = over ? (along && MarkerAtX(p.X) >= 0 ? Hand : SizeWE) : Arrow;
                    if (!ghost.Equals(was)) InvalidateVisual();
                    SetHint(HintFor(p, e.KeyModifiers));
                    break;
                }
            }
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (gesture != Gesture.None) return;
            hoverX = double.NaN;
            if (!double.IsNaN(ghost)) { ghost = double.NaN; InvalidateVisual(); }
            SetHint();
        }

        /// <summary>A marker drag in progress: from the markers as they were when it began, so it never accumulates (the same arithmetic as the model's).</summary>
        void DragMarkers(double x, bool free, bool along)
        {
            int p = startMarkers.FindIndex(m => Math.Abs(m.Sample - primary) < 0.5);
            if (p < 0) return;
            WarpResult r;
            if (along)
            {
                // The marker takes the audio under the pointer (a nearby transient when not Shift) and keeps its beat.
                var startMap = new WarpMap(startMarkers.Select(m => new WarpMarker(m.Sample, m.Beat)));
                double sample = startMap.SampleAt(AudioBeat(BeatAt(x)));
                if (!free)
                {
                    double best = double.NaN, bestD = Grab / Math.Max(1e-9, pxPerBeat) * startMap.SamplesPerBeat(startMarkers[p].Beat);
                    foreach (double t in transients)
                    {
                        double d = Math.Abs(t - sample);
                        if (d <= bestD) { best = t; bestD = d; }
                    }
                    if (!double.IsNaN(best)) sample = best;
                }
                r = WarpMath.MoveAlong(startMarkers, p, sample);
            }
            else
            {
                double target = AudioBeat(Snap(BeatAt(x), free));
                var sel = startMarkers.Select((m, i) => (m, i)).Where(t => selection.Contains(t.m.Sample)).Select(t => t.i).ToList();
                r = WarpMath.Move(startMarkers, sel, target - startMarkers[p].Beat);
            }
            if (r.Ok)
            {
                markers = r.Markers;
                map = MapNow();
                SetHint(along ? $"Moving along the audio ({AltName})" : "");
            }
            else SetHint(r.Error);
            InvalidateVisual();
            SelectionChanged?.Invoke();
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
            if (e.InitialPressMouseButton != MouseButton.Left && e.InitialPressMouseButton != MouseButton.Middle) return;
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
            var was = gesture;
            bool didMove = moved, collapse = collapseOnRelease, toggle = toggleOffOnRelease;
            var which = dragHandle;
            var r = dragRegion;
            gesture = Gesture.None;
            dragHandle = Handle.None;
            moved = false;
            collapseOnRelease = false;
            toggleOffOnRelease = false;
            if (was == Gesture.None || was == Gesture.Pan) return;
            if (was == Gesture.Marker)
            {
                if (didMove) MarkersCommitted?.Invoke(markers.ToList());
                else if (toggle) { selection.Remove(grabSample); primary = LastSelected(); Changed(); }
                else if (collapse) { selection.Clear(); selection.Add(grabSample); primary = grabSample; Changed(); }
            }
            else if (was == Gesture.Range)
            {
                if (!didMove) { range = null; selection.Clear(); primary = double.NaN; Changed(); }
            }
            else if (was == Gesture.Handle && didMove)
            {
                region = (r.Start, r.LoopStart, r.LoopEnd, region.Loop);
                RegionCommitted?.Invoke(which == Handle.Start ? r.Start : null, which == Handle.LoopStart ? r.LoopStart : null, which == Handle.LoopEnd ? r.LoopEnd : null);
            }
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (source == null) return;
            bool sideways = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) || sideways)
            {
                double d = sideways ? e.Delta.X : e.Delta.Y;
                offset -= d * 60;
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            ZoomAround(e.GetPosition(this).X, e.Delta.Y > 0 ? 1.25 : 0.8);
            e.Handled = true;
        }

        /// <summary>A trackpad pinch zooms around the pointer (a Mac's).</summary>
        void OnPinch(object sender, PointerDeltaEventArgs e)
        {
            if (source == null) return;
            ZoomAround(e.GetPosition(this).X, Math.Max(0.2, 1 + e.Delta.X));
            e.Handled = true;
        }

        void ZoomAround(double x, double factor)
        {
            double beat = BeatAt(x);
            pxPerBeat = Math.Clamp(pxPerBeat * factor, 2, 4000);
            offset = beat * pxPerBeat - x;
            InvalidateVisual();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (source == null) return;
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
            bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if ((e.Key == Key.Delete || e.Key == Key.Back) && !cmd)
            {
                DeleteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && !cmd)
            {
                ClearSelection();
                e.Handled = true;
            }
            else if (cmd && e.Key == Key.A && !shift) { SelectAll(); e.Handled = true; }
            else if (cmd && e.Key == Key.U && !shift) { Quantize(); e.Handled = true; }
            else if (cmd && e.Key == Key.B && !shift) { SetBarOneHere(); e.Handled = true; }
            else if (cmd && e.Key == Key.B && shift) { InsertEveryBar(); e.Handled = true; }
            else if (cmd && e.Key == Key.T && !shift) { InsertAtTransients(); e.Handled = true; }
            else if (cmd && e.Key == Key.L && !shift) { WarpAsLoop(); e.Handled = true; }
        }

        /// <summary>Zooms to a pixels-per-beat (tests, and the page's zoom buttons); the view keeps the clip's start where it was.</summary>
        public void SetZoom(double pixelsPerBeat)
        {
            pxPerBeat = Math.Clamp(pixelsPerBeat, 2, 4000);
            InvalidateVisual();
        }

        /// <summary>Scrolls the view to a beat at its left edge (tests, and the page).</summary>
        public void ScrollTo(double beat)
        {
            offset = beat * pxPerBeat;
            InvalidateVisual();
        }

        /// <summary>The first beat in view.</summary>
        public double LeftBeat => BeatAt(0);

        /// <summary>Zooms around a point of the view (tests drive the wheel and the pinch through the same call).</summary>
        public void Zoom(double x, double factor) => ZoomAround(x, factor);

        /// <summary>Scrolls by pixels (the Shift+wheel's own).</summary>
        public void Scroll(double pixels)
        {
            offset += pixels;
            InvalidateVisual();
        }

        /// <summary>Makes a marker move by the same route a drag takes, for tests that have no mouse (a gesture's end commits once).</summary>
        internal void CommitDragFor(IEnumerable<(double Sample, double Beat)> list) => MarkersCommitted?.Invoke(list.ToList());
    }
}
