// Fork (docs/specs/ux-overhaul.md P5b, the Samples view): a wave drawn as its outline (the loudest and quietest sample of each column),
// the loop shaded between its points, and a line where the preview is. Read-only: the loop points are the numbers beside it.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Samples
{
    using Control = Avalonia.Controls.Control;

    internal sealed class WaveformView : Control
    {
        const int Bins = 1024;
        float[] lo = Array.Empty<float>(), hi = Array.Empty<float>();
        int count, loopStart, loopEnd;
        bool loop;
        double playhead = -1;

        public WaveformView()
        {
            Height = 140;
            MinWidth = 200;
            ClipToBounds = true;
            Focusable = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Waveform");
        }

        /// <summary>The columns drawn as (lowest, highest) per bin, for tests.</summary>
        internal int Peaks => lo.Length;

        /// <summary>Whether the wave has any sound in it (a column away from zero).</summary>
        internal bool HasSignal
        {
            get
            {
                for (int i = 0; i < lo.Length; i++) if (hi[i] > 0.001f || lo[i] < -0.001f) return true;
                return false;
            }
        }

        public void Show(IWaveLayer layer, bool loopOn)
        {
            if (layer == null || layer.SampleCount <= 0)
            {
                lo = hi = Array.Empty<float>();
                count = 0;
            }
            else
            {
                count = layer.SampleCount;
                int bins = Math.Min(Bins, count);
                lo = new float[bins];
                hi = new float[bins];
                var buffer = new float[Math.Max(1, (int)Math.Ceiling(count / (double)bins))];
                int channels = Math.Max(1, layer.ChannelCount);
                for (int b = 0; b < bins; b++)
                {
                    int from = (int)((long)b * count / bins), to = (int)((long)(b + 1) * count / bins);
                    int n = Math.Max(1, Math.Min(buffer.Length, to - from));
                    float min = 0, max = 0;
                    try
                    {
                        for (int ch = 0; ch < channels; ch++)
                        {
                            layer.GetDataAsFloat(buffer, 0, 1, ch, from, n);
                            for (int i = 0; i < n; i++) { if (buffer[i] < min) min = buffer[i]; if (buffer[i] > max) max = buffer[i]; }
                        }
                    }
                    catch (Exception) { }
                    lo[b] = min;
                    hi[b] = max;
                }
                loopStart = layer.LoopStart;
                loopEnd = layer.LoopEnd;
            }
            loop = loopOn && loopEnd > loopStart;
            InvalidateVisual();
        }

        /// <summary>Moves the loop shading (the points in samples).</summary>
        public void SetLoop(bool on, int start, int end)
        {
            loop = on && end > start;
            loopStart = start;
            loopEnd = end;
            InvalidateVisual();
        }

        /// <summary>Where the preview is, 0 to 1 of the wave; negative: not playing.</summary>
        public double Playhead
        {
            get => playhead;
            set { playhead = value; InvalidateVisual(); }
        }

        public override void Render(DrawingContext context)
        {
            var b = new Rect(Bounds.Size);
            context.FillRectangle(StudioTokens.SurfaceBaseBrush, b);
            double mid = b.Height / 2;
            context.FillRectangle(StudioTokens.SurfaceBorderBrush, new Rect(0, mid, b.Width, 1));
            if (count <= 0 || lo.Length == 0)
            {
                var t = new FormattedText("Empty", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, StudioTokens.FontBody, StudioTokens.TextSecondaryBrush);
                context.DrawText(t, new Point((b.Width - t.Width) / 2, mid - t.Height / 2));
                return;
            }
            if (loop)
            {
                double x0 = loopStart / (double)count * b.Width, x1 = loopEnd / (double)count * b.Width;
                var accent = StudioTokens.Accent;
                context.FillRectangle(new SolidColorBrush(accent, 0.18), new Rect(x0, 0, Math.Max(1, x1 - x0), b.Height));
                context.FillRectangle(new SolidColorBrush(accent), new Rect(x0, 0, 1.5, b.Height));
                context.FillRectangle(new SolidColorBrush(accent), new Rect(Math.Max(0, x1 - 1.5), 0, 1.5, b.Height));
            }
            double half = mid - 2;
            for (int x = 0; x < (int)b.Width; x++)
            {
                int bin = Math.Min(lo.Length - 1, (int)((long)x * lo.Length / Math.Max(1, (int)b.Width)));
                double top = mid - Math.Clamp(hi[bin], 0, 1) * half, bottom = mid - Math.Clamp(lo[bin], -1, 0) * half;
                context.FillRectangle(StudioTokens.TextPrimaryBrush, new Rect(x, top, 1, Math.Max(1, bottom - top)));
            }
            if (playhead >= 0) context.FillRectangle(StudioTokens.AccentBrush, new Rect(Math.Clamp(playhead, 0, 1) * b.Width, 0, 1.5, b.Height));
        }
    }
}
