#nullable disable
// Neo Shift's editor (docs/specs/neo-shift.md): the live spectrum (what comes in, what goes out) over 30 Hz - 20 kHz with a picture of the shift
// drawn over it - the input's spectrum line moved by the ratio (Pitch) or by the hertz (Frequency), or the carrier's marker (Ring); the three
// modes as tabs (the Mode parameter, kept in step both ways) over the page of the mode; Follow notes and Root; and the output row (cuts, Amount, Mix,
// Output, the meters). One source for WPF and Avalonia (the Neo editor kit); the picture draws on the kit's canvas (INeoCanvas) from a lock-free
// snapshot, with no allocation per frame.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Shift
{
    internal sealed class NeoShiftEditor : NeoEditor
    {
        // Violet in the classic app; the Modulation identity in the studio (ux-overhaul.md P2a).
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Modulation, NeoColor.FromRgb(0x9B, 0x7B, 0xFF));
        static readonly NeoColor ShiftColor = NeoColor.FromRgb(0xFF, 0xB0, 0x40);
        static readonly double[] DbLines = { 0.0, -30.0, -60.0, -90.0 };
        static readonly string[] DbLabels = { "0", "-30", "-60", "-90" };
        static readonly double[] HzLines = { 100.0, 1000.0, 10000.0 };
        static readonly string[] HzLabels = { "100 Hz", "1 kHz", "10 kHz" };
        // The picture's label, rebuilt only when its numbers change (the picture is drawn 30 times a second).
        static int labelMode = -1;
        static double labelValue = double.NaN;
        static string label = "";

        protected override string Title => "Neo Shift";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoShiftMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            var view = new ShiftView();
            var inPath = new NeoPath();
            var outPath = new NeoPath();
            var moved = new NeoPath();
            var graph = new GraphView(520, 160, (g, r) => DrawSpectrum(g, r, ctx, machine, view, inPath, outPath, moved), animate: true);

            var pitch = Layout.Row(
                Layout.Row(new ParamKnob(ctx, P.Pitch), new ParamKnob(ctx, P.Fine), new ParamKnob(ctx, P.Detune)),
                Layout.Gap(8),
                Layout.Column(
                    new ParamDropdown(ctx, P.Quality, "Quality", 92),
                    Layout.Gap(1, 4)),
                Layout.Gap(8),
                new ParamKnob(ctx, P.Window));

            var frequency = Layout.Row(
                new ParamKnob(ctx, P.Shift), new ParamKnob(ctx, P.ShiftFine),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.Feedback), new ParamKnob(ctx, P.Spread)));

            var ring = Layout.Row(
                new ParamKnob(ctx, P.Carrier),
                Layout.Gap(8),
                Layout.Column(
                    new ParamDropdown(ctx, P.Shape, "Shape", 92),
                    Layout.Gap(1, 4)),
                Layout.Gap(8),
                new ParamKnob(ctx, P.Depth));

            var notes = Layout.Row(
                new ParamChoice(ctx, P.Follow, "Follow notes", 90),
                Layout.Gap(8),
                new ParamKnob(ctx, P.Root));

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 96),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 96));

            // The cuts, Output, then Amount and Mix side by side, Amount first, the same size as on every Neo effect (machines.md, "Mix and Amount"); Mix last.
            var output = Layout.Row(
                Layout.Row(new ParamKnob(ctx, P.LowCut), new ParamKnob(ctx, P.HighCut), new ParamKnob(ctx, P.Output)),
                Layout.Gap(10),
                new ParamKnob(ctx, P.Amount, diameter: 40),
                new ParamKnob(ctx, P.Mix, diameter: 40),
                Layout.Gap(10),
                meters);

#if AVALONIA
            var pages = new Pages(AccentColor, PageWidth(pitch, frequency, ring), PageHeight(pitch, frequency, ring));
            pages.Add("Pitch", pitch);
            pages.Add("Frequency", frequency);
            pages.Add("Ring", ring);
            // The tabs are the Mode parameter: a tab changes it, and a change of it (automation, a preset) shows its tab.
            var mode = ctx.Bind(P.Mode, raw => pages.Selected = raw);
            pages.Selected = Math.Clamp(ctx.Raw(P.Mode), 0, 2);
            pages.SelectedChanged += i => { if (ctx.Raw(P.Mode) != i) mode.Set(i); };
            return Layout.Column(
                S("Spectrum", graph),
                S("Mode", pages),
                S("Notes", notes),
                S("Output", output));
