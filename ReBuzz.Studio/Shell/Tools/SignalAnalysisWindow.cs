// Fork (docs/specs/ux-overhaul.md P6b, §9): "Signal Analysis..." on a machine's menu (and the Master's), after BuzzGUI.MachineView's
// SignalAnalysisWindow: the spectrum (2048-point FFT, a choice of windows, a peak line that falls slowly) and the scope of what the
// machine sends out. For a machine with several outputs, the output to look at is chosen; for the Master, its tap. The tap
// copies into a ring made up front (SignalTap: no allocation, no lock on the engine's thread); the window draws at 30 frames a second
// and subscribes only while it is open.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;


namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class SignalAnalysisWindow : ToolWindow
    {
        public const string PlacementName = "SignalAnalysis";
        const int FftSize = 2048;

        readonly IMachine machine;
        readonly bool master;
        readonly SignalTap tap = new SignalTap();
        readonly Spectrum spectrum = new Spectrum(FftSize);
        readonly double[] left = new double[FftSize], right = new double[FftSize];
        readonly double[] dbL = new double[FftSize / 2], dbR = new double[FftSize / 2], peak = new double[FftSize / 2];
        readonly ComboBox windows = new ComboBox { ItemsSource = Spectrum.Windows, SelectedIndex = 0, MinWidth = 140 };
        readonly ComboBox outputs = new ComboBox { MinWidth = 180 };
        readonly Plot plot = new Plot();
        readonly TextBlock info = Label("", secondary: true);
        IMachineConnection selected;
        bool listening;
        DateTime lastTick = DateTime.UtcNow;

        SignalAnalysisWindow(StudioHost host, IMachine machine) : base(host, "Signal Analysis - " + machine.Name, PlacementName, 720, 460, refreshMs: 33)
        {
            this.machine = machine;
            master = machine.DLL.Info.Type == MachineType.Master;
            AutomationProperties.SetName(this, Title);
            AutomationProperties.SetName(windows, "FFT window");
            AutomationProperties.SetName(outputs, "Output to analyse");
            AutomationProperties.SetName(plot, "Spectrum and scope");
            Array.Fill(peak, -120);
            Array.Fill(dbL, -120);
            Array.Fill(dbR, -120);
            windows.SelectionChanged += (s, e) => spectrum.SetWindow(Spectrum.Windows[Math.Max(0, windows.SelectedIndex)]);
            outputs.SelectionChanged += (s, e) =>
            {
                if (outputs.SelectedIndex >= 0 && outputs.SelectedIndex < machine.Outputs.Count) Select(machine.Outputs[outputs.SelectedIndex]);
            };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12, 8) };
            bar.Children.Add(Label("Window"));
            bar.Children.Add(windows);
            if (!master)
            {
                bar.Children.Add(Label("Output"));
                bar.Children.Add(outputs);
            }
            var reset = new Button { Content = "Reset peaks" };
            reset.Click += (s, e) => Array.Fill(peak, -120);
            AutomationProperties.SetName(reset, "Reset peaks");
            bar.Children.Add(reset);
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            DockPanel.SetDock(info, Dock.Bottom);
            info.Margin = new Thickness(12, 4, 12, 8);
            dock.Children.Add(bar);
            dock.Children.Add(info);
            dock.Children.Add(plot);
            Content = dock;
            RefreshOutputs();
            Closed += (s, e) => { if (current == this) current = null; };
        }

        static SignalAnalysisWindow current;
        internal static SignalAnalysisWindow Current => current;

        /// <summary>Opens the analysis of <paramref name="machine"/> (its own window; a second ask brings it to the front).</summary>
        public static SignalAnalysisWindow Open(StudioHost host, IMachine machine, Window owner = null)
        {
            if (current != null && current.machine == machine)
            {
                current.Activate();
                return current;
            }
            current?.Close();
            current = new SignalAnalysisWindow(host, machine);
            ShowOver(current, owner);
            return current;
        }

        internal SignalTap Tap => tap;
        internal Spectrum Analyser => spectrum;
        internal double[] SpectrumLeft => dbL;
        internal double[] SpectrumRight => dbR;
        internal string InfoText => info.Text;
        internal ComboBox WindowChoice => windows;
        internal ComboBox OutputChoice => outputs;
        internal bool Listening => listening;

        void RefreshOutputs()
        {
            if (master) return;
            var names = machine.Outputs.Select(c => "to " + c.Destination.Name).ToList();
            if (outputs.ItemCount == names.Count && outputs.Items.Cast<string>().SequenceEqual(names) && selected != null && machine.Outputs.Contains(selected)) return;
            outputs.ItemsSource = names;
            var keep = selected != null && machine.Outputs.Contains(selected) ? selected : machine.Outputs.FirstOrDefault();
            Select(keep);
            outputs.SelectedIndex = keep == null ? -1 : machine.Outputs.ToList().IndexOf(keep);
        }

        void Select(IMachineConnection c)
        {
            if (c == selected) return;
            if (selected != null && listening) selected.Tap -= OnTap;
            selected = c;
            if (selected != null && listening) selected.Tap += OnTap;
        }

        void OnTap(float[] samples, bool stereo, SongTime time) => tap.Add(samples, stereo);

        protected override void Attach()
        {
            listening = true;
            if (master) Core.MasterTap += OnTap;
            else if (selected != null) selected.Tap += OnTap;
        }

        protected override void Detach()
        {
            listening = false;
            if (master) { if (Core != null) Core.MasterTap -= OnTap; }
            else if (selected != null) selected.Tap -= OnTap;
        }

        internal override void Tick()
        {
            RefreshOutputs();
            int n = tap.CopyLatest(left, right);
            spectrum.Magnitudes(left, dbL);
            spectrum.Magnitudes(right, dbR);
            var now = DateTime.UtcNow;
            double fall = 20 * Math.Min(0.5, (now - lastTick).TotalSeconds);     // the peak line falls 20 dB a second
            lastTick = now;
            for (int i = 0; i < peak.Length; i++) peak[i] = Math.Max(Math.Max(dbL[i], dbR[i]), peak[i] - fall);
            int rate = Math.Max(1, Core?.SelectedAudioDriverSampleRate ?? 44100);
            int top = 0;
            for (int i = 1; i < peak.Length; i++) if (dbL[i] > dbL[top] || dbR[i] > dbR[top]) top = i;
            double hz = (double)top * rate / FftSize;
            double level = Math.Max(dbL[top], dbR[top]);
            info.Text = n == 0 || level <= -119 ? "No signal" : $"Peak {hz:0} Hz at {level:0.0} dB";
            plot.Show(this, rate, left, right);
        }

        /// <summary>The drawing: the spectrum on a log frequency axis (20 Hz to the Nyquist) and -90 to 0 dB, the scope under it.</summary>
        sealed class Plot : Avalonia.Controls.Control
        {
            SignalAnalysisWindow owner;
            int rate = 44100;
            double[] l, r;

            public void Show(SignalAnalysisWindow o, int sampleRate, double[] left, double[] right)
            {
                owner = o; rate = sampleRate; l = left; r = right;
                InvalidateVisual();
            }

            public override void Render(DrawingContext ctx)
            {
                var b = new Rect(Bounds.Size);
                ctx.FillRectangle(StudioTokens.SurfaceViewBrush, b);
                if (owner == null || b.Width < 40 || b.Height < 60) return;
                double scopeH = Math.Min(110, b.Height * 0.3);
                var spec = new Rect(40, 8, b.Width - 52, b.Height - scopeH - 40);
                var grid = new Pen(StudioTokens.GridLineBrush, 1);
                var text = StudioTokens.TextSecondaryBrush;
                for (int db = 0; db >= -90; db -= 15)
                {
                    double y = spec.Y + spec.Height * (-db / 90.0);
                    ctx.DrawLine(grid, new Point(spec.X, y), new Point(spec.Right, y));
                    Text(ctx, db + "", 4, y - 7, text);
                }
                double nyq = rate / 2.0, lo = Math.Log10(20), hi = Math.Log10(nyq);
                foreach (double f in new double[] { 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 })
                {
                    if (f >= nyq) continue;
                    double x = spec.X + spec.Width * (Math.Log10(f) - lo) / (hi - lo);
                    ctx.DrawLine(grid, new Point(x, spec.Y), new Point(x, spec.Bottom));
                    Text(ctx, f >= 1000 ? (f / 1000) + "k" : f + "", x - 8, spec.Bottom + 2, text);
                }
                Curve(ctx, owner.peak, spec, nyq, lo, hi, new Pen(StudioTokens.WarningBrush, 1));
                Curve(ctx, owner.dbL, spec, nyq, lo, hi, new Pen(StudioTokens.PlayBrush, 1.5));
                Curve(ctx, owner.dbR, spec, nyq, lo, hi, new Pen(StudioTokens.AccentBrush, 1.5));
                // The scope: the last 512 frames.
                var sc = new Rect(40, b.Height - scopeH - 8 + 22, b.Width - 52, scopeH - 22);
                ctx.DrawRectangle(null, grid, sc);
                Scope(ctx, l, sc, new Pen(StudioTokens.PlayBrush, 1));
                Scope(ctx, r, sc, new Pen(StudioTokens.AccentBrush, 1));
            }

            static void Text(DrawingContext ctx, string s, double x, double y, IBrush brush) =>
                ctx.DrawText(new FormattedText(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush), new Point(x, y));

            void Curve(DrawingContext ctx, double[] db, Rect area, double nyq, double lo, double hi, Pen pen)
            {
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    bool first = true;
                    for (int k = 1; k < db.Length; k++)
                    {
                        double f = k * nyq / db.Length;
                        if (f < 20) continue;
                        double x = area.X + area.Width * (Math.Log10(f) - lo) / (hi - lo);
                        double y = area.Y + area.Height * Math.Clamp(-db[k] / 90.0, 0, 1);
                        if (first) { c.BeginFigure(new Point(x, y), false); first = false; }
                        else c.LineTo(new Point(x, y));
                    }
                    if (!first) c.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, g);
            }

            static void Scope(DrawingContext ctx, double[] x, Rect area, Pen pen)
            {
                if (x == null) return;
                const int frames = 512;
                var g = new StreamGeometry();
                using (var c = g.Open())
                {
                    for (int i = 0; i < frames; i++)
                    {
                        double v = Math.Clamp(x[x.Length - frames + i], -1, 1);
                        var p = new Point(area.X + area.Width * i / (frames - 1), area.Y + area.Height * (0.5 - v / 2));
                        if (i == 0) c.BeginFigure(p, false); else c.LineTo(p);
                    }
                    c.EndFigure(false);
                }
                ctx.DrawGeometry(null, pen, g);
            }
        }
    }
}
