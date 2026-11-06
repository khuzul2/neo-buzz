#nullable disable
// Neo Sampler's editor (docs/specs/machines.md §4, §5): the zone map (a keyboard with each zone as
// a box over its keys and velocities, or its key in Kit mode; click to select, drag an edge to move
// a range) and the selected zone's wave (start, end and loop markers to drag, the crossfade, the
// voices playing it); below, the selected zone's wave (step through the song's waves and the
// built-ins, or load a file into the song's wavetable) and its controls; then play, the amp
// envelope, the filter and its envelope, and the output. The pictures draw on the kit's canvas with
// toolkit-free input (SamplerViews.cs), so WPF and the studio's Avalonia show the same.

using System;
using System.IO;
using System.Linq;
#if !AVALONIA   // the studio's flavour takes these names from its kit (NeoBuzz.Editor.Avalonia/WpfNames.cs)
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
#endif
using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.Sampler
{
    internal sealed class NeoSamplerEditor : NeoEditor
    {
        // Fork (ux-overhaul.md P2a): the Sampler identity in the studio, its own colour in the classic app.
        static readonly NeoColor AccentColor = Tokens.Identity(MachineCategory.Sampler, NeoColor.FromRgb(0xF2, 0xB4, 0x4C));

        protected override string Title => "Neo Sampler";
        protected override NeoColor Accent => AccentColor;
        internal override ParamDef[] Table => Params.Table;

        /// <summary>The pictures' view of the machine: its parameters through the editor, its bank and voices.</summary>
        sealed class View : ISamplerView
        {
            readonly EditorContext ctx;
            readonly NeoSamplerMachine machine;
            readonly ParamValues mirror = new ParamValues(Params.Table);

            public View(EditorContext ctx, NeoSamplerMachine machine)
            {
                this.ctx = ctx;
                this.machine = machine;
            }

            public ZoneSettings Zone(int z)
            {
                int b = SamplerEngine.ZoneParam(z, 0);
                for (int f = 0; f < SamplerEngine.ZoneStride; f++) mirror.Set(b + f, ctx.Raw(b + f));
                return SamplerEngine.Zone(mirror, z);
            }

            public bool Kit => ctx.Raw(P.Mode) == 1;
            public int KitBase => ctx.Raw(P.KitBase);
            public int Raw(int index) => ctx.Raw(index);
            public void Set(int index, int raw) => ctx.Set(index, raw);

            public SampleLayer Layer(int z)
            {
                var slot = machine?.Bank.Get(ctx.Raw(SamplerEngine.ZoneParam(z, SamplerEngine.FWave)));
                if (slot == null || slot.Layers.Length == 0) return null;
                return slot.Layers.OrderBy(l => Math.Abs(l.Root - 60)).First();
            }

            public bool Voice(int i, out int zone, out int note, out float position)
            {
                zone = note = -1;
                position = 0;
                return machine != null && machine.Engine.VoiceInfo(i, out zone, out note, out position);
            }
        }

        internal override UIElement BuildBody(EditorContext ctx)
        {
            var machine = ctx.Machine?.ManagedMachine as NeoSamplerMachine;
            var view = new View(ctx, machine);
            var map = new ZoneMap(view);
            var wave = new WaveView(view, () => map.Selected);

            var mapView = new InteractiveGraph(560, 210, (g, r) => map.Draw(g, r, AccentColor), map, animate: true)
            {
                ToolTip = "The zones over the keys (across) and velocities (up); in Kit mode zone n sits on key Kit base + n − 1. " +
                          "Click a zone to select it; drag the selected zone's edges to move its keys or velocities.",
            };
            var waveView = new InteractiveGraph(514, 210, (g, r) => wave.Draw(g, r, AccentColor), wave, animate: true)
            {
                ToolTip = "The selected zone's wave: drag S and E (start, end) and, with a Forward or Ping-pong loop, L and L (loop start, end); " +
                          "the crossfade is shaded. Lines show where its voices are.",
            };
            ctx.Changed += mapView.InvalidateVisual;
            ctx.Changed += waveView.InvalidateVisual;

            // One panel of controls per zone; only the selected zone's is shown.
            var panels = new Grid();
            var panelOf = new UIElement[SamplerEngine.ZoneCount];
            for (int z = 0; z < SamplerEngine.ZoneCount; z++)
            {
                int Q(int f) => SamplerEngine.ZoneParam(z, f);
                var picker = new WavePicker(ctx, Q(SamplerEngine.FWave), machine, AccentColor);
                var load = new ActionButton("Load…", AccentColor, 64) { ToolTip = "Load an audio file into the song's wavetable (this zone's slot if it is empty, else the first empty one) and play it here" };
                int zone = z;
                load.Click += () => EditorFiles.Open(load, "Load a sample", "Audio (*.wav;*.aif;*.aiff;*.flac;*.ogg)|*.wav;*.aif;*.aiff;*.flac;*.ogg|All files (*.*)|*.*",
                    path => LoadInto(ctx, machine, zone, path));
                var panel = Layout.Column(
                    Layout.Row(picker, Layout.Gap(6), Layout.Column(Layout.Gap(1, 16), load), Layout.Gap(14),
                        new ParamKnob(ctx, Q(SamplerEngine.FKeyLo), "Key low", 40), new ParamKnob(ctx, Q(SamplerEngine.FKeyHi), "Key high", 40),
                        new ParamKnob(ctx, Q(SamplerEngine.FVelLo), "Vel low", 40), new ParamKnob(ctx, Q(SamplerEngine.FVelHi), "Vel high", 40),
                        new ParamKnob(ctx, Q(SamplerEngine.FRoot), "Root", 40), new ParamKnob(ctx, Q(SamplerEngine.FTune), "Tune", 40),
                        new ParamKnob(ctx, Q(SamplerEngine.FFine), "Fine", 40), new ParamKnob(ctx, Q(SamplerEngine.FLevel), "Level", 40),
                        new ParamKnob(ctx, Q(SamplerEngine.FPan), "Pan", 40), new ParamKnob(ctx, Q(SamplerEngine.FChoke), "Choke", 40)),
                    Layout.Gap(1, 4),
                    Layout.Row(
                        new ParamKnob(ctx, Q(SamplerEngine.FStart), "Start", 40), new ParamKnob(ctx, Q(SamplerEngine.FEnd), "End", 40),
                        Layout.Gap(8),
                        new ParamChoice(ctx, Q(SamplerEngine.FLoop), "Loop", 64),
                        Layout.Gap(8),
                        new ParamKnob(ctx, Q(SamplerEngine.FLoopS), "Loop start", 40), new ParamKnob(ctx, Q(SamplerEngine.FLoopE), "Loop end", 40),
                        new ParamKnob(ctx, Q(SamplerEngine.FXfade), "Crossfade", 40),
                        Layout.Gap(8),
                        new ParamChoice(ctx, Q(SamplerEngine.FTrig), "Trigger", 64),
                        Layout.Gap(8),
                        new ParamChoice(ctx, Q(SamplerEngine.FRev), "Reverse", 40)));
                panel.Visibility = z == map.Selected ? Visibility.Visible : Visibility.Collapsed;
                panelOf[z] = panel;
                panels.Children.Add(panel);
            }
            var tabs = new ZoneTabs(ctx, map);
            map.SelectedChanged += sel =>
            {
                for (int z = 0; z < panelOf.Length; z++) panelOf[z].Visibility = z == sel ? Visibility.Visible : Visibility.Collapsed;
                tabs.InvalidateVisual();
                mapView.InvalidateVisual();
                waveView.InvalidateVisual();
            };

            var play = Layout.Column(
                new ParamChoice(ctx, P.Mode, "Mode", 76),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.KitBase, "Kit base", 40), new ParamKnob(ctx, P.Polyphony, "Voices", 40), new ParamKnob(ctx, P.BendRange, "Bend", 40)),
                Layout.Row(new ParamKnob(ctx, P.VelAmp, "Vel → amp", 40), new ParamKnob(ctx, P.VelXfade, "Vel fade", 40)));

            var amp = Layout.Column(
                Build.Envelope(ctx, P.AmpA, P.AmpD, P.AmpS, P.AmpR, 170, 54),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.AmpA, "A", 38), new ParamKnob(ctx, P.AmpD, "D", 38), new ParamKnob(ctx, P.AmpS, "S", 38), new ParamKnob(ctx, P.AmpR, "R", 38)));

            var filter = Layout.Column(
                new ParamDropdown(ctx, P.FType, "Filter", 120),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.FCutoff, "Cutoff", 40), new ParamKnob(ctx, P.FReso, "Reso", 40), new ParamKnob(ctx, P.FEnv, "Env", 40)),
                Layout.Row(new ParamKnob(ctx, P.FKey, "Key", 40), new ParamKnob(ctx, P.FVel, "Vel", 40)));

            var fenv = Layout.Column(
                Build.Envelope(ctx, P.FltA, P.FltD, P.FltS, P.FltR, 170, 54),
                Layout.Gap(1, 4),
                Layout.Row(new ParamKnob(ctx, P.FltA, "A", 38), new ParamKnob(ctx, P.FltD, "D", 38), new ParamKnob(ctx, P.FltS, "S", 38), new ParamKnob(ctx, P.FltR, "R", 38)));

            var output = Layout.Row(
                new ParamKnob(ctx, P.Volume, "Volume", 50),
                Layout.Gap(6),
                new Meter("Output", () => machine?.Engine.OutMeter.Peak ?? 0, AccentColor, 150));

            return Layout.Column(
                Layout.Row(
                    new Section("Zones", AccentColor, mapView),
                    new Section("Wave", AccentColor, waveView)),
                new Section("Zone", AccentColor, Layout.Column(tabs, Layout.Gap(1, 4), panels)),
                Layout.Row(
                    new Section("Play", AccentColor, play),
                    new Section("Amp", AccentColor, amp),
                    new Section("Filter", AccentColor, filter),
                    new Section("Filter env", AccentColor, fenv),
                    new Section("Output", AccentColor, output)));
        }

        /// <summary>Loads a file into the song's wavetable and points the zone at it (the UI thread, which owns the song).</summary>
        static void LoadInto(EditorContext ctx, NeoSamplerMachine machine, int zone, string path)
        {
            try
            {
                var table = machine?.Wavetable;
                if (table == null) return;
                int param = SamplerEngine.ZoneParam(zone, SamplerEngine.FWave);
                int slot = ctx.Raw(param);
                var waves = table.Waves;
                bool emptyHere = slot >= 1 && slot <= waves.Count && (waves[slot - 1] == null || waves[slot - 1].Layers.Count == 0);
                if (!emptyHere)
                {
                    slot = -1;
                    for (int i = 0; i < waves.Count; i++)
                        if (waves[i] == null) { slot = i + 1; break; }
                    if (slot < 0)
                    {
                        MessageBox.Show("The song's wavetable is full.", "Neo Sampler");
                        return;
                    }
                }
                table.LoadWave(slot - 1, path, Path.GetFileNameWithoutExtension(path), false);
                ctx.Set(param, slot);
                ctx.RaiseChanged();
            }
            catch (Exception e)
            {
                MessageBox.Show($"Couldn't load {Path.GetFileName(path)}: {e.Message}", "Neo Sampler");
            }
        }

        /// <summary>
        /// A zone's wave: its name, stepped (arrows, or the wheel) through Off, the song's filled
        /// slots and the built-in samples.
        /// </summary>
        sealed class WavePicker : FrameworkElement
        {
            const double W = 240, Arrow = 22;
            readonly NeoSamplerMachine machine;
            readonly NeoColor accent;
            readonly WpfCanvas canvas;
            readonly ParamBinding binding;

            public WavePicker(EditorContext ctx, int index, NeoSamplerMachine machine, NeoColor accent)
            {
                this.machine = machine;
                this.accent = accent;
                canvas = new WpfCanvas(this);
                Width = W;
                Height = DropdownFace.Height;
                binding = ctx.Bind(index, _ => InvalidateVisual());
                ToolTip = "The zone's wave: arrows (or the wheel) step through Off, the song's waves and the built-in samples";
                Cursor = Cursors.Hand;
            }

            /// <summary>The waves to step through: Off, the song's filled slots, the built-ins.</summary>
            int[] Choices()
            {
                var list = new System.Collections.Generic.List<int> { 0 };
                try
                {
                    var waves = machine?.Wavetable?.Waves;
                    if (waves != null)
                        for (int i = 0; i < waves.Count && i < SampleBank.SongSlots; i++)
                            if (waves[i] != null && waves[i].Layers.Count > 0) list.Add(i + 1);
                }
                catch (Exception) { }
                for (int n = 1; n <= BuiltIns.Count; n++) list.Add(SampleBank.SongSlots + n);
                return list.ToArray();
            }

            void Step(int dir)
            {
                var choices = Choices();
                int now = binding.Value;
                int i = Array.IndexOf(choices, now);
                if (i < 0)
                {
                    // Not in the list (an empty slot): the next one on that side.
                    i = dir > 0 ? Array.FindIndex(choices, c => c > now) - 1 : Array.FindLastIndex(choices, c => c < now) + 1;
                    if (i < -1) i = choices.Length - 1;
                }
                i = Math.Clamp(i + dir, 0, choices.Length - 1);
                binding.Set(choices[i]);
                InvalidateVisual();
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                double x = e.GetPosition(this).X;
                Step(x < Arrow ? -1 : 1);
                e.Handled = true;
            }

            protected override void OnMouseWheel(MouseWheelEventArgs e)
            {
                Step(e.Delta > 0 ? -1 : 1);
                e.Handled = true;
            }

            protected override void OnRender(DrawingContext dc)
            {
                var c = canvas.On(dc);
                c.Text("Wave", 1, 0, 11, Palette.DimText);
                var r = new NeoRect(0, 18, W, 22);
                c.Rect(r, Palette.Background, new NeoStroke(Palette.PanelEdge, 1), 3);
                c.Text("◀", Arrow / 2, 21, 11, accent, align: NeoAlign.Center);
                c.Text("▶", W - Arrow / 2, 21, 11, accent, align: NeoAlign.Center);
                int raw = binding.Value;
                string text = raw <= 0 ? "Off" : raw <= SampleBank.SongSlots ? $"{raw}: {SamplerText.Wave(raw)}" : SamplerText.Wave(raw);
                c.Text(text, Arrow + 4, 21, 11.5, Palette.Text, maxWidth: W - 2 * Arrow - 8);
            }
        }

        /// <summary>The zone shown below: 16 tabs, each with its colour and whether it plays a wave.</summary>
        sealed class ZoneTabs : FrameworkElement
        {
            static readonly string[] Names = Enumerable.Range(1, SamplerEngine.ZoneCount).Select(i => i.ToString()).ToArray();
            readonly EditorContext ctx;
            readonly ZoneMap map;
            readonly WpfCanvas canvas;
            readonly ChoiceStrip strip;
            int hover = -1;

            public ZoneTabs(EditorContext ctx, ZoneMap map)
            {
                this.ctx = ctx;
                this.map = map;
                canvas = new WpfCanvas(this);
                strip = new ChoiceStrip("Zone", Names, 30, false, canvas);
                Width = strip.Width;
                Height = strip.Height + 10;
                Cursor = Cursors.Hand;
                ToolTip = "The zone shown below (also: click it in the zone map)";
                ctx.Changed += InvalidateVisual;
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                var p = e.GetPosition(this);
                int i = strip.CellAt(p.X, p.Y);
                if (i >= 0) map.Select(i);
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                var p = e.GetPosition(this);
                int i = strip.CellAt(p.X, p.Y);
                if (i == hover) return;
                hover = i;
                InvalidateVisual();
            }

            protected override void OnMouseLeave(MouseEventArgs e)
            {
                hover = -1;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc)
            {
                var c = canvas.On(dc);
                strip.Draw(c, map.Selected, hover, AccentColor);
                for (int i = 0; i < Names.Length; i++)
                {
                    var cell = strip.Cell(i);
                    var colour = ZoneColors.Of(i);
                    var centre = new NeoPoint(cell.X + cell.Width / 2, cell.Bottom + 5);
                    bool on = ctx.Raw(SamplerEngine.ZoneParam(i, SamplerEngine.FWave)) > 0;
                    c.Ellipse(centre, 3, 3, on ? (NeoFill)colour : NeoFill.None, new NeoStroke(colour, 1.2));
                }
            }
        }
    }
}
