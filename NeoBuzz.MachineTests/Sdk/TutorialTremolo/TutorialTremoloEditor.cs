#nullable disable
// Tutorial Tremolo's editor (docs/sdk/tutorial.md, docs/sdk/editor.md): the tremolo's controls on the left, the gain curve of the
// two channels in the middle, Amount, Mix and the meters on the right. The editor is written with WPF's names; the studio builds the
// same source on the Avalonia kit (NeoBuzz.Editor.Avalonia/WpfNames.cs). The curve draws on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.TutorialTremolo
{
    internal sealed class TutorialTremoloEditor : NeoEditor
    {
        // snippet EditorHead
        // The machine's category gives its accent (Tokens.Identity); the second argument is the colour the classic app keeps.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Modulation, NeoColor.FromRgb(0x5B, 0xC0, 0xEB));
        static readonly NeoColor PartnerColor = Tokens.Partner(AccentColor, NeoColor.FromRgb(0xF2, 0xA9, 0x3B));

        protected override string Title => "Tutorial Tremolo";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        // end snippet EditorHead

        // snippet EditorBody
        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as TutorialTremoloMachine;

            var tremolo = Layout.Row(
                Layout.Column(
                    new ParamKnob(ctx, P.Rate, diameter: 58),
                    new ParamKnob(ctx, P.Phase)),
                Layout.Gap(8),
                Layout.Column(
                    new ParamKnob(ctx, P.Depth, diameter: 58),
                    new ParamChoice(ctx, P.Shape, cellWidth: 70)));

            var wave = new GraphView(220, 140, (g, r) => DrawWave(g, r, ctx));
            ctx.Changed += wave.InvalidateVisual;   // redraw when a value changes (a knob, automation, a preset)

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 120),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 120));

            // House style: Amount, then Mix, two 40 px knobs, in the last section of every Neo effect.
            var output = Layout.Row(
                Layout.Column(Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40))),
                Layout.Gap(6),
                meters);

            return Layout.Row(
                new Section("Tremolo", AccentColor, tremolo),
                new Section("Level", AccentColor, wave),
                new Section("Output", AccentColor, output));
        }
        // end snippet EditorBody

        // snippet EditorDraw
        /// <summary>Two cycles of the gain of the left (accent) and the right channel (partner colour), drawn with the engine's own Gain.</summary>
        static void DrawWave(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            // What the engine would run: the raw values with Amount applied (the same FxStage.Fill the engine uses).
            var rawValues = new ParamValues(Params.Table);
            var values = new ParamValues(Params.Table);
            values.AllowPhysical();
            for (int i = 0; i < Params.Table.Length; i++) rawValues.Set(i, ctx.Raw(i));
            FxStage.Fill(rawValues, values, P.Amount, TremoloEngine.AmountRules);

            var grid = new NeoStroke(Palette.Grid, 1, false);
            double top = r.Y + 8, bottom = r.Bottom - 8;
            g.Line(r.Left, top, r.Right, top, grid);          // gain 1
            g.Line(r.Left, bottom, r.Right, bottom, grid);    // gain 0

            const int steps = 200;
            foreach (bool right in new[] { true, false })     // the left channel last, so it is on top
            {
                var curve = new NeoPath();
                for (int i = 0; i <= steps; i++)
                {
                    double cycle = 2.0 * i / steps;
                    double gain = TremoloEngine.CurveAt(values, cycle, right);
                    curve.LineTo(r.X + r.Width * i / steps, bottom - gain * (bottom - top));
                }
                g.Path(curve, NeoFill.None, new NeoStroke(right ? PartnerColor : AccentColor, 2));
            }
            g.Text("1", r.X + 4, top - 8, 11, Palette.DimText);
            g.Text("0", r.X + 4, bottom - 14, 11, Palette.DimText);
        }
        // end snippet EditorDraw
    }
}
