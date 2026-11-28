#nullable disable
// Neo Follow's editor (docs/specs/neo-follow.md): the shared modulator template (NeoModulator/ModulatorEditor.cs) over this machine's controls:
// what it listens to, the mode tabs (Envelope, Pitch, Onset: the tab shown is the Mode parameter), the shape of the control signal, and beside
// them the live Output meter (the template's scope) above the Targets table. One source for WPF and Avalonia (the Neo editor kit); the tabs
// are the studio's, the classic app shows the three modes' sections in a row.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Modulator;

namespace NeoBuzz.FollowMachine
{
    internal sealed class NeoFollowEditor : ModulatorEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));

        protected override string Title => "Neo Follow";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        protected override UIElement BuildControls(EditorContext ctx)
        {
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            var envelope = Layout.Row(
                new ParamChoice(ctx, P.Detector, "Detector", 52, vertical: true),
                new ParamKnob(ctx, P.Attack, diameter: 46),
                new ParamKnob(ctx, P.Release, diameter: 46),
                new ParamKnob(ctx, P.Floor, diameter: 46),
                new ParamKnob(ctx, P.Ceiling, diameter: 46),
                new ParamKnob(ctx, P.Curve, diameter: 46));
            var pitch = Layout.Row(
                new ParamDropdown(ctx, P.PitchRange, "Range", 92),
                new ParamKnob(ctx, P.Low, diameter: 46),
                new ParamKnob(ctx, P.High, diameter: 46),
                new ParamKnob(ctx, P.Confidence, diameter: 46),
                new ParamKnob(ctx, P.PitchGlide, diameter: 46));
            var onset = Layout.Row(
                new ParamKnob(ctx, P.Sensitivity, diameter: 46),
                new ParamKnob(ctx, P.Decay, diameter: 46),
                new ParamKnob(ctx, P.Hold, diameter: 46),
                new ParamChoice(ctx, P.Dynamic, "Dynamic", 52, vertical: true));
            var shape = Layout.Row(
                new ParamChoice(ctx, P.Range, "Range", 66, vertical: true),
                new ParamChoice(ctx, P.Invert, "Invert", 52, vertical: true),
                new ParamKnob(ctx, P.Smooth, diameter: 46),
                new ParamKnob(ctx, P.Depth, diameter: 46),
                new ParamKnob(ctx, P.Bias, diameter: 46),
                new ParamKnob(ctx, P.Glide, diameter: 46));
            var source = Layout.Row(new ParamChoice(ctx, P.Listen, "Listen to", 70));

#if AVALONIA
            // The tabs are the Mode parameter: a tab clicked writes it, and a Mode set from outside (a preset, the agent) shows its tab.
            var pages = new Pages(AccentColor, PageWidth(envelope, pitch, onset), PageHeight(envelope, pitch, onset), null);
            pages.Add("Envelope", envelope);
            pages.Add("Pitch", pitch);
            pages.Add("Onset", onset);
            var mode = ctx.Bind(P.Mode, i => pages.Selected = i);
            pages.Selected = mode.Value;
            pages.SelectedChanged += i => mode.Set(i);
            return Layout.Column(
                S("Source", source),
                S("Mode", pages),
                S("Shape", shape));
#else
            return Layout.Column(
                S("Source", Layout.Row(source, Layout.Gap(8), new ParamChoice(ctx, P.Mode, "Mode", 70))),
                S("Envelope", envelope),
                S("Pitch", pitch),
                S("Onset", onset),
                S("Shape", shape));
#endif
        }

#if AVALONIA
        // A page is as wide and as high as the largest of the three, so that the tabs do not move the sections below.
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
