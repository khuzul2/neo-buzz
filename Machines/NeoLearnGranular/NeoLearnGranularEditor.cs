#nullable disable
// Neo Learn Granular's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the source
// waveform with the grains drawn over it as they start (each at its place, length and pan, in the contour's window, an octave up in white), the scan
// position as a line and the spray's reach as a band, the output's scope faint behind. The pad is Scan Position (across) and Grain Size (up).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;
using NeoBuzz.Sampler;

namespace NeoBuzz.LearnGrain
{
    internal sealed class NeoLearnGranularEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xE8, 0xB4, 0x6A));

        /// <summary>The grain window is drawn from this many points.</summary>
        public const int WindowPoints = 24;

        readonly GrainCloud cloud = new GrainCloud();
        readonly double[] window = new double[WindowPoints];

        protected override string Title => "Neo Learn Granular";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Granular";
        protected override int PadX => P.SamplePosition;
        protected override int PadY => P.GrainSize;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnGranularMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoLearnGranularMachine;
            var host = machine?.Host;
            SampleLayer layer = null;
            if (machine != null) layer = machine.Source >= SampleSources.Count ? host.Engine.UserSample(0) : SampleSources.Get(machine.Source);
            double seconds = layer != null ? layer.Length / (double)Math.Max(1, layer.Rate) : 1;
            cloud.Update(host?.Engine.Bank, seconds, Environment.TickCount64);
            double contour = ctx.Value(P.GrainShape);
            for (int i = 0; i < WindowPoints; i++) window[i] = GranularMap.Window(contour, i / (double)(WindowPoints - 1));
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.Overview(g, r, layer?.Overview, accent);
            LearnPictureParts.Grains(g, r, ctx.Value(P.SamplePosition), ctx.Value(P.PositionSpray), window, cloud.Count, cloud.Pos, cloud.Length, cloud.Pan, cloud.Age, cloud.Up, accent);
            if (layer == null) g.Text("building the source...", r.X + 6, r.Y + 20, 10, Palette.DimText);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Scan ->, Grain Size up: drag the dot; double-click resets");
        }
    }
}
