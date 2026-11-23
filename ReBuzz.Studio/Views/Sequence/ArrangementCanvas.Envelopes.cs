// Fork (docs/specs/daw-modes.md EV1): the curves on the clips. With the Envelopes toggle on (key E), every clip of a track draws the one
// parameter its track's header chose (saved with the song; the first parameter with data until then): its engine points as a line, a
// Tracker column's steps dashed, the repeats of a looped clip at half strength. Read only: the Detail panel's lane edits the points, and
// a double click on a curve opens it there.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Views.Sequence
{
    internal sealed partial class ArrangementCanvas
    {
        bool envelopesOn;

        // What a clip draws, read once and kept until a column or clip changes: (the parameter, its points, whether they are the Tracker's steps).
        readonly Dictionary<IPattern, (ColumnKey Key, AutoParam Param, IReadOnlyList<AutoPoint> Points, bool FromTracker)?> curves = new();
        readonly Dictionary<IMachine, ColumnKey?> chosen = new();

        /// <summary>Whether the clips draw their curves (the Envelopes toggle).</summary>
        public bool Envelopes
        {
            get => envelopesOn;
            set
            {
                if (envelopesOn == value) return;
                envelopesOn = value;
                ClearCurves();
                if (value) ReadLanes();       // the clips' columns are watched whether or not a parameter lane is open
                InvalidateVisual();
                EnvelopesChanged?.Invoke();
            }
        }

        public event Action EnvelopesChanged;

        /// <summary>A double click on a clip's curve: the clip and the parameter.</summary>
        public event Action<IPattern, ColumnKey> EnvelopeOpened;

        void ClearCurves()
        {
            curves.Clear();
            chosen.Clear();
        }

        /// <summary>The parameter the track's clips draw (the song's choice, else the default), or null when there is none.</summary>
        public ColumnKey? CurveKeyOf(IMachine machine)
        {
            if (machine == null) return null;
            if (!chosen.TryGetValue(machine, out var key)) chosen[machine] = key = ClipEnvelopes.ChoiceOf(core, machine);
            return key;
        }

        /// <summary>What a clip's pattern draws now (null when its track chose nothing or the clip holds none).</summary>
        internal (ColumnKey Key, AutoParam Param, IReadOnlyList<AutoPoint> Points, bool FromTracker)? CurveOfClip(IPattern pattern)
        {
            if (pattern?.Machine == null) return null;
            if (curves.TryGetValue(pattern, out var known)) return known;
            (ColumnKey, AutoParam, IReadOnlyList<AutoPoint>, bool FromTracker)? curve = null;
            if (CurveKeyOf(pattern.Machine) is { } key && AutomationParams.Find(pattern.Machine, key) is { } param)
            {
                var (points, fromTracker) = ClipEnvelopes.CurveOf(pattern, key);
                if (points.Count > 0) curve = (key, param, points, fromTracker);
            }
            Watch(pattern);
            foreach (var column in pattern.Columns) WatchColumn(column);
            curves[pattern] = curve;
            return curve;
        }

        const double CurveTop = 19;

        void DrawEnvelopes(DrawingContext ctx, ClipItem item, IReadOnlyList<ClipSegment> segments, double h)
        {
            if (CurveOfClip(item.Pattern) is not { } curve || h < CurveTop + 8) return;
            var colour = Colors.White;
            for (int i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                double sx = (s.Time - item.Time) * geo.TickWidth, sw = s.Length * geo.TickWidth;
                if (sw < 2) continue;
                var area = new Rect(sx + 1, CurveTop, Math.Max(0, sw - 2), Math.Max(0, h - CurveTop - 3));
                using var clip = ctx.PushClip(area);
                using var fade = ctx.PushOpacity(i == 0 ? 1 : 0.5);
                var path = PathOf(curve, area, s, item.Time);
                var dark = new Pen(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3.5, lineJoin: PenLineJoin.Round);
                var pen = new Pen(new SolidColorBrush(colour), 1.5, curve.FromTracker ? DashStyle.Dash : null, lineJoin: PenLineJoin.Round);
                ctx.DrawGeometry(null, dark, path);
                ctx.DrawGeometry(null, pen, path);
            }
        }

        /// <summary>The curve through a segment's area: a line to the next point for a ramp, a step otherwise; held to the segment's end after the last point.</summary>
        StreamGeometry PathOf((ColumnKey Key, AutoParam Param, IReadOnlyList<AutoPoint> Points, bool FromTracker) curve, Rect area, ClipSegment s, int itemTime)
        {
            double X(int time) => area.X - 1 + (time / (double)Unit - s.Offset) * geo.TickWidth;
            double Y(int value) => area.Y + (1 - curve.Param.Fraction(value)) * area.Height;
            var g = new StreamGeometry();
            using var c = g.Open();
            var pts = curve.Points;
            c.BeginFigure(new Point(X(pts[0].Time), Y(pts[0].Value)), false);
            for (int k = 0; k < pts.Count; k++)
            {
                var p = pts[k];
                if (k + 1 < pts.Count)
                {
                    var n = pts[k + 1];
                    if (p.Ramp) c.LineTo(new Point(X(n.Time), Y(n.Value)));
                    else { c.LineTo(new Point(X(n.Time), Y(p.Value))); c.LineTo(new Point(X(n.Time), Y(n.Value))); }
                }
                else c.LineTo(new Point(Math.Max(X(p.Time), area.Right + 1), Y(p.Value)));
            }
            c.EndFigure(false);
            return g;
        }

        /// <summary>A double click inside a clip, on or near its curve: the clip opens in the Detail panel with that parameter's lane.</summary>
        bool TryOpenEnvelope(ClipItem item, Point p)
        {
            if (!envelopesOn || item == null || CurveOfClip(item.Pattern) is not { } curve || EnvelopeOpened == null) return false;
            var r = ClipRect(item);
            double h = r.Height;
            foreach (var s in item.Actual)
            {
                double sx = r.X + (s.Time - item.Time) * geo.TickWidth, sw = s.Length * geo.TickWidth;
                if (p.X < sx || p.X > sx + sw) continue;
                var area = new Rect(sx + 1, r.Y + CurveTop, Math.Max(0, sw - 2), Math.Max(0, h - CurveTop - 3));
                double t = (p.X - sx) / geo.TickWidth + s.Offset;                 // ticks into the pattern
                var pts = curve.Points;
                int time = (int)Math.Round(t * Unit);
                int? v = null;
                if (time >= pts[0].Time) v = AutomationMath.ValueAt(pts, time);
                if (v == null) return false;
                double y = area.Y + (1 - curve.Param.Fraction(v.Value)) * area.Height;
                if (Math.Abs(p.Y - y) > 10) return false;
                EnvelopeOpened.Invoke(item.Pattern, curve.Key);
                return true;
            }
            return false;
        }
    }
}
