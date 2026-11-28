#nullable disable
// Neo Looper's editor (docs/specs/neo-looper.md §7): the loop's waveform with the play head and the undo layers behind it, a state banner that
// reads from across a room, the big buttons a pad-minded player uses with a mouse or a touch screen (the same seven triggers a MIDI control sends),
// then the timing, the loop and the output. One source for the studio (the Neo editor kit); the pads are drawn on the kit's canvas.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
#if AVALONIA
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
#endif

namespace NeoBuzz.Looper
{
    internal sealed class NeoLooperEditor : NeoEditor
    {
        // The Modulation identity in the studio; the state colours are the studio's reserved record, play and override colours.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Modulation, NeoColor.FromRgb(0x4F, 0xC3, 0xA1));

        const int WaveColumns = 640;
        const double WaveW = 640, WaveH = 150, BannerW = 250;

        protected override string Title => "Neo Looper";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoLooperMachine;
            Section S(string title, UIElement content) => new Section(title, AccentColor, content);

            // The loop: its peaks (the strip is made again when the loop changes, and ten times a second during an overdub), the layers behind it.
            var strips = new float[3][][];
            for (int i = 0; i < 3; i++) strips[i] = new[] { new float[WaveColumns], new float[WaveColumns] };
            var have = new bool[3];
            int revision = -1;
            long last = 0;
            void Refresh()
            {
                var e = machine?.Engine;
                if (e == null) return;
                long now = Environment.TickCount64;
                bool over = e.State == LooperState.Overdubbing;
                if (e.Revision == revision && !(over && now - last > 100)) return;
                revision = e.Revision;
                last = now;
                for (int i = 0; i < 3; i++) have[i] = e.Wave(i, WaveColumns, strips[i][0], strips[i][1]);
            }

            var wave = new GraphView(WaveW, WaveH, (g, r) =>
            {
                Refresh();
                DrawWave(g, r, machine?.Engine, strips, have);
            }, animate: true);

            var banner = new GraphView(BannerW, WaveH, (g, r) => DrawBanner(g, r, machine?.Engine), animate: true);

            // The pads: big, in the order of a performance. The first one's face follows the cycle (what the next press does).
            var rec = new Pad(ctx, P.Record, () => RecordFace(machine?.Engine), 168, 76, bold: true);
            var stop = new Pad(ctx, P.Stop, () => ("STOP", Tokens.Override), 96, 76);
            var undo = new Pad(ctx, P.Undo, () => ("UNDO", AccentColor), 96, 76);
            var redo = new Pad(ctx, P.Redo, () => ("REDO", AccentColor), 96, 76);
            var mul = new Pad(ctx, P.Multiply, () => ($"x{2 + ctx.Raw(P.MultiplyBy)}", AccentColor), 96, 76);
            var clear = new Pad(ctx, P.Clear, () => ("CLEAR", Tokens.Record), 96, 76);
            var play = new Pad(ctx, P.Play, () => ("PLAY", Tokens.Play), 96, 76);

            var pads = Layout.Row(rec, Layout.Gap(8), play, Layout.Gap(8), stop, Layout.Gap(8), undo, Layout.Gap(8), redo, Layout.Gap(8), mul, Layout.Gap(8), clear);

