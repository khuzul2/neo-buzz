#nullable disable
// Neo Keys' editor (docs/specs/neo-keys.md): the model strip and the Pickup drawing (the pickup's curve of voltage against how far the tine swings, and
// the harmonics it makes of a hard note), then the Hammer (Hardness, Touch, Velocity curve), the Decay and damper, Key noise, the Preamp (Drive, Tone),
// the Tremolo with its auto-pan and the Output. One source for WPF and Avalonia (the Neo editor kit).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Keys
{
    internal sealed class NeoKeysEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Synth identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xC2, 0x6A, 0x4A));
        const double D = 44, GraphWidth = 396, GraphHeight = 132;
        const int Harmonics = 10, Samples = 256;
        const double FloorDb = -60;

        protected override string Title => "Neo Keys";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        readonly double[] harmonic = new double[Harmonics];
        readonly double[] cosTable = new double[Samples], sinTable = new double[Samples];

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoKeysMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);
            for (int n = 0; n < Samples; n++) { cosTable[n] = Math.Cos(2 * Math.PI * n / Samples); sinTable[n] = Math.Sin(2 * Math.PI * n / Samples); }

            var pickupGraph = new GraphView(GraphWidth, GraphHeight, (g, r) => DrawPickup(g, r, ctx));
            ctx.Changed += pickupGraph.InvalidateVisual;

            var model = S("Model and pickup", Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Model, "Model", 70), Layout.Gap(10), new ParamChoice(ctx, P.PickupSel, "Pickup select", 56), Layout.Gap(10), new ParamChoice(ctx, P.Mute, "Mute", 56)),
                Layout.Gap(1, 4),
                pickupGraph,
                Layout.Gap(1, 4),
                Build.Knobs(ctx, 2, D, P.Pickup, P.Bar)));

            var hammer = S("Hammer", Layout.Column(
                Build.Knobs(ctx, 2, D, P.Hardness, P.Touch),
                Build.Knobs(ctx, 2, D, P.VelCurve, P.KeyNoise)));

            var decay = S("Decay and damper", Layout.Column(new ParamKnob(ctx, P.Decay, diameter: D), new ParamKnob(ctx, P.Release, diameter: D)));

            var preamp = S("Preamp", Layout.Row(new ParamKnob(ctx, P.Drive, diameter: D), new ParamKnob(ctx, P.Tone, diameter: D)));

            var tremolo = S("Tremolo", Build.Knobs(ctx, 3, D, P.TremRate, P.TremDepth, P.Stereo));

            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 100);
            var output = S("Output", Layout.Row(new ParamKnob(ctx, P.Volume, diameter: 50), Layout.Gap(6), meter));

            return Layout.Column(
                Layout.Row(model, hammer, decay),
                Layout.Row(preamp, tremolo, output));
        }

        /// <summary>
        /// The pickup, as the engine reads it: on the left, the curve of the flux through the pickup against how far the tine has swung (the dotted line is
        /// a pickup too far to bend it); on the right, the harmonics of a note struck hard, in dB under the first (the bark).
        /// </summary>
        void DrawPickup(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            var model = (KeysModel)Math.Clamp(ctx.Raw(P.Model), 0, 2);
            float a = (float)(KeysModels.MaxA[(int)model] * ctx.Value(P.Pickup));
            float scale = KeysModels.PickupIn[(int)model];
            double half = (r.Width - 24) * 0.4;
            double cx = r.X + 12 + half, cy = r.Y + r.Height * 0.5 - 6, top = r.Y + 8, bottom = r.Y + r.Height - 20;
            double sx = half / 1.2, sy = (cy - top) / 3.0;

            g.Rect(new NeoRect(r.X, r.Y, r.Width, r.Height), Palette.Track, new NeoStroke(Palette.PanelEdge, 1), 4);
            g.Line(new NeoPoint(cx - half, cy), new NeoPoint(cx + half, cy), new NeoStroke(Palette.Grid, 1, false));
            g.Line(new NeoPoint(cx, top), new NeoPoint(cx, bottom), new NeoStroke(Palette.Grid, 1, false));
            g.Line(new NeoPoint(cx - half, cy + sy * 1.2), new NeoPoint(cx + half, cy - sy * 1.2), new NeoStroke(Palette.DimText.WithAlpha(120), 1, true));
            var curve = new NeoPath();
            for (int i = 0; i <= 48; i++)
            {
                double y = -1.2 + 2.4 * i / 48.0;
                double f = Math.Clamp(KeysModels.Flux(model, (float)(y / scale), a) * scale, -3.0, 3.0);
                curve.LineTo(cx + y * sx, cy - f * sy, true);
            }
            g.Path(curve, NeoFill.None, new NeoStroke(AccentColor, 2));
            g.Text("swing", cx, bottom + 4, 11, Palette.DimText, false, NeoAlign.Center);

            // The harmonics of the difference of the flux of a full-speed swing: the voltage the coil makes.
            double A = 0.8 / scale;
            double[] re = new double[Harmonics], im = new double[Harmonics];
            float prev = KeysModels.Flux(model, (float)(A * sinTable[Samples - 1]), a);
            for (int n = 0; n < Samples; n++)
            {
                float f = KeysModels.Flux(model, (float)(A * sinTable[n]), a);
                double o = f - prev; prev = f;
                for (int k = 0; k < Harmonics; k++)
                {
                    int ix = ((k + 1) * n) % Samples;
                    re[k] += o * cosTable[ix]; im[k] -= o * sinTable[ix];
                }
            }
            double first = Math.Sqrt(re[0] * re[0] + im[0] * im[0]) + 1e-12;
            double x0 = r.X + r.Width * 0.5 + 8, w = r.X + r.Width - 8 - x0, slot = w / Harmonics;
            g.Line(new NeoPoint(x0, bottom), new NeoPoint(x0 + w, bottom), new NeoStroke(Palette.Grid, 1, false));
            for (int k = 0; k < Harmonics; k++)
            {
                double db = 20 * Math.Log10(Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / first + 1e-9);
                double h = (bottom - top) * Math.Clamp((db - FloorDb) / -FloorDb, 0.02, 1.0);
                g.Rect(new NeoRect(x0 + slot * k + 3, bottom - h, slot - 6, h), k == 0 ? AccentColor.Tint(0.25) : AccentColor, NeoStroke.None, 1);
                g.Text((k + 1).ToString(), x0 + slot * k + slot / 2, bottom + 4, 11, k == 0 ? Palette.Text : Palette.DimText, k == 0, NeoAlign.Center);
            }
        }
    }
}
