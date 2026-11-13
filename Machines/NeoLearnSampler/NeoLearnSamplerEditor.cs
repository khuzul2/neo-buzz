#nullable disable
// Neo Learn Sampler's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is the source waveform with the
// start offset as a line and, when Loop is up, the looped region as a band; the output's scope is faint behind. The pad is Start Offset (across) and Loop (up).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;
using NeoBuzz.Sampler;

namespace NeoBuzz.LearnSampler
{
    internal sealed class NeoLearnSamplerEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xE8, 0xB4, 0x6A));

        protected override string Title => "Neo Learn Sampler";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Sampling";
        protected override int PadX => P.SampleOffset;
        protected override int PadY => P.Loop;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnSamplerMachine)?.Host;

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoLearnSamplerMachine;
            var host = machine?.Host;
            int source = (int)Math.Clamp(Math.Round(ctx.Value(P.Source)), 0, SampleSources.Count);
            SampleLayer layer = null;
            if (machine != null) layer = source >= SampleSources.Count && machine.UserSampleName.Length > 0 ? host.Engine.UserSample(0) : SampleSources.Get(source >= SampleSources.Count ? 0 : source);
            double loop = ctx.Value(P.Loop);
            SamplerMap.Region(ctx.Value(P.SampleOffset), loop, out double start, out double end);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            LearnPictureParts.Overview(g, r, layer?.Overview, accent);
            LearnPictureParts.Region(g, r, start, end, loop > 0, accent);
            if (layer == null) g.Text("building the source...", r.X + 6, r.Y + 20, 10, Palette.DimText);
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Start ->, Loop up: drag the dot; double-click resets");
        }
    }
}
