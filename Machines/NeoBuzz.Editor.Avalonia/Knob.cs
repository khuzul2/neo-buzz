// Fork (docs/specs/studio-mvp.md S1): the rotary knob on Avalonia, ported from the WPF kit's
// Knob.cs: drag up/down (Shift for fine), mouse wheel, double-click for the default; its label,
// and its value while hovered or dragged. State, input logic and drawing are toolkit-free
// (Core/KnobModel.cs). A drag, a reset or a run of wheel turns is one undo step (EditGesture).

using System;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;

namespace NeoBuzz.Editor
{
    internal class Knob : NeoElement
    {
        readonly KnobModel model;
        readonly AvaloniaCanvas canvas;
        readonly EditGesture gesture;

        public Knob(NeoColor accent, double diameter = 46)
        {
            Accent = accent;
            model = new KnobModel(diameter);
            canvas = new AvaloniaCanvas(this);
            gesture = new EditGesture(this);
            Width = model.Width;
            Height = model.Height;
            Cursor = Cursors.SizeNS;
            Focusable = false;
        }

        public NeoColor Accent { get; }
        public double Diameter => model.Diameter;

        public string Label
        {
            get => model.Label;
            set
            {
                model.Label = value;
                AutomationProperties.SetName(this, value);
            }
        }

        /// <summary>Text of the current value (set by the owner).</summary>
        public string ValueText { get => model.ValueText; set => model.ValueText = value; }
        /// <summary>The value arc grows from the centre (for −/+ parameters).</summary>
        public bool Bipolar { get => model.Bipolar; set => model.Bipolar = value; }
        public double Default { get => model.Default; set => model.Default = value; }
        /// <summary>Steps for wheel and arrow keys: the parameter's range.</summary>
        public int Steps { get => model.Steps; set => model.Steps = value; }
        /// <summary>The safe range marked on the knob, positions 0 … 1 (U4, machine-safety.md §3.4); null: none.</summary>
        public (double From, double To)? Safe
        {
            get => model.Safe;
            set
            {
                model.Safe = value;
                InvalidateVisual();
            }
        }

        /// <summary>Raised on a right click (the manifest editor takes a typed value then).</summary>
        public event Action RightClicked;

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

        static bool Fine(MouseEventArgs e) => (e.KeyModifiers & KeyModifiers.Shift) != 0;

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
                gesture.Begin();
                UserMoved(model.ResetToDefault());
                gesture.End();
                e.Handled = true;
                return;
            }
            gesture.Begin();
            model.BeginDrag(e.GetPosition(this).Y);
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!model.Dragging) return;
            UserMoved(model.DragTo(e.GetPosition(this).Y, Fine(e)));
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!model.EndDrag()) return;
            ReleaseMouseCapture();
            gesture.End();
            e.Handled = true;
            InvalidateVisual();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (!model.EndDrag()) return;   // the drag ended without a release (the window lost the pointer)
            gesture.End();
            InvalidateVisual();
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            if (RightClicked == null) return;
            RightClicked();
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            gesture.Wheel();
            UserMoved(model.Wheel(e.Delta, Fine(e)));
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            model.Draw(canvas.On(dc), Bounds.Width > 0 ? Bounds.Width : Width, Accent);
    }
}