#else
            return Layout.Column(
                S("Spectrum", graph),
                S("Mode", new ParamDropdown(ctx, P.Mode, "Mode", 92)),
                Layout.Row(S("Pitch", pitch), S("Frequency", frequency)),
                Layout.Row(S("Ring", ring), S("Notes", notes)),
                S("Output", output));
#endif
        }

        /// <summary>The spectrum in and out (dB, -90 ... 0) over 30 Hz - 20 kHz, with the picture of the shift over it.</summary>
        static void DrawSpectrum(INeoCanvas g, NeoRect r, EditorContext ctx, NeoShiftMachine machine, ShiftView view, NeoPath inPath, NeoPath outPath, NeoPath moved)
        {
            var plot = new NeoRect(r.X + 30, r.Y + 8, r.Width - 36, r.Height - 28);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            const double top = 0, bottom = -90, lo = 30, hi = 20000;
            double Y(double db) => plot.Top + (top - Math.Clamp(db, bottom, top)) / (top - bottom) * plot.Height;
            double X(double hz) => plot.Left + Math.Log(Math.Clamp(hz, lo, hi) / lo) / Math.Log(hi / lo) * plot.Width;
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

            int mode = Math.Clamp(ctx.Raw(P.Mode), 0, 2);
            if (machine != null) machine.Engine.ReadView(view);
            else
            {
                // No machine behind the editor (the gallery): a plain shape so the picture reads.
                for (int b = 0; b < ShiftView.Bands; b++)
                {
                    double f = lo * Math.Pow(hi / lo, (b + 0.5) / ShiftView.Bands);
                    view.In[b] = (float)(-18 - 22 * Math.Log10(f / 100.0 + 1) - 14 * Math.Abs(Math.Sin(b * 0.37)));
                    view.Out[b] = view.In[b] - 6;
                }
            }
            const int bands = ShiftView.Bands;
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

            // The shift itself: what the engine runs now (the effective values: Amount and the notes included).
            double ratio = machine != null ? machine.Engine.Ratio : Math.Pow(2, ctx.Value(P.Pitch) / 12.0);
            double shiftHz = machine != null ? machine.Engine.ShiftHz : ctx.Value(P.Shift) + ctx.Value(P.ShiftFine);
            double carrier = machine != null ? machine.Engine.CarrierHz : ctx.Value(P.Carrier);
            double value = mode == 0 ? ratio : mode == 1 ? shiftHz : carrier;
            if (mode == 2)
            {
                // The carrier: a marker line with the sidebands' spacing it makes.
                double cx = X(carrier);
                g.Line(cx, plot.Top, cx, plot.Bottom, new NeoStroke(ShiftColor, 2));
            }
            else
            {
                // The input's line moved: by the ratio (the same distance on the log axis everywhere) or by the hertz (a growing distance in octaves down low).
                moved.Clear();
                bool started = false;
                for (int b = 0; b < bands; b++)
                {
                    double f = lo * Math.Pow(hi / lo, (b + 0.5) / bands);
                    double f2 = mode == 0 ? f * ratio : f + shiftHz;
                    if (f2 < lo || f2 > hi) continue;
                    double x = X(f2), y = Y(view.In[b]);
                    if (!started) { moved.MoveTo(x, y); started = true; }
                    else moved.LineTo(x, y);
                }
                g.Path(moved, NeoFill.None, new NeoStroke(ShiftColor, 1.5));
            }
            if (mode != labelMode || value != labelValue)
            {
                labelMode = mode;
                labelValue = value;
                label = mode == 0 ? $"shift {12 * Math.Log2(Math.Max(ratio, 1e-6)):+0.00;-0.00;0.00} st"
                      : mode == 1 ? $"shift {shiftHz:+0.0;-0.0;0.0} Hz"
                      : $"carrier {carrier:0.0} Hz";
            }
            g.Text(label, plot.Right - 4, plot.Top + 2, 9, ShiftColor, align: NeoAlign.Right);
        }

#if AVALONIA
        // A page is as wide and as high as the largest of the three, so that the tabs do not move the sections below.
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
