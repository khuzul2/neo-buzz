#nullable disable
// Neo Envelope's editor (docs/specs/neo-patch.md §3): the shared modulator template (NeoModulator/ModulatorEditor.cs) over this machine's controls.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Modulator;

namespace NeoBuzz.EnvelopeMachine
{
    internal sealed class NeoEnvelopeEditor : ModulatorEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));

        protected override string Title => "Neo Envelope";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string ListenLabel => "Listens to";

        protected override UIElement BuildControls(EditorContext ctx) => Layout.Row(
            new Section("Shape", AccentColor, Layout.Row(
                Layout.Column(new ParamDropdown(ctx, P.Mode, "Mode", 96), Layout.Gap(1, 4), new ParamChoice(ctx, P.Gate, "Gate", 46)),
                new ParamKnob(ctx, P.Attack, diameter: 48),
                new ParamKnob(ctx, P.Decay, diameter: 48),
                new ParamKnob(ctx, P.Sustain, diameter: 48),
                new ParamKnob(ctx, P.Release, diameter: 48))),
            new Section("Output", AccentColor, Layout.Row(
                new ParamKnob(ctx, P.Threshold, diameter: 48),
                new ParamKnob(ctx, P.Curve, diameter: 48),
                new ParamKnob(ctx, P.Glide, diameter: 48))));
    }
}
