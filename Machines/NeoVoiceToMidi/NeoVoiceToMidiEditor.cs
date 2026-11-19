#nullable disable
// Neo Voice to MIDI's editor (docs/specs/voice-to-midi.md §8): the live view (the sung pitch over the last seconds on a piano roll with the
// scale's notes lit, the chord, the controller meters), the key/scale keyboard (click a note to mute it, Shift-click to flip it in the Custom
// scale), the sound pads (they light when a trigger fires; click one to choose it) with the Train panel, and the knobs.
// One source for WPF and Avalonia (the Neo editor kit); everything draws through INeoCanvas.

using System;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.VoiceToMidi
{
    /// <summary>Hands a surface's pointer to two functions (the keyboard and the pads).</summary>
    internal sealed class ClickInput : IGraphInput
    {
        readonly Func<double, double, bool, bool> down;
        public ClickInput(Func<double, double, bool, bool> down) { this.down = down; }
        public bool Down(double x, double y, int clicks, bool fine) => down(x, y, fine);
        public bool Move(double x, double y, bool fine) => false;
        public bool Up() => false;
        public bool Wheel(double x, double y, int delta, bool fine) => false;
        public bool Leave() => false;
        public bool Capturing => false;
    }

    internal sealed class NeoVoiceToMidiEditor : NeoEditor
    {
        // Coral: a voice, clear of the meters' red and the gate's teal.
        static readonly NeoColor AccentColor = NeoColor.FromRgb(0xE8, 0x7A, 0x6A);
        static readonly NeoColor ChordColor = NeoColor.FromRgb(0xE8, 0xC1, 0x5A);
        const int HistoryLength = 600;   // 3 s at 5 ms
        static readonly bool[] BlackKey = { false, true, false, true, false, false, true, false, true, false, true, false };

        int selected;

        protected override string Title => "Neo Voice to MIDI";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoVoiceToMidiMachine;
            var engine = machine?.Engine;

            // ---- live view
            var roll = new GraphView(360, 150, (g, r) => DrawRoll(g, r, engine), animate: true);
            var chordStrip = new GraphView(360, 26, (g, r) => DrawChord(g, r, engine), animate: true);
            var live = new Section("Live", AccentColor, Layout.Column(roll, chordStrip));

            // ---- key and scale
            var keys = new InteractiveGraph(260, 74, (g, r) => DrawKeys(g, r, ctx, engine), new ClickInput((x, y, fine) => ClickKey(x, ctx, fine)), animate: true);
            var key = new Section("Key", AccentColor, Layout.Column(
                new ParamChoice(ctx, P.Lock, "Lock", 54),
                Layout.Row(new ParamDropdown(ctx, P.Root, "Root", 70), new ParamDropdown(ctx, P.Scale, "Scale", 120)),
                keys));

            // ---- input and pitch
            var input = new Section("Input", AccentColor, Layout.Column(
                Layout.Row(new ParamKnob(ctx, P.Monitor), new ParamKnob(ctx, P.Level)),
                Layout.Gap(1, 8),
                new ParamChoice(ctx, P.Range, "Range", 66)));
            var pitch = new Section("Pitch", AccentColor, Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Pitch, "Pitch", 40), new ParamChoice(ctx, P.Legato, "Legato", 40), new ParamChoice(ctx, P.VelMode, "Velocity", 56), new ParamChoice(ctx, P.Response, "Response", 56)),
                Layout.Row(new ParamKnob(ctx, P.Octave), new ParamKnob(ctx, P.Stick), new ParamKnob(ctx, P.Settle), new ParamKnob(ctx, P.MinNote)),
                Layout.Row(new ParamKnob(ctx, P.Bend), new ParamKnob(ctx, P.Anchor), new ParamKnob(ctx, P.Fixed), new ParamKnob(ctx, P.PitchCh))));

            // ---- triggers
            var pads = new InteractiveGraph(8 * PadW, 52, (g, r) => DrawPads(g, r, machine), new ClickInput((x, y, fine) => ClickPad(x)), animate: true);
            var status = new GraphView(8 * PadW, 18, (g, r) => DrawTrainStatus(g, r, machine), animate: true);
            var train = new ActionButton("Train", AccentColor, 70) { ToolTip = "Records one pass of the chosen sound: say it 10 to 20 times; it stops by itself at 20" };
            var stop = new ActionButton("Done", AccentColor, 60) { ToolTip = "Ends the pass and trains the sounds" };
            var forget = new ActionButton("Forget", AccentColor, 66) { ToolTip = "Forgets the chosen sound's examples" };
            train.Click += () => machine?.StartTraining(selected);
            stop.Click += () => machine?.StopTraining();
            forget.Click += () => machine?.ClearSound(selected);
            var perSound = new UIElement[8];
            for (int i = 0; i < 8; i++)
                perSound[i] = Layout.Column(new ParamKnob(ctx, P.T1Note + 3 * i, "Note " + (i + 1), 38), new ParamKnob(ctx, P.T1Sens + 3 * i, "Sens " + (i + 1), 38));
            var perSoundRow = Layout.Row(perSound);
            var triggers = new Section("Triggers", AccentColor, Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Triggers, "Triggers", 40), new ParamChoice(ctx, P.Window, "Window", 60), new ParamKnob(ctx, P.Sensitivity, null, 36), new ParamKnob(ctx, P.Gap, null, 36), new ParamKnob(ctx, P.Length, null, 36), new ParamKnob(ctx, P.TrigCh, null, 36)),
                pads, status, Layout.Row(train, stop, forget), perSoundRow));

            // ---- chords
            var chords = new Section("Chords", ChordColor, Layout.Column(
                Layout.Row(new ParamChoice(ctx, P.Chords, "Chords", 40), new ParamChoice(ctx, P.ChordFollow, "Follows", 56)),
                Layout.Row(new ParamChoice(ctx, P.ChordType, "Type", 56), new ParamDropdown(ctx, P.Inversion, "Inversion", 80)),
                Layout.Row(new ParamKnob(ctx, P.Spread), new ParamKnob(ctx, P.ChordOct), new ParamKnob(ctx, P.ChordCh), new ParamKnob(ctx, P.ChordVel))));

            // ---- expression
            var lanes = Layout.Row(Lane(ctx, "Level", P.LvlOn, P.LvlCC, P.LvlMin, P.LvlMax, P.LvlCh, () => LevelMeter(engine)),
                Lane(ctx, "Aa", P.AaOn, P.AaCC, P.AaMin, P.AaMax, P.AaCh, () => engine?.VowelAa ?? 0),
                Lane(ctx, "Oo", P.OoOn, P.OoCC, P.OoMin, P.OoMax, P.OoCh, () => engine?.VowelOo ?? 0),
                Lane(ctx, "Ee", P.EeOn, P.EeCC, P.EeMin, P.EeMax, P.EeCh, () => engine?.VowelEe ?? 0));
            var expression = new Section("Expression", AccentColor, lanes);

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
                Layout.Row(live, Layout.Column(input, output), key, pitch),
                Layout.Row(triggers, Layout.Column(chords, expression)));
        }

        static string ToText(NeoVoiceToMidiMachine m) => m == null || m.TargetName.Length == 0 ? "To machine: (none)" : "To machine: " + m.TargetName + (m.TargetFound ? "" : " (missing)");
        static string DevText(NeoVoiceToMidiMachine m) => m == null || m.DeviceName.Length == 0 ? "To device: (none)" : "To device: " + m.DeviceName;

        /// <summary>The next name in the list after <paramref name="current"/>; "" (none) after the last.</summary>
        static string Next(System.Collections.Generic.IEnumerable<string> names, string current)
        {
            var list = new System.Collections.Generic.List<string> { "" };
            list.AddRange(names);
            int at = list.IndexOf(current);
            return list[(at + 1) % list.Count];
        }

        static float LevelMeter(VoiceEngine e) => e == null ? 0 : (float)Math.Clamp((e.LevelDb + 60) / 60.0, 0, 1);

        static UIElement Lane(EditorContext ctx, string name, int on, int cc, int min, int max, int ch, Func<float> level)
        {
            return Layout.Column(
                new ParamChoice(ctx, on, name, 40),
                Layout.Row(new ParamKnob(ctx, cc, null, 34), new Meter("", level, AccentColor, 70)),
                Layout.Row(new ParamKnob(ctx, min, null, 34), new ParamKnob(ctx, max, null, 34)),
                new ParamKnob(ctx, ch, null, 34));
        }

        // ------------------------------------------------------------------------------------------------------------ drawing

        void DrawRoll(INeoCanvas g, NeoRect r, VoiceEngine e)
        {
            g.Rect(r, Palette.Panel, NeoStroke.None);
            // The window follows the voice: 36 semitones centred on the last sung note (kept to MIDI 24-96).
            double centre = 60;
            int head = e?.TrailHead ?? 0;
            float Past(int ago) => e == null ? 0f : e.Trail[((head - 1 - ago) % VoiceEngine.TrailLength + VoiceEngine.TrailLength) % VoiceEngine.TrailLength];
            for (int k = 0; k < HistoryLength; k++)
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
                if (inScale) g.Rect(new NeoRect(r.X, y, r.Width, rowH), BlackKey[pc] ? NeoColor.FromArgb(0x30, 0xE8, 0x7A, 0x6A) : NeoColor.FromArgb(0x1C, 0xE8, 0x7A, 0x6A), NeoStroke.None);
                if (pc == 0) { g.Line(r.X, y + rowH, r.Right, y + rowH, new NeoStroke(Palette.Grid, 1, false)); g.Text("C" + (n / 12 - 1), r.X + 3, y + rowH - 10, 8, Palette.DimText); }
            }
            var path = new NeoPath();
            bool open = false;
            for (int k = 0; k < HistoryLength; k++)
            {
                float v = Past(HistoryLength - 1 - k);
                if (v <= 0) { open = false; continue; }
                double px = r.X + k / (double)(HistoryLength - 1) * r.Width, py = r.Bottom - (v - lo + 0.5) * rowH;
                if (!open) { path.MoveTo(px, py); open = true; } else path.LineTo(px, py);
            }
            g.Path(path, NeoFill.None, new NeoStroke(AccentColor, 2));
            if (e != null && e.NoteSounding)
                g.Text(Scales.NoteNames[((e.CurrentNote % 12) + 12) % 12], r.Right - 4, r.Y + 3, 12, Palette.Text, true, NeoAlign.Right);
        }

        static void DrawChord(INeoCanvas g, NeoRect r, VoiceEngine e)
        {
            g.Rect(r, Palette.Panel, NeoStroke.None);
            string now = e != null && e.ChordSounding ? ChordBuilder.ChordName(e.ChordRoot, e.ChordQuality) : "-";
            g.Text("Chord " + now, r.X + 6, r.Y + 5, 12, ChordColor, true);
            string suggest = Suggestion(e);
            if (suggest.Length > 0) g.Text("Fits: " + suggest, r.Right - 6, r.Y + 6, 10, Palette.DimText, false, NeoAlign.Right, r.Width - 110);
        }

        static string Suggestion(VoiceEngine e)
        {
            var s = e?.Suggester;
            if (s == null || s.Count == 0) return "";
            var parts = new string[Math.Min(s.Count, ChordSuggester.Top)];
            for (int i = 0; i < parts.Length; i++) parts[i] = ChordBuilder.ChordName(s.RootPc[i], s.Quality[i]);
            return string.Join("  ", parts);
        }

        static void DrawKeys(INeoCanvas g, NeoRect r, EditorContext ctx, VoiceEngine e)
        {
            int mask = e?.ActiveMask ?? 0xFFF, muted = (int)ctx.Value(P.Muted), root = e?.ActiveRoot ?? 0;
            double w = r.Width / 12.0;
            for (int pc = 0; pc < 12; pc++)
            {
                var cell = new NeoRect(r.X + pc * w + 1, r.Y + (BlackKey[pc] ? 0 : 14), w - 2, BlackKey[pc] ? 28 : r.Height - 30);
                bool inScale = (mask & (1 << pc)) != 0, off = (muted & (1 << pc)) != 0;
                NeoColor fill = off ? NeoColor.FromRgb(0x2A, 0x2E, 0x36) : inScale ? (pc == root ? AccentColor : NeoColor.FromRgb(0xA8, 0x5A, 0x50)) : Palette.Track;
                g.Rect(cell, fill, new NeoStroke(Palette.PanelEdge, 1, false), 2);
                g.Text(Scales.NoteNames[pc], cell.X + cell.Width / 2, cell.Bottom - 14, 8, off ? Palette.DimText : Palette.Text, false, NeoAlign.Center);
            }
            g.Text("click: mute   shift-click: Custom scale", r.X, r.Bottom - 12, 8, Palette.DimText);
        }

        bool ClickKey(double x, EditorContext ctx, bool shift)
        {
            int pc = Math.Clamp((int)(x / (260 / 12.0)), 0, 11);
            int p = shift ? P.Custom : P.Muted;
            int v = (int)ctx.Value(p) ^ (1 << pc);
            ctx.Set(p, Params.Table[p].Raw(v));
            ctx.RaiseChanged();
            return true;
        }

        const int PadW = 60;

        void DrawPads(INeoCanvas g, NeoRect r, NeoVoiceToMidiMachine m)
        {
            var e = m?.Engine;
            for (int i = 0; i < 8; i++)
            {
                var cell = new NeoRect(r.X + i * PadW + 2, r.Y + 2, PadW - 4, r.Height - 4);
                bool lit = e != null && e.LitAt[i] > 0 && e.Total - e.LitAt[i] < e.Rate * 0.15 && e.Total >= e.LitAt[i];
                int count = e?.Classifier.ExamplesOf(i) ?? 0;
                g.Rect(cell, lit ? AccentColor : Palette.Track, new NeoStroke(i == selected ? Palette.Text : Palette.PanelEdge, i == selected ? 2 : 1, false), 4);
                g.Text(m?.SoundName(i) ?? "", cell.X + PadW / 2 - 2, cell.Y + 6, 9, lit ? Palette.Background : Palette.Text, true, NeoAlign.Center, PadW - 6);
                g.Text(count > 0 ? count + " ex" : "untrained", cell.X + PadW / 2 - 2, cell.Y + 28, 8, lit ? Palette.Background : Palette.DimText, false, NeoAlign.Center, PadW - 6);
            }
        }

        bool ClickPad(double x)
        {
            selected = Math.Clamp((int)(x / PadW), 0, 7);
            return true;
        }

        void DrawTrainStatus(INeoCanvas g, NeoRect r, NeoVoiceToMidiMachine m)
        {
            var e = m?.Engine;
            if (e == null) return;
            string text = e.Learning >= 0
                ? $"Training {m.SoundName(e.Learning)}: {e.LearnedThisPass} heard - say it again, press Done when finished"
                : e.Classifier.Count > 0 ? $"{e.Classifier.ClassesTrained} sounds trained; chosen: {m.SoundName(selected)}" : "Choose a sound, press Train and say it 10 to 20 times";
            g.Text(text, r.X + 2, r.Y + 3, 10, Palette.DimText, false, NeoAlign.Left, r.Width - 4);
        }
    }
}
