#nullable disable
// Neo Amp's editor (docs/specs/neo-amp.md §9): Input (channel, Trim, the gate, a level meter), Amp (voicing, Quality, the knobs of the amp),
// Cabinet (the cabinet, its picture with the mic and the cuts drawn on it, mic position and cuts), Tuner (a switch, the reference and a
// display: the note, its cents, a needle) and Output. One source for WPF and Avalonia (the Neo editor kit); the pictures draw on the kit's
// canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Amp
{
    internal sealed class NeoAmpEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Distortion identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Distortion, NeoColor.FromRgb(0xFF, 0x8A, 0x3D));
        static readonly NeoColor WarnColor = NeoColor.FromRgb(0xE0, 0xA0, 0x40);
        static readonly NeoColor GoodColor = NeoColor.FromRgb(0x4C, 0xC9, 0x7A);

        protected override string Title => "Neo Amp";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoAmpMachine;

            var input = Layout.Row(
                Layout.Column(
                    new ParamChoice(ctx, P.InputCh, "Input", 40),
                    Layout.Row(new ParamKnob(ctx, P.Trim), new ParamKnob(ctx, P.GateThresh)),
                    new ParamKnob(ctx, P.GateRelease)),
                Layout.Gap(4),
                new Meter("Input", () => machine?.Engine.InMeter.Peak ?? 0, Palette.DimText, 190));

            var amp = Layout.Column(
                Layout.Row(new ParamDropdown(ctx, P.Voicing, width: 130), Layout.Gap(10), new ParamChoice(ctx, P.Quality, "Quality", 34)),
                Layout.Row(new ParamKnob(ctx, P.Gain, diameter: 52), new ParamKnob(ctx, P.Bass), new ParamKnob(ctx, P.Mid), new ParamKnob(ctx, P.Treble)),
                Layout.Row(new ParamKnob(ctx, P.Sag), new ParamKnob(ctx, P.Presence), new ParamKnob(ctx, P.Resonance), new ParamKnob(ctx, P.Master)));

            var picture = new CabinetPicture(ctx, machine);
            var cabinet = Layout.Column(
                new ParamDropdown(ctx, P.Cabinet, width: 130),
                picture.View,
                Layout.Row(new ParamKnob(ctx, P.CabWave), new ParamKnob(ctx, P.MicPos), new ParamKnob(ctx, P.LowCut), new ParamKnob(ctx, P.HighCut)));

            var tunerView = new GraphView(210, 150, (g, r) => DrawTuner(g, r, ctx, machine), animate: true);
            ctx.Changed += tunerView.InvalidateVisual;
            var tuner = Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Tuner, "Tuner", 60), Layout.Gap(8), new ParamKnob(ctx, P.Reference)),
                tunerView);

            // Amount (the strength of the effect: Gain in dB and Master) and Mix (dry/wet) side by side, Amount first, the same size as on every Neo
            // effect (machines.md, "Mix and Amount"); the level of the amp under them.
            var output = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40)),
                    new ParamKnob(ctx, P.Output)),
                Layout.Gap(6),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 190));

            return Layout.Row(
                new Section("Input", AccentColor, input),
                new Section("Amp", AccentColor, amp),
                new Section("Cabinet", AccentColor, cabinet),
                new Section("Tuner", AccentColor, tuner),
                new Section("Output", AccentColor, output));
        }

        // ---- the tuner

        /// <summary>The note, its cents and a needle: the scale runs from −50 to +50 cents, green within 5 cents of the note.</summary>
        static void DrawTuner(INeoCanvas g, NeoRect r, EditorContext ctx, NeoAmpMachine machine)
        {
            bool on = ctx.Value(P.Tuner) > 0;
            var reading = machine?.Engine.Tuner ?? default;
            bool valid = on && reading.Valid;
            double ref0 = 415 + ctx.Raw(P.Reference);
            var plot = new NeoRect(r.X + 10, r.Y + 78, r.Width - 20, 42);
            double X(double cents) => plot.Left + (Math.Clamp(cents, -50, 50) + 50) / 100.0 * plot.Width;
            // The scale: ticks every 10 cents, the centre and the in-tune zone.
            g.Rect(new NeoRect(X(-5), plot.Top, X(5) - X(-5), plot.Height - 12), new NeoFill(GoodColor.WithAlpha(0x30), default, false), NeoStroke.None);
            var grid = new NeoStroke(Palette.Grid, 1, false);
            for (int c = -50; c <= 50; c += 10)
            {
                double len = c == 0 ? plot.Height - 12 : (c % 50 == 0 ? 20 : 12);
                g.Line(X(c), plot.Top, X(c), plot.Top + len, c == 0 ? new NeoStroke(Palette.DimText, 1.5) : grid);
                if (c % 50 == 0 || c == 0) g.Text(c == 0 ? "0" : (c > 0 ? "+50" : "-50"), X(c), plot.Bottom - 11, 9, Palette.DimText, align: NeoAlign.Center);
            }
            if (!on)
            {
                g.Text("Tuner off", r.X + r.Width / 2, r.Y + 30, 14, Palette.DimText, align: NeoAlign.Center);
                g.Text("Switch it on to mute the output and read the pitch", r.X + r.Width / 2, r.Y + 54, 9, Palette.DimText, align: NeoAlign.Center, maxWidth: r.Width - 12);
                g.Text($"A4 = {ref0:0} Hz", r.X + r.Width / 2, r.Bottom - 16, 10, Palette.DimText, align: NeoAlign.Center);
                return;
            }
            if (!valid)
            {
                g.Text("-", r.X + r.Width / 2, r.Y + 14, 40, Palette.DimText, align: NeoAlign.Center);
                g.Text("Play a note", r.X + r.Width / 2, r.Y + 62, 10, Palette.DimText, align: NeoAlign.Center);
                g.Text($"A4 = {ref0:0} Hz", r.X + r.Width / 2, r.Bottom - 16, 10, Palette.DimText, align: NeoAlign.Center);
                return;
            }
            bool inTune = Math.Abs(reading.Cents) <= 5;
            var colour = inTune ? GoodColor : Math.Abs(reading.Cents) <= 20 ? AccentColor : WarnColor;
            g.Text(reading.NoteName, r.X + r.Width / 2, r.Y + 8, 40, colour, bold: true, align: NeoAlign.Center);
            g.Text($"{reading.Cents:+0;-0;0} cents    {reading.Hz:0.0} Hz", r.X + r.Width / 2, r.Y + 58, 11, Palette.Text, align: NeoAlign.Center);
            // The needle.
            double nx = X(reading.Cents);
            g.Line(nx, plot.Top - 2, nx, plot.Bottom - 12, new NeoStroke(colour, 3));
            g.Ellipse(new NeoPoint(nx, plot.Top - 2), 3.5, 3.5, new NeoFill(colour, default, false), NeoStroke.None);
            g.Text(reading.Cents < -5 ? "flat" : reading.Cents > 5 ? "sharp" : "in tune", r.X + r.Width / 2, r.Bottom - 16, 10, colour, align: NeoAlign.Center);
            g.Text($"A4 = {ref0:0} Hz", r.X + 8, r.Bottom - 16, 9, Palette.DimText);
        }

        // ---- the cabinet

        /// <summary>
        /// The cabinet's response (dB over frequency, 40 Hz to 16 kHz): the impulse response that plays, with the mic position's shelf and the two cuts
        /// on top of it as the signal gets them. The response is read from the impulse response once per cabinet.
        /// </summary>
        sealed class CabinetPicture
        {
            const int Points = 120;
            readonly EditorContext ctx;
            readonly NeoAmpMachine machine;
            CabBuilt shown;
            readonly double[] curve = new double[Points];
            public GraphView View { get; }

            public CabinetPicture(EditorContext ctx, NeoAmpMachine machine)
            {
                this.ctx = ctx;
                this.machine = machine;
                View = new GraphView(260, 130, Draw, animate: true);
                ctx.Changed += View.InvalidateVisual;
            }

            static double Hz(int i) => 40.0 * Math.Pow(400.0, i / (double)(Points - 1));

            void Measure(CabBuilt built)
            {
                shown = built;
                var ir = built?.Ir;
                if (ir == null) { Array.Clear(curve, 0, curve.Length); return; }
                double rate = built.Rate;
                int n = ir.Length;
                for (int i = 0; i < Points; i++)
                {
                    double w = 2 * Math.PI * Hz(i) / rate, re = 0, im = 0;
                    // A recurrence for the cosine and sine (no libm per sample).
                    double c = PortableMath.Cos(w), s = PortableMath.Sin(w), cr = 1, ci = 0;
                    for (int k = 0; k < n; k++)
                    {
                        re += ir[k] * cr; im -= ir[k] * ci;
                        double t = cr * c - ci * s; ci = cr * s + ci * c; cr = t;
                    }
                    curve[i] = 20 * Math.Log10(Math.Sqrt(re * re + im * im) + 1e-9);
                }
            }

            void Draw(INeoCanvas g, NeoRect r)
            {
                var built = machine?.Engine.CurrentCab;
                var status = machine?.Engine.Status ?? AmpCabStatus.Preparing;
                if (!ReferenceEquals(built, shown)) Measure(built);
                var plot = new NeoRect(r.X + 8, r.Y + 22, r.Width - 16, r.Height - 40);
                double X(double hz) => plot.Left + Math.Log(hz / 40.0) / Math.Log(400.0) * plot.Width;
                double Y(double db) => plot.Top + (12 - Math.Clamp(db, -30, 12)) / 42.0 * plot.Height;
                var grid = new NeoStroke(Palette.Grid, 1, false);
                for (int d = 0; d >= -30; d -= 10) g.Line(plot.Left, Y(d), plot.Right, Y(d), grid);
                foreach (double f in new[] { 100.0, 1000.0, 10000.0 }) g.Line(X(f), plot.Top, X(f), plot.Bottom, grid);
                // Mic position and cuts, as the signal gets them (the same shapes the engine uses).
                double pos = ctx.Value(P.MicPos) / 100.0;
                bool lowOn = ctx.Raw(P.LowCut) > Params.Table[P.LowCut].Min, highOn = ctx.Raw(P.HighCut) < Params.Table[P.HighCut].Max;
                double low = ctx.Value(P.LowCut), high = ctx.Value(P.HighCut);
                bool cabOn = built != null;
                var path = new NeoPath();
                for (int i = 0; i < Points; i++)
                {
                    double hz = Hz(i);
                    double db = cabOn ? curve[i] : 0.0;
                    if (cabOn)
                    {
                        double shelfDb = 2.5 - 11.0 * pos;
                        double s = 1.0 / (1.0 + Math.Pow(2800.0 / hz, 2.0 * 1.0));
                        db += shelfDb * s;
                        double lpHz = Math.Min(16000, 20000) * (1.0 - 0.45 * pos);
                        db -= 10 * Math.Log10(1 + Math.Pow(hz / lpHz, 4));
                    }
                    if (lowOn) db -= 10 * Math.Log10(1 + Math.Pow(low / hz, 4));
                    if (highOn) db -= 10 * Math.Log10(1 + Math.Pow(hz / high, 4));
                    if (i == 0) path.MoveTo(X(hz), Y(db)); else path.LineTo(X(hz), Y(db));
                }
                g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
                string name = built?.Name ?? (ctx.Raw(P.Cabinet) == AmpCabs.Off ? "Off" : "…");
                g.Text(name, r.X + 8, r.Y + 3, 11, AccentColor);
                if (ctx.Raw(P.Cabinet) == AmpCabs.Off) g.Text("the amp direct", r.Right - 8, r.Y + 3, 10, Palette.DimText, align: NeoAlign.Right);
                string note = status switch
                {
                    AmpCabStatus.WaveMissing => $"Wave {ctx.Raw(P.CabWave) + 1} is not in the song: the closed 4x12 plays",
                    AmpCabStatus.WaveLoading => "Reading the wave…",
                    AmpCabStatus.Preparing => "Preparing the cabinet…",
                    _ => null,
                };
                if (note != null) g.Text(note, r.X + 8, r.Bottom - 15, 10, status == AmpCabStatus.WaveMissing ? WarnColor : Palette.DimText, maxWidth: r.Width - 12);
                else g.Text("40 Hz", plot.Left, r.Bottom - 15, 9, Palette.DimText);
                if (note == null) g.Text("16 kHz", plot.Right, r.Bottom - 15, 9, Palette.DimText, align: NeoAlign.Right);
            }
        }
    }
}
