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
        // Fork (ux-overhaul.md P2a): the Drums identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Drums, NeoColor.FromRgb(0xFF, 0x7A, 0x45));

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

            var padView = new InteractiveGraph(480, 200, (g, r) => pads.Draw(g, r, AccentColor), pads, animate: true)
            {
                ToolTip = "The voices: each lights as it plays; click one to play it (harder towards its top) and show it on the right. " +
                          "Each names its hit column (BD, SD, …) and its key (General MIDI from C2).",
            };
            var hitView = new GraphView(400, 200, (g, r) => hit.Draw(g, r, view, pads.Selected, AccentColor))
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

#if AVALONIA
            return StudioBody(ctx, machine, view, pads, padView, hitView, kit);
#else
            var kick = Voice(ctx,
                Row((P.KickTune, "Tune"), (P.KickSweep, "Sweep"), (P.KickSweepT, "Sweep time"), (P.KickDecay, "Decay"), (P.KickPunch, "Punch")),
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
#endif
        }

#if AVALONIA
        /// <summary>
        /// ux-overhaul.md P5a, the studio's layout: the pads (a click or the arrow keys select one; each lights as it plays), the
        /// selected pad's panel alone (its tune, decay and tone; the kick its sweep, click and drive), and a mix strip with
        /// every pad's level and pan under its name (the closed hat chokes the open hat, which the machine does itself and has no
        /// control for; it has no sends). The same parameters, in the same order, as the classic layout; only where they sit differs.
        /// </summary>
        UIElement StudioBody(EditorContext ctx, NeoDrumsMachine machine, View view, DrumPads pads, UIElement padView, UIElement hitView, UIElement kit)
        {
            // (parameter, label) knobs of each voice's panel, in rows; level and pan are in the mix strip.
            var rows = new (int, string)[][][]
            {
                new[] { Row((P.KickTune, "Tune"), (P.KickSweep, "Sweep"), (P.KickSweepT, "Sweep time"), (P.KickDecay, "Decay"), (P.KickPunch, "Punch")),
                        Row((P.KickClick, "Click"), (P.KickDrive, "Drive")) },
                new[] { Row((P.SnareTune, "Tune"), (P.SnareTone, "Tone"), (P.SnareSnap, "Snap"), (P.SnareDecay, "Decay")) },
                new[] { Row((P.ClapTune, "Tune"), (P.ClapSpread, "Spread"), (P.ClapDecay, "Decay")) },
                new[] { Row((P.RimTune, "Tune"), (P.RimDecay, "Decay")) },
                new[] { Row((P.ChTune, "Tune"), (P.ChTone, "Tone"), (P.ChDecay, "Decay")) },
                new[] { Row((P.OhTune, "Tune"), (P.OhTone, "Tone"), (P.OhDecay, "Decay")) },
                new[] { Row((P.LtTune, "Tune"), (P.LtDecay, "Decay"), (P.LtTone, "Tone")) },
                new[] { Row((P.MtTune, "Tune"), (P.MtDecay, "Decay"), (P.MtTone, "Tone")) },
                new[] { Row((P.HtTune, "Tune"), (P.HtDecay, "Decay"), (P.HtTone, "Tone")) },
                new[] { Row((P.CbTune, "Tune"), (P.CbDecay, "Decay")) },
                new[] { Row((P.CcTune, "Tune"), (P.CcTone, "Tone"), (P.CcDecay, "Decay")) },
                new[] { Row((P.RcTune, "Tune"), (P.RcTone, "Tone"), (P.RcDecay, "Decay")) },
            };
            int[] level = { P.KickLevel, P.SnareLevel, P.ClapLevel, P.RimLevel, P.ChLevel, P.OhLevel, P.LtLevel, P.MtLevel, P.HtLevel, P.CbLevel, P.CcLevel, P.RcLevel };
            int[] pan = { P.KickPan, P.SnarePan, P.ClapPan, P.RimPan, P.ChPan, P.OhPan, P.LtPan, P.MtPan, P.HtPan, P.CbPan, P.CcPan, P.RcPan };

            var panels = new Section[DrumsEngine.VoiceCount];
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
            for (int v = 0; v < panels.Length; v++)
            {
                var content = Voice(ctx, rows[v]);
                if (v == 4 || v == 5)
                    ((Avalonia.Controls.StackPanel)content).Children.Add(new TextBlock
                    {
                        Text = v == 4 ? "Chokes the open hat." : "Choked by the closed hat.",
                        FontFamily = Theme.FontFamily,
                        FontSize = 11,
                        Foreground = Theme.Brush(Palette.DimText),
                        Margin = new Thickness(0, 4, 0, 0),
                    });
                panels[v] = new Section(DrumsEngine.Names[v], AccentColor, content) { IsVisible = v == pads.Selected };
                host.Children.Add(panels[v]);
            }
            // The panels share the kick's size (the tallest), so the window doesn't move from pad to pad.
            panels[0].IsVisible = true;
            panels[0].Measure(Avalonia.Size.Infinity);
            host.MinWidth = panels[0].DesiredSize.Width;
            host.MinHeight = panels[0].DesiredSize.Height;
            for (int v = 0; v < panels.Length; v++) panels[v].IsVisible = v == pads.Selected;

            Avalonia.Automation.AutomationProperties.SetName(padView, "Pad grid");
            var strip = new MixStrip(ctx, view, pads, AccentColor, level, pan);
            Avalonia.Controls.Control mix = new Section("Mix", AccentColor, strip);

            pads.SelectedChanged += v =>
            {
                for (int i = 0; i < panels.Length; i++) panels[i].IsVisible = i == v;
                strip.Redraw();
            };

            return Layout.Column(
                Layout.Row(
                    new Section("Pads", AccentColor, padView),
                    new Section("Hit", AccentColor, hitView),
                    new Section("Kit", AccentColor, kit)),
                Layout.Row(host, mix));
        }

        /// <summary>
        /// Every pad's level and pan in a column under its name: the name lights as the pad does and a click on it selects the pad
        /// (the same selection as the pads'); the selected pad's column is outlined.
        /// </summary>
        sealed class MixStrip : Avalonia.Controls.StackPanel
        {
            public const double Column = 56, Header = 22;
            readonly InteractiveGraph names;

            public MixStrip(EditorContext ctx, IDrumsView view, DrumPads pads, NeoColor accent, int[] level, int[] pan)
            {
                var input = new MixNames(view, pads, Column, Header);
                names = new InteractiveGraph(Column * DrumsEngine.VoiceCount, Header, (g, r) => input.Draw(g, r, accent), input, animate: true)
                {
                    ToolTip = "Each pad's level and pan. A name lights as its pad plays; click one to select the pad. The closed hat chokes the open hat.",
                };
                Avalonia.Automation.AutomationProperties.SetName(names, "Mix: pad names");
                var levels = Layout.Row();
                var pans = Layout.Row();
                for (int v = 0; v < DrumsEngine.VoiceCount; v++)
                {
                    var l = new ParamKnob(ctx, level[v], "Level", 30) { Width = Column };
                    var p = new ParamKnob(ctx, pan[v], "Pan", 30) { Width = Column };
                    Avalonia.Automation.AutomationProperties.SetName(l, DrumsEngine.Names[v] + " level");
                    Avalonia.Automation.AutomationProperties.SetName(p, DrumsEngine.Names[v] + " pan");
                    levels.Children.Add(l);
                    pans.Children.Add(p);
                }
                Orientation = Avalonia.Layout.Orientation.Vertical;
                Children.Add(names);
                Children.Add(levels);
                Children.Add(pans);
            }

            public void Redraw() => names.InvalidateVisual();
        }
#endif
    }
}
