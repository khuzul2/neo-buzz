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
        public const double Height = 10;      // the strip along the slot's bottom
        const double Pad = 3;

        readonly IPattern pattern;
        readonly ColumnKey key;
        readonly AutoParam param;
        readonly HashSet<PatternColumnCore> watched = new();
        IReadOnlyList<AutoPoint> points = Array.Empty<AutoPoint>();
        bool fromTracker;
        IBrush brush = Brushes.White;

        SlotCurve(IPattern pattern, ColumnKey key, AutoParam param)
        {
            this.pattern = pattern;
            this.key = key;
            this.param = param;
            IsHitTestVisible = false;
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom;
            Margin = new Thickness(0, 0, 0, 2);          // above the progress bar
            base.Height = Height;
            Read();
        }

        /// <summary>The slot's curve for the track's chosen parameter, or null when the clip holds none of it.</summary>
        public static SlotCurve For(IPattern pattern, ColumnKey? key)
        {
            if (pattern?.Machine == null || key == null || AutomationParams.Find(pattern.Machine, key.Value) is not { } param) return null;
            var curve = new SlotCurve(pattern, key.Value, param);
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
            double len = ClipEnvelopes.LengthOf(pattern);
            int time = (int)Math.Round(Math.Clamp(x / Bounds.Width, 0, 1) * len);
            if (time < points[0].Time) return double.PositiveInfinity;
            if (AutomationMath.ValueAt(points, time) is not { } v) return double.PositiveInfinity;
            return Math.Abs(y - YOf(v));
        }

        double YOf(int value) => Pad + (1 - param.Fraction(value)) * Math.Max(1, Bounds.Height - 2 * Pad);

        void Read()
        {
            var (p, tracker) = ClipEnvelopes.CurveOf(pattern, key);
            points = p;
            fromTracker = tracker;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            foreach (var column in pattern.Columns)
                if (column is PatternColumnCore { Type: PatternColumnType.Parameter } c && watched.Add(c)) c.EventsChanged += OnEvents;
            Read();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            foreach (var c in watched) c.EventsChanged -= OnEvents;
            watched.Clear();
            base.OnDetachedFromVisualTree(e);
        }

        void OnEvents(IEnumerable<PatternEvent> events, bool set) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            Read();
            InvalidateVisual();
        });

        public override void Render(DrawingContext ctx)
        {
            if (points.Count == 0 || Bounds.Width < 6) return;
            double len = ClipEnvelopes.LengthOf(pattern), w = Bounds.Width;
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
            ctx.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3, lineJoin: PenLineJoin.Round), g);
            ctx.DrawGeometry(null, new Pen(brush, 1.5, fromTracker ? DashStyle.Dash : null, lineJoin: PenLineJoin.Round), g);
        }
    }
}
