#nullable disable
// Neo Learn Subtractive's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template (NeoLearn/LearnEditor.cs) over this
// machine's 12 controls. The picture's pad is Cutoff (across) and Resonance (up).

using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnSub
{
    internal sealed class NeoLearnSubtractiveEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x4F, 0xC3, 0xA1));

        protected override string Title => "Neo Learn Subtractive";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Subtractive";
        protected override int PadX => P.FilterCutoff;
        protected override int PadY => P.FilterResonance;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnSubtractiveMachine)?.Host;
    }
}