            var timing = Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Sync, "Sync", 52), Layout.Gap(8), new ParamChoice(ctx, P.Quantise, "Quantise", 52)),
                Layout.Gap(1, 6),
                Layout.Row(new ParamChoice(ctx, P.MultiplyBy, "Multiply by", 40), Layout.Gap(8), new ParamChoice(ctx, P.Pedal, "Pedal", 110)));

            var loop = Layout.Row(
                Layout.Column(
                    Layout.Row(new ParamKnob(ctx, P.Feedback), new ParamKnob(ctx, P.LoopLevel), new ParamKnob(ctx, P.Crossfade)),
                    Layout.Gap(1, 6),
                    Layout.Row(new ParamChoice(ctx, P.Monitor, "Monitor", 70), Layout.Gap(6), new ParamChoice(ctx, P.Reverse, "Reverse", 70), Layout.Gap(6), new ParamChoice(ctx, P.HalfSpeed, "Half speed", 80))));

            var memory = Layout.Column(
                new ParamChoice(ctx, P.MaxLoop, "Max loop", 52),
                Layout.Gap(1, 6),
                new ParamChoice(ctx, P.UndoLevels, "Undo levels", 30),
                Layout.Gap(1, 6),
                new GraphView(210, 28, (g, r) => DrawMemory(g, r, machine?.Engine), animate: true));

            // House style: Amount, then Mix, two 40 px knobs, in the last section.
            var output = Layout.Row(new ParamKnob(ctx, P.Amount, diameter: 40), new ParamKnob(ctx, P.Mix, diameter: 40));

            return Layout.Column(
                Layout.Row(S("Loop", wave), Layout.Gap(8), S("State", banner)),
                Layout.Gap(1, 8),
                S("Play", pads),
                Layout.Gap(1, 8),
                Layout.Row(
                    S("Timing", timing), Layout.Gap(8),
                    S("Loop", loop), Layout.Gap(8),
                    S("Memory", memory), Layout.Gap(8),
                    S("Output", output)));
        }

        // ------------------------------------------------------------------------------------------------ what it shows

        static (string, NeoColor) RecordFace(LooperEngine e)
        {
            switch (e?.State ?? LooperState.Empty)
            {
                case LooperState.Recording: return ("CLOSE", Tokens.Play);
                case LooperState.Playing: return ("OVERDUB", Tokens.Override);
                case LooperState.Overdubbing: return ("PLAY", Tokens.Play);
                case LooperState.Stopped: return ("PLAY", Tokens.Play);
                default: return ("REC", Tokens.Record);
            }
        }

        static NeoColor StateColour(LooperState s) => s switch
        {
            LooperState.Recording => Tokens.Record,
            LooperState.Playing => Tokens.Play,
            LooperState.Overdubbing => Tokens.Override,
            LooperState.Stopped => Palette.Text,
            _ => Palette.DimText,
        };

        static string StateWord(LooperState s) => s switch
        {
            LooperState.Recording => "RECORDING",
            LooperState.Playing => "PLAYING",
            LooperState.Overdubbing => "OVERDUB",
            LooperState.Stopped => "STOPPED",
            _ => "EMPTY",
        };

        static void DrawWave(INeoCanvas g, NeoRect r, LooperEngine e, float[][][] strips, bool[] have)
        {
            double mid = r.Y + r.Height / 2, half = r.Height / 2 - 6;
            var state = e?.State ?? LooperState.Empty;
            var colour = StateColour(state);
            g.Line(r.Left, mid, r.Right, mid, new NeoStroke(Palette.Grid, 1, false));
            if (e == null) return;
            int frames = e.LoopFrames;

            // Bar lines: the loop's bars (Sync), else quarters of its length.
            if (frames > 0)
            {
                double bars = e.LoopBeats > 0 ? e.LoopBeats / Math.Max(1, e.BeatsPerBar) : 4;
                int lines = (int)Math.Round(bars);
                if (lines >= 1 && lines <= 64)
                    for (int i = 1; i < lines; i++)
                    {
                        double x = r.X + r.Width * i / lines;
                        g.Line(x, r.Y + 4, x, r.Bottom - 4, new NeoStroke(Palette.Grid, 1, false));
                    }
            }

            // The layers behind (older undo levels, dimmer), then the loop.
            for (int w = 2; w >= 0; w--)
            {
                if (!have[w]) continue;
                var c = w == 0 ? colour : Palette.DimText.WithAlpha(w == 1 ? (byte)0x90 : (byte)0x50);
                var lo = strips[w][0];
                var hi = strips[w][1];
                double scale = 1.0;
                double peak = 0.001;
                for (int i = 0; i < lo.Length; i++) peak = Math.Max(peak, Math.Max(hi[i], -lo[i]));
                scale = peak > 1 ? 1 / peak : 1;
                var path = new NeoPath();
                int n = lo.Length;
                for (int i = 0; i < n; i++)
                {
                    double x = r.X + r.Width * (i + 0.5) / n;
                    g.Line(x, mid - hi[i] * scale * half - 0.5, x, mid - lo[i] * scale * half + 0.5, new NeoStroke(c, 1, false));
                }
            }

            // The play head.
            if (frames > 0 && (state == LooperState.Playing || state == LooperState.Overdubbing || state == LooperState.Stopped))
            {
                double x = r.X + r.Width * e.Position / frames;
                g.Line(x, r.Y + 2, x, r.Bottom - 2, new NeoStroke(Palette.Text, 2, false));
            }
            if (state == LooperState.Empty) g.Text("press REC", r.X + r.Width / 2, mid - 8, 14, Palette.DimText, align: NeoAlign.Center);
            if (state == LooperState.Recording) g.Text("recording...", r.X + r.Width / 2, mid - 8, 14, Tokens.Record, align: NeoAlign.Center);
        }

        static void DrawBanner(INeoCanvas g, NeoRect r, LooperEngine e)
        {
            var state = e?.State ?? LooperState.Empty;
            var c = StateColour(state);
            g.Rect(new NeoRect(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4), c.WithAlpha(0x26), new NeoStroke(c, 3), 8);
            g.Text(StateWord(state), r.X + r.Width / 2, r.Y + 28, 30, c, bold: true, align: NeoAlign.Center, maxWidth: r.Width - 16);
            string sub = "";
            if (e != null && e.LoopFrames > 0)
            {
                double secs = e.LoopFrames / Math.Max(1.0, e.Rate);
                if (e.LoopBeats > 0)
                {
                    int bars = (int)Math.Round(e.LoopBeats / Math.Max(1, e.BeatsPerBar));
                    int bar = Math.Min(bars - 1, (int)((long)e.Position * bars / Math.Max(1, e.LoopFrames))) + 1;
                    sub = $"bar {bar} of {bars}";
                }
                else sub = $"{secs:0.0} s";
            }
            g.Text(sub, r.X + r.Width / 2, r.Y + 76, 20, Palette.Text, align: NeoAlign.Center, maxWidth: r.Width - 16);
            string next = "";
            if (e != null)
            {
                var q = e.Queued;
                if (q != LooperCmd.None) next = $"next: {q.ToString().ToUpperInvariant()} on the " + (e.Syncing ? "bar" : "next block");
                else if (e.UndoDepth > 0 || e.RedoDepth > 0) next = $"undo {e.UndoDepth}  redo {e.RedoDepth}";
            }
            g.Text(next, r.X + r.Width / 2, r.Y + 112, 13, Palette.DimText, align: NeoAlign.Center, maxWidth: r.Width - 16);
        }

        static void DrawMemory(INeoCanvas g, NeoRect r, LooperEngine e)
        {
            if (e == null) return;
            double mb = e.PoolBytes / 1048576.0;
            string text = $"memory {mb:0} MB, {e.PoolLevels} undo level" + (e.PoolLevels == 1 ? "" : "s");
            if (e.LoopFrames > 0)
            {
                text += $"\nsaved with the song: about {e.LoopFrames / Math.Max(1.0, e.Rate) * 0.3:0.0} MB";
            }
            g.Text(text.Split('\n')[0], r.X, r.Y, 11, Palette.DimText, maxWidth: r.Width);
            if (text.Contains('\n')) g.Text(text.Split('\n')[1], r.X, r.Y + 14, 11, Palette.DimText, maxWidth: r.Width);
        }

