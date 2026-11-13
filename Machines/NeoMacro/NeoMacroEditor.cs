#nullable disable
// Neo Macro's editor (docs/specs/neo-patch.md §3): the shared modulator template (NeoModulator/ModulatorEditor.cs) over this machine's controls.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Modulator;

namespace NeoBuzz.MacroMachine
{
    internal sealed class NeoMacroEditor : ModulatorEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));

        protected override string Title => "Neo Macro";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override UIElement BuildControls(EditorContext ctx) => Layout.Row(
            new Section("Macro", AccentColor, Layout.Row(
                new ParamKnob(ctx, P.Value, diameter: 76),
                new ParamKnob(ctx, P.Glide, diameter: 48))));
    }
}
