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
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xF2, 0x5C, 0x7A);
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
        /// Levels over time (dB, −36 … +12): the input after the gain (grey), the output's true peak
        /// (accent) under the ceiling line, and the gain reduction hanging from the top (0 … 24 dB).
        /// </summary>
        static void DrawHistory(INeoCanvas g, NeoRect r, EditorContext ctx, NeoLimiterMachine machine, HistoryBuffers h)
        {
            const double lo = -36, hi = 12, grRange = 24;
            var plot = new NeoRect(r.X + 30, r.Y + 6, r.Width - 36, r.Height - 12);
            double Y(double db) => plot.Bottom - (Math.Clamp(db, lo, hi) - lo) / (hi - lo) * plot.Height;
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (double d = -30; d <= hi; d += 6)
            {
                g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
                if (((int)d) % 12 == 0) g.Text(d.ToString("+0;−0;0"), r.X + 26, Y(d) - 7, 9.5, Palette.DimText, align: NeoAlign.Right);
            }
            double ceiling = ctx.Value(P.Ceiling);
            int count = LimiterEngine.HistoryLength;
            if (machine != null) machine.Engine.History(h.In, h.Out, h.Gr, count);
            double X(int i) => plot.Left + i * plot.Width / (count - 1);

            var input = new NeoPath().MoveTo(X(0), plot.Bottom);
            var output = new NeoPath().MoveTo(X(0), plot.Bottom);
            var reduction = new NeoPath().MoveTo(X(0), plot.Top);
            for (int i = 0; i < count; i++)
            {
                input.LineTo(X(i), Y(DspMath.GainToDb(Math.Max(1e-6f, h.In[i]))));
                output.LineTo(X(i), Y(DspMath.GainToDb(Math.Max(1e-6f, h.Out[i]))));
                reduction.LineTo(X(i), plot.Top + Math.Clamp(h.Gr[i], 0, grRange) / grRange * plot.Height * 0.5);
            }
            input.LineTo(X(count - 1), plot.Bottom).Close();
            output.LineTo(X(count - 1), plot.Bottom).Close();
            reduction.LineTo(X(count - 1), plot.Top).Close();
            g.Path(input, InputColor, NeoStroke.None);
            g.Path(output, AccentColor.WithAlpha(0x90), NeoStroke.None);
            g.Path(reduction, AccentColor.Tint(0.35).WithAlpha(0x60), new NeoStroke(AccentColor.Tint(0.35), 1));
            g.Line(plot.Left, Y(ceiling), plot.Right, Y(ceiling), new NeoStroke(Palette.Text.WithAlpha(0xA0), 1, false));
            g.Text($"ceiling {Params.Table[P.Ceiling].Describe(ctx.Raw(P.Ceiling))}TP", plot.Right - 4, Y(ceiling) - 15, 10, Palette.Text, align: NeoAlign.Right);
            if (machine != null)
            {
                g.Text($"GR {machine.Engine.GainReductionDb:F1} dB", plot.Left + 4, plot.Top + 2, 10, Palette.Text);
                g.Text($"latency {machine.Engine.Latency} samples", plot.Left + 4, plot.Bottom - 16, 9.5, Palette.DimText);
            }
        }
    }
}
