// Fork (docs/specs/studio-mvp.md S0): play, stop, record, loop, tempo and where the song is; the
// master meter with its clip indicator and limiting lamp (machine-safety.md §3.3, MasterMeter).
// Fork (ux-overhaul.md §3.1, §4): Play lights in the play green and Record in the record red (both lit the same blue,
// gallery mac-071/072), Loop and the metronome lit plainly when on; go to start and the metronome are here; Record
// lights whenever anything records (the Session View's session record too); the tooltips name the keys, from the keymap.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;
using ReBuzz.Core.Actions;
using ReBuzz.Studio.Shell.Commands;
using ReBuzz.Studio.Shell.Design;
using BuzzCommand = BuzzGUI.Interfaces.BuzzCommand;

namespace ReBuzz.Studio.Shell
{
    internal sealed class TransportBar : Border
    {
        readonly StudioHost host;
        readonly Button start = new Button { Content = "⏮", MinWidth = 40 };
        readonly Button play = new Button { Content = "▶", MinWidth = 40 };
        readonly Button stop = new Button { Content = "■", MinWidth = 40 };
        readonly ToggleButton record = new ToggleButton { Content = "●", MinWidth = 40 };
        readonly ToggleButton loop = new ToggleButton { Content = "Loop" };
        readonly ToggleButton metronome = new ToggleButton { Content = "♩", MinWidth = 40 };
        readonly NumericUpDown bpm = new NumericUpDown { Minimum = 16, Maximum = 500, Increment = 1, FormatString = "0", Width = 120 };
        readonly TextBlock position = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = StudioTokens.FontTitle, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        readonly MasterMeter meter;
        bool updating;

