// Fork (docs/specs/ux-overhaul.md §11, MX1): the mixer's parts, drawn in code and shared by every density of the one mixer
// (Full, Compact, Header): the slider that is a fader, a pan or a send, and the peak meter. They moved out of the Session View
// (docs/specs/session-view.md 0.4), where they were born, and behave as they did.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Mixer
{
    using Control = Avalonia.Controls.Control;

    internal enum StripKind { Track, Group, Return, Master }

    /// <summary>How much of a strip shows: Full (every part), Compact (name, fader, meter, pan, On, Solo, Arm) or Header (the parts laid out sideways, for a track header).</summary>
    internal enum MixerDensity { Full, Compact, Header }

    /// <summary>
    /// Slider drawn in one pass: horizontal (sends, pan; caption and value inside) or
    /// vertical (fader with a thumb). Dragging is relative, so a click doesn't jump; Shift
    /// is fine control; the wheel steps; double-click resets.
    /// </summary>
    internal sealed class MixerBar : Control
    {
        static readonly Cursor SizeNS = new Cursor(StandardCursorType.SizeNorthSouth);
        static readonly Cursor SizeWE = new Cursor(StandardCursorType.SizeWestEast);

        readonly bool vertical;
        readonly bool bipolar;
        readonly IBrush fill;
        double current;
        string valueText = "";
        Point? last;
        const double ThumbHeight = 10;

        public double DefaultValue { get; set; }
        public double? Mark { get; set; }
        public string Caption { get; set; } = "";
        public bool IsDragging => last != null;

        /// <summary>Raised when the user changes the value (not when it's set from code).</summary>
        public event Action<double> Changed;

        /// <summary>Raised when a drag ends (the clip panel commits a dragged value then).</summary>
        public event Action DragEnded;

        public MixerBar(bool vertical, bool bipolar, IBrush fill)
        {
            this.vertical = vertical;
            this.bipolar = bipolar;
            this.fill = fill;
            Cursor = vertical ? SizeNS : SizeWE;
            ClipToBounds = true;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsEnabledProperty)
            {
                Opacity = IsEnabled ? 1.0 : 0.35;
                InvalidateVisual();
            }
        }

        public double Value
        {
            get => current;
            set
            {
                double v = Math.Clamp(value, 0, 1);
                if (Math.Abs(v - current) < 1e-9) return;
                current = v;
                InvalidateVisual();
            }
        }

        public string ValueText
        {
            get => valueText;
            set
            {
                value ??= "";
                if (value == valueText) return;
                valueText = value;
                InvalidateVisual();
            }
        }

        /// <summary>Sets the value as the user would (raises <see cref="Changed"/>).</summary>
        internal void SetByUser(double v)
        {
            v = Math.Clamp(v, 0, 1);
            if (Math.Abs(v - current) < 1e-9) return;
            Value = v;
            Changed?.Invoke(v);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!IsEnabled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (e.ClickCount == 2)
            {
                SetByUser(DefaultValue);
            }
            else
            {
                last = e.GetPosition(this);
                e.Pointer.Capture(this);
            }
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (last is not Point from) return;
            var p = e.GetPosition(this);
            double length = vertical ? Math.Max(1, Bounds.Height - ThumbHeight) : Math.Max(1, Bounds.Width);
            double delta = vertical ? from.Y - p.Y : p.X - from.X;
            double scale = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 1.0;
            last = p;
            SetByUser(current + delta / length * scale);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton != MouseButton.Left) return;
            EndDrag();
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndDrag();
        }

        void EndDrag()
        {
            if (last == null) return;
            last = null;
            DragEnded?.Invoke();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (!IsEnabled) return;
            double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.005 : 0.02;
            double delta = Math.Abs(e.Delta.Y) >= Math.Abs(e.Delta.X) ? e.Delta.Y : e.Delta.X;
            SetByUser(current + Math.Sign(delta) * step);
            e.Handled = true;
        }

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            dc.FillRectangle(SessionPalette.Transparent, new Rect(0, 0, w, h));   // hit area

            if (vertical)
            {
                double travel = h - ThumbHeight;
                double y = ThumbHeight / 2 + (1 - current) * travel;
                var groove = new Rect(w / 2 - 2, ThumbHeight / 2, 4, travel);
                dc.FillRectangle(SessionPalette.Groove, groove);
                dc.FillRectangle(fill, new Rect(w / 2 - 2, y, 4, Math.Max(0, groove.Bottom - y)));
                if (Mark is double mark)
                {
                    double my = ThumbHeight / 2 + (1 - mark) * travel;
                    dc.FillRectangle(SessionPalette.DimText, new Rect(0, Math.Round(my), 5, 1));
                    dc.FillRectangle(SessionPalette.DimText, new Rect(w - 5, Math.Round(my), 5, 1));
                }
                dc.DrawRectangle(SessionPalette.Thumb, null, new Rect(2, y - ThumbHeight / 2, w - 4, ThumbHeight), 2, 2);
                dc.FillRectangle(SessionPalette.Groove, new Rect(4, Math.Round(y), w - 8, 1));
                return;
            }

            dc.DrawRectangle(SessionPalette.Groove, null, new Rect(0, 0, w, h), 2, 2);
            if (bipolar)
            {
                double x0 = w / 2, x1 = current * w;
                dc.FillRectangle(fill, new Rect(Math.Min(x0, x1), 0, Math.Max(1, Math.Abs(x1 - x0)), h));
            }
            else if (current > 0)
            {
                using (dc.PushOpacity(0.6))
                    dc.DrawRectangle(fill, null, new Rect(0, 0, current * w, h), 2, 2);
            }

            if (!string.IsNullOrEmpty(Caption))
            {
                var caption = MakeText(Caption, 11, SessionPalette.Text);
                dc.DrawText(caption, new Point(4, (h - caption.Height) / 2));
            }
            if (!string.IsNullOrEmpty(valueText))
            {
                var text = MakeText(valueText, 11, SessionPalette.Text);
                dc.DrawText(text, new Point(Math.Max(0, w - text.Width - 4), (h - text.Height) / 2));
            }
        }

        static FormattedText MakeText(string text, double size, IBrush brush) =>
            new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
    }

    /// <summary>
    /// Stereo peak meter, vertical or sideways: green, yellow from -12 dB, red above 0 dB, with a peak hold (a thin line at the loudest
    /// level of the last second and a half, falling after).
    /// </summary>
    internal sealed class MeterBar : Control
    {
        static readonly double Yellow = SessionMixer.LevelOfDb(-12);
        static readonly double Red = SessionMixer.LevelOfDb(0);

        readonly bool sideways;
        double left, right, holdLeft, holdRight;

        public double Left => left;
        public double Right => right;

        /// <summary>The held peak of the louder channel, 0..1 on the meter's scale.</summary>
        public double Hold => Math.Max(holdLeft, holdRight);

        public MeterBar(bool sideways = false)
        {
            this.sideways = sideways;
            ToolTip.SetTip(this, "Peak, post-fader (-60 … +6 dB); the line is the held peak");
        }

        public void Set(double l, double r, double holdL = 0, double holdR = 0)
        {
            if (Math.Abs(l - left) < 0.002 && Math.Abs(r - right) < 0.002 && Math.Abs(holdL - holdLeft) < 0.004 && Math.Abs(holdR - holdRight) < 0.004) return;
            left = l;
            right = r;
            holdLeft = holdL;
            holdRight = holdR;
            InvalidateVisual();
        }

        public override void Render(DrawingContext dc)
        {
            double w = Bounds.Width, h = Bounds.Height;
            if (w <= 0 || h <= 0) return;
            if (sideways)
            {
                double bar = Math.Floor((h - 1) / 2);
                DrawAcross(dc, 0, bar, w, left, holdLeft);
                DrawAcross(dc, bar + 1, bar, w, right, holdRight);
                dc.FillRectangle(SessionPalette.DimText, new Rect(Math.Round(Red * w), 0, 1, h));
                return;
            }
            double across = Math.Floor((w - 1) / 2);
            Draw(dc, 0, across, h, left, holdLeft);
            Draw(dc, across + 1, across, h, right, holdRight);
            double zero = Math.Round((1 - Red) * h);
            dc.FillRectangle(SessionPalette.DimText, new Rect(0, zero, w, 1));
        }

        static void Draw(DrawingContext dc, double x, double width, double h, double level, double hold)
        {
            dc.FillRectangle(SessionPalette.Groove, new Rect(x, 0, width, h));
            if (level > 0)
            {
                Segment(dc, x, width, h, 0, Math.Min(level, Yellow), SessionPalette.MeterLow);
                if (level > Yellow) Segment(dc, x, width, h, Yellow, Math.Min(level, Red), SessionPalette.MeterMid);
                if (level > Red) Segment(dc, x, width, h, Red, level, SessionPalette.MeterHigh);
            }
            if (hold > level && hold > 0.01) dc.FillRectangle(SessionPalette.Text, new Rect(x, Math.Round((1 - hold) * h), width, 1));
        }

        static void Segment(DrawingContext dc, double x, double width, double h, double from, double to, IBrush brush) =>
            dc.FillRectangle(brush, new Rect(x, (1 - to) * h, width, Math.Max(0, (to - from) * h)));

        static void DrawAcross(DrawingContext dc, double y, double height, double w, double level, double hold)
        {
            dc.FillRectangle(SessionPalette.Groove, new Rect(0, y, w, height));
            if (level > 0)
            {
                dc.FillRectangle(SessionPalette.MeterLow, new Rect(0, y, Math.Min(level, Yellow) * w, height));
                if (level > Yellow) dc.FillRectangle(SessionPalette.MeterMid, new Rect(Yellow * w, y, (Math.Min(level, Red) - Yellow) * w, height));
                if (level > Red) dc.FillRectangle(SessionPalette.MeterHigh, new Rect(Red * w, y, (level - Red) * w, height));
            }
            if (hold > level && hold > 0.01) dc.FillRectangle(SessionPalette.Text, new Rect(Math.Round(hold * w), y, 1, height));
        }
    }
}
