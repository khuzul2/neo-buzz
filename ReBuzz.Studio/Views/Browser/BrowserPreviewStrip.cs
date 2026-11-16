// Fork (docs/specs/browser.md, BR1): the browser's preview strip: the speaker toggle ("Listen": a selected sample plays), Sync (a loop plays
// in time with the song while the transport runs, raw otherwise), the waveform with its playhead, and the length, rate and tempo.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Browser
{
    /// <summary>The waveform, as bars of peaks, with the playhead.</summary>
    internal sealed class WaveStrip : Avalonia.Controls.Control
    {
        float[] peaks = Array.Empty<float>();
        double fraction;

        public float[] Peaks
        {
            get => peaks;
            set { peaks = value ?? Array.Empty<float>(); InvalidateVisual(); }
        }

        public double Fraction
        {
            get => fraction;
            set { if (Math.Abs(value - fraction) > 0.002) { fraction = value; InvalidateVisual(); } }
        }

        public override void Render(DrawingContext context)
        {
            var r = new Rect(Bounds.Size);
            context.DrawRectangle(StudioTokens.SurfaceViewBrush, null, r, 3, 3);
            if (peaks.Length == 0 || r.Width < 4) return;
            double mid = r.Height / 2;
            var pen = new Pen(StudioTokens.AccentBrush, 1);
            int cols = (int)Math.Max(1, r.Width - 4);
            for (int x = 0; x < cols; x++)
            {
                float p = peaks[(int)((long)x * peaks.Length / cols)];
                double h = Math.Max(1, p * (mid - 2));
                context.DrawLine(pen, new Point(2 + x + 0.5, mid - h), new Point(2 + x + 0.5, mid + h));
            }
            if (fraction > 0)
                context.DrawLine(new Pen(StudioTokens.TextPrimaryBrush, 1), new Point(2 + fraction * cols, 1), new Point(2 + fraction * cols, r.Height - 1));
        }
    }

    internal sealed class PreviewStrip : Border
    {
        readonly ToggleButton listen, sync;
        readonly WaveStrip wave = new WaveStrip { Height = 34, MinWidth = 40 };
        readonly TextBlock text = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
        readonly BrowserPreview preview = new BrowserPreview();
        readonly Func<ReBuzzCore> core;
        string current;

        public PreviewStrip(Func<ReBuzzCore> core)
        {
            this.core = core;
            listen = StudioButtons.KeepFocus(new ToggleButton { Content = "♪ Listen", IsChecked = false, Padding = new Thickness(8, 2), FontSize = 11 });
            sync = StudioButtons.KeepFocus(new ToggleButton { Content = "Sync", IsChecked = false, Padding = new Thickness(8, 2), FontSize = 11, Margin = new Thickness(4, 0, 0, 0) });
            AutomationProperties.SetName(listen, "Preview: listen to the selected sample");
            AutomationProperties.SetName(sync, "Preview: play loops in time with the song");
            AutomationProperties.SetName(wave, "Waveform");
            ToolTip.SetTip(listen, "Listen: a selected sample plays through the master");
            ToolTip.SetTip(sync, "Sync: a loop plays in time with the song while it plays (raw when it is stopped)");
            listen.IsCheckedChanged += (s, e) => { if (listen.IsChecked == true) Replay(); else preview.Stop(); Changed?.Invoke(); };
            sync.IsCheckedChanged += (s, e) => { if (listen.IsChecked == true) Replay(); Changed?.Invoke(); };
            var top = new StackPanel { Orientation = Orientation.Horizontal, Children = { listen, sync } };
            var stack = new StackPanel { Spacing = 3, Margin = new Thickness(8, 4, 8, 6), Children = { top, wave, text } };
            Child = stack;
            Background = StudioTokens.SurfacePanelBrush;
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(0, 1, 0, 0);
            AutomationProperties.SetName(this, "Preview");
        }

        public event Action Changed;

        public bool Listening { get => listen.IsChecked == true; set => listen.IsChecked = value; }
        public bool Sync { get => sync.IsChecked == true; set => sync.IsChecked = value; }
        public ToggleButton ListenButton => listen;
        public ToggleButton SyncButton => sync;
        public WaveStrip Wave => wave;
        public string Text => text.Text ?? "";
        public BrowserPreview Engine => preview;
        public string Current => current;

        /// <summary>Whether the last start played in time with the song.</summary>
        public bool PlayedInSync { get; private set; }

        /// <summary>Shows what a selection is: a sample (loaded, plays when Listen is on) or anything else (its detail, nothing to hear).</summary>
        public void Show(BrowserItem item, double knownBpm = 0)
        {
            if (item?.Path == null || item.Kind != ReBuzz.Agent.Browser.BrowserKind.Audio)
            {
                preview.Stop();
                current = null;
                wave.Peaks = Array.Empty<float>();
                wave.Fraction = 0;
                text.Text = item == null ? "Select a sample to see and hear it" : item.Kind == ReBuzz.Agent.Browser.BrowserKind.Midi ? "MIDI file: drag it to a track" : item.Detail.Length > 0 ? item.Detail : item.Name;
                return;
            }
            if (current != item.Path)
            {
                current = item.Path;
                if (!preview.Load(item.Path, knownBpm > 0 ? knownBpm : item.File?.Bpm ?? 0))
                {
                    current = null;
                    wave.Peaks = Array.Empty<float>();
                    text.Text = "Can't read " + System.IO.Path.GetFileName(item.Path);
                    return;
                }
                wave.Peaks = preview.Peaks(512);
            }
            text.Text = $"{preview.Seconds:0.0#} s · {preview.SampleRate / 1000.0:0.#} kHz · {(preview.Channels == 1 ? "mono" : preview.Channels == 2 ? "stereo" : preview.Channels + " ch")}" + (item.File?.Bpm > 0 ? $" · {item.File.Bpm:0.#} BPM" : "");
            if (Listening) Replay();
        }

        void Replay()
        {
            if (!preview.Loaded || current == null) return;
            PlayedInSync = preview.Play(core(), Sync, false);
            if (PlayedInSync && text.Text.Length > 0 && !text.Text.Contains("synced")) text.Text += $" · synced at {preview.Bpm:0.#} BPM";
        }

        public void Poll() => wave.Fraction = preview.IsPlaying ? preview.Fraction : 0;

        public void Stop() => preview.Stop();
    }
}
