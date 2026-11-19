#nullable disable
// Neo Audio to MIDI's editor (docs/specs/audio-to-midi.md §9): the live roll (in Melody mode the input's pitch over the last three seconds as a line and the notes
// sent as bars; in Drums mode a lane for each drum with its hits), the key and scale keyboard, the drum pads (they light on a hit; click one to choose it) with the
// Calibrate panel, and the knobs. One source for WPF and Avalonia (the Neo editor kit); everything draws through INeoCanvas.

using System;
using System.Collections.Generic;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.AudioToMidi
{
    internal sealed class NeoAudioToMidiEditor : NeoEditor
    {
        // Amber: an instrument, clear of the voice machine's coral and the meters' red.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xE0, 0xA8, 0x3C);
        static readonly NeoColor OutColor = NeoColor.FromRgb(0x5A, 0xC8, 0xB8);
        static readonly bool[] BlackKey = { false, true, false, true, false, false, true, false, true, false, true, false };
        const double WindowSeconds = 3.0;

        int selected;
        readonly List<(long At, int Cls)> hits = new List<(long, int)>();
        int seenSerial;

        protected override string Title => "Neo Audio to MIDI";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoAudioToMidiMachine;
            var engine = machine?.Engine;

            // ---- live view
            var roll = new GraphView(380, 170, (g, r) => DrawRoll(g, r, engine, ctx), animate: true);
            var live = new Section("Live", AccentColor, roll);

            // ---- input
            var input = new Section("Input", AccentColor, Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Monitor), new ParamKnob(ctx, P.Level), new ParamKnob(ctx, P.Sensitivity)),
                Layout.Gap(1, 6),
                new ParamChoice(ctx, P.Mode, "Mode", 66),
                new ParamChoice(ctx, P.Range, "Range", 66),
                new ParamChoice(ctx, P.Response, "Response", 66)));

            // ---- key and scale
            var keys = new InteractiveGraph(260, 70, (g, r) => DrawKeys(g, r, ctx, engine), new ClickInput((x, y, fine) => ClickKey(x, ctx)), animate: true);
            var key = new Section("Key", AccentColor, Layout.Column(
                new ParamChoice(ctx, P.Lock, "Lock", 54),
                Layout.Row(new ParamDropdown(ctx, P.Root, "Root", 70), new ParamDropdown(ctx, P.Scale, "Scale", 120)),
                keys));

            // ---- melody
            var melody = new Section("Melody", AccentColor, Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Retrigger, "Retrigger", 40), new ParamChoice(ctx, P.Legato, "Legato", 40), new ParamChoice(ctx, P.VelMode, "Velocity", 56), new ParamChoice(ctx, P.BendMode, "Bend", 70)),
                Layout.Row(new ParamKnob(ctx, P.Octave), new ParamKnob(ctx, P.Stick), new ParamKnob(ctx, P.Settle), new ParamKnob(ctx, P.MinNote), new ParamKnob(ctx, P.Release)),
                Layout.Row(new ParamKnob(ctx, P.Bend), new ParamKnob(ctx, P.Anchor), new ParamKnob(ctx, P.Fixed), new ParamKnob(ctx, P.NoteCh))));

            // ---- drums
            var pads = new InteractiveGraph(3 * PadW, 52, (g, r) => DrawPads(g, r, machine), new ClickInput((x, y, fine) => ClickPad(x)), animate: true);
            var status = new GraphView(3 * PadW + 60, 18, (g, r) => DrawStatus(g, r, machine), animate: true);
            var cal = new ActionButton("Calibrate", AccentColor, 80) { ToolTip = "Records five hits of the chosen drum (in Drums mode): hit it five times; it stops by itself" };
            var done = new ActionButton("Done", AccentColor, 56) { ToolTip = "Ends the pass and trains" };
            var forget = new ActionButton("Forget", AccentColor, 62) { ToolTip = "Forgets the chosen drum's examples" };
            cal.Click += () => machine?.StartCalibration(selected);
            done.Click += () => machine?.StopCalibration();
            forget.Click += () => machine?.ClearDrum(selected);
            var perDrum = new UIElement[3];
            for (int i = 0; i < 3; i++)
                perDrum[i] = Layout.Column(new ParamKnob(ctx, P.KickNote + 4 * i, null, 40), new ParamKnob(ctx, P.KickVel + 4 * i, null, 40), new ParamKnob(ctx, P.KickSens + 4 * i, null, 40), new ParamKnob(ctx, P.KickGap + 4 * i, null, 40));
            var drums = new Section("Drums", AccentColor, Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Length, null, 36), new ParamKnob(ctx, P.DrumCh, null, 36)),
                pads, status, Layout.Row(cal, done, forget), Layout.Row(perDrum)));

            // ---- where the MIDI goes
            var to = new ActionButton(ToText(machine), AccentColor, 170) { ToolTip = "The machine the notes go to (click to change; the Commands menu lists them)" };
            var dev = new ActionButton(DevText(machine), AccentColor, 170) { ToolTip = "The external MIDI device the notes go to" };
            to.Click += () => { machine?.SetTarget(Next(machine.TargetNames(), machine.TargetName)); to.Text = ToText(machine); to.InvalidateVisual(); };
            dev.Click += () => { machine?.SetDevice(Next(machine.DeviceNames(), machine.DeviceName)); dev.Text = DevText(machine); dev.InvalidateVisual(); };
            Poll.Every(to, 0.5, () =>
            {
                string a = ToText(machine), b = DevText(machine);
                if (a != to.Text) { to.Text = a; to.InvalidateVisual(); }
                if (b != dev.Text) { dev.Text = b; dev.InvalidateVisual(); }
            });
            var output = new Section("MIDI to", AccentColor, Layout.Column(to, dev));

            return Layout.Column(
                Layout.Row(live, Layout.Column(input, output), key),
                Layout.Row(melody, drums));
        }

        static string ToText(NeoAudioToMidiMachine m) => m == null || m.TargetName.Length == 0 ? "To machine: (none)" : "To machine: " + m.TargetName + (m.TargetFound ? "" : " (missing)");
        static string DevText(NeoAudioToMidiMachine m) => m == null || m.DeviceName.Length == 0 ? "To device: (none)" : "To device: " + m.DeviceName;

        /// <summary>The next name in the list after <paramref name="current"/>; "" (none) after the last.</summary>
        static string Next(IEnumerable<string> names, string current)
        {
            var list = new List<string> { "" };
            list.AddRange(names);
            int at = list.IndexOf(current);
            return list[(at + 1) % list.Count];
        }

        // ------------------------------------------------------------------------------------------------------------ drawing

        void DrawRoll(INeoCanvas g, NeoRect r, AudioMidiEngine e, EditorContext ctx)
        {
            g.Rect(r, Palette.Panel, NeoStroke.None);
            bool drumMode = e != null ? e.DrumMode : ctx.Value(P.Mode) >= 1;
            if (drumMode) DrawDrumLanes(g, r, e);
            else DrawMelodyRoll(g, r, e);
        }

        void DrawMelodyRoll(INeoCanvas g, NeoRect r, AudioMidiEngine e)
        {
            // The window follows the playing: 36 semitones centred on the last pitch heard (kept to MIDI 24-96).
            double centre = 52;
            int head = e?.TrailHead ?? 0;
            float Past(int ago) => e == null ? 0f : e.Trail[((head - 1 - ago) % AudioMidiEngine.TrailLength + AudioMidiEngine.TrailLength) % AudioMidiEngine.TrailLength];
            int history = (int)(WindowSeconds * 200);
            for (int k = 0; k < history; k++)
            {
                float v = Past(k);
                if (v > 0) { centre = v; break; }
            }
            double lo = Math.Clamp(Math.Round(centre) - 18, 24, 60), hi = lo + 36;
            double rowH = r.Height / 36;
            int mask = e?.ActiveMask ?? 0xFFF;
            for (int n = (int)lo; n < hi; n++)
            {
                double y = r.Bottom - (n - lo + 1) * rowH;
                int pc = n % 12;
                bool inScale = (mask & (1 << pc)) != 0;
                if (inScale && mask != 0xFFF) g.Rect(new NeoRect(r.X, y, r.Width, rowH), BlackKey[pc] ? NeoColor.FromArgb(0x30, 0xE0, 0xA8, 0x3C) : NeoColor.FromArgb(0x1C, 0xE0, 0xA8, 0x3C), NeoStroke.None);
                if (pc == 0) { g.Line(r.X, y + rowH, r.Right, y + rowH, new NeoStroke(Palette.Grid, 1, false)); g.Text("C" + (n / 12 - 1), r.X + 3, y + rowH - 10, 8, Palette.DimText); }
            }
            // The input's pitch (a line) ...
            var path = new NeoPath();
            bool open = false;
            for (int k = 0; k < history; k++)
            {
                float v = Past(history - 1 - k);
                if (v <= 0) { open = false; continue; }
                double px = r.X + k / (double)(history - 1) * r.Width, py = r.Bottom - (v - lo + 0.5) * rowH;
                if (!open) { path.MoveTo(px, py); open = true; } else path.LineTo(px, py);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 1.5));
            // ... and the notes sent (bars).
            if (e != null)
            {
                var m = e.Melody;
                long now = e.Total, span = (long)(WindowSeconds * e.Rate);
                int top = m.LogHead;
                for (int i = 0; i < MelodyNotes.LogLength; i++)
                {
                    int idx = ((top - 1 - i) % MelodyNotes.LogLength + MelodyNotes.LogLength) % MelodyNotes.LogLength;
                    long s = m.LogStart[idx], en = m.LogEnd[idx];
                    if (s <= 0 && en <= 0) continue;
                    if (en <= 0 || en < s) en = now;
                    if (en < now - span) continue;
                    int note = m.LogNote[idx] - 12 * m.Octave;
                    double x0 = r.X + Math.Clamp((s - (now - span)) / (double)span, 0, 1) * r.Width, x1 = r.X + Math.Clamp((en - (now - span)) / (double)span, 0, 1) * r.Width;
                    double y = r.Bottom - (note - lo + 1) * rowH;
                    if (y < r.Y - rowH || y > r.Bottom) continue;
                    g.Rect(new NeoRect(x0, y + 1, Math.Max(2, x1 - x0), Math.Max(2, rowH - 2)), NeoColor.FromArgb(0xB0, 0x5A, 0xC8, 0xB8), NeoStroke.None, 1);
                }
                if (m.Sounding) g.Text(Scales.NoteNames[((m.CurrentNote % 12) + 12) % 12], r.Right - 4, r.Y + 3, 12, Palette.Text, true, NeoAlign.Right);
            }
            g.Text("line: input pitch   bars: notes sent", r.X + 4, r.Y + 3, 8, Palette.DimText);
        }

        void DrawDrumLanes(INeoCanvas g, NeoRect r, AudioMidiEngine e)
        {
            if (e != null && e.HitSerial != seenSerial)
            {
                seenSerial = e.HitSerial;
                hits.Add((e.Total, Math.Max(0, e.LastHit - 1)));
                if (hits.Count > 256) hits.RemoveRange(0, 128);
            }
            double laneH = r.Height / 3;
            for (int c = 0; c < 3; c++)
            {
                double y = r.Y + c * laneH;
                g.Line(r.X, y + laneH, r.Right, y + laneH, new NeoStroke(Palette.Grid, 1, false));
                g.Text(NeoAudioToMidiMachine.DrumNames[c], r.X + 4, y + 4, 9, Palette.DimText);
            }
            if (e == null) return;
            long now = e.Total, span = (long)(WindowSeconds * e.Rate);
            foreach (var (at, c) in hits)
            {
                if (at < now - span || at > now) continue;
                double x = r.X + (at - (now - span)) / (double)span * r.Width;
                g.Rect(new NeoRect(x - 2, r.Y + c * laneH + 6, 5, laneH - 12), c == 0 ? AccentColor : c == 1 ? OutColor : NeoColor.FromRgb(0xE8, 0x7A, 0x6A), NeoStroke.None, 2);
            }
        }

        static void DrawKeys(INeoCanvas g, NeoRect r, EditorContext ctx, AudioMidiEngine e)
        {
            int mask = e?.ActiveMask ?? 0xFFF, root = e?.ActiveRoot ?? 0;
            double w = r.Width / 12.0;
            for (int pc = 0; pc < 12; pc++)
            {
                var cell = new NeoRect(r.X + pc * w + 1, r.Y + (BlackKey[pc] ? 0 : 14), w - 2, BlackKey[pc] ? 26 : r.Height - 30);
                bool inScale = (mask & (1 << pc)) != 0;
                NeoColor fill = inScale ? (pc == root ? AccentColor : NeoColor.FromRgb(0x9A, 0x74, 0x2A)) : Palette.Track;
                g.Rect(cell, fill, new NeoStroke(Palette.PanelEdge, 1, false), 2);
                g.Text(Scales.NoteNames[pc], cell.X + cell.Width / 2, cell.Bottom - 14, 8, inScale ? Palette.Text : Palette.DimText, false, NeoAlign.Center);
            }
            g.Text("click: flip a note of the Custom scale", r.X, r.Bottom - 12, 8, Palette.DimText);
        }

        bool ClickKey(double x, EditorContext ctx)
        {
            int pc = Math.Clamp((int)(x / (260 / 12.0)), 0, 11);
            int v = (int)ctx.Value(P.Custom) ^ (1 << pc);
            ctx.Set(P.Custom, Params.Table[P.Custom].Raw(v));
            ctx.RaiseChanged();
            return true;
        }

        const int PadW = 80;

        void DrawPads(INeoCanvas g, NeoRect r, NeoAudioToMidiMachine m)
        {
            var e = m?.Engine;
            for (int i = 0; i < 3; i++)
            {
                var cell = new NeoRect(r.X + i * PadW + 2, r.Y + 2, PadW - 4, r.Height - 4);
                bool lit = e != null && e.LitAt[i] > 0 && e.Total - e.LitAt[i] < e.Rate * 0.15 && e.Total >= e.LitAt[i];
                int count = e?.Classifier.ExamplesOf(i) ?? 0;
                g.Rect(cell, lit ? AccentColor : Palette.Track, new NeoStroke(i == selected ? Palette.Text : Palette.PanelEdge, i == selected ? 2 : 1, false), 4);
                g.Text(NeoAudioToMidiMachine.DrumNames[i], cell.X + PadW / 2 - 2, cell.Y + 6, 10, lit ? Palette.Background : Palette.Text, true, NeoAlign.Center, PadW - 6);
                g.Text(count > 0 ? count + " ex" : "built in", cell.X + PadW / 2 - 2, cell.Y + 28, 8, lit ? Palette.Background : Palette.DimText, false, NeoAlign.Center, PadW - 6);
            }
        }

        bool ClickPad(double x)
        {
            selected = Math.Clamp((int)(x / PadW), 0, 2);
            return true;
        }

        void DrawStatus(INeoCanvas g, NeoRect r, NeoAudioToMidiMachine m)
        {
            var e = m?.Engine;
            if (e == null) return;
            string text = e.Learning >= 0
                ? $"Calibrating {NeoAudioToMidiMachine.DrumNames[e.Learning]}: {e.LearnedThisPass} of {AudioMidiEngine.CalibrationHits} heard"
                : !e.DrumMode ? "Set Mode to Drums to calibrate"
                : e.Classifier.ClassesTrained >= 3 ? "Calibrated: the kit is recognised from your hits" : "Built in rules; choose a drum, press Calibrate, hit it five times";
            g.Text(text, r.X + 2, r.Y + 3, 10, Palette.DimText, false, NeoAlign.Left, r.Width - 4);
        }
    }
}
