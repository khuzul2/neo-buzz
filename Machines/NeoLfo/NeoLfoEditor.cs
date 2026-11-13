#nullable disable
// Neo LFO's editor (docs/specs/neo-patch.md §3): the shared modulator template (NeoModulator/ModulatorEditor.cs) over this machine's controls.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Modulator;

namespace NeoBuzz.Lfo
{
    internal sealed class NeoLfoEditor : ModulatorEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));

        protected override string Title => "Neo LFO";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string ListenLabel => "Retrigger on notes of";

        protected override UIElement BuildControls(EditorContext ctx) => Layout.Row(
            new Section("Wave", AccentColor, Layout.Row(
                Layout.Column(new ParamDropdown(ctx, P.Shape, "Shape", 112), Layout.Gap(1, 4), new ParamDropdown(ctx, P.Sync, "Sync", 112), Layout.Gap(1, 4), new ParamDropdown(ctx, P.Feel, "Feel", 112)),
                new ParamKnob(ctx, P.Rate, diameter: 54),
                new ParamKnob(ctx, P.Phase, diameter: 54))),
            new Section("Behaviour", AccentColor, Layout.Row(
                new ParamDropdown(ctx, P.Retrigger, "Retrigger", 96),
                new ParamKnob(ctx, P.Smooth, diameter: 54),
                new ParamKnob(ctx, P.Glide, diameter: 54))));
    }
}
