#nullable disable
// Neo Learn Spectral's editor (docs/specs/neo-learn.md §5.4): the Neo Learn template over this machine's 12 controls. The picture is a spectrogram of the source's
// analysis (the partials it was made of, time across, frequency up): the partials the Partial Gate keeps are bright and those it drops are dim, a line marks where
// the reading starts (and, at 100 % Freeze, holds), and the output's scope is faint behind. The pad is Stretch (across) and Time Smear (up).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using NeoBuzz.Editor;
using NeoBuzz.Learn;

namespace NeoBuzz.LearnSpectral
{
    internal sealed class NeoLearnSpectralEditor : LearnEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x9C, 0xC8, 0xE8));

        protected override string Title => "Neo Learn Spectral";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override string Category => "Sample";
        protected override int PadX => P.SpectralShift;
        protected override int PadY => P.SpectralBlur;

        protected override LearnHost HostOf(EditorContext ctx) => (ctx.Machine?.ManagedMachine as NeoLearnSpectralMachine)?.Host;

        // The cells of the source's spectrogram: made once for a model and a gate (the number of partials kept), not every frame.
        const int Cols = 96, Rows = 40;
        readonly float[] kept = new float[Cols * Rows], dropped = new float[Cols * Rows];
        SpectralModel gridModel;
        int gridKeep = -1;

        void Grid(SpectralModel m, int keep)
        {
            if (ReferenceEquals(m, gridModel) && keep == gridKeep) return;
            gridModel = m;
            gridKeep = keep;
            Array.Clear(kept, 0, kept.Length);
            Array.Clear(dropped, 0, dropped.Length);
            if (m == null || m.Frames < 1) return;
            float peak = 1e-6f;
            for (int i = 0; i < m.Amp.Length; i++) peak = Math.Max(peak, m.Amp[i]);
            double lo = Math.Log2(50.0), span = Math.Log2(16000.0) - lo;
            for (int c = 0; c < Cols; c++)
            {
                int f = Math.Min(m.Frames - 1, (int)((c + 0.5) * m.Frames / Cols));
                for (int p = m.Start[f]; p < m.Start[f + 1]; p++)
                {
                    float a = m.Amp[p];
                    if (a <= 0f) continue;
                    // The gate keeps the loudest `keep` partials of the frame.
                    int louder = 0;
                    for (int q = m.Start[f]; q < m.Start[f + 1]; q++) if (m.Amp[q] > a) louder++;
                    double db = 20 * Math.Log10(a / peak);
                    float level = (float)Math.Clamp((db + 60) / 60, 0, 1);
                    int y = (int)Math.Clamp((Math.Log2(Math.Max(m.Freq[p], 1f)) - lo) / span * Rows, 0, Rows - 1);
                    var cell = louder < keep ? kept : dropped;
                    int i = c * Rows + y;
                    cell[i] = Math.Max(cell[i], level);
                }
            }
        }

        protected override void DrawPicture(INeoCanvas g, NeoRect r, EditorContext ctx, float[] scope, int scopeCount, NeoPoint? handle, bool dragging, NeoColor accent)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoLearnSpectralMachine;
            var host = machine?.Host;
            int source = (int)Math.Clamp(Math.Round(ctx.Value(P.SpectralSource)), 0, SampleSources.Count);
            SpectralModel model = null;
            if (machine != null) model = source >= SampleSources.Count && machine.UserSampleName.Length > 0 ? host.Engine.UserModel(0) : SpectralSources.Get(source >= SampleSources.Count ? 0 : source);
            double freeze = ctx.Value(P.SpectralFreeze);
            SpectralMap.Reading(freeze, out double position, out _);
            LearnPictureParts.Scope(g, r, scope, scopeCount, Palette.DimText.WithAlpha(0x40), 0.5, 0.3);
            Grid(model, SpectralMap.Partials(ctx.Value(P.BinGate)));
            LearnPictureParts.Spectrogram(g, r, kept, dropped, Cols, Rows, model != null && freeze > 0 ? position : -1, freeze >= 0.995, accent);
            if (model == null)
            {
                float progress = machine?.AnalysisProgress ?? -1f;
                g.Text(progress >= 0 ? $"analysing {progress * 100:0} %..." : "analysing the source...", r.X + 6, r.Y + 20, 10, Palette.DimText);
            }
            LearnPictureParts.Handle(g, r, handle, dragging, accent, "Stretch ->, Time Smear up: drag the dot; double-click resets");
        }
    }
}
