// Fork (docs/specs/studio-mvp.md S1): a graph surface that takes the pointer, on Avalonia, ported
// from the WPF kit's InteractiveGraph.cs: the pointer goes to a toolkit-free IGraphInput
// (Core/ResponseGraph.cs: band handles), which asks for redraws. A drag is one undo step.

using System;
using Avalonia.Input;

namespace NeoBuzz.Editor
{
    internal sealed class InteractiveGraph : GraphView
    {
        readonly IGraphInput input;
        readonly IGraphInputPlus plus;
        readonly EditGesture gesture;
        bool dragging;

        public InteractiveGraph(double width, double height, Action<INeoCanvas, NeoRect> draw, IGraphInput input, bool animate = false)
            : base(width, height, draw, animate)
        {
            this.input = input;
            gesture = new EditGesture(this);
            // A surface that also takes the right button, keys and text (Neo Patch's inner canvas) is focusable; the others stay out of the tab order.
            plus = input as IGraphInputPlus;
            Focusable = plus != null;
        }

        static bool Fine(MouseEventArgs e) => (e.KeyModifiers & KeyModifiers.Shift) != 0;

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            var p = e.GetPosition(this);
            if (plus != null) Focus();
            gesture.Begin();
            if (input.Down(p.X, p.Y, e.ClickCount, Fine(e)))
            {
                if (input.Capturing)
                {
                    CaptureMouse();
                    dragging = true;
                }
                InvalidateVisual();
                e.Handled = true;
            }
            if (!dragging) gesture.End();   // a click (a double-click toggles a band): its step is done
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            if (plus == null) return;
            var p = e.GetPosition(this);
            Focus();
            if (plus.RightDown(p.X, p.Y))
            {
                InvalidateVisual();
                e.Handled = true;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (plus == null) return;
            bool ctrl = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0, shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
            if (plus.Key(e.Key.ToString(), ctrl, shift))
            {
                InvalidateVisual();
                e.Handled = true;
            }
        }

        protected override void OnRender(Avalonia.Media.DrawingContext dc)
        {
            base.OnRender(dc);
            if (plus != null) DrawFocusRing(dc, new Avalonia.Rect(Bounds.Size));
        }

        protected override void OnTextInput(TextInputEventArgs e)
        {
            base.OnTextInput(e);
            if (plus == null || !plus.WantsText) return;
            if (plus.Text(e.Text))
            {
                InvalidateVisual();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (input.Move(p.X, p.Y, Fine(e))) InvalidateVisual();
            Cursor = input.Capturing ? Cursors.SizeAll : Cursors.Arrow;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (input.Up()) InvalidateVisual();
            EndDrag();
        }

        // The drag ended without a release (the window lost the pointer).
        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (!dragging) return;
            if (input.Up()) InvalidateVisual();
            EndDrag();
        }

        void EndDrag()
        {
            if (!dragging) return;
            dragging = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            gesture.End();
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var p = e.GetPosition(this);
            gesture.Wheel();
            if (input.Wheel(p.X, p.Y, e.Delta, Fine(e))) InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (input.Leave()) InvalidateVisual();
        }
    }
}
