#nullable disable
// Neo Arp's editor (docs/specs/neo-arp.md §8): Pattern and Rate on top, the 16-step row (a click cycles Off / On / Tie / Accent, a drag below sets the
// step's transpose, the step playing is lit), then Gate, Swing, Octaves, Velocity and Hold. One source for WPF and Avalonia (the Neo editor kit).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Arp
{
    /// <summary>The step row as a pointer surface: the upper cell of a step cycles its kind, the lower part sets its transpose by a drag.</summary>
    internal sealed class ArpStepInput : IGraphInput
    {
        public const double Pad = 6, Slot = 40, CellTop = 6, CellHeight = 40, BarTop = 58, BarHeight = 70, Width = Pad * 2 + Slot * 16, Height = 148;
        readonly EditorContext ctx;
        int dragging = -1;

        public ArpStepInput(EditorContext ctx) => this.ctx = ctx;

        public bool Capturing => dragging >= 0;

        static int StepAt(double x) => x >= Pad && x < Width - Pad ? (int)((x - Pad) / Slot) : -1;

        static int SemitonesAt(double y) => (int)Math.Clamp(Math.Round(12 - 24 * (y - BarTop) / BarHeight), -12, 12);

        public bool Down(double x, double y, int clicks, bool fine)
        {
            int s = StepAt(x);
            if (s < 0) return false;
            if (y >= CellTop && y < CellTop + CellHeight + 4)
            {
                ctx.Edits?.Begin();
                ctx.Set(P.S1 + s, (ctx.Raw(P.S1 + s) + 1) % 4);
                ctx.Edits?.End();
                return true;
            }
            if (y >= BarTop - 4 && y <= BarTop + BarHeight + 4)
            {
                dragging = s;
                ctx.Edits?.Begin();
                ctx.Set(P.X1 + s, 12 + (clicks >= 2 ? 0 : SemitonesAt(y)));
                return true;
            }
            return false;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragging < 0) return false;
            ctx.Set(P.X1 + dragging, 12 + SemitonesAt(y));
            return true;
        }

        public bool Up()
        {
            if (dragging < 0) return false;
            dragging = -1;
            ctx.Edits?.End();
            return true;
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            int s = StepAt(x);
            if (s < 0) return false;
            ctx.Set(P.X1 + s, ctx.Raw(P.X1 + s) + (delta > 0 ? 1 : -1));
            return true;
        }

        public bool Leave() => false;
    }

    internal sealed class NeoArpEditor : NeoEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0xE0, 0x8F, 0x4F));
        const double D = 44;

        protected override string Title => "Neo Arp";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoArpMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            for (int i = 0; i < 16; i++) { ctx.Bind(P.S1 + i, _ => { }); ctx.Bind(P.X1 + i, _ => { }); }
            ctx.Bind(P.StepLen, _ => { });
            ctx.Bind(P.Pattern, _ => { });
            var steps = new InteractiveGraph(ArpStepInput.Width, ArpStepInput.Height, (g, r) => DrawSteps(g, r, ctx, machine), new ArpStepInput(ctx), animate: true);
            ctx.Changed += steps.InvalidateVisual;

            var pattern = Layout.Row(
                new ParamDropdown(ctx, P.Pattern, "Pattern", 150), Layout.Gap(8),
                new ParamDropdown(ctx, P.OctMode, "Octave mode", 90), Layout.Gap(8),
                new ParamKnob(ctx, P.Octaves, diameter: D), new ParamKnob(ctx, P.Repeats, diameter: D), new ParamKnob(ctx, P.Transpose, diameter: D));
            var rate = Layout.Row(
                new ParamDropdown(ctx, P.Sync, "Timing", 100), Layout.Gap(8),
                new ParamDropdown(ctx, P.Rate, "Rate", 70), Layout.Gap(8),
                new ParamDropdown(ctx, P.Feel, "Feel", 80), Layout.Gap(8),
                new ParamKnob(ctx, P.FreeMs, diameter: D));
            var stepsSection = Layout.Column(steps, Layout.Gap(1, 4), Layout.Row(new ParamKnob(ctx, P.StepLen, diameter: D)));

            var velocity = Layout.Row(
                new ParamDropdown(ctx, P.VelMode, "Velocity", 100), Layout.Gap(8),
                new ParamKnob(ctx, P.FixedVel, diameter: D), new ParamKnob(ctx, P.AccentLevel, diameter: D),
                new ParamKnob(ctx, P.HumTime, diameter: D), new ParamKnob(ctx, P.HumVel, diameter: D));
            var hold = Layout.Row(new ParamChoice(ctx, P.Latch, "Latch", 60), Layout.Gap(8), new ParamDropdown(ctx, P.Retrigger, "Retrigger", 110));

            return Layout.Column(
                Layout.Row(S("Pattern", pattern), S("Rate", rate)),
                S("Steps", stepsSection),
                Layout.Row(S("Gate", new ParamKnob(ctx, P.Gate, diameter: D)), S("Swing", new ParamKnob(ctx, P.Swing, diameter: D)),
                    S("Velocity", velocity), S("Hold", hold)));
        }

        static void DrawSteps(INeoCanvas g, NeoRect r, EditorContext ctx, NeoArpMachine machine)
        {
            int len = Math.Clamp(ctx.Raw(P.StepLen), 1, 16);
            bool used = ctx.Raw(P.Pattern) == 11;                           // the row shapes the Steps pattern only
            int now = machine?.Engine.CurrentStep ?? -1;
            var line = new NeoStroke(Palette.Grid, 1, false);
            double zero = ArpStepInput.BarTop + ArpStepInput.BarHeight / 2;
            for (int i = 0; i < 16; i++)
            {
                double x = r.X + ArpStepInput.Pad + i * ArpStepInput.Slot;
                bool inRow = i < len;
                int kind = Math.Clamp(ctx.Raw(P.S1 + i), 0, 3);
                int tr = Math.Clamp(ctx.Raw(P.X1 + i) - 12, -12, 12);
                bool lit = inRow && (used ? i == now : false);
                var cell = new NeoRect(x + 2, r.Y + ArpStepInput.CellTop, ArpStepInput.Slot - 4, ArpStepInput.CellHeight);
                g.Rect(cell, inRow ? Palette.Panel : Palette.Background, new NeoStroke(lit ? AccentColor.Tint(0.4) : Palette.PanelEdge, lit ? 2 : 1), 4);
                if (inRow && kind != 0)
                {
                    var fill = kind == 3 ? AccentColor.Tint(0.25) : AccentColor.WithAlpha(kind == 2 ? (byte)0x70 : (byte)0xC8);
                    if (lit) fill = AccentColor.Tint(0.5);
                    if (kind == 2)
                    {
                        // A tie: a slim bar that reaches into the next cell, as a held note.
                        g.Rect(new NeoRect(cell.X - 2, cell.Y + 14, cell.Width + 4, 12), fill, default, 3);
                    }
                    else
                    {
                        g.Rect(new NeoRect(cell.X + 5, cell.Y + 5, cell.Width - 10, cell.Height - 10), fill, default, 3);
                        if (kind == 3) g.Text("!", cell.X + cell.Width / 2, cell.Y + 11, 16, Palette.Background, true, NeoAlign.Center);
                    }
                }
                g.Text((i + 1).ToString(), x + ArpStepInput.Slot / 2, r.Y + ArpStepInput.CellTop + ArpStepInput.CellHeight + 2, 9, i % 4 == 0 ? Palette.Text : Palette.DimText, false, NeoAlign.Center);
                // The transpose: a bar from the middle line.
                g.Rect(new NeoRect(x + 8, r.Y + ArpStepInput.BarTop, ArpStepInput.Slot - 16, ArpStepInput.BarHeight), inRow ? Palette.Panel : Palette.Background, default, 3);
                double y = r.Y + zero - tr * (ArpStepInput.BarHeight / 24.0);
                if (inRow && tr != 0) g.Rect(new NeoRect(x + 10, Math.Min(y, r.Y + zero), ArpStepInput.Slot - 20, Math.Max(2, Math.Abs(y - (r.Y + zero)))), AccentColor.WithAlpha(0xB0), default, 2);
                if (inRow && tr != 0) g.Text(tr > 0 ? "+" + tr : tr.ToString(), x + ArpStepInput.Slot / 2, tr > 0 ? y - 12 : y + 1, 9, Palette.Text, true, NeoAlign.Center);
            }
            g.Line(r.X + ArpStepInput.Pad + 6, r.Y + zero, r.X + ArpStepInput.Width - ArpStepInput.Pad - 6, r.Y + zero, line);
            if (!used) g.Text("Used by the Steps pattern", r.X + ArpStepInput.Width - ArpStepInput.Pad - 4, r.Y + ArpStepInput.Height - 14, 10, Palette.DimText, false, NeoAlign.Right);
        }
    }
}
