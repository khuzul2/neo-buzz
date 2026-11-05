// Fork (docs/specs/studio-mvp.md S0): play, stop, record, loop, tempo and where the song is; the
// master meter with its clip indicator and limiting lamp (machine-safety.md §3.3, MasterMeter).

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;
using BuzzCommand = BuzzGUI.Interfaces.BuzzCommand;

namespace ReBuzz.Studio.Shell
{
    internal sealed class TransportBar : Border
    {
        readonly StudioHost host;
        readonly Button play = new Button { Content = "▶", MinWidth = 40 };
        readonly Button stop = new Button { Content = "■", MinWidth = 40 };
        readonly ToggleButton record = new ToggleButton { Content = "●", MinWidth = 40 };
        readonly ToggleButton loop = new ToggleButton { Content = "Loop" };
        readonly NumericUpDown bpm = new NumericUpDown { Minimum = 16, Maximum = 500, Increment = 1, FormatString = "0", Width = 120 };
        readonly TextBlock position = new TextBlock { FontFamily = new FontFamily("monospace"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center, MinWidth = 90 };
        readonly MasterMeter meter;
        bool updating;

        public TransportBar(StudioHost host)
        {
            this.host = host;
            meter = new MasterMeter(() => host.Core) { Margin = new Thickness(16, 0, 0, 0) };
            Padding = new Thickness(8, 4);
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
            BorderThickness = new Thickness(0, 0, 0, 1);
            ToolTip.SetTip(play, "Play (Space or F5)");
            ToolTip.SetTip(stop, "Stop (Space or F8). Stop or F8 again silences everything");
            ToolTip.SetTip(record, "Record (F7)");
            play.Click += (s, e) => Play();
            stop.Click += (s, e) => Stop();
            record.Click += (s, e) => { if (!updating) ToggleRecord(); };
            loop.Click += (s, e) => { if (!updating && Core != null) Core.Looping = loop.IsChecked == true; };
            bpm.ValueChanged += (s, e) =>
            {
                if (updating || Core == null || e.NewValue is not decimal v) return;
                int value = (int)Math.Round(v);
                if (value != Core.BPM) Core.BPM = value;
            };
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    play, stop, record, loop,
                    new TextBlock { Text = "BPM", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) },
                    bpm,
                    new TextBlock { Text = "Position", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) },
                    position,
                    meter,
                },
            };
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

        /// <summary>The transport's state now (the main window calls it ten times a second).</summary>
        public void Refresh()
        {
            var core = Core;
            if (core == null) return;
            updating = true;
            try
            {
                play.Classes.Set("accent", core.PlayRequested);
                record.IsChecked = core.Recording;
                loop.IsChecked = core.Looping;
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
    }
}
