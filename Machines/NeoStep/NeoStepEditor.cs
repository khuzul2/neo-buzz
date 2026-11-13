#nullable disable
// Neo Step's editor (docs/specs/neo-patch.md §3): the shared modulator template (NeoModulator/ModulatorEditor.cs) over this machine's controls.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Modulator;

namespace NeoBuzz.Step
{
    internal sealed class NeoStepEditor : ModulatorEditor
    {
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));

        protected override string Title => "Neo Step";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;
        protected override UIElement BuildControls(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoStepMachine;
            var bars = new StepBars(ctx, () => machine?.Engine.Current ?? -1);
            var view = new InteractiveGraph(StepBars.Width, StepBars.Height, (g, r) => bars.Draw(g, r), bars, animate: true);
            return Layout.Row(
                new Section("Pattern", AccentColor, Layout.Column(
                    view,
                    Layout.Gap(1, 4),
                    Layout.Row(new ParamKnob(ctx, P.Steps, diameter: 40), new ParamDropdown(ctx, P.Rate, "Rate", 90), new ParamKnob(ctx, P.Glide, diameter: 40),
                        new ParamKnob(ctx, P.Random, diameter: 40), new ParamKnob(ctx, P.Swing, diameter: 40), new ParamKnob(ctx, P.WriteGlide, diameter: 40)))));
        }
    }

    /// <summary>The 32 step levels as bars: a click or a drag across them sets the levels (one undo gesture); the step playing is lit.</summary>
    internal sealed class StepBars : IGraphInput
    {
        public const double Width = 420, Height = 74;
        const double Left = 4, Top = 4, Bottom = 70;

        readonly EditorContext ctx;
        readonly Func<int> current;
        bool dragging;

        public StepBars(EditorContext ctx, Func<int> current)
        {
            this.ctx = ctx;
            this.current = current;
        }

        public bool Capturing => dragging;

        static double Slot => (Width - 2 * Left) / 32.0;

        public bool Down(double x, double y, int clicks, bool fine)
        {
            if (clicks >= 2)
            {
                // A double-click on a bar puts it back at its default.
                int s = StepAt(x);
                ctx.Edits?.Begin();
                ctx.Set(P.S1 + s, ctx.Table[P.S1 + s].Default);
                ctx.Edits?.End();
                return true;
            }
            dragging = true;
            ctx.Edits?.Begin();
            Write(x, y);
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (!dragging) return false;
            Write(x, y);
            return true;
        }

        public bool Up()
        {
            if (!dragging) return false;
            dragging = false;
            ctx.Edits?.End();
            return true;
        }

        public bool Wheel(double x, double y, int delta, bool fine) => false;
        public bool Leave() => false;

        static int StepAt(double x) => Math.Clamp((int)((x - Left) / Slot), 0, 31);

        void Write(double x, double y)
        {
            int s = StepAt(x);
            double level = Math.Clamp((Bottom - y) / (Bottom - Top), 0, 1);
            var def = ctx.Table[P.S1 + s];
            ctx.Set(P.S1 + s, def.Min + (int)Math.Round(level * (def.Max - def.Min)));
        }

        public void Draw(INeoCanvas g, NeoRect r)
        {
            int steps = Math.Clamp((int)ctx.Value(P.Steps), 1, 32);
            int now = current();
            for (int i = 0; i < 32; i++)
            {
                double x = r.X + Left + i * Slot;
                bool on = i < steps;
                double level = ParamMapping.Normalized(ctx.Table[P.S1 + i], ctx.Raw(P.S1 + i));
                g.Rect(new NeoRect(x + 1, r.Y + Top, Slot - 2, Bottom - Top), on ? Palette.Panel : Palette.Background, NeoStroke.None, 2);
                if (!on) continue;
                double h = level * (Bottom - Top);
                g.Rect(new NeoRect(x + 1, r.Y + Bottom - h, Slot - 2, Math.Max(1, h)), i == now ? AccentColor.Tint(0.35) : AccentColor.WithAlpha(0xB0), NeoStroke.None, 2);
                if (i % 4 == 0) g.Line(x, r.Y + Top, x, r.Y + Top + 3, new NeoStroke(Palette.DimText, 1, false));
            }
        }

        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Control, NeoColor.FromRgb(0x4F, 0xC3, 0xE0));
    }
}
