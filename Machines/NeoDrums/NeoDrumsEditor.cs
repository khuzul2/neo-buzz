#nullable disable
// Neo Drums' editor (docs/specs/machines.md §4, §5): the pads (one per voice, lit as it plays; click to play it, harder
// towards its top), the selected voice's hit as its settings make it now, the kit's level and dynamics with the output
// meter, then each voice's controls: kick, snare, clap, rim and cowbell; the hats; the toms; the cymbals. The pictures
// draw on the kit's canvas with toolkit-free input (DrumsViews.cs), so WPF and the studio's Avalonia show the same.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Drums
{
    internal sealed class NeoDrumsEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xFF, 0x7A, 0x45);

        protected override string Title => "Neo Drums";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        sealed class View : IDrumsView
        {
            readonly EditorContext ctx;
            readonly NeoDrumsMachine machine;

            public View(EditorContext ctx, NeoDrumsMachine machine)
            {
                this.ctx = ctx;
                this.machine = machine;
            }

            public int Raw(int index) => ctx.Raw(index);
            public float Peak(int voice) => machine?.Engine.VoicePeak[voice] ?? 0f;
            public void Audition(int voice, int velocity) => machine?.Audition(voice, velocity);
        }

        const double D = 38;

        static UIElement Knob(EditorContext ctx, int index, string label) => new ParamKnob(ctx, index, label, D);

        /// <summary>A voice's knobs in rows (its tune, decay and tone first, then level and pan).</summary>
        static UIElement Voice(EditorContext ctx, params (int Index, string Label)[][] rows)
        {
            var column = Layout.Column();
            foreach (var row in rows)
            {
                var r = Layout.Row();
                foreach (var (index, label) in row) r.Children.Add(Knob(ctx, index, label));
                column.Children.Add(r);
            }
            return column;
        }

        static (int, string)[] Row(params (int, string)[] knobs) => knobs;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoDrumsMachine;
            var view = new View(ctx, machine);
            var pads = new DrumPads(view);
            var hit = new HitPicture();

            var padView = new InteractiveGraph(480, 150, (g, r) => pads.Draw(g, r, AccentColor), pads, animate: true)
            {
                ToolTip = "The voices: each lights as it plays; click one to play it (harder towards its top) and show it on the right. " +
                          "Each names its hit column (BD, SD, …) and its key (General MIDI from C2).",
            };
            var hitView = new GraphView(400, 150, (g, r) => hit.Draw(g, r, view, pads.Selected, AccentColor))
            {
                ToolTip = "The selected voice's hit at velocity 100, as its settings make it now: its waveform over its decay (scaled to fill), its peak and how long it sounds",
            };
            ctx.Changed += () =>
            {
                hit.Invalidate();
                hitView.InvalidateVisual();
            };
            pads.SelectedChanged += _ =>
            {
                padView.InvalidateVisual();
                hitView.InvalidateVisual();
            };

            var kit = Layout.Column(
                Layout.Row(Knob(ctx, P.Volume, "Volume"), Knob(ctx, P.Accent, "Accent"),
                    Layout.Gap(4), new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 140)),
                Layout.Row(Knob(ctx, P.VelLevel, "Vel → level"), Knob(ctx, P.VelTone, "Vel → tone")));

            UIElement Line(string name, params (int Index, string Label)[] knobs)
            {
                var r = Layout.Row(new TextBlock
                {
                    Text = name,
                    Width = 58,
                    FontFamily = Theme.FontFamily,
                    FontSize = 11,
                    Foreground = Theme.Brush(Palette.DimText),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                foreach (var (index, label) in knobs) r.Children.Add(Knob(ctx, index, label));
                return r;
            }

            var kick = Voice(ctx,
                Row((P.KickTune, "Tune"), (P.KickSweep, "Sweep"), (P.KickSweepT, "Sweep time"), (P.KickDecay, "Decay")),
                Row((P.KickClick, "Click"), (P.KickDrive, "Drive"), (P.KickLevel, "Level"), (P.KickPan, "Pan")));
            var snare = Voice(ctx,
                Row((P.SnareTune, "Tune"), (P.SnareTone, "Tone"), (P.SnareSnap, "Snap")),
                Row((P.SnareDecay, "Decay"), (P.SnareLevel, "Level"), (P.SnarePan, "Pan")));
            var clap = Voice(ctx,
                Row((P.ClapTune, "Tune"), (P.ClapSpread, "Spread"), (P.ClapDecay, "Decay")),
                Row((P.ClapLevel, "Level"), (P.ClapPan, "Pan")));
            var rim = Voice(ctx,
                Row((P.RimTune, "Tune"), (P.RimDecay, "Decay")),
                Row((P.RimLevel, "Level"), (P.RimPan, "Pan")));
            var cowbell = Voice(ctx,
                Row((P.CbTune, "Tune"), (P.CbDecay, "Decay")),
                Row((P.CbLevel, "Level"), (P.CbPan, "Pan")));
            var hats = Layout.Column(
                Line("Closed", (P.ChTune, "Tune"), (P.ChTone, "Tone"), (P.ChDecay, "Decay"), (P.ChLevel, "Level"), (P.ChPan, "Pan")),
                Line("Open", (P.OhTune, "Tune"), (P.OhTone, "Tone"), (P.OhDecay, "Decay"), (P.OhLevel, "Level"), (P.OhPan, "Pan")));
            var toms = Layout.Column(
                Line("Low", (P.LtTune, "Tune"), (P.LtDecay, "Decay"), (P.LtTone, "Tone"), (P.LtLevel, "Level"), (P.LtPan, "Pan")),
                Line("Mid", (P.MtTune, "Tune"), (P.MtDecay, "Decay"), (P.MtTone, "Tone"), (P.MtLevel, "Level"), (P.MtPan, "Pan")),
                Line("High", (P.HtTune, "Tune"), (P.HtDecay, "Decay"), (P.HtTone, "Tone"), (P.HtLevel, "Level"), (P.HtPan, "Pan")));
            var cymbals = Layout.Column(
                Line("Crash", (P.CcTune, "Tune"), (P.CcTone, "Tone"), (P.CcDecay, "Decay"), (P.CcLevel, "Level"), (P.CcPan, "Pan")),
                Line("Ride", (P.RcTune, "Tune"), (P.RcTone, "Tone"), (P.RcDecay, "Decay"), (P.RcLevel, "Level"), (P.RcPan, "Pan")));

            return Layout.Column(
                Layout.Row(
                    new Section("Pads", AccentColor, padView),
                    new Section("Hit", AccentColor, hitView),
                    new Section("Kit", AccentColor, kit)),
                Layout.Row(
                    new Section("Kick", AccentColor, kick),
                    new Section("Snare", AccentColor, snare),
                    new Section("Clap", AccentColor, clap),
                    new Section("Rim", AccentColor, rim),
                    new Section("Cowbell", AccentColor, cowbell)),
                Layout.Row(
                    new Section("Hats", AccentColor, hats),
                    new Section("Toms", AccentColor, toms),
                    new Section("Cymbals", AccentColor, cymbals)));
        }
    }
}
