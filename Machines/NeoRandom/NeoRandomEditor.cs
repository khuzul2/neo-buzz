#nullable disable
// Neo Random's editor (docs/specs/neo-random.md): the shared modulator template (NeoModulator/ModulatorEditor.cs)
// over three compact control rows sized to sit cleanly beside the Output scope and flush with the Targets table.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Modulator;

namespace NeoBuzz.RandomMachine
{
    internal sealed class NeoRandomEditor : ModulatorEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));

        protected override string Title => "Neo Random";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string ListenLabel => "Retrigger on notes of";

        protected override UIElement BuildControls(EditorContext ctx) => Layout.Column(
            new Section("Source", AccentColor, Layout.Row(
                new ParamDropdown(ctx, P.Mode, "Mode", 112),
                ParamTempoTime.Create(ctx, new TempoTimeSpec { Division = P.Sync, Feel = P.Feel, Rate = P.Rate, Hz = true, KnobDiameter = 48 }),
                new ParamKnob(ctx, P.Prob, diameter: 48),
                new ParamKnob(ctx, P.Chaos, diameter: 48))),
            new Section("Shape", AccentColor, Layout.Row(
                new ParamDropdown(ctx, P.Range, "Range", 92),
                new ParamKnob(ctx, P.Smooth, diameter: 46),
                new ParamKnob(ctx, P.Depth, diameter: 46),
                new ParamKnob(ctx, P.Bias, diameter: 46),
                new ParamKnob(ctx, P.Quantise, diameter: 46))),
            new Section("Behaviour", AccentColor, Layout.Row(
                new ParamDropdown(ctx, P.Retrigger, "Retrigger", 92),
                new ParamKnob(ctx, P.Seed, diameter: 46),
                new ParamKnob(ctx, P.Glide, diameter: 46))));
    }
}
