// Fork (docs/specs/studio-mvp.md S2): the parameter window's slider, ported from BuzzGUI.Common's
// ParameterSlider (WPF): a thumb as wide as one value's share of the width (at least 12 pixels),
// dragged; the areas left and right of it page by a sixth of the range, repeating while held; the
// wheel and the arrow keys step by one; a digit opens the value editor. Its geometry is
// ParameterSliderGeometry, as the WPF control computes it.

using ReBuzz.Studio.Shell.Design;
using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ReBuzz.Studio.Parameters
{
    using Control = Avalonia.Controls.Control;

    /// <summary>The WPF slider's arithmetic (RecalculateWidths, thumb_MouseMove, the page buttons).</summary>
    internal static class ParameterSliderGeometry
    {
        /// <summary>The thumb's left edge and width, in whole pixels, for a value.</summary>
        public static (int X, int Width) Thumb(int value, int min, int max, int totalWidth, int minThumbWidth)
        {
            int count = Math.Max(1, max - min + 1);
            int w = Math.Max(minThumbWidth, totalWidth / count);
            int x = w > minThumbWidth
                ? (value - min) * totalWidth / count
                : (value - min) * (totalWidth - w) / Math.Max(1, max - min);
            return (x, w);
        }

        /// <summary>
        /// The value under a dragged thumb: <paramref name="pointerX"/> is the pointer, <paramref name="grab"/>
        /// where on the thumb it was pressed (pointer minus thumb left, at the press).
        /// </summary>
        public static int DragValue(double pointerX, double grab, int min, int max, int totalWidth, int thumbWidth, int minThumbWidth)
        {
            int value;
            if (thumbWidth > minThumbWidth)
                value = min + (int)pointerX * (max - min + 1) / Math.Max(1, totalWidth);
            else
            {
                int x = (int)(pointerX - grab);
                int travel = Math.Max(1, totalWidth - thumbWidth);
                value = (int)Math.Round(min + (double)x * (max - min) / travel);
            }
            return Math.Clamp(value, min, Math.Max(min, max));
        }

        /// <summary>What a click beside the thumb moves by.</summary>
        public static int PageStep(int min, int max) => (max - min) / 6 + 1;
    }

    internal sealed class ParameterSlider : Control
    {
        int minimum, maximum = 1, value;
        double indicator = -1;

        // Gesture state: the value when it began, and how it goes on.
        int gestureStart;
        bool dragging;
        double grab;
        int pageDirection;
        readonly DispatcherTimer repeat = new DispatcherTimer();
        double wheel;

        public ParameterSlider()
        {
            Focusable = true;
            ClipToBounds = true;
            repeat.Tick += (s, e) => RepeatPage();
        }

        public int Minimum { get => minimum; set { minimum = value; InvalidateVisual(); } }

        public int Maximum { get => maximum; set { maximum = value; InvalidateVisual(); } }

        public int MinimumThumbWidth { get; set; } = 12;

        /// <summary>The value shown (the parameter's); setting it doesn't raise the events.</summary>
        public int Value
        {
            get => value;
            set
            {
                int v = Math.Clamp(value, minimum, Math.Max(minimum, maximum));
                if (v == this.value) return;
                this.value = v;
                InvalidateVisual();
            }
        }

        /// <summary>A position marker, 0–1 (a machine's metadata); below 0: none.</summary>
        public double Indicator { get => indicator; set { indicator = value; InvalidateVisual(); } }

        /// <summary>The user moved the value (each step of a gesture).</summary>
        public event Action<int> ValueChanging;

        /// <summary>A gesture ended: (value before it, value after it), one undo step. Wheel and keys: one step each.</summary>
        public event Action<int, int, bool> GestureEnded;

        /// <summary>A digit typed on the slider: (the digit), for the value editor.</summary>
        public event Action<int> DigitTyped;

        int TotalWidth => (int)Bounds.Width;

        (int X, int Width) ThumbNow => ParameterSliderGeometry.Thumb(value, minimum, maximum, TotalWidth, MinimumThumbWidth);

        /// <summary>Where the thumb is (for tests and the value editor's placement).</summary>
        public Rect ThumbBounds
        {
            get
            {
                var (x, w) = ThumbNow;
                return new Rect(x, 0, w, Bounds.Height);
            }
        }

        // Its share of the row's width comes from the row (a star column); on its own it asks for little.
        protected override Size MeasureOverride(Size availableSize) => new Size(160, 14);

        public override void Render(DrawingContext context)
        {
            var b = new Rect(Bounds.Size);
            // The sunken track (the WPF template's two borders), then the thumb.
            context.FillRectangle(ParameterTheme.Track, b);
            context.FillRectangle(ParameterTheme.TrackShadow, new Rect(0, 0, b.Width, 1));
            context.FillRectangle(ParameterTheme.TrackShadow, new Rect(0, 0, 1, b.Height));
            context.FillRectangle(ParameterTheme.TrackLight, new Rect(0, b.Height - 1, b.Width, 1));
            context.FillRectangle(ParameterTheme.TrackLight, new Rect(b.Width - 1, 0, 1, b.Height));
            var (x, w) = ThumbNow;
            var thumb = new Rect(x + 1, 1, Math.Max(1, w - 2), Math.Max(1, b.Height - 2));
            context.DrawRectangle(IsFocused ? ParameterTheme.ThumbFocused : ParameterTheme.Thumb, null, new RoundedRect(thumb, 3));
            if (indicator >= 0)
            {
                double ix = w / 2.0 + indicator * (TotalWidth - w) - 1;
                context.FillRectangle(ParameterTheme.Indicator, new Rect((int)ix, 0, 1, b.Height));
            }
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            InvalidateVisual();
        }

        void Change(int v)
        {
            v = Math.Clamp(v, minimum, Math.Max(minimum, maximum));
            if (v == value) return;
            value = v;
            InvalidateVisual();
            ValueChanging?.Invoke(v);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            var point = e.GetCurrentPoint(this);
            if (!point.Properties.IsLeftButtonPressed) return;
            Focus();
            e.Handled = true;
            gestureStart = value;
            var (x, w) = ThumbNow;
            double px = point.Position.X;
            if (px >= x && px < x + w)
            {
                dragging = true;
                grab = px - x;
                e.Pointer.Capture(this);
            }
            else
            {
                // A page button: one step now, then repeated while held (WPF's RepeatButton).
                pageDirection = px < x ? -1 : 1;
                e.Pointer.Capture(this);
                Change(value + pageDirection * ParameterSliderGeometry.PageStep(minimum, maximum));
                repeat.Interval = TimeSpan.FromMilliseconds(400);
                repeat.Start();
            }
        }

        void RepeatPage()
        {
            repeat.Interval = TimeSpan.FromMilliseconds(50);
            Change(value + pageDirection * ParameterSliderGeometry.PageStep(minimum, maximum));
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!dragging) return;
            var (_, w) = ThumbNow;
            Change(ParameterSliderGeometry.DragValue(e.GetPosition(this).X, grab, minimum, maximum, TotalWidth, w, MinimumThumbWidth));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (dragging || pageDirection != 0) e.Pointer.Capture(null);
            EndGesture();
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            EndGesture();
        }

        void EndGesture()
        {
            if (!dragging && pageDirection == 0) return;
            dragging = false;
            pageDirection = 0;
            repeat.Stop();
            if (value != gestureStart) GestureEnded?.Invoke(gestureStart, value, false);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            e.Handled = true;
            wheel += e.Delta.Y;
            int steps = (int)wheel;
            if (steps == 0) return;
            wheel -= steps;
            Step(steps);
        }

        void Step(int by)
        {
            int before = value;
            Change(value + by);
            if (value != before) GestureEnded?.Invoke(before, value, true);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyModifiers != KeyModifiers.None) return;
            switch (e.Key)
            {
                case Key.Left:
                    Step(-1);
                    e.Handled = true;
                    return;
                case Key.Right:
                    Step(1);
                    e.Handled = true;
                    return;
            }
            int digit = e.Key >= Key.D0 && e.Key <= Key.D9 ? e.Key - Key.D0
                : e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
            if (digit >= 0 && DigitTyped != null)
            {
                e.Handled = true;
                DigitTyped(digit);
            }
        }

        /// <summary>Sets a value as the user's own one-step gesture (the value editor's result).</summary>
        public void Commit(int v)
        {
            int before = value;
            Change(v);
            if (value != before) GestureEnded?.Invoke(before, value, false);
        }
    }

    /// <summary>The parameter window's colours: the studio's dark palette over the WPF window's shapes.</summary>
    internal static class ParameterTheme
    {
        public static readonly IBrush Background = StudioTokens.SurfacePanelBrush;
        public static readonly IBrush Text = StudioTokens.TextPrimaryBrush;
        public static readonly IBrush ValueText = StudioTokens.TextSecondaryBrush;
        public static readonly IBrush DimText = StudioTokens.TextPlaceholderBrush;
        public static readonly IBrush Track = StudioTokens.Brush(StudioTokens.SurfaceBase);
        public static readonly IBrush TrackShadow = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0));
        public static readonly IBrush TrackLight = StudioTheme.Solid(Colors.White, 0x40);
        public static readonly IBrush Thumb = StudioTokens.TextSecondaryBrush;
        public static readonly IBrush ThumbFocused = StudioTokens.TextPrimaryBrush;
        public static readonly IBrush Indicator = StudioTheme.Solid(Colors.White, 0x60);
        public static readonly IBrush Separator = StudioTokens.SurfaceBorderBrush;
        public static readonly IBrush Lock = StudioTokens.AccentBrush;
    }
}
