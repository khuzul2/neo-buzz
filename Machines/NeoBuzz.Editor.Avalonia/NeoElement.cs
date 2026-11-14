// Fork (docs/specs/studio-mvp.md S1): the base of the kit's code-drawn controls on Avalonia, in
// WPF's shape: OnRender(DrawingContext) and the mouse callbacks (OnMouseLeftButtonDown…), raised
// from Avalonia's pointer events. The kit's controls port from WPF line for line on it, and so do
// the editors' own (Neo EQ's band tabs, Neo FM6's algorithm), as FrameworkElement (WpfNames.cs).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.VisualTree;

namespace NeoBuzz.Editor
{
    internal class NeoElement : Control, ICustomHitTest
    {
        IPointer pointer;

        /// <summary>The tooltip, as WPF's property (Avalonia's attached ToolTip.Tip).</summary>
        public object ToolTip
        {
            get => Avalonia.Controls.ToolTip.GetTip(this);
            set => Avalonia.Controls.ToolTip.SetTip(this, value);
        }

        /// <summary>The whole box takes the pointer, not only what is drawn.</summary>
        public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

        public sealed override void Render(DrawingContext context) => OnRender(context);

        protected virtual void OnRender(DrawingContext dc)
        {
        }

        protected virtual void OnMouseEnter(MouseEventArgs e)
        {
        }

        protected virtual void OnMouseLeave(MouseEventArgs e)
        {
        }

        protected virtual void OnMouseMove(MouseEventArgs e)
        {
        }

        protected virtual void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
        }

        protected virtual void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
        }

        protected virtual void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
        }

        protected virtual void OnMouseWheel(MouseWheelEventArgs e)
        {
        }

        protected void CaptureMouse() => pointer?.Capture(this);

        protected void ReleaseMouseCapture()
        {
            if (IsMouseCaptured) pointer.Capture(null);
        }

        protected bool IsMouseCaptured => pointer != null && ReferenceEquals(pointer.Captured, this);

        /// <summary>The undo history of the editor this control is in (null outside one).</summary>
        internal ParamEdits Edits => this.FindAncestorOfType<NeoEditor>()?.Context?.Edits;

        bool keyFocus;

        /// <summary>True while the control has the focus because the keyboard (Tab, arrows) gave it, not the pointer (as :focus-visible).</summary>
        protected bool HasKeyFocus => keyFocus && IsFocused;

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            keyFocus = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
            InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            keyFocus = false;
            InvalidateVisual();
        }

        /// <summary>The ring a control with the keyboard focus draws inside <paramref name="area"/> (P5a).</summary>
        protected void DrawFocusRing(DrawingContext dc, Rect area)
        {
            if (HasKeyFocus) dc.DrawRectangle(null, new Pen(NeoBuzz.Editor.Theme.Text, 2), area.Deflate(1), 4, 4);
        }

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            OnMouseEnter(new MouseEventArgs(e));
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            OnMouseLeave(new MouseEventArgs(e));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            OnMouseMove(new MouseEventArgs(e));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            pointer = e.Pointer;
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.LeftButtonPressed) OnMouseLeftButtonDown(new MouseButtonEventArgs(e, e.ClickCount));
            else if (kind == PointerUpdateKind.RightButtonPressed) OnMouseRightButtonDown(new MouseButtonEventArgs(e, e.ClickCount));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (e.InitialPressMouseButton == MouseButton.Left) OnMouseLeftButtonUp(new MouseButtonEventArgs(e, 1));
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            OnMouseWheel(new MouseWheelEventArgs(e));
        }
    }
}
