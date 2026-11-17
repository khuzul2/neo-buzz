// Fork (docs/specs/neo-audio.md, NA2): audio clips on the arrangement. A waveform (from the asset's peak cache, made in the background) drawn along the
// clip's beats, so a warped clip's waveform is stretched with it and a looped or trimmed one shows what it plays; warp markers as ticks; fades, the gain
// line and their handles; the crossfade where a clip's tail runs under the next one. The gestures are in ArrangementCanvas.cs (Drag.FadeIn, FadeOut, Gain,
// Stretch); the edits are ClipEditor's (ArrangementEditing.Audio.cs).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Sequence
{
    internal sealed partial class ArrangementCanvas
    {
        /// <summary>A part of a clip a mouse press can take besides its edges and body.</summary>
        enum AudioHandle { None, FadeIn, FadeOut, Gain }

        const double NameStrip = 19, HandleSize = 8, MinHandleClip = 56;
        /// <summary>The gain line's range: its top is +12 dB, its bottom -24 dB (0 dB is a third of the way down).</summary>
        const double GainTopDb = 12, GainBottomDb = -24;

        readonly Dictionary<IPattern, AudioClipView> views = new();
        ClipItem hover;
        double dragFade, dragDb;

        /// <summary>Whether the item is an audio clip (the editor's word for it).</summary>
        internal bool IsAudioItem(ClipItem item) => item != null && editor.IsAudio(item.Pattern);

        AudioClipView ViewOf(ClipItem item)
        {
            if (!IsAudioItem(item)) return null;
            if (!views.TryGetValue(item.Pattern, out var view))
                views[item.Pattern] = view = core.SessionLayout?.ViewOf(item.Pattern);
            return view;
        }

        /// <summary>The clip's waveform area, inside its rectangle (below the name strip).</summary>
        static Rect WaveArea(double w, double h) => new Rect(1, NameStrip, Math.Max(0, w - 2), Math.Max(0, h - NameStrip - 2));

        double FadeTicks(ClipItem item, bool fadeIn)
        {
            if (drag is Drag.FadeIn && fadeIn && item.Equals(dragItem)) return dragFade;
            if (drag is Drag.FadeOut && !fadeIn && item.Equals(dragItem)) return dragFade;
            var a = ViewOf(item)?.Audio;
            return a == null ? 0 : (fadeIn ? a.FadeIn : a.FadeOut) * TicksPerBeat;
        }

        double GainDbOf(ClipItem item)
        {
            if (drag is Drag.Gain && item.Equals(dragItem)) return dragDb;
            return ViewOf(item)?.Audio.GainDb ?? 0;
        }

        static double YOfDb(Rect area, double db) => area.Y + (GainTopDb - Math.Clamp(db, GainBottomDb, GainTopDb)) / (GainTopDb - GainBottomDb) * area.Height;

        static double DbOfY(Rect area, double y) => Math.Clamp(GainTopDb - (y - area.Y) / Math.Max(1, area.Height) * (GainTopDb - GainBottomDb), GainBottomDb, GainTopDb);

        bool HandlesShown(ClipItem item, double width) => width >= MinHandleClip && (item.Equals(selected) || item.Equals(hover));

        /// <summary>The handle of an audio clip under a point (in the canvas's pixels), or none.</summary>
        AudioHandle HandleAt(ClipItem item, Point p)
        {
            if (!IsAudioItem(item) || ViewOf(item) == null) return AudioHandle.None;
            var r = ClipRect(item);
            if (!HandlesShown(item, r.Width)) return AudioHandle.None;
            var local = new Point(p.X - r.X, p.Y - r.Y);
            var area = WaveArea(r.Width, r.Height);
            double fi = FadeTicks(item, true) * geo.TickWidth, fo = FadeTicks(item, false) * geo.TickWidth;
            var gain = new Rect(r.Width / 2 - HandleSize / 2 - 2, YOfDb(area, GainDbOf(item)) - HandleSize / 2 - 2, HandleSize + 4, HandleSize + 4);
            if (gain.Contains(local)) return AudioHandle.Gain;
            var inRect = new Rect(Math.Clamp(fi, 0, r.Width) - 1, NameStrip + 1, HandleSize + 4, HandleSize + 4);
            var outRect = new Rect(Math.Clamp(r.Width - fo - HandleSize, 0, r.Width) - 3, NameStrip + 1, HandleSize + 4, HandleSize + 4);
            if (inRect.Contains(local)) return AudioHandle.FadeIn;
            if (outRect.Contains(local)) return AudioHandle.FadeOut;
            return AudioHandle.None;
        }

        // ------------------------------------------------------------------ drawing

        /// <summary>One part of a clip's waveform: the peaks along the part's ticks, through the clip's warp (a stretched clip shows stretched audio), times the gain.</summary>
        void DrawWaveform(DrawingContext ctx, AudioClipView view, ClipItem item, ClipSegment s, double sx, double sw, double h, IBrush brush)
        {
            var area = new Rect(sx + 1, NameStrip, Math.Max(0, sw - 2), Math.Max(0, h - NameStrip - 2));
            if (area.Width < 1 || area.Height < 4) return;
            var peaks = view.Asset.PeaksOrBuild(() => Dispatcher.UIThread.Post(InvalidateVisual));
            double mid = area.Y + area.Height / 2, half = area.Height / 2 - 1;
            double gain = Math.Pow(10, GainDbOf(item) / 20);
            double tpb = TicksPerBeat;
            var clipRect = ClipRect(item);
            // Only the columns on screen.
            int first = Math.Max(0, (int)Math.Floor(-clipRect.X - sx)), last = (int)Math.Min(Math.Ceiling(area.Width), Math.Ceiling(Bounds.Width - clipRect.X - sx));
            if (last <= first) return;
            if (peaks == null)
            {
                // Still being made: a flat line where the audio goes.
                ctx.FillRectangle(brush, new Rect(area.X + first, mid, last - first, 1));
                return;
            }
            var mins = new double[last - first];
            var maxs = new double[last - first];
            for (int c = first; c < last; c++)
            {
                double t0 = s.Offset + c / geo.TickWidth, t1 = s.Offset + (c + 1) / geo.TickWidth;
                double a = view.SampleAt(t0 / tpb), b = view.SampleAt(t1 / tpb);
                peaks.Range(a, b, out float lo, out float hi);
                mins[c - first] = Math.Clamp(lo * gain, -1, 1);
                maxs[c - first] = Math.Clamp(hi * gain, -1, 1);
            }
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(area.X + first, mid - maxs[0] * half), true);
                for (int i = 1; i < maxs.Length; i++) g.LineTo(new Point(area.X + first + i, mid - maxs[i] * half));
                for (int i = mins.Length - 1; i >= 0; i--) g.LineTo(new Point(area.X + first + i + 1, mid - Math.Min(mins[i], maxs[i] - 0.01) * half));
                g.EndFigure(true);
            }
            ctx.DrawGeometry(brush, null, geometry);
        }

        /// <summary>Warp markers as ticks along the top of the waveform.</summary>
        void DrawWarpMarkers(DrawingContext ctx, AudioClipView view, ClipItem item, double h, IBrush brush)
        {
            if ((view.Audio.Markers?.Count ?? 0) < 2) return;
            foreach (double beat in view.MarkerBeats())
            {
                double tick = beat * TicksPerBeat;
                foreach (var s in item.Actual)
                {
                    if (tick < s.Offset || tick >= s.Offset + s.Length) continue;
                    double x = (s.Time - item.Time + (tick - s.Offset)) * geo.TickWidth;
                    ctx.DrawLine(new Pen(brush, 1), new Point(x, NameStrip), new Point(x, NameStrip + 7));
                    ctx.FillRectangle(brush, new Rect(x - 1.5, NameStrip, 4, 3));
                }
            }
        }

        static double Smooth(double x, bool sine) => sine ? Math.Sin(x * Math.PI / 2) : x;

        /// <summary>The fades as lines from the corners, the gain line and the handles (of a clip selected or under the mouse).</summary>
        void DrawAudioDecor(DrawingContext ctx, AudioClipView view, ClipItem item, double w, double h, IBrush shadow)
        {
            var area = WaveArea(w, h);
            if (area.Width < 4 || area.Height < 4) return;
            var line = new Pen(Brushes.White, 1.2);
            var dim = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            double fi = Math.Min(FadeTicks(item, true) * geo.TickWidth, area.Width), fo = Math.Min(FadeTicks(item, false) * geo.TickWidth, area.Width);
            bool cross = view.Audio.CrossIn;
            if (fi > 0.5)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(area.X, area.Y), true);
                    int steps = cross ? 12 : 1;
                    for (int i = 0; i <= steps; i++)
                    {
                        double x = i / (double)steps;
                        c.LineTo(new Point(area.X + x * fi, area.Bottom - Smooth(x, cross) * area.Height));
                    }
                    c.LineTo(new Point(area.X + fi, area.Y));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(dim, null, g);
                ctx.DrawGeometry(null, line, StrokeOf(area, fi, cross));
            }
            if (fo > 0.5)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    c.BeginFigure(new Point(area.Right - fo, area.Y), true);
                    c.LineTo(new Point(area.Right, area.Bottom));
                    c.LineTo(new Point(area.Right, area.Y));
                    c.EndFigure(true);
                }
                ctx.DrawGeometry(dim, null, g);
                ctx.DrawLine(line, new Point(area.Right - fo, area.Y), new Point(area.Right, area.Bottom));
            }
            if (!HandlesShown(item, w)) return;
            double db = GainDbOf(item);
            double gy = YOfDb(area, db);
            var gainPen = new Pen(db == 0 ? new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)) : Brushes.White, 1, DashStyle.Dash);
            ctx.DrawLine(gainPen, new Point(area.X, gy), new Point(area.Right, gy));
            var handle = Brushes.White;
            ctx.DrawRectangle(handle, new Pen(Brushes.Black, 1), new Rect(w / 2 - HandleSize / 2, gy - HandleSize / 2, HandleSize, HandleSize));
            ctx.DrawText(Text($"{db:+0.0;-0.0;0.0} dB", Look.BlockText ?? Brushes.Black, 10), new Point(w / 2 + HandleSize, Math.Clamp(gy - 6, NameStrip, h - 14)));
            ctx.DrawRectangle(handle, new Pen(Brushes.Black, 1), new Rect(Math.Clamp(fi, 0, w) , NameStrip + 2, HandleSize, HandleSize));
            ctx.DrawRectangle(handle, new Pen(Brushes.Black, 1), new Rect(Math.Clamp(w - fo - HandleSize, 0, w), NameStrip + 2, HandleSize, HandleSize));
        }

        static StreamGeometry StrokeOf(Rect area, double fi, bool cross)
        {
            var g = new StreamGeometry();
            using var c = g.Open();
            int steps = cross ? 12 : 1;
            c.BeginFigure(new Point(area.X, area.Bottom), false);
            for (int i = 1; i <= steps; i++)
            {
                double x = i / (double)steps;
                c.LineTo(new Point(area.X + x * fi, area.Bottom - Smooth(x, cross) * area.Height));
            }
            c.EndFigure(false);
            return g;
        }

        /// <summary>
        /// Where a clip's tail runs under the clip that starts where it ends (a crossfade): the two curves crossing, over the later clip's first beats.
        /// Drawn after the clips of the lane, since the later clip is over it.
        /// </summary>
        void DrawCrossfades(DrawingContext ctx, ArrangementRow row)
        {
            double y = YOf(row) + ClipInset;
            foreach (var item in ItemsOf(row.Lane))
            {
                if (!IsAudioItem(item) || ViewOf(item) is not { } view || view.Audio.Tail <= 0) continue;
                double x0 = geo.XOf(item.End), width = view.Audio.Tail * TicksPerBeat * geo.TickWidth;
                if (width < 2 || x0 > Bounds.Width || x0 + width < 0) continue;
                var area = new Rect(x0, y + NameStrip, width, Math.Max(0, row.Height - 2 * ClipInset - NameStrip - 2));
                ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), area);
                var pen = new Pen(Brushes.White, 1.3);
                Point? a = null, b = null;
                for (int i = 0; i <= 12; i++)
                {
                    double t = i / 12.0;
                    var pa = new Point(area.X + t * area.Width, area.Bottom - Math.Cos(t * Math.PI / 2) * area.Height);
                    var pb = new Point(area.X + t * area.Width, area.Bottom - Math.Sin(t * Math.PI / 2) * area.Height);
                    if (a != null) { ctx.DrawLine(pen, a.Value, pa); ctx.DrawLine(pen, b.Value, pb); }
                    a = pa;
                    b = pb;
                }
            }
        }

        // ------------------------------------------------------------------ the audio gestures' words

        string AudioTip(ClipItem item, BlockPart part, Point p)
        {
            var view = ViewOf(item);
            if (view == null) return null;
            var handle = HandleAt(item, p);
            string seconds = $"{view.Source.Seconds:0.0} s audio";
            string text = handle switch
            {
                AudioHandle.FadeIn => "Drag: fade in" + (view.Audio.CrossIn ? " (a crossfade: the clip before it fades out as long)" : ""),
                AudioHandle.FadeOut => "Drag: fade out",
                AudioHandle.Gain => $"Drag up or down: gain ({view.Audio.GainDb:+0.0;-0.0;0.0} dB) · Shift: fine",
                _ => part switch
                {
                    BlockPart.LeftEdge => "Drag: trim the start (the audio stays in time)",
                    BlockPart.RightEdge => item.Spec.Loop ? "Drag: trim the end, or repeat the loop" : "Drag: trim the end · Alt-drag: stretch (warp the audio to fill)",
                    _ => "Drag: move (to another audio track too) · Alt-drag: copy · Shift: no snap\nCtrl+E: split at the playhead · Ctrl+J: consolidate · R: reverse",
                },
            };
            // The name on a line of its own: a short clip's label is cut to a few letters, and here it is whole.
            return $"{item.Pattern.Name}\n{BarBeat(item.Time)} · {item.End - item.Time} ticks · {view.Audio.Warp} · {seconds}\n{text}";
        }

        void BeginAudioHandleDrag(ClipItem item, AudioHandle handle, Point p, PointerPressedEventArgs e)
        {
            Select(item);
            dragItem = item;
            ghostTime = item.Time;
            ghostLength = item.End - item.Time;
            ghostLane = item.Sequence;
            ghostMode = ClipEditor.CopyMode.Move;
            pressPoint = p;
            switch (handle)
            {
                case AudioHandle.FadeIn: drag = Drag.FadeIn; dragFade = FadeTicks(item, true); break;
                case AudioHandle.FadeOut: drag = Drag.FadeOut; dragFade = FadeTicks(item, false); break;
                default: drag = Drag.Gain; dragDb = GainDbOf(item); break;
            }
            e.Pointer.Capture(this);
            InvalidateVisual();
        }

        /// <summary>A handle's drag moved: the fade length or the gain follows the pointer.</summary>
        void MoveAudioHandle(Point p, bool fine)
        {
            if (dragItem == null) return;
            var r = ClipRect(dragItem);
            int length = Math.Max(1, dragItem.Spec.Length);
            switch (drag)
            {
                case Drag.FadeIn:
                    dragFade = Math.Clamp(Math.Round((p.X - r.X) / geo.TickWidth), 0, length);
                    break;
                case Drag.FadeOut:
                    dragFade = Math.Clamp(Math.Round((r.Right - p.X) / geo.TickWidth), 0, length);
                    break;
                case Drag.Gain:
                    var area = WaveArea(r.Width, r.Height);
                    double db = DbOfY(area, p.Y - r.Y);
                    dragDb = fine ? Math.Round(db * 10) / 10 : Math.Round(db * 2) / 2;
                    break;
            }
            InvalidateVisual();
        }

        // ------------------------------------------------------------------ commands the view and the keys run

        public bool IsAudioSelected => IsAudioItem(selected);

        /// <summary>Split (Ctrl+E): the audio clip at the play position, or the selected one when the play position is inside it. False when no audio clip is concerned (the key then does its other job).</summary>
        public bool SplitAtPlayhead()
        {
            int at = Song.PlayPosition;
            ClipItem target = null;
            if (IsAudioSelected)
            {
                if (at > selected.Time && at < selected.End) target = selected;
                else
                {
                    SetStatus("Put the play position inside the clip to split it there.");
                    return true;
                }
            }
            else
            {
                var lane = cursorLane;
                if (lane != null) target = ItemsOf(lane).FirstOrDefault(i => IsAudioItem(i) && i.Time < at && at < i.End);
                if (target == null) return false;
            }
            var seq = target.Sequence;
            bool ok = Run(editor.Split(target, at));
            if (ok) Reselect(seq, at);
            return true;
        }

        /// <summary>Consolidate (Ctrl+J): the selected audio clip, rendered into a new asset and put back as a plain clip. False for no audio clip.</summary>
        public bool ConsolidateSelected()
        {
            if (!IsAudioSelected) return false;
            var lane = selected.Sequence;
            int time = selected.Time;
            Run(editor.Consolidate(lane, selected.Time, selected.End, out _));
            Reselect(lane, time);
            return true;
        }

        public bool ReverseSelected()
        {
            if (!IsAudioSelected) return false;
            var lane = selected.Sequence;
            int time = selected.Time;
            bool ok = Run(editor.Reverse(selected));
            if (ok) Reselect(lane, time);
            return ok;
        }

        /// <summary>The selected audio clip's gain, in dB (the menu's reset).</summary>
        public bool ResetGain()
        {
            if (!IsAudioSelected) return false;
            var lane = selected.Sequence;
            int time = selected.Time;
            bool ok = Run(editor.SetGain(selected, 0));
            if (ok) Reselect(lane, time);
            return ok;
        }
    }
}
