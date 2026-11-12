using BuzzGUI.Common;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WDE.ModernPatternEditor.ColumnRenderer
{
    class BeatVisual : DrawingVisual
    {
        static Random rnd = new Random();

        static bool stretch = false;
        static bool usecache = true;

        public void Render(ColumnSetElement cse, IColumnSet columnSet, int index, int h)
        {
            // Fork (docs/specs/dye-essence.md §5.5): the columns the element draws (those in or near the
            // view), at their place in the set, not every column of the set.
            int c0 = cse.FirstDrawnColumn;
            var beats = new IBeat[cse.DrawnColumnCount];
            var widths = new int[beats.Length];
            var shape = new int[beats.Length + 1];
            for (int i = 0; i < beats.Length; i++)
            {
                var column = columnSet.Columns[c0 + i];
                beats[i] = column.FetchBeat(index);
                widths[i] = cse.ColumnWidthAt(c0 + i);
                shape[i] = (int)column.Type | column.DigitCount << 4 | (column.TiedToNext ? 1 << 12 : 0);
            }
            // The first row's highlight takes its height from the set's first column, as before.
            int firstRows = columnSet.Columns.Count > 0 ? (c0 == 0 && beats.Length > 0 ? beats[0] : columnSet.Columns[0].FetchBeat(index)).Rows.Count : 0;
            shape[beats.Length] = firstRows;
            int x0 = cse.ColumnLeft(c0);

            if (usecache)
            {
                // The shape (each column's type, digits and tie, and the first column's rows) tells apart
                // two runs of columns of the same widths and values, which draw differently.
                var key = new BeatVisualCacheKey(beats, index & 1, widths, h, shape);

                var br = BeatVisualCache.Lookup(key);
                if (br == null)
                {
                    br = CreateBitmapCacheBrush(cse, beats, c0, firstRows, index, widths, h);
                    BeatVisualCache.Cache(key, br);
                }

                var dc = RenderOpen();
                dc.DrawRectangle(br, null, new Rect(x0, 0, widths.Sum(), h));
                dc.Close();
            }
            else
            {
                var dc = RenderOpen();
                dc.PushTransform(new TranslateTransform(x0, 0));
                RenderVisual(cse, dc, beats, c0, firstRows, index, widths, h);
                dc.Pop();
                dc.Close();
            }
        }

        public static int GetColumnWidth(IColumn column)
        {
            int w = (int)Math.Ceiling(Font.GetWidth(column.TiedToNext ? column.DigitCount - 1 : column.DigitCount));
            return w;
        }


        BitmapCacheBrush CreateBitmapCacheBrush(ColumnSetElement cse, IBeat[] beats, int firstColumn, int firstRows, int index, int[] w, int h)
        {
            var dv = new DrawingVisual();
            var dc = dv.RenderOpen();

            RenderVisual(cse, dc, beats, firstColumn, firstRows, index, w, h);

            dc.Close();

            return new BitmapCacheBrush(dv) { BitmapCache = new BitmapCache() { EnableClearType = PatternEditor.Settings.FontClearType, SnapsToDevicePixels = true, RenderAtScale = 1.0 } };
        }

        static Brush backgroundBrush;
        static Brush darkBackgroundBrush;
        static Brush veryDarkBackgroundBrush;
        static Brush textBrush;
        static Brush textDropShadowBrush;
        static Brush noValueBrush;
        static Brush noValueDropShadowBrush;
        static Brush rowNumberBrush;
        static Brush rowNumberDropShadowBrush;
        static Brush[] noteBrushes;

        public static void InvalidateResources()
        {
            backgroundBrush = null;
        }

        SolidColorBrush GetBrush(ColumnSetElement cse, string themeColorName)
        {
            string xamlname = "ThemeColorOverride_" + themeColorName.Replace(" ", "_");
            var br = cse.TryFindResource(xamlname) as SolidColorBrush;
            if (br == null) br = new SolidColorBrush(Global.Buzz.ThemeColors[themeColorName]);
            br.Freeze();
            return br;
        }

        SolidColorBrush[] GetBrushArray(ColumnSetElement cse, string name)
        {
            var x = cse.TryFindResource(name);

            if (x is ObjectDataProvider)
            {
                var odp = x as ObjectDataProvider;
                if (odp.ObjectInstance == null || !(odp.ObjectInstance is HSPAColorProvider)) return null;
                return (odp.ObjectInstance as HSPAColorProvider)
                    .Colors.Select(c => new SolidColorBrush(c)).Do(b => b.Freeze()).ToArray();
            }
            else if (x is Color[])
            {
                // TODO
            }

            return null;
        }

        // beats[i] is column firstColumn + i of the set; firstRows the rows of the set's first column.
        void RenderVisual(ColumnSetElement cse, DrawingContext dc, IBeat[] beats, int firstColumn, int firstRows, int index, int[] w, int h)
        {
            if (backgroundBrush == null)
            {
                backgroundBrush = GetBrush(cse, "PE BG");
                darkBackgroundBrush = GetBrush(cse, "PE BG Dark");
                veryDarkBackgroundBrush = GetBrush(cse, "PE BG Very Dark");
                textBrush = GetBrush(cse, "PE Text");
                textDropShadowBrush = cse.TryFindResource("TextDropShadowBrush") as SolidColorBrush;
                noValueBrush = cse.TryFindResource("NoValueBrush") as SolidColorBrush;
                noValueDropShadowBrush = cse.TryFindResource("NoValueDropShadowBrush") as SolidColorBrush;
                rowNumberBrush = cse.TryFindResource("RowNumberBrush") as SolidColorBrush;
                rowNumberDropShadowBrush = cse.TryFindResource("RowNumberDropShadowBrush") as SolidColorBrush;
                noteBrushes = GetBrushArray(cse, "PatternNoteColors");
            }

            Brush br;

            /* if ((index & 3) == 0)
				br = veryDarkBackgroundBrush;
			else */
            if (PatternEditor.Settings.BeatHighlight == BeatHighlightMode.Block)
            {
                if ((index & 1) == 0)
                    br = darkBackgroundBrush;
                else
                    br = backgroundBrush;

                //br = new SolidColorBrush(Color.FromRgb((byte)rnd.Next(), (byte)rnd.Next(), (byte)rnd.Next()));

                dc.DrawRectangle(br, null, new Rect(0, 0, w.Sum(), h));
            }
            else if (PatternEditor.Settings.BeatHighlight == BeatHighlightMode.FirstRow && firstRows > 0)
            {
                int rowHeight = h / firstRows;
                br = darkBackgroundBrush;
                dc.DrawRectangle(br, null, new Rect(0, 0, w.Sum(), rowHeight));
                br = backgroundBrush;
                dc.DrawRectangle(br, null, new Rect(0, rowHeight, w.Sum(), h - rowHeight));
            }

            int x = 0;

            for (int i = 0; i < beats.Length; i++)
            {
                int xoffset = cse.ColumnSet.Columns[firstColumn + i].TiedToNext ? (int)(Math.Ceiling(Font.GetWidth(0)) / 2) : 0;

                if (PatternEditor.Settings.TextDropShadow && textDropShadowBrush != null)
                    DrawColumn(dc, i, beats[i], x + xoffset + 1, 1, w[i], h, true);

                DrawColumn(dc, i, beats[i], x + xoffset, 0, w[i], h, false);
                x += w[i];
            }
        }

        void DrawColumn(DrawingContext dc, int column, IBeat beat, int x, int yoffs, int w, int h, bool shadow)
        {
            var rows = beat.Rows;

            var lineheight = Font.LineHeight;

            double scaling = h / (lineheight * rows.Count);
            scaling *= 0.999;

            if (stretch)
                dc.PushTransform(new ScaleTransform(1.0, scaling));
            else
                dc.PushTransform(new ScaleTransform(1.0, Math.Min(scaling, 1.0)));

            if (!stretch)
                lineheight *= Math.Max(scaling, 1.0);


            for (int row = 0; row < rows.Count; row++)
            {
                var text = rows[row].ValueString;

                FormattedText ft;

                if (rows[row].Type == BeatValueType.RowNumber)
                    ft = Font.GetFormattedText(text, shadow ? rowNumberDropShadowBrush : rowNumberBrush);
                else if (rows[row].Type == BeatValueType.NoValue)
                    ft = Font.GetFormattedText(text, shadow ? noValueDropShadowBrush : noValueBrush);
                else if (rows[row].Type == BeatValueType.Note)
                {
                    if (!shadow)
                    {
                        int nv = (rows[row].Value & 15) - 1;
                        nv = nv * 7 % 12;

                        if (PatternEditor.Settings.ColorNote != ColorNoteMode.None && noteBrushes != null && rows[row].Value != BuzzNote.Off)
                            ft = Font.GetFormattedText(text, noteBrushes[nv]);
                        else
                            ft = Font.GetFormattedText(text, textBrush);
                    }
                    else
                    {
                        ft = Font.GetFormattedText(text, textDropShadowBrush);
                    }
                }
                else
                    ft = Font.GetFormattedText(text, shadow ? textDropShadowBrush : textBrush);

                ft.MaxTextWidth = w;
                ft.TextAlignment = TextAlignment.Center;
                ft.Trimming = TextTrimming.None;
                ft.MaxLineCount = 1;

                double y = row * lineheight;
                dc.DrawText(ft, new Point(x, y + yoffs));


                //Font.DrawGlyphRun(dc, values[row], new Point(x, y), w, textBrush, PatternEditor.Settings.TextFormattingMode);

            }

            dc.Pop();

        }
    }
}
