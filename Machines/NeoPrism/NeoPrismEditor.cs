#nullable disable
// Neo Prism's editor (docs/specs/neo-prism.md): the live spectrum (what comes in, what goes out) over 30 Hz – 20 kHz with the spectral
// delay's time curve drawn over it; a row of the five sections with their on/off lights, in the order they run; the page of the section
// selected below it (tabs in the studio); and Output last (FFT size, Amount, Mix, the meters). One source for WPF and Avalonia (the Neo
// editor kit); the picture draws on the kit's canvas (INeoCanvas) from a lock-free snapshot, with no allocation per frame.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Prism
{
    internal sealed class NeoPrismEditor : NeoEditor
    {
        // Violet in the classic app; the Modulation identity in the studio (ux-overhaul.md P2a).
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Modulation, NeoColor.FromRgb(0x9B, 0x7B, 0xFF));
        static readonly NeoColor CurveColor = NeoColor.FromRgb(0xFF, 0xB0, 0x40);
        static readonly double[] DbLines = { 0.0, -30.0, -60.0, -90.0 };
        static readonly string[] DbLabels = { "0", "-30", "-60", "-90" };
        static readonly double[] HzLines = { 100.0, 1000.0, 10000.0 };
        static readonly string[] HzLabels = { "100 Hz", "1 kHz", "10 kHz" };
        // The delay's label, rebuilt only when its numbers change (the picture is drawn 30 times a second).
        static double labelLow = double.NaN, labelHigh = double.NaN;
        static string label = "";

        protected override string Title => "Neo Prism";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoPrismMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // The sections in the order they run, each with its light.
            var sections = Layout.Row(
                new ParamChoice(ctx, P.PitchOn, "Pitch", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.ScrOn, "Scramble", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.FreezeOn, "Freeze", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.BlurOn, "Blur", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.DelayOn, "Delay", 70));

            var view = new PrismView();
            var inPath = new NeoPath();
            var outPath = new NeoPath();
            var curve = new NeoPath();
            var graph = new GraphView(520, 160, (g, r) => DrawSpectrum(g, r, ctx, machine, view, inPath, outPath, curve), animate: true);

            var pitch = Layout.Row(
                Layout.Row(new ParamKnob(ctx, P.Semi), new ParamKnob(ctx, P.Cents)),
                Layout.Gap(8),
                new ParamChoice(ctx, P.FormantKeep, "Formant keep", 90));

            var scramble = Layout.Row(
                Layout.Column(
                    new ParamDropdown(ctx, P.ScrSync, "Sync", 92),
                    Layout.Gap(1, 4),
                    new ParamKnob(ctx, P.ScrRate)),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.ScrAmount), new ParamKnob(ctx, P.ScrWidth), new ParamKnob(ctx, P.ScrSeed)));

            var freeze = Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.FreezeTrig, "Trigger", 56),
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamDropdown(ctx, P.FreezeEvery, "Every", 92), Layout.Gap(6), new ParamDropdown(ctx, P.FreezeLen, "Length", 92))),
                Layout.Gap(8),
                new ParamKnob(ctx, P.Hold));

            var blur = Layout.Row(new ParamKnob(ctx, P.BlurTime), new ParamKnob(ctx, P.Smear));

            var delay = Layout.Row(
                new ParamDropdown(ctx, P.DelSync, "Sync", 92),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.DelLow), new ParamKnob(ctx, P.DelHigh), new ParamKnob(ctx, P.DelFeedback), new ParamKnob(ctx, P.DelDamping)));

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 96),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 96));

            // FFT size, then Amount and Mix side by side, Amount first, the same size as on every Neo effect (machines.md, "Mix and Amount"); Mix last.
            var output = Layout.Row(
                new ParamDropdown(ctx, P.FftSize, "FFT size", 80),
                Layout.Gap(10),
                new ParamKnob(ctx, P.Amount, diameter: 40),
                new ParamKnob(ctx, P.Mix, diameter: 40),
                Layout.Gap(10),
                meters);

#if AVALONIA
            var pages = new Pages(AccentColor, PageWidth(pitch, scramble, freeze, blur, delay), PageHeight(pitch, scramble, freeze, blur, delay), "Neo Prism");
            pages.Add("Pitch", pitch);
            pages.Add("Scramble", scramble);
            pages.Add("Freeze", freeze);
            pages.Add("Blur", blur);
            pages.Add("Delay", delay);
            return Layout.Column(
                S("Spectrum", graph),
                S("Sections", sections),
                S("Section", pages),
                S("Output", output));
#else
            return Layout.Column(
                S("Spectrum", graph),
                S("Sections", sections),
                Layout.Row(S("Pitch", pitch), S("Scramble", scramble)),
                Layout.Row(S("Freeze", freeze), S("Blur", blur)),
                S("Delay", delay),
                S("Output", output));
