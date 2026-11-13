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
        /// <summary>Its label; the knob grows to its width (KnobModel.Width).</summary>
        public string Label
        {
            get => model.Label;
            set
            {
                model.Label = value;
                Fit();
            }
        }

        /// <summary>The widest its label goes on a line before it wraps onto a second (KnobModel.MaxLabelWidth).</summary>
        public double MaxLabelWidth
        {
            get => model.MaxLabelWidth;
            set
            {
                model.MaxLabelWidth = value;
                Fit();
            }
        }

        /// <summary>The label's width as drawn (its widest line).</summary>
        public double LabelWidth => model.LabelWidth;

        /// <summary>The label's lines as drawn.</summary>
        public string[] LabelLines => model.LabelLines;

        void Fit()
        {
            model.FitLabel(canvas);
            Width = model.Width;
            Height = model.Height;
            InvalidateVisual();
        }
        /// <summary>Text of the current value (set by the owner).</summary>
        public string ValueText { get => model.ValueText; set => model.ValueText = value; }
        /// <summary>Where the value arc starts, a position 0 … 1 (the value 0 for −/+ parameters, else the minimum).</summary>
        public double Origin { get => model.Origin; set => model.Origin = value; }
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

        /// <summary>Raised when the pointer enters or leaves the knob, a gesture starts (a press, the wheel, a double click) or ends (Explain, neo-learn.md §5.3).</summary>
        public event Action<KnobPhase> Phase;

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
            Phase?.Invoke(KnobPhase.Enter);
            model.Hover = true;
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            Phase?.Invoke(KnobPhase.Leave);
            model.Hover = false;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Phase?.Invoke(KnobPhase.Touch);
            if (e.ClickCount == 2)
            {
                UserMoved(model.ResetToDefault());
                Phase?.Invoke(KnobPhase.Release);
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
            Phase?.Invoke(KnobPhase.Release);
            ReleaseMouseCapture();
            e.Handled = true;
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
            Phase?.Invoke(KnobPhase.Touch);
            UserMoved(model.Wheel(e.Delta, Fine));
            Phase?.Invoke(KnobPhase.Release);
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            model.Draw(canvas.On(dc), ActualWidth > 0 ? ActualWidth : Width, Accent);
    }
}
