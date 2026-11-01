#nullable disable
// Neo Limiter's editor (docs/specs/machines.md §4): drive and ceiling, the last four seconds of
// input, output and gain reduction, release and detector settings, and true-peak meters. WPF lays
// the editor out; the history draws on the kit's canvas (INeoCanvas).

using System;
using System.Windows;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Limiter
{
    internal sealed class NeoLimiterEditor : NeoEditor
    {
        // Violet: clear of the meters' red for "over 0 dB", which a limiter's output never is.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xBE, 0x7C, 0xFF);
        static readonly NeoColor InputColor = NeoColor.FromRgb(0x4A, 0x52, 0x60);

        protected override string Title => "Neo Limiter";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoLimiterMachine;
            var history = new HistoryBuffers();

            var drive = Layout.Column(
                new ParamKnob(ctx, P.InputGain, diameter: 58),
                new ParamKnob(ctx, P.Ceiling, diameter: 58));

            var graph = new GraphView(330, 196, (g, r) => DrawHistory(g, r, ctx, machine, history), animate: true);

            var release = Layout.Column(
                new ParamKnob(ctx, P.Release),
                new ParamChoice(ctx, P.AutoRelease, "Auto release", 36),
                Layout.Gap(1, 8),
                Layout.Row(new ParamKnob(ctx, P.Lookahead), new ParamKnob(ctx, P.Link)));

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output (true peak)", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196),
                Layout.Gap(4),
                new Meter("Gain reduction", () => (float)DspMath.DbToGain(-(machine?.Engine.GainReductionDb ?? 0)), AccentColor, 196, reduction: true));

            return Layout.Row(
                new Section("Drive", AccentColor, drive),
                new Section("History", AccentColor, graph),
                new Section("Release", AccentColor, release),
                new Section("Meters", AccentColor, meters));
        }

        sealed class HistoryBuffers
        {
            public readonly float[] In = new float[LimiterEngine.HistoryLength];
            public readonly float[] Out = new float[LimiterEngine.HistoryLength];
            public readonly float[] Gr = new float[LimiterEngine.HistoryLength];
        }

        /// <summary>
        /// The last four seconds in two lanes. Above: levels (dB, −36 … +12), the input after the
        /// gain (grey) and the output's true peak (accent) under the ceiling line. Below: the gain
        /// reduction, growing down from 0 to 24 dB.
        /// </summary>
        static void DrawHistory(INeoCanvas g, NeoRect r, EditorContext ctx, NeoLimiterMachine machine, HistoryBuffers h)
        {
            const double lo = -36, hi = 12, grRange = 24;
            var levels = new NeoRect(r.X + 32, r.Y + 8, r.Width - 40, (r.Height - 16) * 0.68);
            var gr = new NeoRect(levels.X, levels.Bottom + 8, levels.Width, r.Bottom - 8 - (levels.Bottom + 8));
            double Y(double db) => levels.Bottom - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * levels.Height;
            double G(double db) => gr.Top + Math.Clamp(db, 0, grRange) / grRange * gr.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = lo; d <= hi; d += 12)
            {
                g.Line(levels.Left, Y(d), levels.Right, Y(d), grid);
                g.Text(d.ToString("+0;−0;0"), r.X + 28, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
            foreach (double d in new[] { 0.0, 12, 24 })
            {
                g.Line(gr.Left, G(d), gr.Right, G(d), grid);
                g.Text(d == 0 ? "GR" : "−" + d.ToString("0"), r.X + 28, G(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }

            double ceiling = ctx.Value(P.Ceiling);
            int count = LimiterEngine.HistoryLength;
            if (machine != null) machine.Engine.History(h.In, h.Out, h.Gr, count);
            double X(int i) => levels.Left + i * levels.Width / (count - 1);
            var input = new NeoPath().MoveTo(X(0), levels.Bottom);
            var output = new NeoPath().MoveTo(X(0), levels.Bottom);
            var reduction = new NeoPath().MoveTo(X(0), gr.Top);
            for (int i = 0; i < count; i++)
            {
                input.LineTo(X(i), Y(DspMath.GainToDb(Math.Max(1e-6f, h.In[i]))));
                output.LineTo(X(i), Y(DspMath.GainToDb(Math.Max(1e-6f, h.Out[i]))));
                reduction.LineTo(X(i), G(h.Gr[i]));
            }
            input.LineTo(X(count - 1), levels.Bottom).Close();
            output.LineTo(X(count - 1), levels.Bottom).Close();
            reduction.LineTo(X(count - 1), gr.Top).Close();
            g.Path(input, InputColor, NeoStroke.None);
            g.Path(output, AccentColor.WithAlpha(0xC0), NeoStroke.None);
            g.Path(reduction, AccentColor.Tint(0.25).WithAlpha(0x80), new NeoStroke(AccentColor.Tint(0.25), 1));
            g.Line(levels.Left, Y(ceiling), levels.Right, Y(ceiling), new NeoStroke(Palette.Text.WithAlpha(0xB0), 1, false));
            g.Text($"ceiling {Params.Table[P.Ceiling].Describe(ctx.Raw(P.Ceiling))}TP", levels.Right - 4, Y(ceiling) - 15, 10, Palette.Text, align: NeoAlign.Right);
            g.Text("in", levels.Left + 4, levels.Top + 2, 10, Palette.DimText);
            g.Text("out", levels.Left + 22, levels.Top + 2, 10, AccentColor);
            if (machine != null)
            {
                g.Text($"{machine.Engine.GainReductionDb:F1} dB", gr.Right - 4, gr.Bottom - 16, 10, Palette.Text, align: NeoAlign.Right);
                g.Text($"latency {machine.Engine.Latency} samples", levels.Left + 4, levels.Bottom - 16, 9.5, Palette.DimText);
            }
        }
    }
}
