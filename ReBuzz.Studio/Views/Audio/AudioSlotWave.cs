// Fork (docs/specs/neo-audio.md, NA3): the mini waveform in an audio clip's Session slot. The peaks come from the asset's PeakCache (the coarse level, a column
// every two pixels), through the clip's warp map and times its gain, drawn once into a geometry that is kept until the slot is resized or the peaks arrive.
// A frame draws that geometry and one line (the playing position), so a grid of many audio slots costs nothing while it plays and allocates nothing.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Audio
{
    internal sealed class AudioSlotWave : Avalonia.Controls.Control
    {
        const double Column = 2;
        const double BottomRoom = 4;             // the slot's progress bar

        readonly AudioClipView view;
        readonly double beats;
        StreamGeometry geometry;
        double builtWidth, builtHeight;
        bool builtWithPeaks;
        bool pending;
        double position = -1;
        IBrush brush = Brushes.White;
        IPen playPen;

        /// <param name="view">The clip as the arrangement draws it (the asset, the warp map, the gain).</param>
        /// <param name="lengthBeats">The slot's clip, in beats: its pattern's length.</param>
        public AudioSlotWave(AudioClipView view, double lengthBeats)
        {
            this.view = view;
            beats = Math.Max(1e-6, lengthBeats);
            IsHitTestVisible = false;
        }

        /// <summary>The colour of the waveform and of the playing position (the slot's text colour, so it reads on any clip colour).</summary>
        public IBrush Foreground
        {
            get => brush;
            set
            {
                if (ReferenceEquals(value, brush) || value == null) return;
                brush = value;
                playPen = null;
                InvalidateVisual();
            }
        }

        /// <summary>The playing position as a fraction of the clip (negative: not playing); redraws only when it moves a pixel.</summary>
        public void SetPosition(double fraction)
        {
            if (fraction < 0) fraction = -1;
            double w = Bounds.Width;
            if (Math.Abs(fraction - position) * w < 1 && (fraction < 0) == (position < 0)) return;
            position = fraction;
            InvalidateVisual();
        }

        /// <summary>For tests: the geometry has been built from the peaks.</summary>
        internal bool Drawn => geometry != null && builtWithPeaks;

        /// <summary>For tests: how many times the geometry was made, and the tallest peak in it (in pixels).</summary>
        internal int Builds { get; private set; }
        internal double PeakHeight { get; private set; }
        internal void ForceBuild() { double w = Bounds.Width, h = Bounds.Height - BottomRoom; if (w >= 4 && h >= 4) Build(w, h); }

        public override void Render(DrawingContext ctx)
        {
            double w = Bounds.Width, h = Bounds.Height - BottomRoom;
            if (w < 4 || h < 4) return;
            if (geometry == null || Math.Abs(w - builtWidth) > 0.5 || Math.Abs(h - builtHeight) > 0.5 || (!builtWithPeaks && view.Asset.Peaks != null))
                Build(w, h);
            if (geometry != null) ctx.DrawGeometry(brush, null, geometry);
            if (position >= 0)
            {
                double x = Math.Round(position * w) + 0.5;
                ctx.DrawLine(playPen ??= new Pen(brush, 1.5), new Point(x, 0), new Point(x, h));
            }
        }

        void Build(double w, double h)
        {
            Builds++;
            builtWidth = w;
            builtHeight = h;
            var peaks = view.Asset.PeaksOrBuild(() =>
            {
                lock (this)
                {
                    if (pending) return;
                    pending = true;
                }
                Dispatcher.UIThread.Post(() => { pending = false; InvalidateVisual(); });
            });
            double mid = h / 2, half = h / 2 - 1, peakPx = 0;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                if (peaks == null)
                {
                    // Still being made: a flat line where the audio goes.
                    c.BeginFigure(new Point(0, mid), true);
                    c.LineTo(new Point(w, mid));
                    c.LineTo(new Point(w, mid + 1));
                    c.LineTo(new Point(0, mid + 1));
                    c.EndFigure(true);
                }
                else
                {
                    int n = Math.Max(1, (int)Math.Ceiling(w / Column));
                    double gain = view.Audio.Gain;
                    var mins = new double[n];
                    var maxs = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        double a = view.SampleAt(i * Column / w * beats), b = view.SampleAt((i + 1) * Column / w * beats);
                        peaks.Range(a, b, out float lo, out float hi);
                        mins[i] = Math.Clamp(lo * gain, -1, 1);
                        maxs[i] = Math.Clamp(hi * gain, -1, 1);
                    }
                    for (int i = 0; i < n; i++) peakPx = Math.Max(peakPx, Math.Max(maxs[i], -mins[i]) * half);
                    c.BeginFigure(new Point(0, mid - maxs[0] * half), true);
                    for (int i = 1; i < n; i++) c.LineTo(new Point(i * Column, mid - maxs[i] * half));
                    for (int i = n - 1; i >= 0; i--) c.LineTo(new Point((i + 1) * Column, mid - Math.Min(mins[i], maxs[i] - 0.02) * half));
                    c.EndFigure(true);
                }
            }
            geometry = g;
            PeakHeight = peaks == null ? 0 : peakPx;
            builtWithPeaks = peaks != null;
        }
    }
}
