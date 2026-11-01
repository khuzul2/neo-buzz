#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): the theme for the WPF elements editors are
// laid out with (panels, titles, text blocks). Colours live in Core/Palette.cs; custom drawing
// goes through INeoCanvas (Core/), drawn by WpfCanvas.cs. Code-only WPF (no XAML), linked as
// source into each machine.

using System.Windows.Media;

namespace NeoBuzz.Editor
{
    internal static class Theme
    {
        public static readonly Brush Background = Brush(Palette.Background);
        public static readonly Brush Panel = Brush(Palette.Panel);
        public static readonly Brush PanelEdge = Brush(Palette.PanelEdge);
        public static readonly Brush Text = Brush(Palette.Text);
        public static readonly Brush DimText = Brush(Palette.DimText);

        public static readonly FontFamily FontFamily = new FontFamily(Palette.FontFamily);

        /// <summary>A frozen WPF brush for a kit colour.</summary>
        public static Brush Brush(NeoColor c) => WpfCanvas.Brush(c);
    }
}
