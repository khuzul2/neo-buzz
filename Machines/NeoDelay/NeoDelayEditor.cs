#nullable disable
// Neo Delay's editor (docs/specs/machines.md §4): the time, a picture of the echoes, the loop, the
// wow and ducking, and the output. The picture: the echoes of a centred sound over the next few
// seconds, left above the line and right below, each as tall as its level (the filters left out),
// on the song's beats; under it, one pass through the loop's filters (and the fourth, fainter), 20 Hz
// – 20 kHz. WPF lays the editor out; the picture draws on the kit's canvas (INeoCanvas), so the
// studio's Avalonia flavour draws the same.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Delay
{
    internal sealed class NeoDelayEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xFF, 0x6F, 0x52);
        static readonly NeoColor RightColor = NeoColor.FromRgb(0x7E, 0xC8, 0xFF);

        const int Points = 160;
        const double MinHz = 20, MaxHz = 20000, FloorDb = -48;

        protected override string Title => "Neo Delay";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        // The picture's buffers, kept between frames.
        readonly double[] hz = new double[Points], pass = new double[Points];
        readonly NeoPath curve1 = new NeoPath(), curve4 = new NeoPath();

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoDelayMachine;
            for (int i = 0; i < Points; i++) hz[i] = MinHz * Math.Pow(MaxHz / MinHz, i / (double)(Points - 1));

            var time = Layout.Column(
                new ParamChoice(ctx, P.Mode, vertical: true, cellWidth: 80),
                Layout.Gap(1, 6),
                new ParamDropdown(ctx, P.Sync, width: 112),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Feel, cellWidth: 56),
                Layout.Row(new ParamKnob(ctx, P.Time, diameter: 52), new ParamKnob(ctx, P.Offset)));

            var echoes = new GraphView(330, 214, (g, r) => DrawEchoes(g, r, ctx, machine), animate: true)
            {
                ToolTip = "The echoes of a centred sound: left above the line, right below, as tall as their level; the song's beats behind. " +
                          "Below: one pass through the loop's filters, and the fourth (fainter)",
            };
            ctx.Changed += echoes.InvalidateVisual;

            var loop = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Feedback, diameter: 56), new ParamKnob(ctx, P.Drive)),
                Layout.Row(new ParamKnob(ctx, P.LowCut), new ParamKnob(ctx, P.HighCut)),
                Layout.Row(new ParamKnob(ctx, P.ModDepth), new ParamKnob(ctx, P.ModRate)));

            var duck = Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Duck), new ParamKnob(ctx, P.DuckThresh), new ParamKnob(ctx, P.DuckRelease)),
                Layout.Gap(4),
                new Meter("Ducking", () => 1 - (machine?.Engine.DuckMeter.Peak ?? 0), AccentColor, 214, reduction: true));

            var output = Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Mix, diameter: 56), new ParamKnob(ctx, P.Width), new ParamKnob(ctx, P.Output)),
                Layout.Gap(6),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 214),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 214));

            return Layout.Row(
                new Section("Time", AccentColor, time),
                new Section("Echoes", AccentColor, echoes),
                new Section("Loop", AccentColor, loop),
                new Section("Duck", AccentColor, duck),
                new Section("Output", AccentColor, output));
        }

        void DrawEchoes(INeoCanvas g, NeoRect r, EditorContext ctx, NeoDelayMachine machine)
        {
            var values = new ParamValues(Params.Table);
            for (int i = 0; i < Params.Table.Length; i++) values.Set(i, ctx.Raw(i));
            double rate = 48000, tempo = ctx.SongTempo(machine?.Engine.Tempo ?? 120);
            var s = DelayDesign.From(values, tempo, rate);
            var grid = new NeoStroke(Palette.Grid, 1, false);

            // The echoes: a window of about four times the longest line, at least a beat, at most 10 s.
            var top = new NeoRect(r.X + 4, r.Y + 18, r.Width - 8, (r.Height - 22) * 0.62);
            double span = Math.Clamp(4.2 * Math.Max(s.SecondsL, s.SecondsR), 60.0 / tempo, DelayDesign.MaxSeconds);
            double X(double t) => top.X + t / span * top.Width;
            double mid = top.Y + top.Height / 2;
            double beat = 60.0 / tempo;
            int beats = (int)Math.Floor(span / beat);
            int every = beats > 32 ? 4 : 1;
            for (int b = every; b <= beats; b += every) g.Line(X(b * beat), top.Top, X(b * beat), top.Bottom, grid);
            g.Line(top.Left, mid, top.Right, mid, new NeoStroke(Palette.Track, 1));
            g.Text(ParamDef.Time(span), top.Right - 2, top.Bottom - 12, 9, Palette.DimText, align: NeoAlign.Right);
            // The dry sound at 0.
            g.Line(X(0) + 1, mid - top.Height / 2 + 2, X(0) + 1, mid + top.Height / 2 - 2, new NeoStroke(Palette.Text.WithAlpha(0x70), 2));
            foreach (var e in DelayDesign.Echoes(s, rate, span, 400, Math.Pow(10, FloorDb / 20)))
            {
                double h = Math.Clamp(1 - 20 * Math.Log10(Math.Abs(e.Gain)) / FloorDb, 0, 1) * (top.Height / 2 - 3);
                double x = X(e.Seconds);
                var color = e.Channel == 0 ? AccentColor : RightColor;
                if (e.Gain < 0) color = color.WithAlpha(0x90);   // an inverted repeat
                if (e.Channel == 0) g.Line(x, mid - 1, x, mid - 1 - h, new NeoStroke(color, 2));
                else g.Line(x, mid + 1, x, mid + 1 + h, new NeoStroke(color, 2));
            }

            // One pass through the loop's filters, and the fourth.
            var bottom = new NeoRect(r.X + 4, top.Bottom + 6, r.Width - 8, r.Bottom - top.Bottom - 10);
            double FX(double f) => bottom.X + Math.Log(f / MinHz) / Math.Log(MaxHz / MinHz) * bottom.Width;
            double FY(double db) => bottom.Y + Math.Clamp(-db, 0, 36) / 36 * bottom.Height;
            foreach (double f in new[] { 100.0, 1000, 10000 }) g.Line(FX(f), bottom.Top, FX(f), bottom.Bottom, grid);
            g.Line(bottom.Left, FY(0), bottom.Right, FY(0), new NeoStroke(Palette.Track, 1));
            for (int i = 0; i < Points; i++) pass[i] = DelayDesign.PassDb(s, rate, hz[i]);
            curve1.Clear();
            curve4.Clear();
            for (int i = 0; i < Points; i++)
            {
                if (i == 0) { curve1.MoveTo(FX(hz[i]), FY(pass[i])); curve4.MoveTo(FX(hz[i]), FY(4 * pass[i])); }
                else { curve1.LineTo(FX(hz[i]), FY(pass[i])); curve4.LineTo(FX(hz[i]), FY(4 * pass[i])); }
            }
            g.Path(curve4, NeoFill.None, new NeoStroke(AccentColor.WithAlpha(0x60), 1.2));
            g.Path(curve1, NeoFill.None, new NeoStroke(AccentColor, 1.8));
            g.Text("100", FX(100) + 2, bottom.Bottom - 11, 9, Palette.DimText);
            g.Text("1k", FX(1000) + 2, bottom.Bottom - 11, 9, Palette.DimText);
            g.Text("10k", FX(10000) + 2, bottom.Bottom - 11, 9, Palette.DimText);

            Caption = Summary(s, values, tempo);
            g.Text(Caption, r.X + 6, r.Y + 2, 10, Palette.DimText, maxWidth: r.Width - 12);
        }

        /// <summary>The echoes' caption as last drawn: mode, time, tempo (tests).</summary>
        internal string Caption { get; private set; } = "";

        /// <summary>"Ping-pong · 1/8 dotted = 375 ms · R 375 ms · 120 BPM": the mode, the time and the tempo.</summary>
        static string Summary(in DelaySetting s, ParamValues v, double tempo)
        {
            string mode = s.Mode == DelayMode.PingPong ? "Ping-pong" : s.Mode.ToString();
            string time = s.Synced ? $"{DelayDesign.NoteName(v.Choice(P.Sync), v.Choice(P.Feel))} = {ParamDef.Time(s.SecondsL)}" : ParamDef.Time(s.SecondsL);
            string right = Math.Abs(s.SecondsR - s.SecondsL) > 1e-6 ? $" · R {ParamDef.Time(s.SecondsR)}" : "";
            return $"{mode} · {time}{right} · {tempo:0} BPM";
        }
    }
}
