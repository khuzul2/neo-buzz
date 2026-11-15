// Fork (docs/specs/ux-overhaul.md P5b, drag-to-modulate): a canvas modulator's output handle. Press it and drag onto any knob, slider
// or device-view row, in any window; letting go there adds the modulation (the studio's ModulationBoard.DropHook finds the control under
// the pointer and records one undo step). Escape while dragging, or letting go over nothing, cancels. The keyboard's way is the
// modulation matrix (+ Add), which the handle's tooltip says.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Editor
{
    internal sealed class ModSourceHandle : NeoElement
    {
        const double DragThreshold = 4;
        readonly IModulator source;
        readonly NeoColor accent;
        readonly AvaloniaCanvas canvas;
        readonly string label;
        Point down;
        bool pressed, dragging, hover;

        public ModSourceHandle(IModulator source, NeoColor accent, double width = 210, string label = "Drag onto a knob to modulate it")
        {
            this.source = source;
            this.accent = accent;
            this.label = label;
            canvas = new AvaloniaCanvas(this);
            Width = width;
            Height = ButtonFace.Height;
            Cursor = Cursors.SizeAll;
            Focusable = true;
            ToolTip = "Press and drag onto any knob or slider: the modulator drives it. Without a mouse: the modulation matrix's + Add.";
            AutomationProperties.SetName(this, "Modulation output: " + (source?.Machine?.Name ?? "modulator") + ". Drag onto a knob to modulate it.");
        }

        /// <summary>The modulator the handle drags.</summary>
        public IModulator Source => source;

        /// <summary>A drag is in progress (tests).</summary>
        public bool IsDragging => dragging;

        protected override void OnMouseEnter(MouseEventArgs e) { hover = true; InvalidateVisual(); }
        protected override void OnMouseLeave(MouseEventArgs e) { hover = false; InvalidateVisual(); }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            pressed = true;
            down = e.GetPosition(this);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!pressed || dragging) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - down.X) + Math.Abs(p.Y - down.Y) < DragThreshold) return;
            dragging = true;
            ModulationBoard.BeginDrag(source);
            Cursor = Cursors.SizeAll;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (!pressed) return;
            bool was = dragging;
            Finish(was ? ScreenPoint(e.GetPosition(this)) : null);
            e.Handled = true;
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            if (pressed) Finish(null);   // the drag ended without a release (the window lost the pointer)
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape && pressed)
            {
                Finish(null);
                e.Handled = true;
            }
            else if (e.Key is Key.Enter or Key.Space && !pressed && ModulationBoard.MatrixHook != null)
            {
                ModulationBoard.MatrixHook(source);   // the keyboard's way: the matrix, with this source ready to add from
                e.Handled = true;
            }
        }

        (double X, double Y)? ScreenPoint(Point local)
        {
            var screen = this.PointToScreen(local);
            return (screen.X, screen.Y);
        }

        void Finish((double X, double Y)? at)
        {
            pressed = false;
            bool was = dragging;
            dragging = false;
            Cursor = Cursors.SizeAll;
            ReleaseMouseCapture();
            if (was) ModulationBoard.EndDrag(at);
            InvalidateVisual();
        }

        protected override void OnRender(Avalonia.Media.DrawingContext dc)
        {
            double w = Bounds.Width > 0 ? Bounds.Width : Width, h = Bounds.Height > 0 ? Bounds.Height : Height;
            var c = canvas.On(dc);
            var ring = KnobModel.ModulationRing;
            c.Rect(new NeoRect(0, 0, w, h), hover || dragging ? Palette.PanelEdge : Palette.KnobTrack, new NeoStroke(dragging ? ring : accent, 1.5), 4);
            c.Ellipse(new NeoPoint(12, h / 2), 4, 4, ring, new NeoStroke(ring, 1));
            c.Line(new NeoPoint(16, h / 2), new NeoPoint(26, h / 2), new NeoStroke(ring, 2));
            c.Text(label, 32, (h - 13) / 2, 11, Palette.Text, maxWidth: Math.Max(1, w - 38));
            DrawFocusRing(dc, new Rect(0, 0, w, h));
        }
    }
}
