using System;

namespace ReBuzz.Core
{
    /// <summary>
    /// A bitmap as plain bytes (engine-split.md S4): native machine skins and LEDs. 32 bits per
    /// pixel in B, G, R, X order, top row first; the 4th byte is ignored (the image is opaque), as
    /// GDI+'s Format32bppRgb did. The UI makes its own image from it once (<see cref="View"/>).
    /// </summary>
    public sealed class RawImage
    {
        public RawImage(int width, int height, byte[] bgr32)
        {
            Width = width;
            Height = height;
            Bgr32 = bgr32;
        }

        public int Width { get; }
        public int Height { get; }
        public byte[] Bgr32 { get; }

        /// <summary>Makes the UI's image of a RawImage (a frozen BitmapSource in the WPF app); unset headless.</summary>
        public static Func<RawImage, object> Viewer { get; set; }

        object view;

        /// <summary>The UI's image of this one, made on first use; null when no UI set <see cref="Viewer"/>.</summary>
        public object View => view ??= Viewer?.Invoke(this);
    }

    /// <summary>A size in pixels with WPF Size's property names, so theme XAML that binds SkinLEDSize.Width keeps working.</summary>
    public readonly record struct SkinSize(double Width, double Height);

    /// <summary>A position in pixels with WPF Point's property names (theme XAML binds SkinLEDPosition.X).</summary>
    public readonly record struct SkinPoint(double X, double Y);
}
