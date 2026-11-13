#nullable disable
// Neo Learn Physical's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the modes' bars
// (frequency across, level up: the blended body's spacing, where it is struck, how fast each dies and the body size's radiation) with the exciter's
// force in a box at the top (mallet, pick, bow and what is between), the output's scope faint behind. The pad is Material (across) and Exciter Mode (up).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnPhys
{
    internal sealed class NeoLearnPhysicalEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x7F, 0xD1, 0xAE));

        /// <summary>The picture's reference note (Hz), the frequency range it shows (times the note) and the points of the exciter's curve.</summary>
        public const double NoteHz = 220, MaxRatio = 40;
        public const int ImpulsePoints = 48;

        readonly double[] ratios = new double[PhysicalMap.Modes];
        readonly double[] levels = new double[PhysicalMap.Modes];
        readonly double[] impulse = new double[ImpulsePoints];

        protected override string Title => "Neo Learn Physical";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Physical";
        protected override int PadX => P.Material;
        protected override int PadY => P.ExciterType;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnPhysicalMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            int n = PhysicalMap.BlendedRatios(ctx.Value(P.ResonatorShape), ratios);
            n = Math.Min(n, PhysicalMap.Modes);
            PhysicalMap.ModeLevels(ratios, n, NoteHz, ctx.Value(P.Material), ctx.Value(P.Damping), ctx.Value(P.BodySize), levels);
            double force = ctx.Value(P.ExciterForce), morph = ctx.Value(P.ExciterType);
            for (int k = 0; k < ImpulsePoints; k++) impulse[k] = PhysicalMap.Impulse(morph, force, k / (double)(ImpulsePoints - 1));
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.ModesAndImpulse(g, r, ratios, levels, n, MaxRatio, impulse, accent);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Material ->, Exciter Mode up: drag the dot; double-click resets");
        }
    }
}
