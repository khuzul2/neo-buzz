// Fork (docs/specs/studio-mvp.md S0): play, stop, record, loop, tempo and where the song is; the
// master meter with its clip indicator and limiting lamp (machine-safety.md §3.3, MasterMeter).
// Fork (ux-overhaul.md §3.1, §4): Play lights in the play green and Record in the record red (both lit the same blue,
// gallery mac-071/072), Loop and the metronome lit plainly when on; go to start and the metronome are here; Record
// lights whenever anything records (the Session View's session record too); the tooltips name the keys, from the keymap.
// Fork (ux-overhaul.md P3g, decision 80): the one global transport: the metronome, the time signature, the tempo (drag it), Tap, where the song is as
// bar.beat.tick and as minutes and seconds, and the master meter. The Session View has no transport of its own any more.

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
        /// <summary>A horizontal group that wraps as one.</summary>
        sealed class Group : StackPanel { }

        readonly StudioHost host;
        readonly Button start = new Button { Content = "⏮" };
        readonly Button play = new Button { Content = "▶" };
        readonly Button stop = new Button { Content = "■" };
        readonly ToggleButton record = new ToggleButton { Content = "●" };
        readonly ToggleButton loop = new ToggleButton { Content = "Loop" };
        readonly ToggleButton metronome = new ToggleButton { Content = "♩" };
        readonly TempoField bpm = new TempoField { VerticalAlignment = VerticalAlignment.Center };
        readonly Button tap = new Button { Content = "Tap", MinWidth = 48 };
        readonly ComboBox signature = new ComboBox { Width = 76, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock position = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = StudioTokens.FontTitle, VerticalAlignment = VerticalAlignment.Center, MinWidth = 84 };
        readonly TextBlock minutes = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = StudioTokens.FontTitle, VerticalAlignment = VerticalAlignment.Center, MinWidth = 52, Foreground = StudioTokens.TextSecondaryBrush };
        readonly MasterMeter meter;
        bool updating;

        public TransportBar(StudioHost host)
        {
            this.host = host;
            meter = new MasterMeter(() => host.Core) { Margin = new Thickness(0, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
            Padding = new Thickness(8, 4);
            for (int i = 1; i <= 12; i++) signature.Items.Add($"{i}/4");
            Avalonia.Automation.AutomationProperties.SetName(signature, "Time signature");
            Avalonia.Automation.AutomationProperties.SetName(tap, "Tap tempo");
            // Fork (ux-overhaul.md P4a): the transport's buttons are the one "large" size, answer at once (no transition: they are the
            // audio path's), and the glyph ones are named for a screen reader.
            foreach (var (b, name) in new (Button, string)[] { (start, "Go to start"), (play, "Play"), (stop, "Stop"), (record, "Record"), (loop, "Loop"), (metronome, "Metronome"), (tap, "Tap tempo") })
            {
                b.Classes.Add("large");
                b.Classes.Add("instant");
                Avalonia.Automation.AutomationProperties.SetName(b, name);
            }
            Avalonia.Automation.AutomationProperties.SetName(minutes, "Minutes and seconds");
            ToolTip.SetTip(signature, "Time signature: beats in a bar (the bar lines of the Session View's grid and the transport's clock; the Tracker counts ticks per beat).");
            ToolTip.SetTip(tap, "Tap tempo: tap the beat; from the third tap the tempo follows the last four. (Right-click it in the Session View's ⚙ menu to MIDI-learn it.)");
            ToolTip.SetTip(minutes, "Where the song is in minutes and seconds, at the current tempo");
            signature.SelectionChanged += (s, e) =>
            {
                if (updating || Core?.SessionLayout is not { } layout || signature.SelectedIndex < 0) return;
                if (signature.SelectedIndex + 1 != Math.Max(1, Core.SessionCore?.BeatsPerBar ?? 4)) layout.SetBeatsPerBar(signature.SelectedIndex + 1);
            };
            tap.Click += (s, e) => Core?.SessionLayout?.TapTempo();
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(0, 0, 0, 1);
            ToolTip.SetTip(start, StudioKeymap.Tip("transport.start"));
            ToolTip.SetTip(play, StudioKeymap.Tip("transport.play", "Space plays and stops in every view"));
            ToolTip.SetTip(stop, StudioKeymap.Tip("transport.stop", "Space plays and stops in every view"));
            ToolTip.SetTip(record, StudioKeymap.Tip("transport.record"));
            ToolTip.SetTip(loop, StudioKeymap.Tip("transport.loop"));
            ToolTip.SetTip(metronome, StudioKeymap.Tip("transport.metronome"));
            ToolTip.SetTip(position, "Where the song is: bar.beat.tick");
            Lit(play, "AccentButton", () => StudioTokens.Play);
            Lit(record, "ToggleButton", () => StudioTokens.Record, checkedOnly: true);
            Lit(loop, "ToggleButton", () => StudioTokens.TextPrimary, checkedOnly: true);
            Lit(metronome, "ToggleButton", () => StudioTokens.TextPrimary, checkedOnly: true);
            start.Click += (s, e) => GoToStart();
            play.Click += (s, e) => Play();
            stop.Click += (s, e) => Stop();
            record.Click += (s, e) => { if (!updating) ToggleRecord(); };
            loop.Click += (s, e) => { if (!updating && Core != null) TransportEdits.SetLooping(Core, loop.IsChecked == true); };
            metronome.Click += (s, e) => { if (!updating) SetMetronome(metronome.IsChecked == true); };
            bpm.Edited += value =>
            {
                if (!updating && Core != null && value != Core.BPM) TransportEdits.SetTempo(Core, value);
            };
            // The groups, the meter last, wrap whole when the window is narrow.
            Group Pack(Thickness margin, params Avalonia.Controls.Control[] items)
            {
                var g = new Group { Orientation = Orientation.Horizontal, Spacing = 6, Margin = margin, VerticalAlignment = VerticalAlignment.Center };
                foreach (var item in items) g.Children.Add(item);
                return g;
            }
            var left = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(Pack(new Thickness(0, 2, 16, 2), start, play, stop, record, loop));
            left.Children.Add(Pack(new Thickness(0, 2, 16, 2), metronome, signature, bpm,
                new TextBlock { Text = "BPM", VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush }, tap));
            left.Children.Add(Pack(new Thickness(0, 2, 16, 2), position, minutes));
            left.Children.Add(meter);
            Child = left;
        }

        /// <summary>
        /// A button's lit look in a state colour: the Fluent theme's brushes for it (its checked or accent state), on this
        /// control only. The glyph is black or white, whichever reads better on the colour.
        /// </summary>
        static void Lit(Avalonia.Controls.Control c, string kind, Func<Color> colour, bool checkedOnly = false)
        {
            // The brushes follow the theme (the colour is the token's in the set in use; the glyph reads on it).
            var fill = StudioTokens.Themed(colour);
            var hover = StudioTokens.Themed(() => Lighten(colour(), 0.12));
            var text = StudioTokens.Themed(() => StudioTokens.TextOn(colour()));
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
                if (!bpm.Busy) bpm.Value = core.BPM;
                int beatsPerBar = Math.Max(1, core.SessionCore?.BeatsPerBar ?? 4);
                if (!signature.IsDropDownOpen && signature.SelectedIndex != Math.Min(beatsPerBar, 12) - 1) signature.SelectedIndex = Math.Min(beatsPerBar, 12) - 1;
                position.Text = PositionOf(core);
                minutes.Text = TimeText.Clock(core.SongCore.PlayPosition, core.TPB, core.BPM);
                meter.Refresh();
            }
            finally
            {
                updating = false;
            }
        }

        /// <summary>
        /// Where the music is as bar.beat.tick: the Session View's clock while it plays (bars count on from where a scene last changed the time
        /// signature, 0.5), else the song position. What the Session View's own bar showed before there was one transport.
        /// </summary>
        internal static string PositionOf(ReBuzzCore core)
        {
            int tpb = Math.Max(1, core.TPB);
            var session = core.SessionCore;
            long clock = session?.Clock ?? -1;
            if (session == null || clock < 0) return TimeText.Position(core.SongCore.PlayPosition, tpb, session?.BeatsPerBar ?? 4);
            int bpb = Math.Max(1, session.BeatsPerBar);
            long rel = clock - session.GridOrigin, originBar = session.GridOriginBar;
            if (rel < 0) { rel = clock; originBar = 0; }
            return $"{originBar + rel / (tpb * bpb) + 1}.{rel / tpb % bpb + 1}.{rel % tpb + 1}";
        }

        /// <summary>The position's text now (for tests).</summary>
        public string PositionText => position.Text;

        /// <summary>The minutes and seconds (tests).</summary>
        public string MinutesText => minutes.Text;

        /// <summary>The tempo's field, the time signature's box and the Tap button (tests).</summary>
        internal TempoField Tempo => bpm;
        internal ComboBox SignatureBox => signature;
        internal Button TapButton => tap;

        /// <summary>The master meter, clip indicator and limiting lamp.</summary>
        public MasterMeter Meter => meter;

        internal Button PlayButton => play;
        internal ToggleButton RecordButton => record;
        internal ToggleButton LoopButton => loop;
        internal ToggleButton MetronomeButton => metronome;
        internal Button StartButton => start;
    }
}
