#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReBuzz.Core.Notes
{
    /// <summary>
    /// Fork (docs/specs/machine-safety.md §3.5): reads the common subset of RTF into Song Notes'
    /// Markdown (<see cref="NotesMarkdown"/>): paragraphs and line breaks, bold, italic, underline,
    /// strike-through, font size (a paragraph much larger than the body text becomes a heading),
    /// colour, bulleted and numbered lists and hyperlinks. Everything else (fonts, tables, pictures,
    /// styles, spacing) is ignored. Written for what WPF's RichTextBox saves (the Info machine's
    /// text, <see cref="InfoMachineState"/>) but not tied to it.
    /// </summary>
    internal static class RtfToMarkdown
    {
        /// <summary>
        /// The text of <paramref name="rtf"/> as Markdown. <paramref name="foreground"/> is the
        /// colour the text is drawn in by default (0xAARRGGBB, the Info machine's), which needs no
        /// colour of its own; so does the colour most of the text has.
        /// </summary>
        public static string Convert(string rtf, uint? foreground = null) => Write(Read(rtf), foreground);

        // ------------------------------------------------------------------ the model

        internal sealed class Run
        {
            public string Text;
            public bool Bold, Italic, Underline, Strike;
            public int HalfPoints;
            public uint? Color;     // 0xFFRRGGBB, null: the default
            public string Link;

            public bool SameLook(Run o) =>
                Bold == o.Bold && Italic == o.Italic && Underline == o.Underline && Strike == o.Strike && Color == o.Color && Link == o.Link;
        }

        internal sealed class Paragraph
        {
            public readonly List<Run> Runs = new();
            public string Marker;           // \listtext or \pntext: "•", "1.", …
            public bool InList;             // \ls: a list paragraph even without a marker
            public int Level;               // \ilvl
            public int SpaceAfter, SpaceBefore;

            public string Text => string.Concat(Runs.Select(r => r.Text));
        }

        // ------------------------------------------------------------------ reading

        enum Dest { Text, Skip, ColorTable, FontTable, ListText, FieldInst }

        sealed class Field
        {
            public readonly StringBuilder Inst = new();
        }

        struct State
        {
            public Dest Dest;
            public bool Bold, Italic, Underline, Strike;
            public int HalfPoints;
            public int ColorIndex;
            public int Uc;
            public int Font;
            public Field Field;
            public string Link;
        }

        internal static List<Paragraph> Read(string rtf)
        {
            var paragraphs = new List<Paragraph>();
            if (string.IsNullOrEmpty(rtf)) return paragraphs;
            var colors = new List<uint?>();
            var fontCharsets = new Dictionary<int, int>();
            int fontInTable = -1;
            uint? colorInTable = null;
            bool colorComponent = false;
            int codePage = 1252;
            var stack = new Stack<State>();
            var st = new State { Dest = Dest.Text, HalfPoints = 24, Uc = 1, Font = -1 };
            var para = new Paragraph();
            int skip = 0;                   // fallback characters after \u still to skip
            bool ignorable = false;         // \* seen: the next control word starts a destination we may skip
            var bytes = new List<byte>();   // \'hh bytes, decoded together (double-byte code pages)
            var listText = new StringBuilder();
            int pendingLevel = 0, spaceAfter = 0, spaceBefore = 0;
            bool inList = false;

            void Emit(string s)
            {
                if (s.Length == 0) return;
                switch (st.Dest)
                {
                    case Dest.Text:
                        var last = para.Runs.Count > 0 ? para.Runs[^1] : null;
                        var look = new Run
                        {
                            Bold = st.Bold, Italic = st.Italic, Underline = st.Underline, Strike = st.Strike,
                            HalfPoints = st.HalfPoints, Color = st.ColorIndex >= 0 && st.ColorIndex < colors.Count ? colors[st.ColorIndex] : null,
                            Link = st.Link,
                        };
                        if (last != null && last.SameLook(look) && last.HalfPoints == look.HalfPoints) last.Text += s;
                        else
                        {
                            look.Text = s;
                            para.Runs.Add(look);
                        }
                        break;
                    case Dest.ListText:
                        listText.Append(s);
                        break;
                    case Dest.FieldInst:
                        st.Field?.Inst.Append(s);
                        break;
                }
            }

            void FlushBytes()
            {
                if (bytes.Count == 0) return;
                int cp = st.Font >= 0 && fontCharsets.TryGetValue(st.Font, out int charset) ? CodePageOfCharset(charset, codePage) : codePage;
                Emit(Decode(bytes.ToArray(), cp));
                bytes.Clear();
            }

            void EndParagraph()
            {
                FlushBytes();
                para.Level = pendingLevel;
                para.InList = inList;
                para.SpaceAfter = spaceAfter;
                para.SpaceBefore = spaceBefore;
                if (listText.Length > 0) para.Marker = listText.ToString().Replace("\t", "").Trim();
                listText.Clear();
                paragraphs.Add(para);
                para = new Paragraph();
            }

            int i = 0, n = rtf.Length;
            while (i < n)
            {
                char c = rtf[i];
                if (c == '{')
                {
                    FlushBytes();
                    stack.Push(st);
                    i++;
                    continue;
                }
                if (c == '}')
                {
                    FlushBytes();
                    if (st.Dest == Dest.ColorTable && colorComponent) colors.Add(colorInTable);
                    if (stack.Count > 0) st = stack.Pop();
                    skip = 0;
                    ignorable = false;
                    i++;
                    continue;
                }
                if (c == '\r' || c == '\n')
                {
                    i++;
                    continue;
                }
                if (c != '\\')
                {
                    // Plain text up to the next special character.
                    int start = i;
                    while (i < n && rtf[i] != '\\' && rtf[i] != '{' && rtf[i] != '}' && rtf[i] != '\r' && rtf[i] != '\n') i++;
                    string text = rtf.Substring(start, i - start);
                    if (skip > 0)
                    {
                        int drop = Math.Min(skip, text.Length);
                        text = text.Substring(drop);
                        skip -= drop;
                    }
                    if (st.Dest == Dest.ColorTable)
                    {
                        foreach (char ch in text)
                            if (ch == ';')
                            {
                                colors.Add(colorComponent ? colorInTable : null);
                                colorInTable = null;
                                colorComponent = false;
                            }
                        continue;
                    }
                    if (st.Dest == Dest.FontTable)
                        continue;
                    FlushBytes();
                    Emit(text);
                    continue;
                }

                // A control symbol or word.
                i++;
                if (i >= n) break;
                char s = rtf[i];
                if (!char.IsAsciiLetter(s))
                {
                    i++;
                    switch (s)
                    {
                        case '\'':
                            if (i + 2 <= n && int.TryParse(rtf.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
                            {
                                i += 2;
                                if (skip > 0) { skip--; continue; }
                                if (st.Dest == Dest.Text || st.Dest == Dest.ListText || st.Dest == Dest.FieldInst) bytes.Add((byte)b);
                            }
                            continue;
                        case '*':
                            ignorable = true;
                            continue;
                        case '~': FlushBytes(); if (skip > 0) skip--; else Emit(Ch(0x00A0)); continue;
                        case '_': FlushBytes(); if (skip > 0) skip--; else Emit(Ch(0x2011)); continue;
                        case '-': continue;     // an optional hyphen
                        case '\\': case '{': case '}':
                            FlushBytes();
                            if (skip > 0) skip--; else Emit(s.ToString());
                            continue;
                        case '\r': case '\n':
                            FlushBytes();
                            if (st.Dest == Dest.Text) EndParagraph();
                            continue;
                        default:
                            continue;
                    }
                }

                int wordStart = i;
                while (i < n && char.IsAsciiLetter(rtf[i])) i++;
                string word = rtf.Substring(wordStart, i - wordStart);
                int? param = null;
                if (i < n && (rtf[i] == '-' || char.IsAsciiDigit(rtf[i])))
                {
                    int ps = i;
                    i++;
                    while (i < n && char.IsAsciiDigit(rtf[i])) i++;
                    if (int.TryParse(rtf.AsSpan(ps, i - ps), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int p)) param = p;
                }
                if (i < n && rtf[i] == ' ') i++;    // the delimiting space is part of the word

                FlushBytes();
                if (ignorable)
                {
                    ignorable = false;
                    if (word == "fldinst")
                    {
                        st.Dest = st.Dest == Dest.Skip ? Dest.Skip : Dest.FieldInst;
                        continue;
                    }
                    st.Dest = Dest.Skip;
                    continue;
                }
                if (st.Dest == Dest.Skip) continue;
                if (skip > 0 && word != "u")
                {
                    skip--;
                    continue;
                }

                switch (word)
                {
                    // Destinations.
                    case "fonttbl": st.Dest = Dest.FontTable; break;
                    case "colortbl": st.Dest = Dest.ColorTable; colorInTable = null; colorComponent = false; break;
                    case "stylesheet": case "info": case "pict": case "object": case "header": case "headerl": case "headerr": case "headerf":
                    case "footer": case "footerl": case "footerr": case "footerf": case "footnote": case "listtable": case "listoverridetable":
                    case "rsidtbl": case "generator": case "xmlnstbl": case "themedata": case "colorschememapping": case "latentstyles":
                    case "datastore": case "pntxta": case "pntxtb": case "bkmkstart": case "bkmkend": case "shp": case "nonshppict":
                    case "filetbl": case "revtbl": case "mmathPr":
                        st.Dest = Dest.Skip;
                        break;
                    case "listtext": case "pntext":
                        st.Dest = st.Dest == Dest.Text ? Dest.ListText : Dest.Skip;
                        listText.Clear();
                        break;
                    case "field": st.Field = new Field(); break;
                    case "fldinst": st.Dest = Dest.FieldInst; break;
                    case "fldrslt":
                        st.Dest = Dest.Text;
                        st.Link = HyperlinkTarget(st.Field?.Inst.ToString());
                        break;

                    // The font table: each font's character set (for \'hh in it).
                    case "f":
                        if (st.Dest == Dest.FontTable) fontInTable = param ?? -1;
                        else st.Font = param ?? -1;
                        break;
                    case "fcharset":
                        if (st.Dest == Dest.FontTable && fontInTable >= 0 && param is int cs) fontCharsets[fontInTable] = cs;
                        break;
                    case "ansicpg":
                        if (param is int cp && cp > 0) codePage = cp;
                        break;
                    case "mac": codePage = 10000; break;
                    case "pc": codePage = 437; break;
                    case "pca": codePage = 850; break;

                    // The colour table.
                    case "red": case "green": case "blue":
                        if (st.Dest == Dest.ColorTable)
                        {
                            int v = Math.Clamp(param ?? 0, 0, 255);
                            uint baseColor = colorInTable ?? 0xFF000000;
                            int shift = word == "red" ? 16 : word == "green" ? 8 : 0;
                            colorInTable = (baseColor & ~(0xFFu << shift)) | ((uint)v << shift);
                            colorComponent = true;
                        }
                        break;

                    // Character formatting.
                    case "plain":
                        st.Bold = st.Italic = st.Underline = st.Strike = false;
                        st.HalfPoints = 24;
                        st.ColorIndex = 0;
                        break;
                    case "b": st.Bold = param != 0; break;
                    case "i": st.Italic = param != 0; break;
                    case "ul": case "uld": case "uldash": case "uldashd": case "uldashdd": case "uldb": case "ulth": case "ulw": case "ulwave":
                    case "ulhwave": case "ululdbwave": case "ulthd": case "ulthdash": case "ulldash": case "ulthldash": case "ulthdashd": case "ulthdashdd":
                        st.Underline = param != 0;
                        break;
                    case "ulnone": st.Underline = false; break;
                    case "strike": case "striked": st.Strike = param != 0; break;
                    case "fs": if (param is int fs && fs > 0) st.HalfPoints = fs; break;
                    case "cf": st.ColorIndex = param ?? 0; break;
                    case "uc": st.Uc = Math.Max(0, param ?? 1); break;
                    case "u":
                        if (param is int u)
                        {
                            FlushBytes();
                            Emit(((char)(u < 0 ? u + 65536 : u)).ToString());
                            skip = st.Uc;
                        }
                        break;

                    // Paragraphs.
                    case "par": case "sect": case "page": case "row":
                        if (st.Dest == Dest.Text) EndParagraph();
                        break;
                    case "pard":
                        pendingLevel = 0;
                        inList = false;
                        spaceAfter = spaceBefore = 0;
                        break;
                    case "line": Emit("\n"); break;
                    case "tab": Emit("\t"); break;
                    case "cell": Emit("\t"); break;
                    case "ls": inList = true; break;
                    case "ilvl": pendingLevel = Math.Clamp(param ?? 0, 0, 8); break;
                    case "sa": spaceAfter = param ?? 0; break;
                    case "sb": spaceBefore = param ?? 0; break;

                    // Characters by name.
                    case "emdash": Emit(Ch(0x2014)); break;
                    case "endash": Emit(Ch(0x2013)); break;
                    case "bullet": Emit(Ch(0x2022)); break;
                    case "lquote": Emit(Ch(0x2018)); break;
                    case "rquote": Emit(Ch(0x2019)); break;
                    case "ldblquote": Emit(Ch(0x201C)); break;
                    case "rdblquote": Emit(Ch(0x201D)); break;
                    case "emspace": case "enspace": case "qmspace": Emit(" "); break;
                }
            }
            FlushBytes();
            if (para.Runs.Count > 0 || listText.Length > 0) EndParagraph();
            return paragraphs;
        }

        static string Ch(int code) => ((char)code).ToString();

        /// <summary>The URL of a field instruction ("HYPERLINK \"https://…\" \o \"tip\""), or null.</summary>
        static string HyperlinkTarget(string inst)
        {
            if (string.IsNullOrWhiteSpace(inst)) return null;
            inst = inst.Trim();
            if (!inst.StartsWith("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
            string rest = inst.Substring(9).Trim();
            if (rest.StartsWith("\\l", StringComparison.Ordinal)) return null;     // a link inside the document
            if (rest.StartsWith('"'))
            {
                int end = rest.IndexOf('"', 1);
                return end > 1 ? rest.Substring(1, end - 1) : null;
            }
            int space = rest.IndexOf(' ');
            return space > 0 ? rest.Substring(0, space) : rest.Length > 0 ? rest : null;
        }

        static int CodePageOfCharset(int charset, int fallback) => charset switch
        {
            0 => fallback == 1252 ? 1252 : fallback,
            2 => 1252,      // symbol: shown as is
            77 => 10000,
            128 => 932,
            129 => 949,
            134 => 936,
            136 => 950,
            161 => 1253,
            162 => 1254,
            163 => 1258,
            177 => 1255,
            178 => 1256,
            186 => 1257,
            204 => 1251,
            222 => 874,
            238 => 1250,
            _ => fallback,
        };

        static string Decode(byte[] data, int codePage)
        {
            try
            {
                var e = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
                return e.GetString(data);
            }
            catch (Exception)
            {
                return Encoding.Latin1.GetString(data);
            }
        }

        // ------------------------------------------------------------------ writing

        internal static string Write(List<Paragraph> paragraphs, uint? foreground)
        {
            // The body text's size and colour: those of most of the text.
            var sizes = new Dictionary<int, int>();
            var colors = new Dictionary<uint, int>();
            int plain = 0;
            foreach (var r in paragraphs.SelectMany(p => p.Runs))
            {
                int count = r.Text.Count(ch => !char.IsWhiteSpace(ch));
                sizes[r.HalfPoints] = sizes.GetValueOrDefault(r.HalfPoints) + count;
                if (r.Color is uint col) colors[col] = colors.GetValueOrDefault(col) + count;
                else plain += count;
            }
            int body = sizes.Count == 0 ? 24 : sizes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
            var usual = new HashSet<uint>();
            if (foreground is uint fg) usual.Add(fg | 0xFF000000);
            if (colors.Count > 0)
            {
                var top = colors.OrderByDescending(kv => kv.Value).First();
                if (top.Value >= plain) usual.Add(top.Key);
            }

            var sb = new StringBuilder();
            bool previousList = false, previousHeading = false, first = true;
            int previousSpace = 0;
            foreach (var p in paragraphs)
            {
                string text = p.Text;
                bool empty = text.Trim().Length == 0;
                bool list = !empty && (p.Marker != null || p.InList);
                int heading = 0;
                if (!empty && !list)
                {
                    int min = p.Runs.Where(r => r.Text.Trim().Length > 0).Min(r => r.HalfPoints);
                    double ratio = min / (double)body;
                    heading = ratio >= 1.6 ? 1 : ratio >= 1.3 ? 2 : ratio >= 1.15 ? 3 : 0;
                }

                // Between paragraphs: a line break, or an empty line where the RTF had spacing (and
                // around lists and headings, so the Markdown reads the same elsewhere).
                if (!first)
                {
                    bool spaced = previousSpace + p.SpaceBefore >= 120 || (previousList != list && !empty) || previousHeading || heading > 0;
                    sb.Append(spaced && !(previousList && list) ? "\n\n" : "\n");
                }
                first = false;
                previousSpace = p.SpaceAfter;
                previousList = list;
                previousHeading = heading > 0;
                if (empty) continue;

                string indent = "";
                string prefix = "";
                if (list)
                {
                    indent = new string(' ', 2 * p.Level);
                    prefix = IsNumber(p.Marker) ? NumberOf(p.Marker) + ". " : "- ";
                }
                else if (heading > 0)
                    prefix = new string('#', heading) + " ";

                string continuation = list ? indent + new string(' ', prefix.Length) : "";
                sb.Append(indent).Append(prefix);
                sb.Append(Inline(p.Runs, usual, heading > 0, continuation));
            }
            string md = sb.ToString();
            while (md.Contains("\n\n\n")) md = md.Replace("\n\n\n", "\n\n");
            return md.Trim('\n');
        }

        static bool IsNumber(string marker) =>
            marker != null && marker.Length > 0 && (char.IsDigit(marker[0]) || (marker.Length <= 3 && char.IsLetter(marker[0]) && (marker.EndsWith('.') || marker.EndsWith(')'))));

        static string NumberOf(string marker)
        {
            string digits = new string(marker.TakeWhile(char.IsDigit).ToArray());
            return digits.Length > 0 ? digits : "1";
        }

        /// <summary>A paragraph's runs as inline Markdown; line breaks start a new line (indented in a list item).</summary>
        static string Inline(List<Run> runs, HashSet<uint> usual, bool heading, string continuation)
        {
            // Split at line breaks, then merge neighbours that look the same.
            var lines = new List<List<Run>> { new() };
            foreach (var r in runs)
            {
                var parts = r.Text.Split('\n');
                for (int k = 0; k < parts.Length; k++)
                {
                    if (k > 0) lines.Add(new List<Run>());
                    if (parts[k].Length == 0) continue;
                    var piece = new Run
                    {
                        Text = parts[k], Bold = r.Bold && !heading, Italic = r.Italic, Underline = r.Underline, Strike = r.Strike,
                        // A link's own colour is the link colour (WPF gives hyperlinks one): links look like links.
                        HalfPoints = r.HalfPoints, Color = r.Color is uint c && !usual.Contains(c) && r.Link == null ? c : null, Link = r.Link,
                    };
                    var line = lines[^1];
                    if (line.Count > 0 && line[^1].SameLook(piece)) line[^1].Text += piece.Text;
                    else line.Add(piece);
                }
            }
            var sb = new StringBuilder();
            for (int k = 0; k < lines.Count; k++)
            {
                if (k > 0) sb.Append('\n').Append(continuation);
                var line = new StringBuilder();
                foreach (var r in lines[k]) line.Append(Styled(r, line));
                string text = line.ToString().TrimEnd();
                // Text that would read as a heading, a list item or a quote stays text.
                sb.Append(k == 0 && continuation.Length > 0 ? text : NotesMarkdown.EscapeLineStart(text));
            }
            return sb.ToString();
        }

        static string Styled(Run r, StringBuilder before)
        {
            string text = r.Text;
            int lead = text.Length - text.TrimStart().Length;
            int trail = text.Length - text.TrimEnd().Length;
            if (lead == text.Length) return text;
            string s = NotesMarkdown.Escape(text.Substring(lead, text.Length - lead - trail));
            // Emphasis right after another's closing star takes underscores, so the two don't run together.
            string em = lead == 0 && before.Length > 0 && before[^1] == '*' ? "_" : "*";
            if (r.Italic && r.Bold) s = em + em + em + s + em + em + em;
            else if (r.Bold) s = em + em + s + em + em;
            else if (r.Italic) s = em + s + em;
            if (r.Strike) s = "~~" + s + "~~";
            if (r.Underline && r.Link == null) s = "<u>" + s + "</u>";
            if (r.Link != null) s = "[" + s + "](" + r.Link.Replace(" ", "%20").Replace(")", "%29") + ")";
            if (r.Color is uint c) s = $"<span style=\"color:#{c & 0xFFFFFF:x6}\">" + s + "</span>";
            return text.Substring(0, lead) + s + text.Substring(text.Length - trail);
        }
    }
}
