#nullable disable
// Neo Learn Vector's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the diamond with its four corners
// (lit by the weight the cursor gives them), the cursor and its trail as the orbit moves it; the output's scope is faint behind. The pad is Vector X and Vector Y.
using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnVector
{
    internal sealed class NeoLearnVectorEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x8F, 0xB8, 0xF0));

        public const int Trail = 48;

        static readonly string[][] Names =
        {
            new[] { "saw", "square", "choir", "sub" },
            new[] { "strings", "harmonics", "texture", "sub+air" },
            new[] { "pulse", "digital", "sync sweep", "sub" },
            new[] { "organ", "bell", "formants", "sub+air" },
        };

        readonly double[] weights = new double[4];
        readonly double[] trailX = new double[Trail], trailY = new double[Trail];
        int filled;
        long last;

        protected override string Title => "Neo Learn Vector";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Vector";
        protected override int PadX => P.VectorX;
        protected override int PadY => P.VectorY;
        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnVectorMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            var map = HostOf(ctx)?.Map as VectorMap;
            double x = ctx.Value(P.VectorX), y = ctx.Value(P.VectorY);
            if (map != null)
            {
                map.Weights(out float w, out float e, out float n, out float s);
                weights[0] = w; weights[1] = e; weights[2] = n; weights[3] = s;
                x = map.CursorX; y = map.CursorY;
            }
            else VectorMap.CornerWeights(x, y, out weights[0], out weights[1], out weights[2], out weights[3]);
            long now = Environment.TickCount64;
            if (now - last >= 40)
            {
                last = now;
                if (filled < Trail) filled++;
                Array.Copy(trailX, 1, trailX, 0, Trail - 1);
                Array.Copy(trailY, 1, trailY, 0, Trail - 1);
                trailX[Trail - 1] = x;
                trailY[Trail - 1] = y;
            }
            int palette = (int)Math.Clamp(Math.Round(ctx.Value(P.CornerPalette)), 0, 3);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.VectorDiamond(g, r, Names[palette], weights, trailX, trailY, Trail, x, y, accent);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Vector X ->, Vector Y up: drag the dot; double-click resets");
        }
    }
}
