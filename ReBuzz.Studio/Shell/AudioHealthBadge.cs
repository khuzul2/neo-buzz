// Fork (docs/specs/ux-overhaul.md P1, UX review mac-real-010): the audio's health at the start of the status line, in
// one look: green while it plays cleanly ("48 kHz · 5.3 ms"), amber when it dropped out lately (with the count), red
// when no output is open. A click opens Preferences ▸ Audio, where it is fixed. Dropouts were grey status text.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    internal sealed class AudioHealthBadge : Border
    {
        /// <summary>How long after a dropout the badge stays amber.</summary>
        public static readonly TimeSpan Recent = TimeSpan.FromSeconds(30);

        internal enum State { NoOutput, Dropouts, Good }

        static readonly IBrush Good = StudioTokens.PlayBrush;
        static readonly IBrush Warn = StudioTokens.Brush(StudioTokens.Warning);
        static readonly IBrush Bad = StudioTokens.RecordBrush;

        readonly Func<ReBuzzCore> core;
        readonly Ellipse dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        long seenDropouts = -1;
        DateTime lastDropout = DateTime.MinValue;

        /// <summary>Tests: the clock.</summary>
        internal Func<DateTime> Now { get; set; } = () => DateTime.UtcNow;

        public AudioHealthBadge(Func<ReBuzzCore> core, Action openAudioSettings)
        {
            this.core = core;
            Padding = new Thickness(8, 3);
            Margin = new Thickness(4, 0, 0, 0);
            Cursor = new Cursor(StandardCursorType.Hand);
            Background = Brushes.Transparent;
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, text } };
            PointerPressed += (s, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                openAudioSettings();
                e.Handled = true;
            };
        }

        public State Shown { get; private set; } = State.NoOutput;

        public string Text => text.Text;

        public void Refresh()
        {
            var c = core();
            if (c == null) return;
            long dropouts = ReBuzzCore.DeadlineMisses;
            if (seenDropouts >= 0 && dropouts > seenDropouts) lastDropout = Now();
            seenDropouts = dropouts;
            var device = c.AudioEngine?.SelectedOutDevice;
            string tip;
            if (device == null)
            {
                Shown = State.NoOutput;
                dot.Fill = Bad;
                text.Text = "No audio output";
                tip = "No audio output is open, so nothing is heard.\nClick: Preferences ▸ Audio, to pick an output.";
            }
            else
            {
                int rate = c.SelectedAudioDriverSampleRate;
                int frames = c.AudioEngine.CurrentDriver?.BufferSize ?? 0;
                string latency = rate > 0 && frames > 0 ? $"{frames * 1000.0 / rate:0.#} ms" : "";
                string rateText = rate > 0 ? $"{rate / 1000.0:0.#} kHz" : "";
                bool recent = Now() - lastDropout < Recent;
                Shown = recent ? State.Dropouts : State.Good;
                dot.Fill = recent ? Warn : Good;
                text.Text = recent
                    ? $"{dropouts} {(dropouts == 1 ? "dropout" : "dropouts")}"
                    : string.Join(" · ", new[] { rateText, latency }).Trim(' ', '·');
                if (text.Text.Length == 0) text.Text = "Audio on";
                tip = recent
                    ? $"The audio dropped out lately ({dropouts} since the start): the computer couldn't fill the buffer in time.\n" +
                      "A larger buffer gives it more time (more latency). Click: Preferences ▸ Audio."
                    : $"Playing through {device.Name}, {rateText}, {frames} frames ({latency}).\nClick: Preferences ▸ Audio.";
            }
            text.Foreground = StudioTokens.TextPrimaryBrush;
            ToolTip.SetTip(this, tip);
        }
    }
}
