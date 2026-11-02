#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7 plot): a tiny rasterizer for the plots. RGB pixels,
// filled rectangles, crisp grid lines, anti-aliased polylines of any width (coverage by distance
// to each segment, so joints don't double up), and text from a built-in 5×7 bitmap font, scaled
// by whole pixels and optionally turned to read upwards. Same pixels on every platform.

using System;
using System.Collections.Generic;

namespace ReBuzz.Agent.Audio
{
    internal readonly struct Rgb
    {
        public Rgb(uint hex) { R = (byte)(hex >> 16); G = (byte)(hex >> 8); B = (byte)hex; }
        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }
        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public static Rgb Lerp(Rgb a, Rgb b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return new Rgb((byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));
        }
    }

    internal enum Align { Left, Center, Right }

    internal sealed class Canvas
    {
        readonly byte[] px;
        float[] coverage;

        public Canvas(int width, int height, Rgb background)
        {
            Width = width;
            Height = height;
            px = new byte[width * height * 3];
            Fill(0, 0, width, height, background);
        }

        public int Width { get; }
        public int Height { get; }

        public Rgb Get(int x, int y)
        {
            int i = (y * Width + x) * 3;
            return new Rgb(px[i], px[i + 1], px[i + 2]);
        }

        public void Set(int x, int y, Rgb c)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height) return;
            int i = (y * Width + x) * 3;
            px[i] = c.R;
            px[i + 1] = c.G;
            px[i + 2] = c.B;
        }

        public void Blend(int x, int y, Rgb c, double alpha)
        {
            if (x < 0 || y < 0 || x >= Width || y >= Height || alpha <= 0) return;
            if (alpha >= 1) { Set(x, y, c); return; }
            int i = (y * Width + x) * 3;
            px[i] = (byte)Math.Round(px[i] + (c.R - px[i]) * alpha);
            px[i + 1] = (byte)Math.Round(px[i + 1] + (c.G - px[i + 1]) * alpha);
            px[i + 2] = (byte)Math.Round(px[i + 2] + (c.B - px[i + 2]) * alpha);
        }

        public void Fill(int x, int y, int w, int h, Rgb c)
        {
            int x0 = Math.Max(0, x), y0 = Math.Max(0, y), x1 = Math.Min(Width, x + w), y1 = Math.Min(Height, y + h);
            for (int yy = y0; yy < y1; yy++)
                for (int xx = x0; xx < x1; xx++) Set(xx, yy, c);
        }

        public void HLine(int x0, int x1, int y, Rgb c, int dash = 0)
        {
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
                if (dash == 0 || (x / dash) % 2 == 0) Set(x, y, c);
        }

        public void VLine(int x, int y0, int y1, Rgb c, int dash = 0)
        {
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
                if (dash == 0 || (y / dash) % 2 == 0) Set(x, y, c);
        }

        /// <summary>
        /// An anti-aliased line through the points, <paramref name="width"/> pixels thick, kept inside
        /// the clip rectangle. A NaN coordinate breaks the line.
        /// </summary>
        public void Polyline(IReadOnlyList<(double X, double Y)> points, Rgb c, double width, int clipX0, int clipY0, int clipX1, int clipY1)
        {
            coverage ??= new float[Width * Height];
            double r = width / 2;
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
            void Segment(double ax, double ay, double bx, double by)
            {
                int x0 = Math.Max(clipX0, (int)Math.Floor(Math.Min(ax, bx) - r - 1)), x1 = Math.Min(clipX1, (int)Math.Ceiling(Math.Max(ax, bx) + r + 1));
                int y0 = Math.Max(clipY0, (int)Math.Floor(Math.Min(ay, by) - r - 1)), y1 = Math.Min(clipY1, (int)Math.Ceiling(Math.Max(ay, by) + r + 1));
                if (x0 > x1 || y0 > y1) return;
                bx0 = Math.Min(bx0, x0); by0 = Math.Min(by0, y0); bx1 = Math.Max(bx1, x1); by1 = Math.Max(by1, y1);
                double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        double px0 = x + 0.5 - ax, py0 = y + 0.5 - ay;
                        double t = len2 > 0 ? Math.Clamp((px0 * dx + py0 * dy) / len2, 0, 1) : 0;
                        double ex = px0 - t * dx, ey = py0 - t * dy;
                        double cov = Math.Clamp(r + 0.5 - Math.Sqrt(ex * ex + ey * ey), 0, 1);
                        int i = y * Width + x;
                        if (cov > coverage[i]) coverage[i] = (float)cov;
                    }
            }
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var (ax, ay) = points[i];
                var (bx, by) = points[i + 1];
                if (double.IsNaN(ax) || double.IsNaN(ay) || double.IsNaN(bx) || double.IsNaN(by)) continue;
                Segment(ax, ay, bx, by);
            }
            if (points.Count == 1 && !double.IsNaN(points[0].X) && !double.IsNaN(points[0].Y)) Segment(points[0].X, points[0].Y, points[0].X, points[0].Y);
            for (int y = Math.Max(0, by0); y <= Math.Min(Height - 1, by1); y++)
                for (int x = Math.Max(0, bx0); x <= Math.Min(Width - 1, bx1); x++)
                {
                    int i = y * Width + x;
                    if (coverage[i] <= 0) continue;
                    Blend(x, y, c, coverage[i]);
                    coverage[i] = 0;
                }
        }

        // ---- Text --------------------------------------------------------------------------

        /// <summary>Pixels a string takes across at <paramref name="scale"/>: 6 per character (5 and a gap), less the last gap.</summary>
        public static int TextWidth(string s, int scale) => s.Length == 0 ? 0 : (Font.Clean(s).Length * 6 - 1) * scale;

        public static int TextHeight(int scale) => 7 * scale;

        /// <summary>Text with its top at <paramref name="y"/>, aligned on <paramref name="x"/>.</summary>
        public void Text(int x, int y, string s, Rgb c, int scale = 2, Align align = Align.Left)
        {
            s = Font.Clean(s);
            int w = TextWidth(s, scale);
            int left = align == Align.Left ? x : align == Align.Center ? x - w / 2 : x - w;
            for (int i = 0; i < s.Length; i++)
            {
                var rows = Font.Glyph(s[i]);
                for (int row = 0; row < 7; row++)
                    for (int col = 0; col < 5; col++)
                        if ((rows[row] & (0x10 >> col)) != 0)
                            Fill(left + (i * 6 + col) * scale, y + row * scale, scale, scale, c);
            }
        }

        /// <summary>Text turned to read upwards, centred on (<paramref name="cx"/>, <paramref name="cy"/>): for y-axis titles.</summary>
        public void TextUp(int cx, int cy, string s, Rgb c, int scale = 2)
        {
            s = Font.Clean(s);
            int w = TextWidth(s, scale);
            int bottom = cy + w / 2, left = cx - TextHeight(scale) / 2;
            for (int i = 0; i < s.Length; i++)
            {
                var rows = Font.Glyph(s[i]);
                for (int row = 0; row < 7; row++)
                    for (int col = 0; col < 5; col++)
                        if ((rows[row] & (0x10 >> col)) != 0)
                            Fill(left + row * scale, bottom - (i * 6 + col + 1) * scale, scale, scale, c);
            }
        }

        public byte[] Png() => Audio.Png.Encode(Width, Height, px);
    }

    /// <summary>A 5×7 bitmap font for printable ASCII: per glyph seven rows, five bits each (bit 4 the left column).</summary>
    internal static class Font
    {
        // Rows as hex pairs, characters 32 (space) … 126 (~).
        static readonly string[] Data =
        {
            "00000000000000", "04040404040004", "0A0A0000000000", "0A0A1F0A1F0A0A", "040F140E051E04", "18190204081303", "0C12140815120D", "04040000000000",
            "02040808080402", "08040202020408", "0004150E150400", "0004041F040400", "000000000C0408", "0000001F000000", "00000000000C0C", "00010204081000",
            "0E11131519110E", "040C040404040E", "0E11010204081F", "1F02040201110E", "02060A121F0202", "1F101E0101110E", "0608101E11110E", "1F010204080808",
            "0E11110E11110E", "0E11110F01020C", "000C0C000C0C00", "000C0C000C0408", "02040810080402", "00001F001F0000", "08040201020408", "0E110102040004",
            "0E11010D15150E", "0E1111111F1111", "1E11111E11111E", "0E11101010110E", "1C12111111121C", "1F10101E10101F", "1F10101E101010", "0E11101711110F",
            "1111111F111111", "0E04040404040E", "0702020202120C", "11121418141211", "1010101010101F", "111B1515111111", "11111915131111", "0E11111111110E",
            "1E11111E101010", "0E11111115120D", "1E11111E141211", "0F10100E01011E", "1F040404040404", "1111111111110E", "11111111110A04", "1111111515150A",
            "11110A040A1111", "1111110A040404", "1F01020408101F", "0E08080808080E", "00100804020100", "0E02020202020E", "040A1100000000", "0000000000001F",
            "08040000000000", "00000E010F110F", "1010161911111E", "00000E1010110E", "01010D1311110F", "00000E111F100E", "0609081C080808", "000F11110F010E",
            "10101619111111", "04000C0404040E", "0200060202120C", "10101214181412", "0C04040404040E", "00001A15151111", "00001619111111", "00000E1111110E",
            "00001E111E1010", "00000D130F0101", "00001619101010", "00000E100E011E", "08081C08080906", "0000111111130D", "00001111110A04", "0000111115150A",
            "0000110A040A11", "000011110F010E", "00001F0204081F", "02040408040402", "04040404040404", "08040402040408", "00000815020000",
        };

        static readonly byte[][] Glyphs = Build();

        static byte[][] Build()
        {
            var glyphs = new byte[Data.Length][];
            for (int g = 0; g < Data.Length; g++)
            {
                if (Data[g].Length != 14) throw new InvalidOperationException($"Glyph {(char)(32 + g)} has {Data[g].Length} hex digits, not 14.");
                glyphs[g] = new byte[7];
                for (int r = 0; r < 7; r++) glyphs[g][r] = Convert.ToByte(Data[g].Substring(2 * r, 2), 16);
            }
            return glyphs;
        }

        public static byte[] Glyph(char ch) => ch >= 32 && ch <= 126 ? Glyphs[ch - 32] : Glyphs['?' - 32];

        /// <summary>Typographic characters the font lacks, as ASCII: minus and dashes to '-', '…' to "...", '×' to 'x'.</summary>
        public static string Clean(string s)
        {
            if (s == null) return "";
            var b = new System.Text.StringBuilder(s.Length);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '−': case '–': case '—': b.Append('-'); break;
                    case '…': b.Append("..."); break;
                    case '×': b.Append('x'); break;
                    case '♯': b.Append('#'); break;
                    case 'µ': b.Append('u'); break;
                    case '°': b.Append('o'); break;
                    default: b.Append(ch >= 32 && ch <= 126 ? ch : '?'); break;
                }
            }
            return b.ToString();
        }
    }
}
