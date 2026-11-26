#nullable disable
// Neo Grain's editor (docs/specs/neo-grain.md): the source picker over a drawing of the source (its waveform with the Position and the Spray range on
// it, and a dot for every grain playing, drawn from the engine's published snapshot with no allocation per frame), then the Grain, Pitch, Envelope and
// Output sections. One source for WPF and Avalonia (the Neo editor kit).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Grain
{
    internal sealed class NeoGrainEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Synth identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0x5F, 0xB8, 0x9A));
        const double D = 44, SourceWidth = 520, SourceHeight = 138;

        protected override string Title => "Neo Grain";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        // What the drawing keeps between frames: the outline's path, rebuilt when the source or the width changes; the grains' snapshot.
        readonly NeoPath outline = new NeoPath();
        GrainSource outlined;
        double outlinedWidth, outlinedHeight;
        readonly float[] dotPos = new float[GrainEngine.MaxGrains], dotWeight = new float[GrainEngine.MaxGrains];

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoGrainMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            var source = new GraphView(SourceWidth, SourceHeight, (g, r) => DrawSource(g, r, ctx, machine), animate: true);
            ctx.Changed += source.InvalidateVisual;

            var src = S("Source", Layout.Column(
                Layout.Row(new ParamDropdown(ctx, P.Source, width: 130), Layout.Gap(10), new ParamKnob(ctx, P.Wave, diameter: D)),
                Layout.Gap(1, 4),
                source));

            var grain = S("Grain", Layout.Column(
                Build.Knobs(ctx, 4, D, P.Size, P.Density, P.Position, P.Spray),
                Build.Knobs(ctx, 4, D, P.Scan, P.Align, P.Seed),
                Layout.Gap(1, 2),
                new ParamDropdown(ctx, P.Window, width: 130)));

            var pitch = S("Pitch", Layout.Column(
                Build.Knobs(ctx, 2, D, P.Pitch, P.PitchSpray),
                Build.Knobs(ctx, 2, D, P.Reverse, P.PanSpread)));

            var envelope = S("Envelope", Layout.Column(
                Build.Envelope(ctx, P.Attack, P.Decay, P.Sustain, P.Release, 190, 56),
                Build.Knobs(ctx, 4, D, P.Attack, P.Decay, P.Sustain, P.Release)));

            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 100);
            var output = S("Output", Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Volume, diameter: 50), Layout.Gap(1, 4), new ParamChoice(ctx, P.Drone, "Drone", 44)),
                Layout.Gap(6),
                meter));

            return Layout.Column(
                Layout.Row(src, grain),
                Layout.Row(pitch, envelope, output));
        }

        /// <summary>The source's outline, the Position and Spray on it, and a dot for every grain sounding.</summary>
        void DrawSource(INeoCanvas g, NeoRect r, EditorContext ctx, NeoGrainMachine machine)
        {
            double x0 = r.X + 6, x1 = r.X + r.Width - 6, top = r.Y + 6, bottom = r.Y + r.Height - 6, mid = 0.5 * (top + bottom), half = 0.5 * (bottom - top);
            double width = x1 - x0;
            g.Rect(new NeoRect(r.X, r.Y, r.Width, r.Height), Palette.Track, new NeoStroke(Palette.PanelEdge, 1), 4);
            g.Line(new NeoPoint(x0, mid), new NeoPoint(x1, mid), new NeoStroke(Palette.Grid, 1, false));

            var src = machine?.Engine.CurrentSource;
            bool wave = ctx.Raw(P.Source) == 3;
            if (src == null)
            {
                string why = !wave ? "Source not ready" : machine == null ? "Song wave" : machine.Waves.Status switch
                {
                    GrainWaveSource.Loading => "Reading wave " + (ctx.Raw(P.Wave) + 1) + "...",
                    _ => "Wave " + (ctx.Raw(P.Wave) + 1) + " is empty: the cloud is silent",
                };
                g.Text(why, r.X + 0.5 * r.Width, mid - 7, 13, Palette.DimText, false, NeoAlign.Center);
                return;
            }

            // The outline: the largest value in each of 256 parts, mirrored about the middle.
            if (!ReferenceEquals(outlined, src) || outlinedWidth != width || outlinedHeight != half)
            {
                outline.Clear();
                var ov = src.Overview;
                for (int c = 0; c < ov.Length; c++) outline.LineTo(x0 + width * (c + 0.5) / ov.Length, mid - Math.Max(0.6, half * ov[c]));
                for (int c = ov.Length - 1; c >= 0; c--) outline.LineTo(x0 + width * (c + 0.5) / ov.Length, mid + Math.Max(0.6, half * ov[c]));
                outline.Close();
                outlined = src; outlinedWidth = width; outlinedHeight = half;
            }
            g.Path(outline, AccentColor.WithAlpha(120), new NeoStroke(AccentColor.WithAlpha(200), 1, false));

            // Position and the Spray range around it (Spray is milliseconds of the source).
            double seconds = src.Length / src.Rate;
            double pos = ctx.Value(P.Position), sprayMs = ctx.Value(P.Spray);
            double px = x0 + width * pos, sx = width * (sprayMs * 0.001 / Math.Max(0.001, seconds));
            double sa = Math.Max(x0, px - sx), sb = Math.Min(x1, px + sx);
            if (sb - sa > 1) g.Rect(new NeoRect(sa, top, sb - sa, bottom - top), AccentColor.WithAlpha(46), new NeoStroke(AccentColor.WithAlpha(120), 1, false), 2);
            g.Line(new NeoPoint(px, top - 2), new NeoPoint(px, bottom + 2), new NeoStroke(Palette.Text, 2));

            // The grains: a dot at each one's place in the source, bigger and brighter in the middle of its window.
            int n = machine.Engine.Dots(dotPos, dotWeight);
            for (int i = 0; i < n; i++)
            {
                double w = dotWeight[i];
                double y = top + 6 + (bottom - top - 12) * (((i * 0.6180339887) % 1.0));
                g.Ellipse(new NeoPoint(x0 + width * dotPos[i], y), 2 + 3 * w, 2 + 3 * w, Palette.Text.WithAlpha((byte)(70 + 160 * w)), NeoStroke.None);
            }
            g.Text(src.Name + (wave ? "" : " (built in)"), x0 + 2, top - 2, 10.5, Palette.DimText);
        }
    }
}
