// Fork (docs/specs/studio-mvp.md S1): the Neo editor kit's theme on Avalonia, for the elements
// editors are laid out with (panels, titles, text blocks), as Theme.cs is on WPF. Colours live in
// Core/Palette.cs; custom drawing goes through INeoCanvas, drawn by AvaloniaCanvas.cs.

using Avalonia.Media;

namespace NeoBuzz.Editor
{
    internal static class Theme
    {
        public static readonly IBrush Background = Brush(Palette.Background);
        public static readonly IBrush Panel = Brush(Palette.Panel);
        public static readonly IBrush PanelEdge = Brush(Palette.PanelEdge);
        public static readonly IBrush Text = Brush(Palette.Text);
        public static readonly IBrush DimText = Brush(Palette.DimText);

        public static readonly FontFamily FontFamily = AvaloniaCanvas.Font;

        /// <summary>An immutable brush for a kit colour.</summary>
        public static IBrush Brush(NeoColor c) => AvaloniaCanvas.Brush(c);
    }
}
