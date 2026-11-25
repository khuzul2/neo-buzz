#nullable disable
// Neo Tune's editor (docs/specs/neo-tune.md §7): the live pitch display (the voice and the corrected voice over the last eight
// seconds on a piano roll with the scale's notes lit), the scale keyboard (a click mutes or enables a note and turns the
// scale into Custom), the key, the correction knobs, the formant and vibrato knobs, and the range, input and mix.
// One source for WPF and Avalonia (the Neo editor kit); both pictures draw on the kit's canvas (INeoCanvas).

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Tune
{
    internal sealed class NeoTuneEditor : NeoEditor
    {
        // Warm amber: the corrected line, clear of the meters' red and the other effects' teal and blue.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xE8, 0xA3, 0x3D);
        static readonly NeoColor InputColor = NeoColor.FromRgb(0x8A, 0x92, 0xA0);

        const double GraphW = 380, GraphH = 150, KeysH = 46;
        static readonly bool[] Black = { false, true, false, true, false, false, true, false, true, false, true, false };
        static readonly int[] WhiteIndex = { 0, 2, 4, 5, 7, 9, 11 };

        double centre = 60;

        protected override string Title => "Neo Tune";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoTuneMachine;

            var key = Layout.Column(
                new ParamChoice(ctx, P.Source, "Key from", 52),
                Layout.Gap(1, 4),
                new ParamDropdown(ctx, P.Key, "Key", 96),
                Layout.Gap(1, 4),
                new ParamDropdown(ctx, P.Scale, "Scale", 96),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Target, "Target", 52));

            var roll = new GraphView(GraphW, GraphH, (g, r) => DrawRoll(g, r, ctx, machine), animate: true);
            var keys = new InteractiveGraph(GraphW, KeysH, (g, r) => DrawKeys(g, r, ctx, machine), new KeysInput(ctx, machine), animate: true);
            var display = Layout.Column(roll, Layout.Gap(1, 4), keys);

            // Natural: the main knob. Moving it by hand rewrites the six fine controls in the same undo step (a preset or automation moves only Natural).
            var natural = new ParamKnob(ctx, P.Natural, diameter: 84);
            void ApplyNatural(int naturalRaw)
            {
                double n = Params.Table[P.Natural].Value(naturalRaw);
                for (int i = 0; i < TuneNatural.Controls.Length; i++)
                {
                    int idx = TuneNatural.Controls[i];
                    ctx.Set(idx, Params.Table[idx].Raw(TuneNatural.Value(i, n)));
                }
                ctx.RaiseChanged();
            }
            natural.UserChanged += _ => ApplyNatural(natural.Binding.Value);

            // PL1: a fine control moved by hand (or a preset that did) no longer matches Natural: a small "edited" mark under the knob; a click sets Natural's values again (one undo step).
            var edited = new ActionButton("edited \u21BA", AccentColor, 84)
            {
                ToolTip = "A fine control differs from what Natural sets. Click to set the six fine controls from Natural again (one undo step).",
                Visibility = Visibility.Collapsed
            };
            edited.Click += () => { ApplyNatural(ctx.Raw(P.Natural)); ShowEdited(); };
            void ShowEdited()
            {
                var shown = TuneNatural.Edited(ctx.Raw) ? Visibility.Visible : Visibility.Collapsed;
                if (edited.Visibility != shown) edited.Visibility = shown;
            }
            ctx.Changed += ShowEdited;
            Poll.Every(edited, 0.25, ShowEdited);   // a preset or automation can change the fine controls without the editor's own gesture
            ShowEdited();

            var correction = Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Retune, diameter: 46), new ParamKnob(ctx, P.Zone, diameter: 46), new ParamKnob(ctx, P.Humanize, diameter: 46)),
                Layout.Row(new ParamKnob(ctx, P.VibKeep, diameter: 46), new ParamKnob(ctx, P.Glide, diameter: 46), new ParamKnob(ctx, P.Stick, diameter: 46)));

            var voice = Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Preserve, "Formant preserve", 0), Layout.Gap(6), new ParamKnob(ctx, P.Formant, diameter: 46)),
                Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 46), new ParamKnob(ctx, P.Transpose, diameter: 46)),
                Layout.Row(new ParamKnob(ctx, P.VibDepth, diameter: 46), new ParamKnob(ctx, P.VibRate, diameter: 46), new ParamKnob(ctx, P.VibDelay, diameter: 46)));

            var engine = Layout.Column(
                new ParamChoice(ctx, P.Range, "Range", 64, vertical: true),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Method, "Method", 44),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Input, "Input", 44),
                Layout.Gap(1, 4),
                new ParamChoice(ctx, P.Transients, "Transients", 0),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.FxAmount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40)));

            return Layout.Row(
                new Section("Natural", AccentColor, Layout.Column(natural, Layout.Gap(1, 4), edited)),
                new Section("Key", AccentColor, key),
                new Section("Pitch", AccentColor, display),
                new Section("Fine controls", AccentColor, correction),
                new Section("Voice", AccentColor, voice),
                new Section("Engine", AccentColor, engine));
        }

        /// <summary>The notes the keyboard shows lit: the scale (or the Custom mask) from the key, or the notes held in MIDI mode.</summary>
        static int ShownMask(EditorContext ctx, NeoTuneMachine machine)
        {
            int scale = (int)ctx.Raw(P.Scale);
            if (machine != null) return machine.Engine.NoteMask;
            return TuneScales.Mask(scale, (int)ctx.Raw(P.Key), (int)ctx.Raw(P.Notes));
        }

        void DrawRoll(INeoCanvas g, NeoRect r, EditorContext ctx, NeoTuneMachine machine)
        {
            var plot = new NeoRect(r.X + 26, r.Y + 6, r.Width - 32, r.Height - 12);
            int mask = ShownMask(ctx, machine);
            const double span = 30;   // semitones on screen
            var e = machine?.Engine;
            long count = e?.HistoryCount ?? 0;
            const int shown = 480;    // 8 s at 60 per second
            long first = Math.Max(Math.Max(0, count - shown), count - TuneEngine.HistorySize + 1);

            // The window follows the voice: the middle of the pitches of the last second, moved gently.
            double sum = 0; int cnt = 0;
            for (long i = Math.Max(first, count - 60); i < count; i++)
            {
                e.History(i, out float inp, out float cor);
                if (inp > 0) { sum += inp; cnt++; }
            }
            if (cnt > 6) centre += (sum / cnt - centre) * 0.06;
            double lo = Math.Floor(centre - span / 2), hi = lo + span;
            double Y(double midi) => plot.Bottom - (midi - lo) / span * plot.Height;

            var lit = Palette.Grid.Tint(0.12);
            var scaleFill = AccentColor.WithAlpha(0x2A);
            for (int n = (int)lo; n < hi; n++)
            {
                if (n < 0 || n > 127) continue;
                int pc = n % 12;
                var band = new NeoRect(plot.Left, Y(n + 0.5), plot.Width, plot.Height / span);
                if (((mask >> pc) & 1) != 0) g.Rect(band, scaleFill, NeoStroke.None);
                else if (Black[pc]) g.Rect(band, Palette.Grid.WithAlpha(0x60), NeoStroke.None);
                if (pc == 0)
                {
                    g.Line(plot.Left, Y(n - 0.5), plot.Right, Y(n - 0.5), new NeoStroke(Palette.Grid, 1, false));
                    g.Text("C" + (n / 12 - 1), r.X + 2, Y(n) - 6, 9, Palette.DimText);
                }
            }

            // The correction zone: the band around the note the voice is on, inside which the correction works (none at Natural 0).
            if (e != null && e.Voiced && e.ZoneCents < 99.5)
            {
                double tn = Math.Round(e.TargetNote), half = e.ZoneCents / 100.0;
                g.Rect(new NeoRect(plot.Left, Y(tn + half), plot.Width, Math.Max(1, Y(tn - half) - Y(tn + half))), AccentColor.WithAlpha(0x22), new NeoStroke(AccentColor.WithAlpha(0x60), 1, false));
            }
            if (count > 0)
            {
                DrawLine(g, plot, e, first, count, shown, Y, 0, InputColor, 1.5);
                DrawLine(g, plot, e, first, count, shown, Y, 2, Palette.Text.WithAlpha(0xB0), 1);   // the note centre the correction works on
                DrawLine(g, plot, e, first, count, shown, Y, 1, AccentColor, 2);
            }
            else g.Text("play a voice through it", plot.Left + 8, plot.Top + 6, 10, Palette.DimText);

            if (e != null && e.Voiced)
            {
                string t = TuneScales.NoteNames[(int)Math.Round(e.TargetNote) % 12] + ((int)Math.Round(e.TargetNote) / 12 - 1);
                g.Text($"{t}  {e.CorrectionCents:+0;-0;0} c", plot.Right - 4, plot.Top + 4, 10, AccentColor, true, NeoAlign.Right);
            }
        }

        static void DrawLine(INeoCanvas g, NeoRect plot, TuneEngine e, long first, long count, int shown, Func<double, double> y, int which, NeoColor color, double width)
        {
            var path = new NeoPath();
            bool pen = false;
            double dx = plot.Width / shown;
            double x0 = plot.Right - (count - first) * dx;
            for (long i = first; i < count; i++)
            {
                e.History(i, out float inp, out float cor);
                double m = which == 0 ? inp : which == 1 ? cor : e.HistoryCentre(i);
                if (m <= 0) { pen = false; continue; }
                double x = x0 + (i - first) * dx;
                if (!pen) path.MoveTo(x, y(m)); else path.LineTo(x, y(m));
                pen = true;
            }
            if (path.PointCount > 1) g.Path(path, NeoFill.None, new NeoStroke(color, width));
        }

        // -------------------------------------------------------------------------------- the scale keyboard

        /// <summary>The key a point is on (0 … 11), or -1: the black keys are the upper 60 % and take the point first.</summary>
        static int KeyAt(double x, double y, NeoRect r)
        {
            double w = r.Width / 7;
            if (y < r.Top || y > r.Bottom || x < r.Left || x > r.Right) return -1;
            double bw = w * 0.6;
            for (int pc = 1; pc < 12; pc++)
            {
                if (!Black[pc]) continue;
                double cx = r.Left + BoundaryOfWhite(pc) * w;
                if (y < r.Top + r.Height * 0.6 && Math.Abs(x - cx) < bw / 2) return pc;
            }
            int white = Math.Clamp((int)((x - r.Left) / w), 0, 6);
            return WhiteIndex[white];
        }

        /// <summary>For a black key, the number of white keys to its left (it sits on that boundary).</summary>
        static int BoundaryOfWhite(int pc)
        {
            int n = 0;
            for (int i = 0; i < pc; i++) if (!Black[i]) n++;
            return n;
        }

        void DrawKeys(INeoCanvas g, NeoRect r, EditorContext ctx, NeoTuneMachine machine)
        {
            var area = new NeoRect(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
            int mask = ShownMask(ctx, machine);
            int root = machine?.Engine.Root ?? ctx.Raw(P.Key);
            bool chromaticOrCustom = ctx.Raw(P.Scale) == 0 || ctx.Raw(P.Scale) == TuneScales.Custom;
            double w = area.Width / 7;
            var edge = new NeoStroke(Palette.PanelEdge, 1, false);
            for (int i = 0; i < 7; i++)
            {
                int pc = WhiteIndex[i];
                bool on = ((mask >> pc) & 1) != 0;
                g.Rect(new NeoRect(area.Left + i * w, area.Top, w, area.Height), on ? AccentColor.WithAlpha(0xC0) : Palette.Track, edge, 2);
                if (pc == root && !chromaticOrCustom)
                    g.Ellipse(new NeoPoint(area.Left + (i + 0.5) * w, area.Bottom - 7), 3, 3, Palette.Background, NeoStroke.None);
            }
            for (int pc = 1; pc < 12; pc++)
            {
                if (!Black[pc]) continue;
                double cx = area.Left + BoundaryOfWhite(pc) * w;
                bool on = ((mask >> pc) & 1) != 0;
                g.Rect(new NeoRect(cx - w * 0.3, area.Top, w * 0.6, area.Height * 0.6), on ? AccentColor : Palette.Background, edge, 2);
                if (pc == root && !chromaticOrCustom)
                    g.Ellipse(new NeoPoint(cx, area.Top + area.Height * 0.6 - 7), 3, 3, on ? Palette.Background : AccentColor, NeoStroke.None);
            }
        }

        sealed class KeysInput : IGraphInput
        {
            readonly EditorContext ctx;
            readonly NeoTuneMachine machine;

            public KeysInput(EditorContext ctx, NeoTuneMachine machine)
            {
                this.ctx = ctx;
                this.machine = machine;
            }

            public bool Capturing => false;

            public bool Down(double x, double y, int clicks, bool fine)
            {
                if (ctx.Raw(P.Target) != 0) return false;   // in MIDI mode the held notes decide
                int pc = KeyAt(x, y, new NeoRect(2, 2, GraphW - 4, KeysH - 4));
                if (pc < 0) return false;
                int mask = TuneScales.Mask(ctx.Raw(P.Scale), ctx.Raw(P.Key), ctx.Raw(P.Notes));
                if (ctx.Raw(P.Scale) != TuneScales.Custom && machine != null && ctx.Raw(P.Source) == 1)
                    mask = TuneScales.Mask(ctx.Raw(P.Scale), machine.Engine.Root >= 0 ? machine.Engine.Root : ctx.Raw(P.Key), 0);
                mask ^= 1 << pc;
                if (mask == 0) mask = 1 << pc;           // never an empty scale
                ctx.Set(P.Notes, mask);
                ctx.Set(P.Scale, TuneScales.Custom);
                ctx.RaiseChanged();
                return true;
            }

            public bool Move(double x, double y, bool fine) => false;
            public bool Up() => false;
            public bool Wheel(double x, double y, int delta, bool fine) => false;
            public bool Leave() => false;
        }
    }
}
