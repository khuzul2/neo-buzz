// Fork (docs/specs/machine-safety.md §3.3): the transport's master meter. Two peak bars of what goes
// to the audio device, measured before the safety limiter (SafeMaster); the clip indicator, red with
// the highest over ("+3.2 dB") once the signal went above the clip threshold, latched until clicked,
// as Ableton's master meter does; and a "limiting" lamp, lit while the safety limiter turns the
// output down. Fork (ux-overhaul.md P3g, decision 81): the bars have a scale, -60 to 0 dB with its numbers under them, and the meter
// refreshes itself thirty times a second while shown (the peaks fall at 60 dB a second whatever the rate).

using ReBuzz.Studio.Shell.Design;
using System;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    using Control = Avalonia.Controls.Control;

    internal sealed class MasterMeter : StackPanel
    {
        public static readonly IBrush ClipBrush = StudioTokens.RecordBrush;
        static readonly IBrush LimitBrush = StudioTokens.WarningBrush;
        static readonly IBrush Off = StudioTokens.SurfaceBorderBrush;
        static readonly IBrush OffText = StudioTokens.TextSecondaryBrush;

        readonly Func<ReBuzzCore> core;
        readonly Bars bars = new Bars { Width = 200, Height = 36, VerticalAlignment = VerticalAlignment.Center };
        readonly Button clip = new Button { MinWidth = 64, VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastSeconds;
        readonly Border limit = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 1), VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock limitText = new TextBlock { Text = "LIM", FontSize = 11, FontWeight = FontWeight.Bold };

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
            timer.Tick += (s, e) => Refresh();
            Children.Add(bars);
            Children.Add(clip);
            Children.Add(limit);
            Show("", false);
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            lastSeconds = clock.Elapsed.TotalSeconds;
            timer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            timer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>The bars' levels now, in dB (tests): left, right.</summary>
        public (double Left, double Right) Levels => bars.Levels;

        /// <summary>The scale's lowest and highest dB.</summary>
        public const double FloorDb = -60, TopDb = 0;

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
            double now = clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - lastSeconds, 0.001, 0.25);
            lastSeconds = now;
            bars.Set(l, r, dt);
            Show(c.MasterOverText, c.MasterLimiting);
        }

        void Show(string over, bool limiting)
        {
            OverText = over;
            Limiting = limiting;
            bool on = over.Length > 0;
            clip.Content = on ? over : "clip";
            clip.Background = on ? ClipBrush : Off;
            clip.Foreground = on ? StudioTokens.White : OffText;
            limit.Background = limiting ? LimitBrush : Off;
            limitText.Foreground = limiting ? StudioTokens.Black : OffText;
        }

        /// <summary>Two peak bars on a -60 to 0 dBFS scale, with the numbers under them; green, yellow above -6 dB; they fall back at 60 dB a second.</summary>
        sealed class Bars : Control
        {
            const double Floor = FloorDb, Top = TopDb, FallPerSecond = 60, BarHeight = 9;
            static readonly double[] Marks = { -60, -40, -20, -10, 0 };
            static readonly IBrush Back = StudioTokens.SurfaceViewBrush;
            static readonly IBrush Green = StudioTokens.MeterNormalBrush;
            static readonly IBrush Yellow = StudioTokens.MeterHotBrush;
            static readonly IPen ZeroPen = new Pen(StudioTokens.ControlTrackBrush, 1);
            static readonly Typeface Face = new Typeface(new FontFamily("monospace"));
            double left = Floor, right = Floor;

            public (double Left, double Right) Levels => (left, right);

            public void Set(float peakLeft, float peakRight, double seconds)
            {
                double fall = FallPerSecond * seconds;
                left = Math.Max(Db(peakLeft), left - fall);
                right = Math.Max(Db(peakRight), right - fall);
                InvalidateVisual();
            }

            static double Db(float peak) => peak > 0 ? Math.Clamp(20 * Math.Log10(peak), Floor, Top) : Floor;

            double X(double db) => (db - Floor) / (Top - Floor) * (Bounds.Width - 1);

            public override void Render(DrawingContext ctx)
            {
                var size = Bounds.Size;
                ctx.FillRectangle(Back, new Rect(0, 0, size.Width, 2 * BarHeight + 3));
                Bar(ctx, left, 1);
                Bar(ctx, right, 2 + BarHeight);
                // The scale: a tick and its number under each mark, the last one right-aligned.
                var text = StudioTokens.TextSecondaryBrush;
                foreach (double mark in Marks)
                {
                    double x = X(mark);
                    ctx.DrawLine(ZeroPen, new Point(x, 0), new Point(x, 2 * BarHeight + 5));
                    var ft = new FormattedText(mark == 0 ? "0" : "−" + (-mark).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, Face, StudioTokens.FontCaption, text);
                    double at = mark == Floor ? 0 : mark == Top ? size.Width - ft.Width : x - ft.Width / 2;
                    ctx.DrawText(ft, new Point(at, 2 * BarHeight + 4));
                }
            }

            void Bar(DrawingContext ctx, double db, double y)
            {
                if (db <= Floor) return;
                double x = X(db), six = X(-6);
                ctx.FillRectangle(Green, new Rect(0, y, Math.Min(x, six), BarHeight));
                if (x > six) ctx.FillRectangle(Yellow, new Rect(six, y, x - six, BarHeight));
            }
        }
    }
}
