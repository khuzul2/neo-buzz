// Fork (docs/specs/studio-mvp.md S1): the Neo editor kit's theme on Avalonia, for the elements
// editors are laid out with (panels, titles, text blocks), as Theme.cs is on WPF. Colours live in
// Core/Palette.cs; custom drawing goes through INeoCanvas, drawn by AvaloniaCanvas.cs.

using Avalonia.Media;

namespace NeoBuzz.Editor
{
    internal static class Theme
    {
        // Fork (ux-overhaul.md P4b): brushes that follow the theme (a view keeps them as its background or foreground).
        public static readonly IBrush Background = AvaloniaCanvas.Themed(() => Palette.Background);
        public static readonly IBrush Panel = AvaloniaCanvas.Themed(() => Palette.Panel);
        public static readonly IBrush PanelEdge = AvaloniaCanvas.Themed(() => Palette.PanelEdge);
        public static readonly IBrush Text = AvaloniaCanvas.Themed(() => Palette.Text);
        public static readonly IBrush DimText = AvaloniaCanvas.Themed(() => Palette.DimText);

        public static readonly FontFamily FontFamily = AvaloniaCanvas.Font;

        /// <summary>A brush for a kit colour a view keeps: it follows the theme.</summary>
        public static IBrush Brush(NeoColor c) => AvaloniaCanvas.Themed(c);
    }
}
