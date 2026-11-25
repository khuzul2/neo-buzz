#nullable disable
// Neo Organ's editor (docs/specs/neo-organ.md): the nine drawbars as vertical bars with their footage labels (brown, white and black
// caps as on the instrument), then Percussion, Key, Vibrato, Drive, Rotary (a Slow / Fast / Stop switch and the two rotors' live
// speeds, with their settings) and Output. One source for WPF and Avalonia (the Neo editor kit).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Organ
{
    /// <summary>The drawbars as a pointer surface: press or drag on a bar to set its step, the wheel steps it, a double-click pushes it in.</summary>
    internal sealed class DrawbarInput : IGraphInput
    {
        public const double Pad = 8, Slot = 46, BarWidth = 30, Top = 22, Bottom = 40, CapHeight = 16;
        readonly EditorContext ctx;
        readonly double height;
        int dragging = -1;

        public DrawbarInput(EditorContext ctx, double height)
        {
            this.ctx = ctx;
            this.height = height;
        }

        public static double Width => Pad * 2 + Slot * Tonewheels.Bars;

        /// <summary>The y of a step's cap centre: 8 at the top, 0 at the bottom.</summary>
        public static double StepY(double height, int step) => Top + (height - Top - Bottom) * (1 - step / 8.0);

        int BarAt(double x) => x >= Pad && x < Width - Pad ? (int)((x - Pad) / Slot) : -1;

        int StepAt(double y) => (int)Math.Clamp(Math.Round(8 * (1 - (y - Top) / (height - Top - Bottom))), 0, 8);

        public bool Capturing => dragging >= 0;

        public bool Down(double x, double y, int clicks, bool fine)
        {
            int b = BarAt(x);
            if (b < 0 || y < 0 || y > height - Bottom + CapHeight) return false;
            dragging = b;
            ctx.Set(P.B16 + b, clicks >= 2 ? 0 : StepAt(y));
            return true;
        }

        public bool Move(double x, double y, bool fine)
        {
            if (dragging < 0) return false;
            ctx.Set(P.B16 + dragging, StepAt(y));
            return true;
        }

        public bool Up()
        {
            bool was = dragging >= 0;
            dragging = -1;
            return was;
        }

        public bool Wheel(double x, double y, int delta, bool fine)
        {
            int b = BarAt(x);
            if (b < 0) return false;
            ctx.Set(P.B16 + b, ctx.Raw(P.B16 + b) + (delta > 0 ? 1 : -1));
            return true;
        }

        public bool Leave() => false;
    }

    internal sealed class NeoOrganEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Synth identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xD0, 0x8A, 0x3C));
        static readonly NeoColor Brown = NeoColor.FromRgb(0x7B, 0x4A, 0x2B), White = NeoColor.FromRgb(0xEE, 0xEA, 0xDF), Black = NeoColor.FromRgb(0x20, 0x21, 0x25);
        // 16', 5 1/3' brown; 8', 4' white; 2 2/3' black; 2' white; 1 3/5', 1 1/3' black; 1' white.
        static readonly NeoColor[] CapColour = { Brown, Brown, White, White, Black, White, Black, Black, White };
        const double D = 38, BarsHeight = 176;

        protected override string Title => "Neo Organ";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoOrganMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // Drawbars: one surface, a binding per bar so that a preset or automation redraws it.
            for (int b = 0; b < Tonewheels.Bars; b++) ctx.Bind(P.B16 + b, _ => { });
            var bars = new InteractiveGraph(DrawbarInput.Width, BarsHeight, (g, r) => DrawBars(g, r, ctx), new DrawbarInput(ctx, BarsHeight));
            ctx.Changed += bars.InvalidateVisual;

            var perc = Layout.Column(
                new ParamChoice(ctx, P.PercOn, "Percussion", 90),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.PercHarm, "Harmonic", 40),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.PercDecay, "Decay", 40),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.PercLevel, "Level", 50));

            var key = Layout.Column(new ParamKnob(ctx, P.KeyClick, diameter: 46), new ParamKnob(ctx, P.Leakage, diameter: 46));

            var vib = Layout.Column(
                new ParamChoice(ctx, P.VibOn, "Vibrato", 90),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.VibMode, "Type", 30));

            var drive = new ParamKnob(ctx, P.Drive, diameter: 58);

            var speeds = new GraphView(190, 44, (g, r) => DrawSpeeds(g, r, machine), animate: true);
            var rotary = Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Rotary, "Rotary", 90), Layout.Gap(10), new ParamChoice(ctx, P.Speed, "Speed", 60), Layout.Gap(10), speeds),
                Layout.Gap(1, 6),
                Build.Knobs(ctx, 5, D, P.HornSlow, P.HornFast, P.DrumSlow, P.DrumFast, P.HornAccel),
                Build.Knobs(ctx, 5, D, P.DrumAccel, P.RotDepth, P.Doppler, P.Spread, P.Balance));

            var meter = new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 120);
            var output = Layout.Row(
                Layout.Column(new ParamKnob(ctx, P.Volume, diameter: 50), new ParamKnob(ctx, P.Expression, diameter: 50)),
                Layout.Gap(6),
                meter);

            return Layout.Column(
                Layout.Row(S("Drawbars", bars), S("Percussion", perc), S("Key", key), S("Vibrato", vib)),
                Layout.Row(S("Drive", drive), S("Rotary", rotary), S("Output", output)));
        }

        static void DrawBars(INeoCanvas g, NeoRect r, EditorContext ctx)
        {
            double h = r.Height;
            double trackTop = DrawbarInput.Top, trackBottom = h - DrawbarInput.Bottom;
            for (int b = 0; b < Tonewheels.Bars; b++)
            {
                double cx = DrawbarInput.Pad + DrawbarInput.Slot * b + DrawbarInput.Slot / 2;
                int step = Math.Clamp(ctx.Raw(P.B16 + b), 0, 8);
                // The slot, with a tick for each step.
                g.Rect(new NeoRect(cx - 3, trackTop - 4, 6, trackBottom - trackTop + 8), Palette.Track, new NeoStroke(Palette.PanelEdge, 1), 3);
                for (int s = 0; s <= 8; s++)
                {
                    double y = DrawbarInput.StepY(h, s);
                    g.Line(new NeoPoint(cx + 8, y), new NeoPoint(cx + 13, y), new NeoStroke(s == step ? AccentColor : Palette.Grid, 1, false));
                }
                // The level as a fill from the bottom of the slot up to the cap, then the cap.
                double capY = DrawbarInput.StepY(h, step);
                if (step > 0) g.Rect(new NeoRect(cx - 2, capY, 4, trackBottom + 4 - capY), AccentColor, default, 1);
                var cap = new NeoRect(cx - DrawbarInput.BarWidth / 2, capY - DrawbarInput.CapHeight / 2, DrawbarInput.BarWidth, DrawbarInput.CapHeight);
                g.Rect(cap, CapColour[b], new NeoStroke(Palette.DimText, 1), 3);
                var ink = CapColour[b] == White ? Black : White;
                g.Text(step.ToString(), cx, cap.Top + 1.5, 11, ink, true, NeoAlign.Center);
                g.Text(Tonewheels.BarFootage[b], cx, h - DrawbarInput.Bottom + 12, 11, Palette.Text, true, NeoAlign.Center);
            }
        }

        /// <summary>The two rotors' speeds now, in turns per second, as bars against the fast ends of their ranges.</summary>
        static void DrawSpeeds(INeoCanvas g, NeoRect r, NeoOrganMachine machine)
        {
            double horn = machine?.Engine.HornHz ?? 0, drum = machine?.Engine.DrumHz ?? 0;
            void Row(string name, double hz, double max, double y)
            {
                g.Text(name, r.X + 2, y, 11, Palette.DimText);
                double w = r.Width - 90;
                g.Rect(new NeoRect(r.X + 42, y + 3, w, 8), Palette.Track, default, 4);
                g.Rect(new NeoRect(r.X + 42, y + 3, Math.Max(0, Math.Min(1, hz / max)) * w, 8), AccentColor, default, 4);
                g.Text($"{hz:0.0} Hz", r.X + 42 + w + 6, y, 11, Palette.Text);
            }
            Row("Horn", horn, 14, r.Y + 4);
            Row("Drum", drum, 12, r.Y + 24);
        }
    }
}
