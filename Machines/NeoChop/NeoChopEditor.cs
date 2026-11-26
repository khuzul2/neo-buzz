#nullable disable
// Neo Chop's editor (docs/specs/neo-chop.md): a mode row with an on/off light for each of the six modes, the section of the mode
// selected below it (tabs), the sixteen steps of the gate in a row of their own, and Output last (Amount, Mix, Smoothness, the
// meters). One source for WPF and Avalonia (the Neo editor kit); the tabs are the studio's.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Chop
{
    internal sealed class NeoChopEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Modulation identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Modulation, NeoColor.FromRgb(0x4F, 0xC3, 0xA1));

        static readonly string[] StepNames = { "Off", "On", "Acc" };   // short enough for a 30 px cell

        protected override string Title => "Neo Chop";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoChopMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // The mode row: one light-up toggle per mode, in the order the spec gives (the same as the tabs below).
            var modes = Layout.Row(
                new ParamChoice(ctx, P.StutterOn, "Stutter", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.RepeatOn, "Repeat", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.RetrigOn, "Retrigger", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.TapeOn, "Tape", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.GateOn, "Gate", 70), Layout.Gap(4),
                new ParamChoice(ctx, P.PanOn, "Pan", 70));

            var stutter = Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.StutterTrig, "Trigger", 56),
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamDropdown(ctx, P.StutterEvery, "Every", 92), Layout.Gap(6), new ParamDropdown(ctx, P.Repeats, "Repeats", 70))),
                Layout.Gap(8),
                Layout.Column(
                    Layout.Row(new ParamChoice(ctx, P.Slice, "Slice", 40)),
                    Layout.Gap(1, 4),
                    new ParamChoice(ctx, P.SliceFeel, "Feel", 56)),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.StutterPitch), new ParamKnob(ctx, P.StutterDecay), new ParamKnob(ctx, P.StutterFilter)));

            var repeat = Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.RepeatTrig, "Trigger", 56),
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamDropdown(ctx, P.RepeatInterval, "Interval", 92), Layout.Gap(6), new ParamDropdown(ctx, P.RepeatLength, "Length", 92))),
                Layout.Gap(8),
                Layout.Column(
                    new ParamChoice(ctx, P.RepeatGrid, "Grid", 40),
                    Layout.Gap(1, 4),
                    new ParamChoice(ctx, P.RepeatFeel, "Feel", 56)),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.RepeatChance), new ParamKnob(ctx, P.Variation)));

            var retrig = Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.RetrigTrig, "Trigger", 56),
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamDropdown(ctx, P.RetrigEvery, "Every", 92), Layout.Gap(6), new ParamDropdown(ctx, P.RetrigLength, "Length", 92))),
                Layout.Gap(8),
                Layout.Column(
                    new ParamChoice(ctx, P.RetrigGrid, "Grid", 40),
                    Layout.Gap(1, 4),
                    new ParamChoice(ctx, P.RetrigFeel, "Feel", 56)),
                Layout.Gap(8),
                new ParamChoice(ctx, P.RetrigBack, "Back to", 50));

            var tape = Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.TapeTrig, "Trigger", 56),
                    Layout.Gap(1, 4),
                    new ParamDropdown(ctx, P.TapeEvery, "Every", 92)),
                Layout.Gap(8),
                Layout.Column(
                    new ParamDropdown(ctx, P.StopSync, "Stop sync", 92),
                    Layout.Gap(1, 4),
                    new ParamDropdown(ctx, P.StartSync, "Start sync", 92)),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.StopTime), new ParamKnob(ctx, P.TapeCurve), new ParamKnob(ctx, P.StartTime)));

            var gate = Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.GateLock, "Lock", 50),
                    Layout.Gap(1, 4),
                    new ParamChoice(ctx, P.GateRate, "Rate", 40)),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.GateAttack), new ParamKnob(ctx, P.GateRelease), new ParamKnob(ctx, P.GateDepth), new ParamKnob(ctx, P.GateAccent)));

            var pan = Layout.Row(
                Layout.Column(
                    new ParamDropdown(ctx, P.PanSync, "Sync", 92),
                    Layout.Gap(1, 4),
                    new ParamChoice(ctx, P.PanShape, "Shape", 56)),
                Layout.Gap(8),
                Layout.Row(new ParamKnob(ctx, P.PanRate), new ParamKnob(ctx, P.PanDepth), new ParamKnob(ctx, P.PanPhase)));

            // The sixteen steps of the gate: a small off / on / accent strip for each, in the order they play.
            var stepRow = new UIElement[31];
            for (int i = 0; i < 16; i++)
            {
                stepRow[i * 2] = new ParamChoice(ctx, P.Step1 + i, (i + 1).ToString(), 30, vertical: true, names: StepNames);
                if (i < 15) stepRow[i * 2 + 1] = Layout.Gap(2);
            }
            var steps = Layout.Row(stepRow);

            var meters = Layout.Row(
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 96),
                Layout.Gap(4),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 96));

            // Amount (the strength of the whole effect) and Mix (dry/wet) side by side, Amount first, the same size as on every Neo
            // effect (machines.md, "Mix and Amount"); the crossfade time and the note's choice of slice beside them.
            var output = Layout.Row(
                new ParamKnob(ctx, P.Amount, diameter: 40),
                new ParamKnob(ctx, P.Mix, diameter: 40),
                Layout.Gap(6),
                new ParamKnob(ctx, P.Smoothness),
                Layout.Gap(6),
                new ParamChoice(ctx, P.NotePick, "Note picks slice", 100),
                Layout.Gap(10),
                meters);

#if AVALONIA
            var pages = new Pages(AccentColor, PageWidth(stutter, repeat, retrig, tape, gate, pan), PageHeight(stutter, repeat, retrig, tape, gate, pan), "Neo Chop");
            pages.Add("Stutter", stutter);
            pages.Add("Repeat", repeat);
            pages.Add("Retrigger", retrig);
            pages.Add("Tape", tape);
            pages.Add("Gate", gate);
            pages.Add("Pan", pan);
            return Layout.Column(
                S("Modes", modes),
                S("Mode", pages),
                S("Gate steps", steps),
                S("Output", output));
#else
            return Layout.Column(
                S("Modes", modes),
                Layout.Row(S("Stutter", stutter), S("Repeat", repeat)),
                Layout.Row(S("Retrigger", retrig), S("Tape", tape)),
                Layout.Row(S("Gate", gate), S("Pan", pan)),
                S("Gate steps", steps),
                S("Output", output));
#endif
        }

#if AVALONIA
        // A page is as wide and as high as the largest of the six, so that the tabs do not move the sections below.
        static double PageWidth(params UIElement[] pages)
        {
            double w = 0;
            foreach (var p in pages) { p.Measure(Avalonia.Size.Infinity); w = Math.Max(w, p.DesiredSize.Width); }
            return Math.Ceiling(w);
        }

        static double PageHeight(params UIElement[] pages)
        {
            double h = 0;
            foreach (var p in pages) { p.Measure(Avalonia.Size.Infinity); h = Math.Max(h, p.DesiredSize.Height); }
            return Math.Ceiling(h);
        }
#endif
    }
}
