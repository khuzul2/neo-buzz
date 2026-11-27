// Fork (docs/specs/daw-modes.md EV1): the mini curve along the bottom of a Session slot: the parameter the track's header chose,
// over the clip's length, read only. The Detail panel's lane edits it; a double click on the curve (SlotCell) opens it there.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Session
{
    internal sealed class SlotCurve : Avalonia.Controls.Control
    {
        public const double Height = 9;       // the band along the slot's bottom, under the clip's name (FU2b)
        const double Pad = 2;

        readonly IClip clip;
        readonly ColumnKey key;
        readonly AutoParam param;
        readonly HashSet<ClipColumnCore> watched = new();
        IReadOnlyList<AutoPoint> points = Array.Empty<AutoPoint>();
        bool fromTracker;
        IBrush brush = Brushes.White;

        SlotCurve(IClip clip, ColumnKey key, AutoParam param)
        {
            this.clip = clip;
            this.key = key;
            this.param = param;
            IsHitTestVisible = false;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            Margin = new Thickness(1, 0, 1, 3);          // above the progress bar
            base.Height = Height;
            Read();
        }

        /// <summary>The slot's curve for the track's chosen parameter, or null when the clip holds none of it.</summary>
        public static SlotCurve For(IClip clip, ColumnKey? key)
        {
            if (clip?.Machine == null || key == null || AutomationParams.Find(clip.Machine, key.Value) is not { } param) return null;
            var curve = new SlotCurve(clip, key.Value, param);
            return curve.points.Count > 0 ? curve : null;
        }

        public ColumnKey Key => key;

        public IBrush Foreground
        {
            get => brush;
            set { if (value != null && !ReferenceEquals(value, brush)) { brush = value; InvalidateVisual(); } }
        }

        /// <summary>Where the curve is at a fraction of the slot's width (0..1), as a distance from its line in pixels; infinity outside it.</summary>
        public double DistanceAt(double x, double y)
        {
            if (points.Count == 0 || Bounds.Width <= 0) return double.PositiveInfinity;
            double len = ClipEnvelopes.LengthOf(clip);
            int time = (int)Math.Round(Math.Clamp(x / Bounds.Width, 0, 1) * len);
            if (time < points[0].Time) return double.PositiveInfinity;
            if (AutomationMath.ValueAt(points, time) is not { } v) return double.PositiveInfinity;
            return Math.Abs(y - YOf(v));
        }

        double YOf(int value) => Pad + (1 - param.Fraction(value)) * Math.Max(1, Bounds.Height - 2 * Pad);

        void Read()
        {
            var (p, tracker) = ClipEnvelopes.CurveOf(clip, key);
            points = p;
            fromTracker = tracker;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            foreach (var column in clip.Columns)
                if (column is ClipColumnCore { Type: ClipColumnType.Parameter } c && watched.Add(c)) c.EventsChanged += OnEvents;
            Read();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            foreach (var c in watched) c.EventsChanged -= OnEvents;
            watched.Clear();
            base.OnDetachedFromVisualTree(e);
        }

        void OnEvents(IEnumerable<ClipEvent> events, bool set) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Read();
            InvalidateVisual();
        });

        public override void Render(DrawingContext ctx)
        {
            if (points.Count == 0 || Bounds.Width < 6) return;
            double len = ClipEnvelopes.LengthOf(clip), w = Bounds.Width;
            double X(int t) => Math.Clamp(t / len, 0, 1) * w;
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(X(points[0].Time), YOf(points[0].Value)), false);
                for (int k = 0; k < points.Count; k++)
                {
                    var p = points[k];
                    if (k + 1 < points.Count)
                    {
                        var n = points[k + 1];
                        if (p.Ramp) c.LineTo(new Point(X(n.Time), YOf(n.Value)));
                        else { c.LineTo(new Point(X(n.Time), YOf(p.Value))); c.LineTo(new Point(X(n.Time), YOf(n.Value))); }
                    }
                    else c.LineTo(new Point(w, YOf(p.Value)));
                }
                c.EndFigure(false);
            }
            // FU2b: a plate behind the curve, a halo of the opposite shade around the line and a line of 2 px: it reads on any clip colour, light or dark.
            var line = brush is ISolidColorBrush s ? s.Color : Colors.White;
            bool lightLine = (0.299 * line.R + 0.587 * line.G + 0.114 * line.B) > 140;
            var shade = lightLine ? Colors.Black : Colors.White;
            ctx.DrawRectangle(new SolidColorBrush(Color.FromArgb(lightLine ? (byte)70 : (byte)60, shade.R, shade.G, shade.B)), null, new Rect(0, 0, w, Bounds.Height), 2, 2);
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(170, shade.R, shade.G, shade.B)), 4, lineJoin: PenLineJoin.Round), g);
            ctx.DrawGeometry(null, new Pen(brush, 2, fromTracker ? DashStyle.Dash : null, lineJoin: PenLineJoin.Round), g);
        }
    }
}
