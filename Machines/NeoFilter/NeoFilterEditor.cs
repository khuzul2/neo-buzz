#nullable disable
// Neo Filter's editor (docs/specs/machines.md §4): the filter (type, slope, cutoff, resonance, drive),
// its response, the envelope follower, the LFO and the output. The response is the design the engine
// runs (FilterDesign, the same function): the curve at the knobs' setting, and over it the curve where
// the envelope and the LFO have moved the cutoff now, each channel's, with a handle on the cutoff to
// drag (across: cutoff, up: resonance; the wheel: resonance). The Vowel type (EXT1) draws the three formants' response the same way, and
// its section (Vowel, Voice, Vowel mod and a picture of where the vowel stands) is dimmed and left alone in the other types. WPF lays the editor out; the graphs draw
// on the kit's canvas (INeoCanvas), so the studio's Avalonia flavour draws the same.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Filter
{
    internal sealed class NeoFilterEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the EqFilter identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.EqFilter, NeoColor.FromRgb(0xD4, 0xE1, 0x57));
        static readonly NeoColor RightColor = Tokens.Partner(AccentColor, NeoColor.FromRgb(0x7E, 0xC8, 0xFF));

        protected override string Title => "Neo Filter";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        // The resampling the curves include, per sample rate (the engine's when there is one).
        IirHalfbandDesign[] stages;
        double stagesRate;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoFilterMachine;
            var graph = new ResponseGraph(-42, 24, 6);
            var handle = new BandHandleInput(graph, new CutoffHandle(ctx));
            var view = new InteractiveGraph(586, 236, (g, r) => DrawResponse(g, r, ctx, graph, handle, machine), handle, animate: true)
            {
                ToolTip = "The filter's response at its setting (faint) and where the envelope and the LFO have moved it now (left, and right in blue). " +
                          "Drag the handle: across the cutoff, up the resonance (Shift: fine). Wheel: resonance.",
            };
            ctx.Changed += view.InvalidateVisual;

            var filter = Layout.Column(
                new ParamDropdown(ctx, P.Type, width: 124),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Slope, cellWidth: 62),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.Cutoff, diameter: 60), new ParamKnob(ctx, P.Resonance, diameter: 52)),
                new ParamKnob(ctx, P.Drive));

            var envelope = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.EnvAmount, "Amount", 50), new ParamKnob(ctx, P.EnvSens, "Sensitivity")),
                    Layout.Row(new ParamKnob(ctx, P.EnvAttack, "Attack"), new ParamKnob(ctx, P.EnvRelease, "Release"))),
                Layout.Gap(4),
                new Meter("Envelope", () => (float)(machine?.Engine.EnvelopeLevel ?? 0), AccentColor, 196));

            var lfoPicture = new GraphView(172, 36, (g, r) => DrawLfo(g, r, ctx, machine), animate: true)
            {
                ToolTip = "One cycle of the LFO and where each channel is in it (left, and right in blue)",
            };
            ctx.Changed += lfoPicture.InvalidateVisual;
            var lfo = Layout.Column(
                new ParamDropdown(ctx, P.LfoShape, "Shape", 112),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.LfoRetrig, "Retrig", 58),
                Layout.Gap(1, 4),
                lfoPicture,
                Layout.Row(ParamTempoTime.Create(ctx, new TempoTimeSpec { Division = P.LfoSync, Feel = P.LfoFeel, Rate = P.LfoRate, Hz = true, RateLabel = "Rate" }), new ParamKnob(ctx, P.LfoDepth, "Depth"), new ParamKnob(ctx, P.LfoSpread, "Spread"), new ParamKnob(ctx, P.LfoPhase, "Phase")));

            var vowelPicture = new GraphView(172, 44, (g, r) => DrawVowel(g, r, ctx, machine), animate: true)
            {
                ToolTip = "The vowel: A, E, I, O, U along the line, and where each channel's vowel is now (left, and right in blue)",
            };
            ctx.Changed += vowelPicture.InvalidateVisual;
            var vowel = Layout.Column(
                new ParamChoice(ctx, P.Voice, "Voice", 58),
                Layout.Gap(1, 4),
                vowelPicture,
                Layout.Row(new ParamKnob(ctx, P.Vowel, "Vowel"), new ParamKnob(ctx, P.VowelMod, "Mod")));
            var vowelSection = new Section("Vowel", AccentColor, vowel);
            void ShowVowel()
            {
                // Only the Vowel type uses these controls: the section stays in place, dimmed and out of reach in the others.
                bool on = ctx.Raw(P.Type) == (int)FilterType.Vowel;
                vowelSection.Opacity = on ? 1 : 0.38;
                vowelSection.IsHitTestVisible = on;
            }
            ctx.Changed += ShowVowel;
            ShowVowel();

            var output = Layout.Row(
                Layout.Column(Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40)), new ParamKnob(ctx, P.Output)),
                Layout.Gap(6),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 196),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 196));

            return Layout.Column(
                Layout.Row(
                    new Section("Filter", AccentColor, filter),
                    new Section("Response", AccentColor, view)),
                Layout.Row(
                    new Section("Envelope", AccentColor, envelope),
                    new Section("LFO", AccentColor, lfo),
                    vowelSection,
                    new Section("Output", AccentColor, output)));
        }

        IirHalfbandDesign[] Stages(NeoFilterMachine machine, double rate)
        {
            if (machine != null) return machine.Engine.Stages;
            if (stages == null || stagesRate != rate)
            {
                stages = FilterDesign.Halfbands(rate);
                stagesRate = rate;
            }
            return stages;
        }

        static FilterSetting Setting(EditorContext ctx, double tempo)
        {
            var rawValues = new ParamValues(Params.Table);
            var values = new ParamValues(Params.Table);
            values.AllowPhysical();
            for (int i = 0; i < Params.Table.Length; i++) rawValues.Set(i, ctx.Raw(i));
            FxStage.Fill(rawValues, values, P.Amount, FilterEngine.AmountRules);
            return FilterDesign.From(values, tempo);
        }

        void DrawResponse(INeoCanvas g, NeoRect r, EditorContext ctx, ResponseGraph graph, BandHandleInput handle, NeoFilterMachine machine)
        {
            graph.Layout(r);
            graph.DrawGrid(g);
            var engine = machine?.Engine;
            double rate = engine?.SampleRate ?? 48000;
            var s = Setting(ctx, 120);
            var st = Stages(machine, rate);
            double osc = s.SelfOsc ? 1 : 0;
            if (s.Type == FilterType.Vowel)
            {
                DrawVowelResponse(g, graph, handle, ctx, engine, s, rate, st);
                return;
            }
            double Curve(double cutoff, double hz) => FilterDesign.Db(s.Type, s.Steep, cutoff, s.Q, hz, rate, st, osc);
            // The setting's curve, faint and filled; then where the modulation has the cutoff now, each channel's:
            // inside the plot, never over the frequency labels.
            graph.InPlot(g, () =>
            {
                g.Path(Under(graph, hz => Curve(s.CutoffHz, hz)), AccentColor.WithAlpha(0x28), NeoStroke.None);
                g.Path(graph.Curve(hz => Curve(s.CutoffHz, hz)), NeoFill.None, new NeoStroke(AccentColor.WithAlpha(0x80), 1.2));
                if (engine != null)
                {
                    double l = engine.Cutoff(0), rr = engine.Cutoff(1);
                    if (Math.Abs(rr / l - 1) > 1e-3)
                    {
                        g.Path(graph.Curve(hz => Curve(rr, hz)), NeoFill.None, new NeoStroke(RightColor, 1.6));
                        g.Line(graph.X(rr), graph.Plot.Top, graph.X(rr), graph.Plot.Bottom, new NeoStroke(RightColor.WithAlpha(0x70), 1, false));
                    }
                    g.Path(graph.Curve(hz => Curve(l, hz)), NeoFill.None, new NeoStroke(AccentColor, 2));
                    g.Line(graph.X(l), graph.Plot.Top, graph.X(l), graph.Plot.Bottom, new NeoStroke(AccentColor.WithAlpha(0x70), 1, false));
                }
                g.Text(FilterDesign.Summary(s), graph.Plot.Left + 4, graph.Plot.Top + 2, 10, Palette.DimText);
            });
            handle.Draw(g, new[] { AccentColor });
        }

        /// <summary>The Vowel type's response: the three formants' sum at the setting (faint), and where the modulation has each channel's vowel and shift now.</summary>
        void DrawVowelResponse(INeoCanvas g, ResponseGraph graph, BandHandleInput handle, EditorContext ctx, FilterEngine engine, FilterSetting s, double rate, IirHalfbandDesign[] st)
        {
            double bw = VowelDesign.BandwidthScale(s.Q);
            Func<double, double> Curve(double pos, double shift)
            {
                var f = new double[3]; var k = new double[3]; var a = new double[3];
                VowelDesign.Design(pos, s.Voice, shift, bw, rate, f, k, a);
                return hz => 20 * Math.Log10(Math.Max(1e-15, FilterDesign.ResamplingGain(st, hz, rate) * VowelDesign.Magnitude(f, k, a, hz, rate)));
            }
            graph.InPlot(g, () =>
            {
                var set = Curve(s.VowelPos, VowelDesign.CutoffShift(s.CutoffHz));
                g.Path(Under(graph, set), AccentColor.WithAlpha(0x28), NeoStroke.None);
                g.Path(graph.Curve(set), NeoFill.None, new NeoStroke(AccentColor.WithAlpha(0x80), 1.2));
                if (engine != null)
                {
                    var l = Curve(engine.VowelPosition(0), engine.VowelShiftNow(0));
                    var r = Curve(engine.VowelPosition(1), engine.VowelShiftNow(1));
                    if (Math.Abs(engine.VowelPosition(1) - engine.VowelPosition(0)) > 1e-3 || Math.Abs(engine.VowelShiftNow(1) - engine.VowelShiftNow(0)) > 1e-3)
                        g.Path(graph.Curve(r), NeoFill.None, new NeoStroke(RightColor, 1.6));
                    g.Path(graph.Curve(l), NeoFill.None, new NeoStroke(AccentColor, 2));
                }
                g.Text(FilterDesign.Summary(s), graph.Plot.Left + 4, graph.Plot.Top + 2, 10, Palette.DimText);
            });
            handle.Draw(g, new[] { AccentColor });
        }

        /// <summary>The vowel line: A, E, I, O, U a quarter apart, a dot for each channel's vowel now, and the name of the nearest with its blend.</summary>
        static void DrawVowel(INeoCanvas g, NeoRect r, EditorContext ctx, NeoFilterMachine machine)
        {
            var s = Setting(ctx, 120);
            var area = new NeoRect(r.X + 10, r.Y + 4, r.Width - 20, r.Height - 8);
            double lineY = area.Y + 22;
            g.Line(area.Left, lineY, area.Right, lineY, new NeoStroke(Palette.Track, 1));
            for (int i = 0; i < VowelDesign.Vowels; i++)
            {
                double x = area.X + area.Width * i / (VowelDesign.Vowels - 1.0);
                g.Line(x, lineY - 3, x, lineY + 3, new NeoStroke(Palette.Track, 1));
                g.Text(VowelDesign.VowelNames[i], x, lineY + 5, 10, Palette.DimText, align: NeoAlign.Center);
            }
            for (int c = 1; c >= 0; c--)
            {
                double pos = machine != null && s.Type == FilterType.Vowel ? machine.Engine.VowelPosition(c) : s.VowelPos;
                if (c == 1 && Math.Abs(pos - s.VowelPos) < 1e-3 && (machine == null || Math.Abs(machine.Engine.VowelPosition(0) - pos) < 1e-3)) continue;
                g.Ellipse(new NeoPoint(area.X + pos * area.Width, lineY), 3.5, 3.5, c == 0 ? AccentColor : RightColor, NeoStroke.None);
            }
            g.Text(VowelDesign.Describe(s.VowelPos), area.Left, area.Y - 2, 9, Palette.DimText);
        }

        /// <summary>The area under a curve, down to the plot's floor.</summary>
        static NeoPath Under(ResponseGraph graph, Func<double, double> dbAt, int points = 160)
        {
            var path = new NeoPath().MoveTo(graph.Plot.Left, graph.Plot.Bottom);
            for (int i = 0; i <= points; i++)
            {
                double hz = graph.MinHz * Math.Pow(graph.MaxHz / graph.MinHz, i / (double)points);
                double db = Math.Clamp(dbAt(hz), graph.MinDb, graph.MaxDb + 6);
                path.LineTo(graph.X(hz), graph.Y(db));
            }
            return path.LineTo(graph.Plot.Right, graph.Plot.Bottom).Close();
        }

        static void DrawLfo(INeoCanvas g, NeoRect r, EditorContext ctx, NeoFilterMachine machine)
        {
            var s = Setting(ctx, ctx.SongTempo(machine?.Engine.Tempo ?? 120));
            var area = new NeoRect(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8);
            double mid = area.Y + area.Height / 2;
            g.Line(area.Left, mid, area.Right, mid, new NeoStroke(Palette.Track, 1));
            var path = new NeoPath();
            const int points = 96;
            for (int i = 0; i <= points; i++)
            {
                double p = i / (double)points;
                double v = FilterDesign.Wave(s.LfoShape, 7, Math.Min(p, 0.999999));
                double x = area.X + p * area.Width, y = mid - v * (area.Height / 2 - 2);
                if (i == 0) path.MoveTo(x, y);
                else path.LineTo(x, y);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor.WithAlpha(s.LfoOctaves > 0 ? (byte)0xFF : (byte)0x70), 1.5));
            if (machine == null) return;
            double phase = machine.Engine.LfoPhase;
            for (int c = 0; c < 2; c++)
            {
                double p = phase + (c == 1 ? s.LfoSpread : 0);
                p -= Math.Floor(p);
                double v = machine.Engine.LfoValue(c);
                var colour = c == 0 ? AccentColor : RightColor;
                g.Ellipse(new NeoPoint(area.X + p * area.Width, mid - v * (area.Height / 2 - 2)), 3.5, 3.5, colour, NeoStroke.None);
            }
            string rate = s.LfoSynced ? $"{Params.Table[P.LfoSync].Describe(ctx.Raw(P.LfoSync))} = {s.LfoHz:0.###} Hz" : $"{s.LfoHz:0.###} Hz";
            g.Text(rate, area.Right - 2, area.Y, 9, Palette.DimText, align: NeoAlign.Right);
        }

        /// <summary>The cutoff as a handle on the response: across the cutoff, up the resonance (its height 20·log10 Q dB).</summary>
        sealed class CutoffHandle : IBandHandles
        {
            readonly EditorContext ctx;

            public CutoffHandle(EditorContext ctx) { this.ctx = ctx; }

            public int Count => 1;

            public (double Hz, double Db, bool On, bool HasGain) Band(int i) =>
                (ctx.Value(P.Cutoff), 20 * Math.Log10(ctx.Value(P.Resonance)), true, true);

            public void Move(int i, double hz, double? db)
            {
                ctx.Set(P.Cutoff, Params.Table[P.Cutoff].Raw(hz));
                if (db.HasValue)
                {
                    var res = Params.Table[P.Resonance];
                    // Up to the last step below the top: dragging never lands on self-oscillation by itself.
                    ctx.Set(P.Resonance, Math.Min(res.Max - 1, res.Raw(Math.Pow(10, db.Value / 20))));
                }
                ctx.RaiseChanged();
            }

            public void StepQ(int i, int steps)
            {
                ctx.Set(P.Resonance, ctx.Raw(P.Resonance) + 5 * steps);
                ctx.RaiseChanged();
            }

            public void SetOn(int i, bool on) { }
        }
    }
}
