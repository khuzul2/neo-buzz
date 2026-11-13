#nullable disable
// Neo Learn Additive's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the
// 64 partials' levels as bars (the engine's own tilt, odd/even, count and comb formulas, in dB), the output's scope faint behind. The pad
// is Brightness Tilt (across) and Odd / Even (up).

using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnAdd
{
    internal sealed class NeoLearnAdditiveEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x7F, 0xB5, 0xE8));

        /// <summary>The bars' range: a partial 60 dB down is no bar.</summary>
        public const double RangeDb = 60;
        const int Bars = 64;

        readonly double[] levels = new double[Bars];
        readonly double[] bars = new double[Bars];

        protected override string Title => "Neo Learn Additive";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Additive";
        protected override int PadX => P.SpectralTilt;
        protected override int PadY => P.OddEvenBalance;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnAdditiveMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            AdditiveMap.Spectrum(ctx.Value(P.SpectralTilt), ctx.Value(P.OddEvenBalance), (int)ctx.Value(P.PartialCount), ctx.Value(P.HarmonicComb), levels);
            for (int i = 0; i < Bars; i++) bars[i] = levels[i] > 1e-6 ? System.Math.Max(0, 1 + 20 * System.Math.Log10(levels[i]) / RangeDb) : 0;
            var plot = new NeoRect(r.X + 4, r.Y + 20, r.Width - 8, r.Height - 40);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x50), 0.5, 0.3);
            LearnPictureParts.GridLine(g, plot, 0.5, "-30 dB");
            LearnPictureParts.Bars(g, plot, bars, Bars, accent);
            for (int k = 8; k <= 64; k += 8) LearnPictureParts.VGridLine(g, plot, (k - 0.5) / 64, k.ToString());
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Tilt →, Odd / Even ↑: drag the dot; double-click resets");
        }
    }
}