#endif
        }

        /// <summary>The spectrum in and out (dB, −90 … 0) over 30 Hz – 20 kHz, with the delay's time curve over it (0 … 2000 ms on the right).</summary>
        static void DrawSpectrum(INeoCanvas g, NeoRect r, EditorContext ctx, NeoPrismMachine machine, PrismView view, NeoPath inPath, NeoPath outPath, NeoPath curve)
        {
            var plot = new NeoRect(r.X + 30, r.Y + 8, r.Width - 66, r.Height - 28);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            const double top = 0, bottom = -90;
            double Y(double db) => plot.Top + (top - Math.Clamp(db, bottom, top)) / (top - bottom) * plot.Height;
            double X(double hz) => plot.Left + Math.Log(Math.Clamp(hz, 30, 20000) / 30.0) / Math.Log(20000.0 / 30.0) * plot.Width;
            for (int i = 0; i < DbLines.Length; i++)
            {
                double db = DbLines[i];
                g.Line(plot.Left, Y(db), plot.Right, Y(db), grid);
                g.Text(DbLabels[i], plot.Left - 4, Y(db) - 5, 9, Palette.DimText, align: NeoAlign.Right);
            }
            for (int i = 0; i < HzLines.Length; i++)
            {
                double hz = HzLines[i];
                g.Line(X(hz), plot.Top, X(hz), plot.Bottom, grid);
                g.Text(HzLabels[i], X(hz), plot.Bottom + 4, 9, Palette.DimText, align: NeoAlign.Center);
            }

            if (machine != null) machine.Engine.ReadView(view);
            else
            {
                // No machine behind the editor (the gallery): a plain shape so the picture reads.
                for (int b = 0; b < PrismView.Bands; b++)
                {
                    double f = 30 * Math.Pow(20000.0 / 30.0, (b + 0.5) / PrismView.Bands);
                    view.In[b] = (float)(-18 - 22 * Math.Log10(f / 100.0 + 1) - 14 * Math.Abs(Math.Sin(b * 0.37)));
                    view.Out[b] = view.In[b] - 6;
                }
            }
            const int bands = PrismView.Bands;
            inPath.Clear();
            outPath.Clear();
            outPath.MoveTo(plot.Left, plot.Bottom);
            for (int b = 0; b < bands; b++)
            {
                double x = plot.Left + (b + 0.5) / bands * plot.Width;
                inPath.LineTo(x, Y(view.In[b]));
                outPath.LineTo(x, Y(view.Out[b]));
            }
            outPath.LineTo(plot.Right, plot.Bottom);
            outPath.Close();
            g.Path(inPath, NeoFill.None, new NeoStroke(Palette.DimText, 1));
            g.Path(outPath, AccentColor.WithAlpha(0x55), new NeoStroke(AccentColor, 1.5));

            // The delay's time curve: Low time at 20 Hz to High time at 20 kHz, straight on the log-frequency axis (what the engine runs).
            var eff = machine?.Engine.Effective;
            double lowMs = eff != null ? eff.Value(P.DelLow) : ctx.Value(P.DelLow), highMs = eff != null ? eff.Value(P.DelHigh) : ctx.Value(P.DelHigh);
            bool on = ctx.Value(P.DelayOn) > 0;
            double TimeY(double ms) => plot.Bottom - Math.Clamp(ms, 0, 2000) / 2000.0 * plot.Height;
            var cc = on ? CurveColor : CurveColor.WithAlpha(0x70);
            curve.Clear();
            for (int i = 0; i <= 48; i++)
            {
                double hz = 30 * Math.Pow(20000.0 / 30.0, i / 48.0);
                double pos = Math.Clamp(Math.Log(Math.Max(hz, 20.0) / 20.0, 2) / Math.Log(1000.0, 2), 0, 1);
                curve.LineTo(X(hz), TimeY(lowMs + (highMs - lowMs) * pos));
            }
            g.Path(curve, NeoFill.None, new NeoStroke(cc, 2));
            if (lowMs != labelLow || highMs != labelHigh)
            {
                labelLow = lowMs;
                labelHigh = highMs;
                label = $"delay {lowMs:0} → {highMs:0} ms";
            }
            g.Text(label, plot.Right - 4, plot.Top + 2, 9, cc, align: NeoAlign.Right);
            g.Text("2 s", plot.Right + 4, plot.Top, 9, Palette.DimText);
        }

#if AVALONIA
        // A page is as wide and as high as the largest of the five, so that the tabs do not move the sections below.
        static double PageWidth(params UIElement[] pages)
        {
            double w = 0;
            foreach (var p in pages) { p.Measure(Avalonia.Size.Infinity); w = Math.Max(w, p.DesiredSize.Width); }
            return Math.Ceiling(w);
        }

        static double PageHeight(params UIElement[] pages)
        {
            double h = 0;
            foreach (var p in pages) { p.Measure(Avalonia.Size.Infinity); h = Math.Max(h, p.DesiredSize.Height); }
            return Math.Ceiling(h);
        }
#endif
    }
}
