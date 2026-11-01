#nullable disable
// Neo editor kit, toolkit-free core (docs/specs/cross-platform.md §8.3): the rotary knob's state,
// input and drawing. A toolkit control (Knob.cs for WPF) turns its pointer events into the plain
// calls below and redraws when they return true.

using System;

namespace NeoBuzz.Editor
{
    internal sealed class KnobModel
    {
        /// <summary>Degrees either side of up.</summary>
        public const double Sweep = 135;
        /// <summary>Pixels of drag for the whole range, normally and fine (Shift).</summary>
        public const double DragRange = 200, FineDragRange = 1500;

        double dragStartY, dragStartValue;

        public KnobModel(double diameter)
        {
            Diameter = diameter;
        }

        public double Diameter { get; }
        public double Width => Math.Max(Diameter + 18, 64);
        public double Height => Diameter + 34;

        public string Label { get; set; } = "";
        /// <summary>Text of the current value (set by the owner).</summary>
        public string ValueText { get; set; } = "";
        /// <summary>The value arc grows from the centre (for −/+ parameters).</summary>
        public bool Bipolar { get; set; }
        public double Default { get; set; }
        /// <summary>Steps for the wheel: the parameter's range.</summary>
        public int Steps { get; set; } = 100;

        /// <summary>0 … 1.</summary>
        public double Value { get; private set; }
        public bool Dragging { get; private set; }
        public bool Hover { get; set; }

        /// <summary>Sets the value (clamped to 0 … 1); true when it changed.</summary>
        public bool Set(double v)
        {
            v = Math.Clamp(v, 0, 1);
            if (v == Value) return false;
            Value = v;
            return true;
        }

        // Input. Each call that can move the knob returns true when the value changed.

        /// <summary>Double click: back to the default.</summary>
        public bool ResetToDefault() => Set(Default);

        public void BeginDrag(double y)
        {
            Dragging = true;
            dragStartY = y;
            dragStartValue = Value;
        }

        /// <summary>Drag to <paramref name="y"/>: up raises the value.</summary>
        public bool DragTo(double y, bool fine)
        {
            if (!Dragging) return false;
            double range = fine ? FineDragRange : DragRange;
            double v = dragStartValue + (dragStartY - y) / range;
            if (v < 0 || v > 1)
            {
                // Re-anchor at the ends, so turning back responds at once.
                v = Math.Clamp(v, 0, 1);
                dragStartValue = v;
                dragStartY = y;
            }
            return Set(v);
        }

        /// <summary>True when a drag ended.</summary>
        public bool EndDrag()
        {
            if (!Dragging) return false;
            Dragging = false;
            return true;
        }

        /// <summary>A wheel turn: one step for small ranges, else 1 % (0.1 % fine).</summary>
        public bool Wheel(int delta, bool fine)
        {
            double step = Steps > 0 && Steps <= 48 ? 1.0 / Steps : fine ? 0.001 : 0.01;
            return Set(Value + Math.Sign(delta) * step);
        }

        /// <summary>Arcs, body, pointer, the label and the value text, in a control <paramref name="width"/> wide.</summary>
        public void Draw(INeoCanvas c, double width, NeoColor accent)
        {
            double w = width;
            var centre = new NeoPoint(w / 2, 4 + Diameter / 2);
            double r = Diameter / 2;

            // Track and value arcs.
            c.Arc(centre, r - 2, -Sweep, Sweep, new NeoStroke(Palette.Track, 4));
            double a = -Sweep + 2 * Sweep * Value;
            double from = Bipolar ? 0 : -Sweep;
            if (Math.Abs(a - from) > 0.5)
                c.Arc(centre, r - 2, from, a, new NeoStroke(accent, 4));

            // Body and pointer.
            double body = r - 8;
            c.Ellipse(centre, body, body, Palette.KnobBody, new NeoStroke(Palette.PanelEdge, 1));
            var tip = NeoGeometry.Polar(centre, body - 3, a);
            var inner = NeoGeometry.Polar(centre, body * 0.35, a);
            bool lit = Dragging || Hover;
            c.Line(inner, tip, new NeoStroke(lit ? accent : Palette.Text, 2.5));

            // Label, then the value while hovered or dragged.
            double y = 6 + Diameter;
            c.Text(Label, w / 2, y, 11, lit ? Palette.DimText : Palette.Text, align: NeoAlign.Center);
            c.Text(ValueText, w / 2, y + 14, 11, lit ? accent : Palette.DimText, align: NeoAlign.Center);
        }
    }
}
