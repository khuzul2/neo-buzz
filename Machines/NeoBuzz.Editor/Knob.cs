#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): the rotary knob. Drag up/down (Shift for
// fine), mouse wheel, double-click for the default. Shows its label, and its value while hovered
// or dragged.

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NeoBuzz.Editor
{
    internal class Knob : FrameworkElement
    {
        const double Sweep = 135;   // degrees either side of up

        double normalized;
        double dragStartY, dragStartValue;
        bool dragging, hover;

        public Knob(Color accent, double diameter = 46)
        {
            Accent = accent;
            Diameter = diameter;
            AccentBrush = Theme.Solid(accent);
            Width = Math.Max(diameter + 18, 64);
            Height = diameter + 34;
            Cursor = Cursors.SizeNS;
            Focusable = false;
        }

        public Color Accent { get; }
        public Brush AccentBrush { get; }
        public double Diameter { get; }
        public string Label { get; set; } = "";
        /// <summary>Text of the current value (set by the owner).</summary>
        public string ValueText { get; set; } = "";
        /// <summary>The value arc grows from the centre (for −/+ parameters).</summary>
        public bool Bipolar { get; set; }
        public double Default { get; set; }
        /// <summary>Steps for wheel and arrow keys: the parameter's range.</summary>
        public int Steps { get; set; } = 100;

        /// <summary>0 … 1.</summary>
        public double Value
        {
            get => normalized;
            set
            {
                double v = Math.Clamp(value, 0, 1);
                if (v == normalized) return;
                normalized = v;
                InvalidateVisual();
            }
        }

        /// <summary>Raised when the user moves the knob (not when Value is set in code).</summary>
        public event Action<double> UserChanged;

        void SetFromUser(double v)
        {
            v = Math.Clamp(v, 0, 1);
            if (v == normalized) return;
            normalized = v;
            UserChanged?.Invoke(v);
            InvalidateVisual();
        }

        public void Refresh() => InvalidateVisual();

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            hover = true;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            hover = false;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                SetFromUser(Default);
                e.Handled = true;
                return;
            }
            dragging = true;
            dragStartY = e.GetPosition(this).Y;
            dragStartValue = normalized;
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!dragging) return;
            double y = e.GetPosition(this).Y;
            bool fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            double range = fine ? 1500 : 200;
            // Re-anchor on modifier changes so the knob doesn't jump.
            double v = dragStartValue + (dragStartY - y) / range;
            if (v < 0 || v > 1)
            {
                v = Math.Clamp(v, 0, 1);
                dragStartValue = v;
                dragStartY = y;
            }
            SetFromUser(v);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            ReleaseMouseCapture();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            bool fine = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            double step = Steps > 0 && Steps <= 48 ? 1.0 / Steps : fine ? 0.001 : 0.01;
            SetFromUser(normalized + Math.Sign(e.Delta) * step);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth > 0 ? ActualWidth : Width;
            var c = new Point(w / 2, 4 + Diameter / 2);
            double r = Diameter / 2;

            // Track and value arcs.
            dc.DrawGeometry(null, Theme.Pen(Theme.Track, 4), Theme.Arc(c, r - 2, -Sweep, Sweep));
            double a = -Sweep + 2 * Sweep * normalized;
            double from = Bipolar ? 0 : -Sweep;
            if (Math.Abs(a - from) > 0.5)
                dc.DrawGeometry(null, Theme.Pen(AccentBrush, 4), Theme.Arc(c, r - 2, from, a));

            // Body and pointer.
            double body = r - 8;
            dc.DrawEllipse(Theme.KnobBody, Theme.Pen(Theme.PanelEdge, 1), c, body, body);
            var tip = Theme.Polar(c, body - 3, a);
            var inner = Theme.Polar(c, body * 0.35, a);
            dc.DrawLine(Theme.Pen(dragging || hover ? AccentBrush : Theme.Text, 2.5), inner, tip);

            // Label, then the value while hovered or dragged.
            double y = 6 + Diameter;
            bool showValue = dragging || hover;
            Theme.TextCentered(dc, this, Label, w / 2, y, 11, showValue ? Theme.DimText : Theme.Text);
            Theme.TextCentered(dc, this, ValueText, w / 2, y + 14, 11, showValue ? AccentBrush : Theme.DimText);
        }
    }
}