#if AVALONIA
        /// <summary>
        /// A big button for a trigger: it sends the same press a pad or footswitch does (the parameter goes to 1 and, a moment later, back to 0).
        /// Its face (word and colour) is read from the engine every frame.
        /// </summary>
        sealed class Pad : NeoElement
        {
            static readonly TimeSpan Hold = TimeSpan.FromMilliseconds(60);
            readonly EditorContext ctx;
            readonly int index;
            readonly Func<(string Text, NeoColor Colour)> face;
            readonly AvaloniaCanvas canvas;
            readonly DispatcherTimer repaint, release;
            readonly bool bold;
            bool hover, down;

            public Pad(EditorContext ctx, int index, Func<(string, NeoColor)> face, double width, double height, bool bold = false)
            {
                this.ctx = ctx;
                this.index = index;
                this.face = face;
                this.bold = bold;
                Width = width;
                Height = height;
                canvas = new AvaloniaCanvas(this);
                Cursor = new Cursor(StandardCursorType.Hand);
                ToolTip = ctx.Table[index].Description;
                AutomationProperties.SetName(this, ctx.Table[index].Name);
                repaint = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                repaint.Tick += (s, e) => { if (IsEffectivelyVisible) InvalidateVisual(); };
                release = new DispatcherTimer { Interval = Hold };
                release.Tick += (s, e) => { release.Stop(); ctx.Set(index, 0); down = false; InvalidateVisual(); };
                Loaded += (s, e) => repaint.Start();
                Unloaded += (s, e) => { repaint.Stop(); release.Stop(); };
            }

            protected override void OnMouseEnter(MouseEventArgs e) { hover = true; InvalidateVisual(); }
            protected override void OnMouseLeave(MouseEventArgs e) { hover = false; InvalidateVisual(); }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                ctx.Set(index, 1);
                down = true;
                release.Stop();
                release.Start();
                InvalidateVisual();
                e.Handled = true;
            }

            protected override void OnRender(DrawingContext dc)
            {
                double w = Bounds.Width > 0 ? Bounds.Width : Width, h = Bounds.Height > 0 ? Bounds.Height : Height;
                var (text, colour) = face();
                var g = canvas.On(dc);
                byte alpha = down ? (byte)0x80 : hover ? (byte)0x48 : (byte)0x28;
                g.Rect(new NeoRect(1, 1, w - 2, h - 2), colour.WithAlpha(alpha), new NeoStroke(colour, down ? 4 : 2), 8);
                g.Text(text, w / 2, h / 2 - 12, bold ? 24 : 18, colour, bold: true, align: NeoAlign.Center, maxWidth: w - 12);
            }
        }
#endif
    }
}
