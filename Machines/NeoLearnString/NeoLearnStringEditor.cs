#nullable disable
// Neo Learn String's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the string with its pluck
// (a triangle at the pick position), the partials the pluck, the damping and the stiffness leave (bars in the log of the ratio: stiffness stretches them,
// a pluck near the bridge or the middle takes some out), and the decay's fall in a box at the top; the output's scope, the string's own loop
// dying away, is faint behind. The pad is Pick Position (across) and String Damping (up).
using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnString
{
    internal sealed class NeoLearnStringEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xE6, 0xB4, 0x6A));

        /// <summary>The partials drawn, and the frequency range the picture shows (times the note).</summary>
        public const int PartialCount = 24;
        public const double MaxRatio = 32;

        readonly double[] level = new double[PartialCount];
        readonly double[] ratio = new double[PartialCount];

        protected override string Title => "Neo Learn String";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Plucked";
        protected override int PadX => P.PickPosition;
        protected override int PadY => P.StringDamping;
        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnStringMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            double pick = ctx.Value(P.PickPosition);
            StringMap.Partials(pick, ctx.Value(P.StringDamping), ctx.Value(P.PickHardness), ctx.Value(P.Dispersion), level, ratio, PartialCount);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.StringAndPartials(g, r, StringMap.PluckAt(pick), level, ratio, PartialCount, MaxRatio, ctx.Value(P.StringDecay), accent);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Pick Position ->, String Damping up: drag the dot; double-click resets");
        }
    }
}
