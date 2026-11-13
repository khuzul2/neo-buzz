#nullable disable
// Neo Learn Phase Distortion's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the read phase against
// the position (the identity dotted: the curve leaves it as the bend grows, to the chosen geometry) and a sine read through it, two cycles; the output's scope is faint
// behind. The pad is Phase Bend (across) and Resonance (up).
using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnPd
{
    internal sealed class NeoLearnPhaseDistortionEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xF0, 0x9A, 0x7A));

        public const int CurvePoints = 96, WavePoints = 96;

        readonly double[] phase = new double[CurvePoints];
        readonly double[] wave = new double[WavePoints];

        protected override string Title => "Neo Learn Phase Distortion";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Phase distortion";
        protected override int PadX => P.PhaseBend;
        protected override int PadY => P.Resonance;
        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnPhaseDistortionMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            double bend = ctx.Value(P.PhaseBend);
            int shape = (int)Math.Round(ctx.Value(P.PdWaveShape));
            for (int i = 0; i < CurvePoints; i++) phase[i] = PhaseDistortionMap.Phase(shape, bend, i / (double)(CurvePoints - 1));
            for (int i = 0; i < WavePoints; i++) wave[i] = PhaseDistortionMap.Wave(shape, bend, 2.0 * i / (WavePoints - 1) % 1.0);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.PhaseAndWave(g, r, phase, CurvePoints, shape == 3 ? 2 : 1, wave, WavePoints, accent);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Phase Bend ->, Resonance up: drag the dot; double-click resets");
        }
    }
}
