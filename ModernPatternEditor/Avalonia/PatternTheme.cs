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
        // Fork (ux-overhaul.md P4b): each colour has a dark value (the WPF editor's, as before) and a light one; the brushes are kept
        // (a view holds them as its background) and take the theme's colour in place when it changes (BuzzGUI.Common.StudioLook).
        static readonly System.Collections.Generic.List<(SolidColorBrush Brush, uint Dark, uint Light)> table = new();

        static SolidColorBrush B(uint dark, uint light)
        {
            var brush = new SolidColorBrush(Color.FromUInt32(BuzzGUI.Common.StudioLook.Light ? light : dark));
            lock (table) table.Add((brush, dark, light));
            return brush;
        }

        static SolidColorBrush B(uint argb) => B(argb, argb);

        static PatternTheme()
        {
            BuzzGUI.Common.StudioLook.Changed += () =>
            {
                bool light = BuzzGUI.Common.StudioLook.Light;
                lock (table)
                    foreach (var (brush, dark, lightValue) in table) brush.Color = Color.FromUInt32(light ? lightValue : dark);
                for (int i = 0; i < NoteBrushes.Length; i++) ((SolidColorBrush)NoteBrushes[i]).Color = Hspa(0.916667 * i / 11.0, 0.7, light ? 0.40 : 0.6);
            };
        }

        public static readonly SolidColorBrush Background = B(0xFF333333, 0xFFECECF0);            // PatEdBackgroundBrush
        public static readonly SolidColorBrush ElementBackground = B(0xFF444444, 0xFFF8F8FB);     // PatEdElementBackgroundSolidColorBrush
        public static readonly SolidColorBrush ElementBorder = B(0x88000000, 0x88000000);         // PatEdBorderBrush (#8000)
        public static readonly SolidColorBrush ToolbarBackground = B(0xFF595959, 0xFFE4E4EA);     // ToolbarBackgroundBrush
        public static readonly SolidColorBrush BeatDark = B(0x50000000, 0x16000000);              // ThemeColorOverride_PE_BG_Dark
        public static readonly SolidColorBrush Beat = B(0x30000000, 0x0A000000);                  // ThemeColorOverride_PE_BG
        public static readonly SolidColorBrush Text = B(0xFFDDDDDD, 0xFF1B1B20);                  // ThemeColorOverride_PE_Text
        public static readonly SolidColorBrush TextShadow = B(0xFF000000, 0x00000000);            // TextDropShadowBrush (no shadow on light)
        public static readonly SolidColorBrush NoValue = B(0x66CCCCCC, 0x998A8A97);               // NoValueBrush (#6CCC)
        public static readonly SolidColorBrush RowNumber = B(0xFFAAAAAA, 0xFF505059);             // RowNumberBrush
        public static readonly SolidColorBrush RowNumberShadow = B(0xFF000000, 0x00000000);       // RowNumberDropShadowBrush
        public static readonly SolidColorBrush Cursor = B(0x22FFFFFF, 0x261F5FA8);                // CursorBrush (#2FFF)
        public static readonly SolidColorBrush CursorBorder = B(0x88FFFFFF, 0xCC1F5FA8);          // CursorBorderBrush (#8FFF)
        public static readonly SolidColorBrush CursorInactive = B(0x22000055, 0x22000055);        // CursorInactiveBrush (#2005)
        public static readonly SolidColorBrush CursorLine = B(0x2200FF00, 0x28008A20);            // CursorLineBrush (#20F0)
        public static readonly SolidColorBrush ColumnSetLabel = B(0xFFCCCCCC, 0xFF3A3A44);        // ColumnSetLabelBrush
        public static readonly SolidColorBrush ColumnSetLabelMuted = B(0x4DCCCCCC, 0x4D3A3A44);   // the same at 0.3 opacity
        public static readonly SolidColorBrush ColumnLabel = B(0xFFCCCCCC, 0xFF3A3A44);           // ColumnLabelBrush
        // Fork (ux-overhaul.md P3c): the beat and bar rules, 3.4:1 and 7.4:1 against the lighter of the two beat shades (#373737).
        public static readonly SolidColorBrush BeatRule = B(0xFF888888, 0xFF8A8A97);
        public static readonly SolidColorBrush BarRule = B(0xFFCCCCCC, 0xFF3A3A44);
        public static readonly SolidColorBrush LabelHover = B(0x28000000, 0x1E000000);            // PatternControl's highlight
        public static readonly SolidColorBrush PlayPosition = B(0x40000000, 0x1E000000);          // playPosRectangle
        // Fork (ux-overhaul.md §3.1, UX review mac-052): the block selection in the studio's accent at 35 % with an accent
        // outline. WPF's SelectionRectangleStyle (#20400000, a 1.06:1 tint) couldn't be seen on the dark grid.
        public static readonly SolidColorBrush SelectionFill = B(0x594C8DF6, 0x590064C7);
        public static readonly IPen SelectionStroke = new Pen(B(0xFF4C8DF6, 0xFF0064C7), 1.5);
        public static readonly SolidColorBrush StatusText = B(0xFFF0F0F0, 0xFF1B1B20);

        /// <summary>The note colours (PatternNoteColors: 12 colours, hue 0 → 0.916667, saturation 0.7, perceived brightness 0.6; 0.4 on the light theme).</summary>
        public static readonly IBrush[] NoteBrushes = Enumerable.Range(0, 12)
            .Select(i => (IBrush)new SolidColorBrush(Hspa(0.916667 * i / 11.0, 0.7, BuzzGUI.Common.StudioLook.Light ? 0.40 : 0.6))).ToArray();

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
