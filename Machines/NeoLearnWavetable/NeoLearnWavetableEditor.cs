#nullable disable
// Neo Learn Wavetable's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is
// the table's frames as a terrain (a few of them, back to front, the slice at Frame Pos drawn on top: it moves as the LFO scans),
// read from the table the engine is playing. The pad is Frame Pos (across) and Phase Warp (up).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnWt
{
    internal sealed class NeoLearnWavetableEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xB4, 0x8E, 0xE8));

        /// <summary>The terrain: this many frames of the table, each drawn from this many samples.</summary>
        public const int Slices = 9, Points = 96;

        TableArena shown;
        float[][] slices;

        protected override string Title => "Neo Learn Wavetable";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Wavetable";
        protected override int PadX => P.WtPosition;
        protected override int PadY => P.PhaseWarp;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnWavetableMachine)?.Host;

        /// <summary>The slices of <paramref name="table"/> (frames spread evenly across it, its level 0 sampled to <see cref="Points"/> points, peak 1).</summary>
        internal static float[][] SlicesOf(TableArena table)
        {
            if (table == null) return null;
            int frames = table.FrameCount, len = Wavetable.LevelLength(0);
            int n = Math.Min(Slices, frames);
            var s = new float[n][];
            for (int i = 0; i < n; i++)
            {
                int frame = n == 1 ? 0 : (int)Math.Round(i * (frames - 1) / (double)(n - 1));
                int at = TableArena.Offset(frame, 0) + 1;
                var y = new float[Points];
                float peak = 1e-3f;
                for (int j = 0; j < Points; j++)
                {
                    y[j] = table.Data[at + j * len / Points];
                    peak = Math.Max(peak, Math.Abs(y[j]));
                }
                for (int j = 0; j < Points; j++) y[j] /= peak;
                s[i] = y;
            }
            return s;
        }

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            var table = (ctx.Machine?.ManagedMachine as NeoLearnWavetableMachine)?.Host?.Engine.TableFor(0);
            if (!ReferenceEquals(table, shown))
            {
                shown = table;
                slices = SlicesOf(table);
            }
            LearnPictureParts.Terrain(g, r, slices, ctx.Value(P.WtPosition), accent);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Frame Pos →, Phase Warp ↑: drag the dot; double-click resets");
        }
    }
}
