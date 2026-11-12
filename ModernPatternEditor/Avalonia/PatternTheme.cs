// Fork (docs/specs/studio-mvp.md S6): the pattern editor's colours and font on Avalonia: the WPF
// editor's default theme (RunTime/Themes/Default/ModernPatternEditor/ModernPatternEditor.xaml, and the
// note colours of PatternEditor.xaml's HSPAColorProvider) as data, and its font metrics
// (ColumnRenderer/Font.cs) measured with Avalonia's text.

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using System.Globalization;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    // Avalonia's, not the portable editor's stand-in (PortableShims.cs).
    using Color = Avalonia.Media.Color;

    internal static class PatternTheme
    {
        static ImmutableSolidColorBrush B(uint argb) => new ImmutableSolidColorBrush(argb);

        public static readonly ImmutableSolidColorBrush Background = B(0xFF333333);            // PatEdBackgroundBrush
        public static readonly ImmutableSolidColorBrush ElementBackground = B(0xFF444444);     // PatEdElementBackgroundSolidColorBrush
        public static readonly ImmutableSolidColorBrush ElementBorder = B(0x88000000);         // PatEdBorderBrush (#8000)
        public static readonly ImmutableSolidColorBrush ToolbarBackground = B(0xFF595959);     // ToolbarBackgroundBrush
        public static readonly ImmutableSolidColorBrush BeatDark = B(0x50000000);              // ThemeColorOverride_PE_BG_Dark
        public static readonly ImmutableSolidColorBrush Beat = B(0x30000000);                  // ThemeColorOverride_PE_BG
        public static readonly ImmutableSolidColorBrush Text = B(0xFFDDDDDD);                  // ThemeColorOverride_PE_Text
        public static readonly ImmutableSolidColorBrush TextShadow = B(0xFF000000);            // TextDropShadowBrush
        public static readonly ImmutableSolidColorBrush NoValue = B(0x66CCCCCC);               // NoValueBrush (#6CCC)
        public static readonly ImmutableSolidColorBrush RowNumber = B(0xFFAAAAAA);             // RowNumberBrush
        public static readonly ImmutableSolidColorBrush RowNumberShadow = B(0xFF000000);       // RowNumberDropShadowBrush
        public static readonly ImmutableSolidColorBrush Cursor = B(0x22FFFFFF);                // CursorBrush (#2FFF)
        public static readonly ImmutableSolidColorBrush CursorBorder = B(0x88FFFFFF);          // CursorBorderBrush (#8FFF)
        public static readonly ImmutableSolidColorBrush CursorInactive = B(0x22000055);        // CursorInactiveBrush (#2005)
        public static readonly ImmutableSolidColorBrush CursorLine = B(0x2200FF00);            // CursorLineBrush (#20F0)
        public static readonly ImmutableSolidColorBrush ColumnSetLabel = B(0xFFCCCCCC);        // ColumnSetLabelBrush
        public static readonly ImmutableSolidColorBrush ColumnSetLabelMuted = B(0x4DCCCCCC);   // the same at 0.3 opacity
        public static readonly ImmutableSolidColorBrush ColumnLabel = B(0xFFCCCCCC);           // ColumnLabelBrush
        // Fork (ux-overhaul.md P3c): the beat and bar rules, 3.4:1 and 7.4:1 against the lighter of the two beat shades (#373737).
        public static readonly ImmutableSolidColorBrush BeatRule = B(0xFF888888);
        public static readonly ImmutableSolidColorBrush BarRule = B(0xFFCCCCCC);
        public static readonly ImmutableSolidColorBrush LabelHover = B(0x28000000);            // PatternControl's highlight
        public static readonly ImmutableSolidColorBrush PlayPosition = B(0x40000000);          // playPosRectangle
        // Fork (ux-overhaul.md §3.1, UX review mac-052): the block selection in the studio's accent at 35 % with an accent
        // outline. WPF's SelectionRectangleStyle (#20400000, a 1.06:1 tint) couldn't be seen on the dark grid.
        public static readonly ImmutableSolidColorBrush SelectionFill = B(0x594C8DF6);
        public static readonly IPen SelectionStroke = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xF6)), 1.5);
        public static readonly ImmutableSolidColorBrush StatusText = B(0xFFF0F0F0);

        /// <summary>The note colours (PatternNoteColors: 12 colours, hue 0 → 0.916667, saturation 0.7, perceived brightness 0.6).</summary>
        public static readonly IBrush[] NoteBrushes = Enumerable.Range(0, 12)
            .Select(i => (IBrush)new ImmutableSolidColorBrush(Hspa(0.916667 * i / 11.0, 0.7, 0.6))).ToArray();

        /// <summary>BuzzGUI.Common.HSPA.ToColor: hue, saturation and perceived brightness to RGB.</summary>
        static Color Hspa(double h, double s, double p)
        {
            const double Pr = 0.299, Pg = 0.587, Pb = 0.114;
            double R, G, B, part, minOverMax = 1.0 - s;
            if (minOverMax > 0.0)
            {
                if (h < 1.0 / 6.0) { h = 6.0 * h; part = 1.0 + h * (1.0 / minOverMax - 1.0); B = p / Math.Sqrt(Pr / minOverMax / minOverMax + Pg * part * part + Pb); R = B / minOverMax; G = B + h * (R - B); }
                else if (h < 2.0 / 6.0) { h = 6.0 * (-h + 2.0 / 6.0); part = 1.0 + h * (1.0 / minOverMax - 1.0); B = p / Math.Sqrt(Pg / minOverMax / minOverMax + Pr * part * part + Pb); G = B / minOverMax; R = B + h * (G - B); }
                else if (h < 3.0 / 6.0) { h = 6.0 * (h - 2.0 / 6.0); part = 1.0 + h * (1.0 / minOverMax - 1.0); R = p / Math.Sqrt(Pg / minOverMax / minOverMax + Pb * part * part + Pr); G = R / minOverMax; B = R + h * (G - R); }
                else if (h < 4.0 / 6.0) { h = 6.0 * (-h + 4.0 / 6.0); part = 1.0 + h * (1.0 / minOverMax - 1.0); R = p / Math.Sqrt(Pb / minOverMax / minOverMax + Pg * part * part + Pr); B = R / minOverMax; G = R + h * (B - R); }
                else if (h < 5.0 / 6.0) { h = 6.0 * (h - 4.0 / 6.0); part = 1.0 + h * (1.0 / minOverMax - 1.0); G = p / Math.Sqrt(Pb / minOverMax / minOverMax + Pr * part * part + Pg); B = G / minOverMax; R = G + h * (B - G); }
                else { h = 6.0 * (-h + 1.0); part = 1.0 + h * (1.0 / minOverMax - 1.0); G = p / Math.Sqrt(Pr / minOverMax / minOverMax + Pb * part * part + Pg); R = G / minOverMax; B = G + h * (R - G); }
            }
            else
            {
                if (h < 1.0 / 6.0) { h = 6.0 * h; R = Math.Sqrt(p * p / (Pr + Pg * h * h)); G = R * h; B = 0.0; }
                else if (h < 2.0 / 6.0) { h = 6.0 * (-h + 2.0 / 6.0); G = Math.Sqrt(p * p / (Pg + Pr * h * h)); R = G * h; B = 0.0; }
                else if (h < 3.0 / 6.0) { h = 6.0 * (h - 2.0 / 6.0); G = Math.Sqrt(p * p / (Pg + Pb * h * h)); B = G * h; R = 0.0; }
                else if (h < 4.0 / 6.0) { h = 6.0 * (-h + 4.0 / 6.0); B = Math.Sqrt(p * p / (Pb + Pg * h * h)); G = B * h; R = 0.0; }
                else if (h < 5.0 / 6.0) { h = 6.0 * (h - 4.0 / 6.0); B = Math.Sqrt(p * p / (Pb + Pr * h * h)); R = B * h; G = 0.0; }
                else { h = 6.0 * (-h + 1.0); R = Math.Sqrt(p * p / (Pr + Pb * h * h)); B = R * h; G = 0.0; }
            }
            static byte C(double v) => (byte)Math.Min(255, v * 255);
            return Color.FromRgb(C(R), C(G), C(B));
        }
    }

    /// <summary>
    /// The pattern font (ColumnRenderer/Font.cs): the settings' family (Consolas by default) with
    /// monospaced fallbacks for macOS and Linux, its line height, and the width of a character. Text
    /// is cached by string and brush, as the WPF editor cached whole beats.
    /// </summary>
    internal sealed class PatternFont
    {
        public Typeface Typeface { get; }
        public double Size { get; }
        public double LineHeight { get; }
        public double CharWidth { get; }

        readonly Dictionary<(string, IBrush), FormattedText> cache = new();

        public PatternFont()
        {
            var s = PatternEditor.Settings;
            string family = string.IsNullOrEmpty(s.FontFamily) ? "Consolas" : s.FontFamily;
            var weight = Enum.TryParse<FontWeight>(s.FontWeight, true, out var w) ? w : FontWeight.Bold;
            var style = Enum.TryParse<FontStyle>(s.FontStyle, true, out var st) ? st : FontStyle.Normal;
            Typeface = new Typeface(new FontFamily(family + ", Menlo, DejaVu Sans Mono, Courier New, monospace"), style, weight);
            Size = s.FontSize > 0 ? s.FontSize : 13;
            var sample = Make("A", Brushes.Black);
            CharWidth = sample.WidthIncludingTrailingWhitespace;
            LineHeight = Math.Round(sample.Height);
        }

        FormattedText Make(string text, IBrush brush) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, Size, brush);

        public FormattedText Text(string text, IBrush brush)
        {
            if (!cache.TryGetValue((text, brush), out var ft))
            {
                if (cache.Count > 4000) cache.Clear();
                ft = Make(text, brush);
                cache[(text, brush)] = ft;
            }
            return ft;
        }

        /// <summary>A column's width for a number of digits (Font.GetWidth: a character more than the digits).</summary>
        public double Width(int digits) => CharWidth * (digits + 1);

        /// <summary>The advance of each character (monospaced: the same for all).</summary>
        public double[] AdvanceWidths(string text) => Enumerable.Repeat(CharWidth, text?.Length ?? 0).ToArray();
    }
}
