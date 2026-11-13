#nullable disable
// Neo Learn FM's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template (NeoLearn/LearnEditor.cs) over this machine's 12 controls.
// The picture splits in two: the output's waveform on the left, the carrier's sidebands on the right (computed from Mod Ratio and FM
// Depth: the Bessel series of the index, at carrier + n x ratio). The pad is Mod Ratio (across) and FM Depth (up).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnFm
{
    internal sealed class NeoLearnFMEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xE0, 0x8F, 0x4F));

        /// <summary>The sideband bars: 48 bins of half a carrier frequency (0 … 24 x the carrier).</summary>
        public const int Bins = 48;
        const int Orders = 12;

        readonly double[] amps = new double[2 * Orders + 1];
        readonly double[] bins = new double[Bins];

        protected override string Title => "Neo Learn FM";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "FM";
        protected override int PadX => P.ModRatio;
        protected override int PadY => P.FmIndex;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnFMMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            double ratio = ctx.Value(P.ModRatio), index = ctx.Value(P.FmIndex);
            var wave = new NeoRect(r.X, r.Y, r.Width * 0.5, r.Height);
            var spec = new NeoRect(r.X + r.Width * 0.5 + 8, r.Y + 18, r.Width * 0.5 - 14, r.Height - 34);
            g.Line(r.X + r.Width * 0.5, r.Y + 4, r.X + r.Width * 0.5, r.Bottom - 4, new NeoStroke(Palette.Grid, 1, false));
            LearnPictureParts.GridLine(g, wave, 0.5);
            LearnPictureParts.Scope(g, wave, scope, scopeCount, accent, 0.5, 0.34);
            // Sidebands: the carrier at 1, the n-th at 1 + n x ratio (negative ones fold back), by frequency in carrier multiples.
            FmMap.Sidebands(ratio, index, amps, out double[] f);
            Array.Clear(bins, 0, bins.Length);
            for (int i = 0; i < amps.Length; i++)
            {
                int bin = (int)Math.Floor(Math.Abs(f[i]) * 2);
                if (bin >= 0 && bin < Bins) bins[bin] = Math.Max(bins[bin], amps[i]);
            }
            LearnPictureParts.Bars(g, spec, bins, Bins, accent);
            for (int m = 0; m <= 24; m += 4) LearnPictureParts.VGridLine(g, spec, m / 24.0, m == 0 ? null : m + "x");
            g.Text("sidebands", spec.Right - 4, r.Y + 4, 10, Palette.DimText, false, NeoAlign.Right);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Mod Ratio →, FM Depth ↑: drag the dot; double-click resets");
        }
    }
}
