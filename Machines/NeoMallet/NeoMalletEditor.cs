#nullable disable
// Neo Mallet's editor (docs/specs/neo-mallet.md): the model picker with a drawing of the model's modes (one line per mode on a log axis of
// frequency ratio, its height the mode's gain at the strike position), then the Exciter (Hardness, Position, Velocity, Noise), Decay and
// Damping, Tremolo and Output. One source for WPF and Avalonia (the Neo editor kit).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Mallet
{
    internal sealed class NeoMalletEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Synth identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xC9, 0x93, 0x4B));
        const double D = 44, GraphWidth = 420, GraphHeight = 132;
        // The drawing's axis: ratios from 1/2.5 to 64 of the note, in octaves.
        const double LowRatio = 0.4, HighRatio = 64;

        protected override string Title => "Neo Mallet";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        readonly ModeSet shown = new ModeSet();

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoMalletMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            var modes = new GraphView(GraphWidth, GraphHeight, (g, r) => DrawModes(g, r, ctx));
            ctx.Changed += modes.InvalidateVisual;

            var model = S("Model", Layout.Column(
                Layout.Row(new ParamDropdown(ctx, P.Model, width: 130)),
                Layout.Gap(1, 4),
                modes,
                Layout.Gap(1, 4),
                Build.Knobs(ctx, 3, D, P.Modes, P.Stretch, P.Inharm)));

            var exciter = S("Exciter", Layout.Column(
                Build.Knobs(ctx, 2, D, P.Hardness, P.Position),
                Build.Knobs(ctx, 2, D, P.VelHard, P.Noise)));

            var decay = S("Decay and damping", Layout.Column(
                Build.Knobs(ctx, 2, D, P.Decay, P.Tilt),
                Build.Knobs(ctx, 2, D, P.Bright, P.Release)));

            var tremolo = S("Tremolo", Layout.Column(new ParamKnob(ctx, P.TremRate, diameter: D), new ParamKnob(ctx, P.TremDepth, diameter: D)));

            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 100);
            var output = S("Output", Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Volume, diameter: 50), new ParamKnob(ctx, P.Spread, diameter: D)),
                Layout.Gap(6),
                meter));

            return Layout.Column(
                Layout.Row(model, exciter, decay),
                Layout.Row(tremolo, output));
        }

        /// <summary>One line for each mode: its place on a log axis of frequency against the note, its height its gain (at the strike position and Brightness).</summary>
        void DrawModes(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            double x0 = r.X + 8, x1 = r.X + r.Width - 8, top = r.Y + 6, bottom = r.Y + r.Height - 20;
            double span = Math.Log2(HighRatio / LowRatio);
            double X(double ratio) => x0 + (x1 - x0) * Math.Log2(Math.Clamp(ratio, LowRatio, HighRatio) / LowRatio) / span;

            g.Rect(new NeoRect(r.X, r.Y, r.Width, r.Height), Palette.Track, new NeoStroke(Palette.PanelEdge, 1), 4);
            // The octaves of the note.
            for (double ratio = 0.5; ratio <= HighRatio; ratio *= 2)
            {
                double x = X(ratio);
                g.Line(new NeoPoint(x, top), new NeoPoint(x, bottom), new NeoStroke(Palette.Grid, 1, false));
                g.Text(ratio < 1 ? "1/2" : ratio.ToString("0"), x, bottom + 4, 11, ratio == 1 ? Palette.Text : Palette.DimText, ratio == 1, NeoAlign.Center);
            }

            int count = (int)Math.Round(ctx.Value(P.Modes));
            MalletModels.Fill(shown, (MalletModel)Math.Clamp(ctx.Raw(P.Model), 0, 10), count, ctx.Value(P.Stretch), ctx.Value(P.Inharm));
            double position = ctx.Value(P.Position), bright = ctx.Value(P.Bright);
            double refRatio = shown.Ratio[shown.Ref];
            var level = new double[shown.Count];
            double max = 1e-9;
            for (int k = 0; k < shown.Count; k++)
            {
                double ratio = shown.Ratio[k] / refRatio;
                level[k] = Math.Abs(shown.BaseGain[k] * MalletModels.Weight(shown, k, position) * Math.Pow(ratio, bright));
                max = Math.Max(max, level[k]);
            }
            for (int k = 0; k < shown.Count; k++)
            {
                double ratio = shown.Ratio[k] / refRatio;
                if (ratio > HighRatio) continue;
                double x = X(ratio), h = (bottom - top) * Math.Clamp(level[k] / max, 0.02, 1.0);
                bool carrier = k == shown.Ref;
                g.Line(new NeoPoint(x, bottom), new NeoPoint(x, bottom - h), new NeoStroke(carrier ? AccentColor.Tint(0.25) : shown.Partner[k] ? AccentColor.WithAlpha(150) : AccentColor, carrier ? 3 : 2, false));
            }
        }
    }
}
