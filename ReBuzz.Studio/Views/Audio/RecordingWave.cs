// Fork (docs/specs/neo-audio.md, "Takes and comping"): the growing waveform in a Session slot while an audio take records. It draws the peaks the audio
// thread keeps (AudioCapture.PeakAt), the newest on the right, and allocates nothing per frame.

using Avalonia;
using Avalonia.Media;
using ReBuzz.Core.Clips;

namespace ReBuzz.Studio.Views.Audio
{
    internal sealed class RecordingWave : Avalonia.Controls.Control
    {
        const double Column = 2;
        Pen pen;

        public RecordingWave() { IsHitTestVisible = false; }

        /// <summary>PL1: draw in the lower half of the control (the slot name reads above it).</summary>
        public bool LowerHalf { get; set; }

        /// <summary>The take's capture while it records (null: nothing to draw).</summary>
        public AudioCapture Source { get; set; }

        /// <summary>For tests: how many columns the last frame drew.</summary>
        internal int Columns { get; private set; }

        public override void Render(DrawingContext ctx)
        {
            Columns = 0;
            var cap = Source;
            double top = LowerHalf ? System.Math.Floor(Bounds.Height * 0.58) : 0;
            double w = Bounds.Width, h = Bounds.Height - top - 4;
            if (cap == null || w < 4 || h < 4) return;
            using var shift = ctx.PushTransform(Matrix.CreateTranslation(0, top));
            int count = cap.PeakCount;
            if (count <= 0) return;
            pen ??= new Pen(Brushes.White, 1.5);
            double mid = h / 2, half = h / 2 - 1;
            int cols = (int)(w / Column);
            for (int i = 0; i < cols; i++)
            {
                int b = count - cols + i;
                if (b < 0) continue;
                float peak = cap.PeakAt(b);
                double y = System.Math.Min(half, System.Math.Max(1, peak * half));
                double x = i * Column + 1;
                ctx.DrawLine(pen, new Point(x, mid - y), new Point(x, mid + y));
                Columns++;
            }
        }
    }
}