        public TransportBar(StudioHost host)
        {
            this.host = host;
            meter = new MasterMeter(() => host.Core) { Margin = new Thickness(16, 0, 0, 0) };
            Padding = new Thickness(8, 4);
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(0, 0, 0, 1);
            ToolTip.SetTip(start, StudioKeymap.Tip("transport.start"));
            ToolTip.SetTip(play, StudioKeymap.Tip("transport.play", "Space plays and stops in every view"));
            ToolTip.SetTip(stop, StudioKeymap.Tip("transport.stop", "Space plays and stops in every view"));
            ToolTip.SetTip(record, StudioKeymap.Tip("transport.record"));
            ToolTip.SetTip(loop, StudioKeymap.Tip("transport.loop"));
            ToolTip.SetTip(metronome, StudioKeymap.Tip("transport.metronome"));
            ToolTip.SetTip(position, "Where the song is: bar.beat.tick");
            Lit(play, "AccentButton", StudioTokens.Play);
            Lit(record, "ToggleButton", StudioTokens.Record, checkedOnly: true);
            Lit(loop, "ToggleButton", StudioTokens.TextPrimary, checkedOnly: true);
            Lit(metronome, "ToggleButton", StudioTokens.TextPrimary, checkedOnly: true);
            start.Click += (s, e) => GoToStart();
            play.Click += (s, e) => Play();
            stop.Click += (s, e) => Stop();
            record.Click += (s, e) => { if (!updating) ToggleRecord(); };
            loop.Click += (s, e) => { if (!updating && Core != null) TransportEdits.SetLooping(Core, loop.IsChecked == true); };
            metronome.Click += (s, e) => { if (!updating) SetMetronome(metronome.IsChecked == true); };
            bpm.ValueChanged += (s, e) =>
            {
                if (updating || Core == null || e.NewValue is not decimal v) return;
                int value = (int)Math.Round(v);
                if (value != Core.BPM) TransportEdits.SetTempo(Core, value);
            };
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    start, play, stop, record, loop, metronome,
                    new TextBlock { Text = "BPM", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) },
                    bpm,
                    new TextBlock { Text = "Position", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) },
                    position,
                    meter,
                },
            };
        }

        /// <summary>
        /// A button's lit look in a state colour: the Fluent theme's brushes for it (its checked or accent state), on this
        /// control only. The glyph is black or white, whichever reads better on the colour.
        /// </summary>
        static void Lit(Avalonia.Controls.Control c, string kind, Color colour, bool checkedOnly = false)
        {
            var fill = StudioTokens.Brush(colour);
            var hover = StudioTokens.Brush(Lighten(colour, 0.12));
            var text = StudioTokens.TextOnBrush(colour);
            string state = checkedOnly ? "Checked" : "";
            c.Resources[$"{kind}Background{state}"] = fill;
            c.Resources[$"{kind}Background{state}PointerOver"] = hover;
            c.Resources[$"{kind}Background{state}Pressed"] = fill;
            c.Resources[$"{kind}Foreground{state}"] = text;
            c.Resources[$"{kind}Foreground{state}PointerOver"] = text;
            c.Resources[$"{kind}Foreground{state}Pressed"] = text;
            c.Resources[$"{kind}BorderBrush{state}"] = fill;
            c.Resources[$"{kind}BorderBrush{state}PointerOver"] = hover;
            c.Resources[$"{kind}BorderBrush{state}Pressed"] = fill;
        }

        static Color Lighten(Color c, double amount)
        {
            byte L(byte v) => (byte)Math.Round(v + (255 - v) * amount);
            return Color.FromRgb(L(c.R), L(c.G), L(c.B));
        }

        ReBuzzCore Core => host.Core;

        public void Play()
        {
            if (Core != null && !Core.PlayRequested) Core.Playing = true;
        }

        public void Stop() => Core?.ExecuteCommand(BuzzCommand.Stop);

        public void ToggleRecord()
        {
            if (Core != null) Core.Recording = !Core.Recording;
        }

        public void ToggleLoop()
        {
            if (Core != null) TransportEdits.SetLooping(Core, !Core.Looping);
        }

        /// <summary>The metronome (the session's click on every beat, heard and never recorded).</summary>
        public bool MetronomeOn => Core?.SessionLayout?.Document?.Metronome == true;

        public void SetMetronome(bool on) => Core?.SessionLayout?.SetMetronome(on);

        public void ToggleMetronome() => SetMetronome(!MetronomeOn);

        /// <summary>Go to Start: the song position to its start (the loop start when looping), playing or not.</summary>
        public void GoToStart()
        {
            var core = Core;
            if (core?.SongCore == null) return;
            core.SongCore.PlayPosition = core.Looping ? core.SongCore.LoopStart : 0;
        }

        /// <summary>Whether anything records now: the arrangement's Record, or the Session View's session record.</summary>
        public bool Recording => Core is { } c && (c.Recording || c.SessionLayout?.IsSessionRecording == true);

        /// <summary>The transport's state now (the main window calls it ten times a second).</summary>
        public void Refresh()
        {
            var core = Core;
            if (core == null) return;
            updating = true;
            try
            {
                play.Classes.Set("accent", core.PlayRequested);
                record.IsChecked = Recording;
                loop.IsChecked = core.Looping;
                metronome.IsChecked = MetronomeOn;
                if (!bpm.IsFocused) bpm.Value = core.BPM;
                int tpb = Math.Max(1, core.TPB);
                int beatsPerBar = Math.Max(1, core.SessionCore?.BeatsPerBar ?? 4);
                int ticks = core.SongCore.PlayPosition;
                int beat = ticks / tpb;
                position.Text = $"{beat / beatsPerBar + 1}.{beat % beatsPerBar + 1}.{ticks % tpb + 1}";
                meter.Refresh();
            }
            finally
            {
                updating = false;
            }
        }

        /// <summary>The position's text now (for tests).</summary>
        public string PositionText => position.Text;

        /// <summary>The master meter, clip indicator and limiting lamp.</summary>
        public MasterMeter Meter => meter;

        internal Button PlayButton => play;
        internal ToggleButton RecordButton => record;
        internal ToggleButton LoopButton => loop;
        internal ToggleButton MetronomeButton => metronome;
        internal Button StartButton => start;
    }
}
