#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): the rotary knob. Drag up/down (Shift for
// fine), mouse wheel, double-click for the default. Shows its label, and its value while hovered
// or dragged. State, input logic and drawing are toolkit-free (Core/KnobModel.cs); this WPF
// element routes mouse events to it and draws it through WpfCanvas.

using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NeoBuzz.Editor
{
    internal class Knob : FrameworkElement
    {
        readonly KnobModel model;
        readonly WpfCanvas canvas;

        public Knob(NeoColor accent, double diameter = 46)
        {
            Accent = accent;
            model = new KnobModel(diameter);
            canvas = new WpfCanvas(this);
            Width = model.Width;
            Height = model.Height;
            Cursor = Cursors.SizeNS;
            Focusable = false;
        }

        public NeoColor Accent { get; }
        public double Diameter => model.Diameter;
        public string Label { get => model.Label; set => model.Label = value; }
        /// <summary>Text of the current value (set by the owner).</summary>
        public string ValueText { get => model.ValueText; set => model.ValueText = value; }
        /// <summary>The value arc grows from the centre (for −/+ parameters).</summary>
        public bool Bipolar { get => model.Bipolar; set => model.Bipolar = value; }
        public double Default { get => model.Default; set => model.Default = value; }
        /// <summary>Steps for wheel and arrow keys: the parameter's range.</summary>
        public int Steps { get => model.Steps; set => model.Steps = value; }

        /// <summary>0 … 1.</summary>
        public double Value
        {
            get => model.Value;
            set
            {
                if (model.Set(value)) InvalidateVisual();
            }
        }

        /// <summary>Raised when the user moves the knob (not when Value is set in code).</summary>
        public event Action<double> UserChanged;

        void UserMoved(bool changed)
        {
            if (!changed) return;
            UserChanged?.Invoke(model.Value);
            InvalidateVisual();
        }

        public void Refresh() => InvalidateVisual();

        static bool Fine => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            model.Hover = true;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            model.Hover = false;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                UserMoved(model.ResetToDefault());
                e.Handled = true;
                return;
            }
            model.BeginDrag(e.GetPosition(this).Y);
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!model.Dragging) return;
            UserMoved(model.DragTo(e.GetPosition(this).Y, Fine));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!model.EndDrag()) return;
            ReleaseMouseCapture();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            UserMoved(model.Wheel(e.Delta, Fine));
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            model.Draw(canvas.On(dc), ActualWidth > 0 ? ActualWidth : Width, Accent);
    }
}
