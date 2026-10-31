#nullable disable
// Neo Wave's editor (docs/specs/machines.md §5): the two oscillators, each with a 3-D view of its
// table (the frame playing now drawn bright, warp applied, following modulation while notes
// sound), the user-table loader, then filter, voice, envelopes, LFOs and the modulation matrix.

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Wave
{
    internal sealed class NeoWaveEditor : NeoEditor
    {
        static readonly Color AccentColor = Color.FromRgb(0x3D, 0xD6, 0x8C);
        const double D = 36;

        protected override string Title => "Neo Wave";
        protected override Color Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoWaveMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            UIElement Osc(string title, int osc, int table, int pos, int warp, int warpAmt, int oct, int semi, int fine, int level) => S(title, Layout.Column(
                Layout.Row(new ParamDropdown(ctx, table, width: 120), Layout.Gap(8), new ParamDropdown(ctx, warp, width: 110)),
                Layout.Gap(1, 6),
                TableView(ctx, machine, osc, 6 * 64, 104),
                Layout.Gap(1, 4),
                Build.Knobs(ctx, 6, D, pos, warpAmt, oct, semi, fine, level)));

            var oscA = Osc("Osc A", 0, P.ATable, P.APos, P.AWarp, P.AWarpAmt, P.AOct, P.ASemi, P.AFine, P.ALevel);
            var oscB = Osc("Osc B", 1, P.BTable, P.BPos, P.BWarp, P.BWarpAmt, P.BOct, P.BSemi, P.BFine, P.BLevel);
            var sub = S("Sub · noise", Layout.Column(
                new ParamChoice(ctx, P.SubShape, cellWidth: 56),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.SubOct, cellWidth: 56),
                Build.Knobs(ctx, 2, D, P.SubLevel, P.NoiseLevel)));

            var userName = new TextBlock
            {
                Foreground = Theme.Text,
                FontFamily = Theme.FontFamily,
                FontSize = 11.5,
                Width = 128,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = UserLabel(machine),
            };
            Poll.Every(userName, 0.5, () =>
            {
                string label = UserLabel(machine);
                if (userName.Text != label) userName.Text = label;
            });
            var load = new ActionButton("Load table…", AccentColor, 128);
            load.Click += () =>
            {
                if (machine == null) return;
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Load a wavetable",
                    Filter = "Wavetables (*.wav)|*.wav|All files (*.*)|*.*",
                };
                if (dialog.ShowDialog() != true) return;
                try
                {
                    machine.LoadUserTable(dialog.FileName);
                    userName.Text = UserLabel(machine);
                    // Play it right away: put osc A on "User" unless an oscillator already is.
                    int user = Tables.Names.Length;
                    if (ctx.Raw(P.ATable) != user && ctx.Raw(P.BTable) != user) ctx.Set(P.ATable, user);
                    ctx.RaiseChanged();
                }
                catch (Exception e)
                {
                    MessageBox.Show($"Couldn't load {System.IO.Path.GetFileName(dialog.FileName)}: {e.Message}", "Neo Wave", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            var hint = new TextBlock
            {
                Foreground = Theme.DimText,
                FontFamily = Theme.FontFamily,
                FontSize = 10.5,
                Width = 128,
                TextWrapping = TextWrapping.Wrap,
                Text = "WAV with 2048-sample frames (Serum and most editors) or one cycle. Saved with the song.",
            };
            var user = S("User table", Layout.Column(userName, Layout.Gap(1, 6), load, Layout.Gap(1, 6), hint));

            var filter = S("Filter", Layout.Column(
                new ParamDropdown(ctx, P.FType, width: 120),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.FCutoff, diameter: 54), new ParamKnob(ctx, P.FReso, diameter: 54)),
                Build.Knobs(ctx, 4, D, P.FDrive, P.FEnv, P.FKey, P.FVel)));
            var voice = S("Voice", Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.VoiceMode, cellWidth: 50), Layout.Gap(6), new ParamChoice(ctx, P.GlideMode, cellWidth: 50)),
                Build.Knobs(ctx, 4, D, P.Polyphony, P.Unison, P.UniDetune, P.UniSpread, P.Glide, P.BendRange, P.VelAmp, P.Volume)));
            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 150);

            UIElement MakeEnv(string title, int a, int d, int s, int r) => S(title, Layout.Column(
                Build.Envelope(ctx, a, d, s, r, 4 * 64, 46),
                Build.Knobs(ctx, 4, D, a, d, s, r)));
            UIElement MakeLfo(string title, int shape, int rate, int sync, int retrig, int fade) => S(title, Layout.Column(
                new ParamDropdown(ctx, shape, width: 100),
                Layout.Row(new ParamKnob(ctx, rate, diameter: D), new ParamKnob(ctx, fade, diameter: D)),
                Layout.Row(new ParamDropdown(ctx, sync, width: 66), Layout.Gap(4), new ParamChoice(ctx, retrig, "Retrig", 30))));

            var matrix = new Grid();
            for (int c = 0; c < 6; c++) matrix.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int r = 0; r < 4; r++) matrix.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int m = 0; m < 8; m++)
            {
                int b = P.M1Src + 3 * m;
                int row = m % 4, col = (m / 4) * 3;
                var src = new ParamDropdown(ctx, b, $"{m + 1} source", 100) { Margin = new Thickness(0, 0, 4, 2) };
                var dst = new ParamDropdown(ctx, b + 1, "destination", 110) { Margin = new Thickness(0, 0, 4, 2) };
                var amt = new ParamKnob(ctx, b + 2, "amount", 30) { Margin = new Thickness(0, 0, 14, 0) };
                Grid.SetRow(src, row); Grid.SetColumn(src, col);
                Grid.SetRow(dst, row); Grid.SetColumn(dst, col + 1);
                Grid.SetRow(amt, row); Grid.SetColumn(amt, col + 2);
                matrix.Children.Add(src);
                matrix.Children.Add(dst);
                matrix.Children.Add(amt);
            }

            return Layout.Column(
                Layout.Row(oscA, oscB, user),
                Layout.Row(filter, voice, sub, S("Out", meter)),
                Layout.Row(MakeEnv("Amp envelope", P.AmpA, P.AmpD, P.AmpS, P.AmpR),
                           MakeEnv("Filter envelope", P.FltA, P.FltD, P.FltS, P.FltR),
                           MakeEnv("Mod envelope", P.ModA, P.ModD, P.ModS, P.ModR)),
                Layout.Row(MakeLfo("LFO 1", P.L1Shape, P.L1Rate, P.L1Sync, P.L1Retrig, P.L1Fade),
                           MakeLfo("LFO 2", P.L2Shape, P.L2Rate, P.L2Sync, P.L2Retrig, P.L2Fade),
                           S("Modulation matrix", matrix)));
        }

        static string UserLabel(NeoWaveMachine machine) =>
            string.IsNullOrEmpty(machine?.UserTableName) ? "No table loaded" : machine.UserTableName;

        /// <summary>
        /// A table in perspective: up to 20 of its frames from front (first) to back (last), and the
        /// frame playing now (modulated position and warp of the loudest voice, else the knobs).
        /// </summary>
        static GraphView TableView(EditorContext ctx, NeoWaveMachine machine, int osc, double width, double height)
        {
            int tableParam = osc == 0 ? P.ATable : P.BTable;
            int posParam = osc == 0 ? P.APos : P.BPos;
            int warpParam = osc == 0 ? P.AWarp : P.BWarp;
            int warpAmtParam = osc == 0 ? P.AWarpAmt : P.BWarpAmt;
            Wavetable cached = null;
            Geometry back = null;
            Size cachedSize = default;
            const int Shown = 20, Points = 128, NowPoints = 256;

            return new GraphView(width, height, (dc, r, v) =>
            {
                int choice = ctx.Raw(tableParam);
                bool isUser = choice >= Tables.Names.Length;
                var table = isUser ? machine?.Engine.UserTable : Tables.Get(choice);
                if (table == null)
                {
                    Theme.TextCentered(dc, v, isUser ? "No user table: load a WAV" : "Building…", r.Width / 2, r.Height / 2 - 8, 11.5, Theme.DimText);
                    return;
                }
                double depthX = r.Width * 0.24, depthY = r.Height * 0.36;
                double frameWidth = r.Width - depthX - 12;
                double amp = (r.Height - depthY - 14) * 0.45;
                Point At(double depth, double t, double y) =>
                    new Point(r.Left + 6 + depth * depthX + t * frameWidth, r.Bottom - 7 - amp - depth * depthY - y * amp);

                if (!ReferenceEquals(table, cached) || cachedSize != r.Size)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        int shown = Math.Min(Shown, table.FrameCount);
                        for (int k = 0; k < shown; k++)
                        {
                            int frame = shown == 1 ? 0 : (int)Math.Round(k * (table.FrameCount - 1) / (double)(shown - 1));
                            double depth = table.FrameCount == 1 ? 0 : frame / (double)(table.FrameCount - 1);
                            var samples = table.FrameSamples(frame, 4);
                            for (int i = 0; i <= Points; i++)
                            {
                                double t = i / (double)Points;
                                var p = At(depth, t, samples[Math.Min(samples.Length - 1, (int)(t * samples.Length))]);
                                if (i == 0) c.BeginFigure(p, false, false);
                                else c.LineTo(p, true, false);
                            }
                        }
                    }
                    g.Freeze();
                    back = g;
                    cached = table;
                    cachedSize = r.Size;
                }
                dc.DrawGeometry(null, Theme.Pen(Theme.Solid(ctx.Accent, 0x48), 1), back);

                float pos = (float)ctx.Value(posParam);
                float warp = (float)ctx.Value(warpAmtParam);
                var mode = (Warp)ctx.Raw(warpParam);
                if (machine != null && machine.Engine.Live(osc, out float livePos, out float liveWarp))
                {
                    pos = livePos;
                    warp = liveWarp;
                }
                float framePos = pos * (table.FrameCount - 1);
                var now = new StreamGeometry();
                using (var c = now.Open())
                {
                    for (int i = 0; i <= NowPoints; i++)
                    {
                        double t = i / (double)NowPoints;
                        double phase = mode == Warp.PhaseMod ? t : WaveVoice.WarpPhase(mode, warp, Math.Min(t, 0.999999));
                        float y = table.Read(framePos, phase, 3);
                        if (mode == Warp.WindowSync) y *= (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * t));
                        var p = At(pos, t, y);
                        if (i == 0) c.BeginFigure(p, false, false);
                        else c.LineTo(p, true, false);
                    }
                }
                now.Freeze();
                dc.DrawGeometry(null, Theme.Pen(Theme.Solid(ctx.Accent), 2), now);
                Theme.DrawText(dc, v, $"{table.Name} · frame {(int)Math.Round(framePos) + 1} of {table.FrameCount}", r.Left + 7, r.Top + 4, 10.5, Theme.DimText);
            }, animate: true);
        }
    }
}
