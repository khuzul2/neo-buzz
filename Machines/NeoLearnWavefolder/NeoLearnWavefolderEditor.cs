#nullable disable
// Neo Learn Wavefolder's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the folder's transfer
// curve y = f(x) (the identity dotted: the curve leaves it where the wave first meets the edge, then folds back, once for each stage's gain) and a sine
// through it, two cycles; the output's scope is faint behind. The pad is Wavefold (across) and Symmetry (up).
using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnFold
{
    internal sealed class NeoLearnWavefolderEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x8F, 0xB8, 0xF0));

        public const int CurvePoints = 96, WavePoints = 96;

        readonly double[] curve = new double[CurvePoints];
        readonly double[] wave = new double[WavePoints];

        protected override string Title => "Neo Learn Wavefolder";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Folding";
        protected override int PadX => P.FoldDrive;
        protected override int PadY => P.FoldSymmetry;
        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnWavefolderMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            double drive = ctx.Value(P.FoldDrive), sym = ctx.Value(P.FoldSymmetry);
            int stages = (int)Math.Round(ctx.Value(P.FoldStages));
            for (int i = 0; i < CurvePoints; i++) curve[i] = WavefolderMap.Transfer(-1 + 2.0 * i / (CurvePoints - 1), drive, sym, stages);
            for (int i = 0; i < WavePoints; i++) wave[i] = WavefolderMap.Transfer(PortableMath.Sin(4 * Math.PI * i / (WavePoints - 1)), drive, sym, stages);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.FoldTransferAndWave(g, r, curve, CurvePoints, wave, WavePoints, accent);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Wavefold ->, Symmetry up: drag the dot; double-click resets");
        }
    }
}
