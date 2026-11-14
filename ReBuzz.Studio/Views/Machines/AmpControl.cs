// Fork (docs/specs/studio-mvp.md S3): a wire's amp control, code-drawn after BuzzGUI.MachineView's
// AmpControl and the default theme: an amp knob and (for stereo inputs) a pan knob, each with its
// value, and the insert and disconnect buttons. It opens on the wire's arrow, sets the wire live
// while a knob turns, and when the pointer leaves it the change becomes one undo step.

using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Machines
{
    using Control = Avalonia.Controls.Control;

    internal sealed class AmpControl : Control
    {
        const double MinAmp = 66;
        const int PanCenter = 0x4000, PanRight = 0x8000;
        const double SnapPixels = 8;

        /// <summary>Knob drag heights (AmpControl.xaml: the amp knob's DragHeight 128; Knob's default 100).</summary>
        const double AmpDragHeight = 128, PanDragHeight = 100;

        static readonly double MaxDb = Decibel.FromAmplitude((double)0xfffe / 0x4000);

        readonly ReBuzzCore core;
        IMachineConnection connection;
        int oldAmp, oldPan, newAmp, newPan;
        double ampValue, panValue;          // the knobs: 0..1, and -16384..16384

        enum Part { None, AmpKnob, PanKnob, Insert, Disconnect }
        Part dragging, hovering;
        double dragStartY, dragStartValue;

        public AmpControl(ReBuzzCore core)
        {
            this.core = core;
            IsVisible = false;
            Width = 86;
            ClipToBounds = false;
        }

        /// <summary>The insert button's menu (an effect into the wire); set by MachineView.</summary>
        public Func<IMachineConnection, ContextMenu> InsertMenu { get; set; }

        public IMachineConnection Connection => IsVisible ? connection : null;

        bool HasPan => connection?.HasPan == true;

        // The theme's layout: 1 px border, 4 px padding; knobs 24 px; values 50 px wide; buttons 38 px.
        Rect AmpKnob => new Rect(5, 5, 24, 24);
        Rect AmpText => new Rect(29, 5, 52, 24);
        Rect PanKnob => new Rect(5, 33, 24, 24);
        Rect PanText => new Rect(29, 33, 52, 24);
        double ButtonsTop => HasPan ? 61 : 33;
        Rect InsertButton => new Rect(5, ButtonsTop, 38, 38);
        Rect DisconnectButton => new Rect(43, ButtonsTop, 38, 38);

        /// <summary>
        /// Opens on a wire at a point (its parent's pixels), the amp knob under the pointer
        /// (AmpControl.Activate). With a left press it starts turning the amp knob.
        /// </summary>
        public void Show(IMachineConnection wire, Point at, PointerPressedEventArgs press)
        {
            if (wire != connection || !IsVisible)
            {
                if (IsVisible) Hide();
                connection = wire;
                Canvas.SetLeft(this, at.X - 16);
                Canvas.SetTop(this, at.Y - 16);
                Height = HasPan ? 104 : 76;
                newAmp = oldAmp = wire.Amp;
                ampValue = oldAmp <= 0 ? 0 : oldAmp >= 0xfffe ? 1 : Math.Min(1, (Decibel.FromAmplitude(oldAmp / (double)0x4000) + MinAmp) / (MinAmp + MaxDb));
                newPan = oldPan = wire.Pan;
                panValue = oldPan - PanCenter;
                hovering = Part.None;
                IsVisible = true;
                InvalidateVisual();
            }
            if (press != null)
            {
                BeginDrag(Part.AmpKnob, press.GetPosition(Parent as Visual ?? this).Y);
                press.Pointer.Capture(this);
            }
        }

        /// <summary>Closes and makes the change one undo step (AmpControl.Deactivate); or, without commit, just closes.</summary>
        public void Hide(bool commit = true)
        {
            if (!IsVisible) return;
            IsVisible = false;
            dragging = Part.None;
            var c = connection;
            connection = null;
            if (commit && c != null && (newAmp != oldAmp || newPan != oldPan))
                MachineEdits.SetWire(core, c, oldAmp, oldPan, newAmp, newPan);
        }

        void BeginDrag(Part knob, double y)
        {
            dragging = knob;
            dragStartY = y;
            dragStartValue = knob == Part.AmpKnob ? ampValue : panValue;
            if (knob == Part.PanKnob)
            {
                double snap = SnapPixels * 2 * PanCenter / PanDragHeight;
                if (dragStartValue < 0) dragStartValue -= snap;
                else if (dragStartValue > 0) dragStartValue += snap;
            }
        }

        /// <summary>Knob.g_MouseMove: up turns right; Control turns ten times finer; the pan knob sticks at the centre.</summary>
        void DragTo(double y, KeyModifiers mods)
        {
            double dy = dragStartY - y;
            if (mods.HasFlag(KeyModifiers.Control)) dy /= 10;
            if (dragging == Part.AmpKnob)
                SetAmp(Math.Clamp(dragStartValue + dy / AmpDragHeight, 0, 1));
            else if (dragging == Part.PanKnob)
            {
                double range = 2.0 * PanCenter;
                double v = dragStartValue + dy * range / PanDragHeight;
                double snap = SnapPixels * range / PanDragHeight;
                if ((v > 0 && v < snap) || (v < 0 && v > -snap)) v = 0;
                else if (v > 0) v -= snap;
                else if (v < 0) v += snap;
                SetPan(Math.Clamp(v, -PanCenter, PanCenter));
            }
        }

        void SetAmp(double v)
        {
            ampValue = v;
            newAmp = v == 0 ? 0 : (int)Math.Round(Decibel.ToAmplitude(v * (MinAmp + MaxDb) - MinAmp) * 0x4000);
            if (connection != null) core.SongCore.SetConnectionParameter(connection, 0, connection.Amp, newAmp);
            InvalidateVisual();
        }

        void SetPan(double v)
        {
            panValue = v;
            newPan = (int)Math.Round(v + PanCenter);
            if (connection != null) core.SongCore.SetConnectionParameter(connection, 1, connection.Pan, newPan);
            InvalidateVisual();
        }

        Part PartAt(Point p)
        {
            if (AmpKnob.Contains(p)) return Part.AmpKnob;
            if (HasPan && PanKnob.Contains(p)) return Part.PanKnob;
            if (InsertButton.Contains(p)) return Part.Insert;
            if (DisconnectButton.Contains(p)) return Part.Disconnect;
            return Part.None;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Handled = true;
            var pt = e.GetCurrentPoint(this);
            var part = PartAt(pt.Position);
            var kind = pt.Properties.PointerUpdateKind;
            if (kind == PointerUpdateKind.MiddleButtonPressed)
            {
                Disconnect();
                return;
            }
            if (kind == PointerUpdateKind.RightButtonPressed)
            {
                // A right click resets a knob: 0 dB, centre.
                if (part == Part.AmpKnob) SetAmp(Math.Min(1, MinAmp / (MinAmp + MaxDb)));
                else if (part == Part.PanKnob) SetPan(0);
                return;
            }
            if (kind != PointerUpdateKind.LeftButtonPressed) return;
            switch (part)
            {
                case Part.AmpKnob:
                case Part.PanKnob:
                    BeginDrag(part, e.GetPosition(Parent as Visual ?? this).Y);
                    e.Pointer.Capture(this);
                    break;
                case Part.Insert:
                    var wire = connection;
                    var menu = InsertMenu?.Invoke(wire);
                    Hide();
                    if (menu != null && Parent is Control parent) menu.Open(parent);
                    break;
                case Part.Disconnect:
                    Disconnect();
                    break;
            }
        }

        void Disconnect()
        {
            var c = connection;
            // The disconnect is the undo step: the wire goes as it was before the knobs turned, so undo brings that back.
            if (c != null && (newAmp != oldAmp || newPan != oldPan))
            {
                core.SongCore.SetConnectionParameter(c, 0, newAmp, oldAmp);
                core.SongCore.SetConnectionParameter(c, 1, newPan, oldPan);
            }
            Hide(commit: false);
            if (c != null) MachineEdits.Disconnect(core, c);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (dragging != Part.None)
            {
                DragTo(e.GetPosition(Parent as Visual ?? this).Y, e.KeyModifiers);
                return;
            }
            var p = e.GetPosition(this);
            if (!new Rect(Bounds.Size).Contains(p))
            {
                Hide();
                return;
            }
            var part = PartAt(p);
            if (part != hovering)
            {
                hovering = part;
                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (dragging == Part.None) return;
            dragging = Part.None;
            e.Pointer.Capture(null);
            e.Handled = true;
            if (!new Rect(Bounds.Size).Contains(e.GetPosition(this))) Hide();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            if (dragging == Part.None) Hide();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            // Knob.OnMouseWheel: 1/78 of the range a notch, an eighth of that with Control.
            double step = e.Delta.Y * (e.KeyModifiers.HasFlag(KeyModifiers.Control) ? 1 / (8 * 78.0) : 1 / 78.0);
            var part = PartAt(e.GetPosition(this));
            if (part == Part.AmpKnob) SetAmp(Math.Clamp(ampValue + step, 0, 1));
            else if (part == Part.PanKnob) SetPan(Math.Clamp(panValue + step * 2 * PanCenter, -PanCenter, PanCenter));
            e.Handled = true;
        }

        public string AmpTextValue => newAmp > 0 ? string.Format(CultureInfo.InvariantCulture, "{0:F1}dB", Decibel.FromAmplitude(newAmp * (1.0 / 0x4000))) : "-inf.dB";

        public string PanTextValue
        {
            get
            {
                int v = newPan;
                if (v == 0) return "L";
                if (v == PanCenter) return "C";
                if (v == PanRight) return "R";
                return v < PanCenter
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F0}L", (PanCenter - v) * (100.0 / PanCenter))
                    : string.Format(CultureInfo.InvariantCulture, "{0:F0}R", (v - PanCenter) * (100.0 / PanCenter));
            }
        }

        #region Drawing

        // Fork (ux-overhaul.md P4b): translucent black under white values on a dark canvas; a pale veil under dark values on the light one.
        static IBrush Background => NeoBuzz.Editor.Tokens.IsLight ? LightBack : DarkBack;

        static readonly IBrush LightBack = new SolidColorBrush(Color.FromArgb(0xF0, 0xF4, 0xF4, 0xF7));

        static readonly IBrush DarkBack = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0x60, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0x70, 0, 0, 0), 1) },
        };

        static readonly IBrush Glass = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            Opacity = 0.5,
            GradientStops =
            {
                new GradientStop(Colors.WhiteSmoke, 0.2), new GradientStop(Colors.Transparent, 0.4), new GradientStop(Colors.WhiteSmoke, 0.5),
                new GradientStop(Colors.Transparent, 0.75), new GradientStop(Colors.WhiteSmoke, 0.9), new GradientStop(Colors.Transparent, 1),
            },
        };

        static readonly Typeface Face = new Typeface(FontFamily.Default);

        public override void Render(DrawingContext ctx)
        {
            if (connection == null) return;
            ctx.DrawRectangle(Background, new Pen(Brushes.Black, 1), new Rect(Bounds.Size).Deflate(0.5));
            DrawKnob(ctx, AmpKnob, ampValue, hovering == Part.AmpKnob || dragging == Part.AmpKnob);
            DrawValue(ctx, AmpText, AmpTextValue);
            if (HasPan)
            {
                DrawKnob(ctx, PanKnob, (panValue + PanCenter) / (2.0 * PanCenter), hovering == Part.PanKnob || dragging == Part.PanKnob);
                DrawValue(ctx, PanText, PanTextValue);
            }
            DrawButton(ctx, InsertButton, hovering == Part.Insert, insert: true);
            DrawButton(ctx, DisconnectButton, hovering == Part.Disconnect, insert: false);
        }

        /// <summary>BuzzGUI.Common's knob (Generic.xaml): a grey disc, glass, a black ring and pointer, ±135°.</summary>
        static void DrawKnob(DrawingContext ctx, Rect r, double normalized, bool hot)
        {
            var c = r.Center;
            double radius = r.Width / 2 * 15 / 16;
            ctx.DrawEllipse(hot ? Brushes.Gold : Brushes.LightGray, null, c, radius, radius);
            ctx.DrawEllipse(Glass, null, c, radius, radius);
            var pen = new Pen(Brushes.Black, 1.5);
            ctx.DrawEllipse(null, pen, c, radius, radius);
            double angle = (normalized * 2 * 135 - 135) * Math.PI / 180;
            ctx.DrawLine(pen, c, new Point(c.X + Math.Sin(angle) * r.Width / 2, c.Y - Math.Cos(angle) * r.Height / 2));
        }

        /// <summary>The value in the primary text colour, stretched to 50 px wide or 16 px high (a Viewbox).</summary>
        static void DrawValue(DrawingContext ctx, Rect r, string text)
        {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 12, StudioTokens.TextPrimaryBrush);
            double scale = Math.Min(50 / Math.Max(1, ft.Width), 16 / Math.Max(1, ft.Height));
            var c = r.Center;
            using (ctx.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(c.X, c.Y)))
                ctx.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }

        /// <summary>The theme's grey round buttons (gfx/InsertButtonGray, DisconnectButtonGray): a plus, and scissors.</summary>
        static void DrawButton(DrawingContext ctx, Rect r, bool hot, bool insert)
        {
            var c = r.Center;
            var face = new RadialGradientBrush
            {
                GradientStops = { new GradientStop(hot ? Color.FromRgb(0xF0, 0xF0, 0xF0) : Color.FromRgb(0xC8, 0xC8, 0xC8), 0), new GradientStop(hot ? Color.FromRgb(0xA0, 0xA0, 0xA0) : Color.FromRgb(0x80, 0x80, 0x80), 1) },
            };
            ctx.DrawEllipse(face, new Pen(new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), 2), c, 15, 15);
            var pen = new Pen(Brushes.Black, 3) { LineCap = PenLineCap.Round };
            if (insert)
            {
                ctx.DrawLine(pen, new Point(c.X - 7, c.Y), new Point(c.X + 7, c.Y));
                ctx.DrawLine(pen, new Point(c.X, c.Y - 7), new Point(c.X, c.Y + 7));
            }
            else
            {
                var thin = new Pen(Brushes.Black, 2);
                ctx.DrawLine(thin, new Point(c.X - 5, c.Y - 8), new Point(c.X + 3, c.Y + 3));
                ctx.DrawLine(thin, new Point(c.X + 5, c.Y - 8), new Point(c.X - 3, c.Y + 3));
                ctx.DrawEllipse(null, thin, new Point(c.X - 4.5, c.Y + 5.5), 2.8, 2.8);
                ctx.DrawEllipse(null, thin, new Point(c.X + 4.5, c.Y + 5.5), 2.8, 2.8);
            }
        }

        #endregion
    }
}
