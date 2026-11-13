// Fork (docs/specs/ux-overhaul.md P3g, decision 80): the top bar's tempo: a number to drag (up raises it, a pixel step of 3 per BPM, Shift ten times
// as fast), scroll or step with the arrow keys, and double-click to type. Not a spin box: a drag is the way a tempo is felt out. The text box inside
// is the one place a typed value goes; Enter keeps it, Esc puts the old one back.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    internal sealed class TempoField : Border
    {
        public const int Min = 16, Max = 500;

        /// <summary>Logical pixels of a vertical drag for one BPM.</summary>
        public const double PixelsPerBpm = 3;

        readonly TextBox box = new TextBox
        {
            FontFamily = new Avalonia.Media.FontFamily("monospace"),
            FontSize = StudioTokens.FontTitle,
            Width = 64,
            MinHeight = 28,
            Padding = new Thickness(6, 3),
            BorderThickness = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            HorizontalContentAlignment = HorizontalAlignment.Right,
            VerticalContentAlignment = VerticalAlignment.Center,
            Focusable = true,
        };

        int value = 126;
        bool dragging;
        double startY;
        int startValue;

        public TempoField()
        {
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(4);
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
            Child = box;
            Avalonia.Automation.AutomationProperties.SetName(box, "Tempo (BPM)");
            ToolTip.SetTip(this, "Tempo in beats per minute.\nDrag up or down, or scroll, to change it (Shift: ten at a time); double-click to type one.");
            AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            box.GotFocus += (s, e) => Cursor = new Cursor(StandardCursorType.Ibeam);
            box.LostFocus += (s, e) =>
            {
                Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
                Commit();
            };
            box.KeyDown += OnKey;
            Show();
        }

        /// <summary>A value was chosen by the person (a drag step, the wheel, a key, a typed number), already clamped.</summary>
        public event Action<int> Edited;

        public TextBox Box => box;

        public int Value
        {
            get => value;
            set
            {
                this.value = Math.Clamp(value, Min, Max);
                if (!box.IsFocused) Show();
            }
        }

        /// <summary>The person is dragging or typing: the bar leaves the field alone.</summary>
        public bool Busy => dragging || box.IsFocused;

        /// <summary>Starts typing: the number selected.</summary>
        public void BeginEdit()
        {
            box.Focus();
            box.SelectAll();
        }

        void Show() => box.Text = value.ToString(CultureInfo.InvariantCulture);

        void Choose(int v)
        {
            v = Math.Clamp(v, Min, Max);
            if (v == value) return;
            value = v;
            Show();
            Edited?.Invoke(v);
        }

        void Commit()
        {
            if (int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) Choose(v);
            Show();
        }

        void OnPressed(object sender, PointerPressedEventArgs e)
        {
            if (box.IsFocused || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            if (e.ClickCount >= 2)
            {
                BeginEdit();
                return;
            }
            dragging = true;
            startY = e.GetPosition(this).Y;
            startValue = value;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!dragging) return;
            double rate = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            Choose(startValue + (int)Math.Round((startY - e.GetPosition(this).Y) / PixelsPerBpm * rate));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (!dragging) return;
            dragging = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            dragging = false;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (e.Delta.Y == 0) return;
            Choose(value + Math.Sign(e.Delta.Y) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1));
            e.Handled = true;
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            int step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            switch (e.Key)
            {
                case Key.Up:
                    Commit();
                    Choose(value + step);
                    e.Handled = true;
                    break;
                case Key.Down:
                    Commit();
                    Choose(value - step);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Commit();
                    TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Show();
                    TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
                    e.Handled = true;
                    break;
            }
        }
    }
}
