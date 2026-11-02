#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): a graph surface that takes the pointer.
// WPF routes the mouse to a toolkit-free IGraphInput (Core/ResponseGraph.cs: band handles) and
// redraws when it asks; drawing goes through INeoCanvas like every GraphView.

using System;
using System.Windows.Input;

namespace NeoBuzz.Editor
{
    internal sealed class InteractiveGraph : GraphView
    {
        readonly IGraphInput input;

        public InteractiveGraph(double width, double height, Action<INeoCanvas, NeoRect> draw, IGraphInput input, bool animate = false)
            : base(width, height, draw, animate)
        {
            this.input = input;
            Focusable = false;
        }

        static bool Fine => (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            if (input.Down(p.X, p.Y, e.ClickCount, Fine))
            {
                if (input.Capturing) CaptureMouse();
                InvalidateVisual();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (input.Move(p.X, p.Y, Fine)) InvalidateVisual();
            Cursor = input.Capturing ? Cursors.SizeAll : Cursors.Arrow;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (input.Up()) InvalidateVisual();
            if (IsMouseCaptured) ReleaseMouseCapture();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var p = e.GetPosition(this);
            if (input.Wheel(p.X, p.Y, e.Delta, Fine)) InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (input.Leave()) InvalidateVisual();
        }
    }
}
