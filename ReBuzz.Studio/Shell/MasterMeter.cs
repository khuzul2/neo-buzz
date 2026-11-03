// Fork (docs/specs/machine-safety.md §3.3): the transport's master meter. Two peak bars of what goes
// to the audio device, measured before the safety limiter (SafeMaster); the clip indicator, red with
// the highest over ("+3.2 dB") once the signal went above the clip threshold, latched until clicked,
// as Ableton's master meter does; and a "limiting" lamp, lit while the safety limiter turns the
// output down. The main window refreshes it ten times a second.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    using Control = Avalonia.Controls.Control;

    internal sealed class MasterMeter : StackPanel
    {
        public static readonly IBrush ClipBrush = new SolidColorBrush(Color.FromRgb(0xd9, 0x2b, 0x2b));
        static readonly IBrush LimitBrush = new SolidColorBrush(Color.FromRgb(0xf0, 0xa0, 0x20));
        static readonly IBrush Off = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3a));
        static readonly IBrush OffText = new SolidColorBrush(Color.FromRgb(0x9a, 0x9a, 0x9a));

        readonly Func<ReBuzzCore> core;
        readonly Bars bars = new Bars { Width = 120, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        readonly Button clip = new Button { MinWidth = 64, Padding = new Thickness(6, 1), FontSize = 11, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly Border limit = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock limitText = new TextBlock { Text = "LIM", FontSize = 10, FontWeight = FontWeight.Bold };

        public MasterMeter(Func<ReBuzzCore> core)
        {
            this.core = core;
            Orientation = Orientation.Horizontal;
            Spacing = 4;
            limit.Child = limitText;
            ToolTip.SetTip(bars, "The master, before the safety limiter on the audio output");
            ToolTip.SetTip(clip, "Clip indicator: the highest peak above the clip threshold, before the safety limiter. Click to clear it.");
            ToolTip.SetTip(limit, "The safety limiter is turning the output down (Preferences ▸ Audio)");
            clip.Click += (s, e) => Clear();
            Children.Add(bars);
            Children.Add(clip);
            Children.Add(limit);
            Show("", false);
        }

        /// <summary>The clip indicator's text now: the latched over ("+3.2 dB"), empty when none (tests).</summary>
        public string OverText { get; private set; } = "";

        /// <summary>Whether the limiting lamp is lit (tests).</summary>
        public bool Limiting { get; private set; }

        public Button ClipButton => clip;

        /// <summary>A click on the clip indicator.</summary>
        public void Clear()
        {
            core()?.ClearMasterOver();
            Refresh();
        }

        public void Refresh()
        {
            var c = core();
            if (c == null) return;
            c.RefreshMasterLamps();
            var (l, r) = c.SafeMaster.TakePeaks();
            bars.Set(l, r);
            Show(c.MasterOverText, c.MasterLimiting);
        }

        void Show(string over, bool limiting)
        {
            OverText = over;
            Limiting = limiting;
            bool on = over.Length > 0;
            clip.Content = on ? over : "clip";
            clip.Background = on ? ClipBrush : Off;
            clip.Foreground = on ? Brushes.White : OffText;
            limit.Background = limiting ? LimitBrush : Off;
            limitText.Foreground = limiting ? Brushes.Black : OffText;
        }

        /// <summary>Two peak bars, −60 to +6 dBFS, green to yellow to red above 0 dBFS; they fall back in about half a second.</summary>
        sealed class Bars : Control
        {
            const double Floor = -60, Top = 6;
            static readonly IBrush Back = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
            static readonly IBrush Green = new SolidColorBrush(Color.FromRgb(0x3c, 0xc8, 0x50));
            static readonly IBrush Yellow = new SolidColorBrush(Color.FromRgb(0xe6, 0xd2, 0x32));
            static readonly IPen ZeroPen = new Pen(new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)), 1);
            double left = Floor, right = Floor;

            public void Set(float peakLeft, float peakRight)
            {
                left = Math.Max(Db(peakLeft), left - 6);
                right = Math.Max(Db(peakRight), right - 6);
                InvalidateVisual();
            }

            static double Db(float peak) => peak > 0 ? Math.Clamp(20 * Math.Log10(peak), Floor, Top) : Floor;

            double X(double db) => (db - Floor) / (Top - Floor) * Bounds.Width;

            public override void Render(DrawingContext ctx)
            {
                var size = Bounds.Size;
                ctx.FillRectangle(Back, new Rect(size));
                double h = (size.Height - 3) / 2;
                Bar(ctx, left, 1, h);
                Bar(ctx, right, 2 + h, h);
                double zero = X(0);
                ctx.DrawLine(ZeroPen, new Point(zero, 0), new Point(zero, size.Height));
            }

            void Bar(DrawingContext ctx, double db, double y, double h)
            {
                if (db <= Floor) return;
                double x = X(db), six = X(-6), zero = X(0);
                ctx.FillRectangle(Green, new Rect(0, y, Math.Min(x, six), h));
                if (x > six) ctx.FillRectangle(Yellow, new Rect(six, y, Math.Min(x, zero) - six, h));
                if (x > zero) ctx.FillRectangle(ClipBrush, new Rect(zero, y, x - zero, h));
            }
        }
    }
}
