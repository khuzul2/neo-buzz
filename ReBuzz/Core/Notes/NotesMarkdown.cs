#nullable disable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReBuzz.Core.Notes
{
    /// <summary>
    /// Fork (docs/specs/machine-safety.md §3.5): Song Notes' rich text, which is Markdown. The
    /// common subset, plus two things notes need:
    /// <list type="bullet">
    /// <item>blocks: paragraphs, headings (#, ##, ###), bulleted (-, *, +) and numbered (1.) lists
    /// nested by two spaces, quotes (&gt;) and rules (---);</item>
    /// <item>inline: **bold**, *italic* (or _italic_), ~~strike-through~~, `code`, [links](https://…),
    /// &lt;https://…&gt; and bare https:// links, &lt;u&gt;underline&lt;/u&gt;,
    /// &lt;span style="color:#rrggbb"&gt;colour&lt;/span&gt; and &lt;br&gt;; a backslash escapes;</item>
    /// <item>a line break is a line break (as in chat apps and GitHub comments), and an empty line
    /// starts a new paragraph.</item>
    /// </list>
    /// Pure text in, a block model out: the studio draws it (Views/Notes), agents read and write
    /// the text itself.
    /// </summary>
    internal static class NotesMarkdown
    {
        internal enum BlockKind { Paragraph, Heading, Bullet, Numbered, Quote, Rule }

        internal sealed class Block
        {
            public BlockKind Kind;
            /// <summary>A heading's level (1–6), a list item's depth (0…).</summary>
            public int Level;
            /// <summary>A numbered item's number.</summary>
            public int Number;
            public string Source = "";
            public List<Span> Spans = new();
        }

        internal sealed class Span
        {
            public string Text = "";
            public bool Bold, Italic, Underline, Strike, Code, LineBreak;
            public uint? Color;
            public string Link;

            internal Span With(string text) => new Span
            {
                Text = text, Bold = Bold, Italic = Italic, Underline = Underline, Strike = Strike, Code = Code, Color = Color, Link = Link,
            };

            internal bool SameLook(Span o) =>
                !LineBreak && !o.LineBreak && Bold == o.Bold && Italic == o.Italic && Underline == o.Underline && Strike == o.Strike &&
                Code == o.Code && Color == o.Color && Link == o.Link;
        }

        // ------------------------------------------------------------------ blocks

        static readonly Regex HeadingLine = new(@"^ {0,3}(#{1,6})(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$", RegexOptions.Compiled);
        static readonly Regex RuleLine = new(@"^ {0,3}([-*_])(?:[ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
        static readonly Regex ItemLine = new(@"^([ \t]*)([-*+]|\d{1,9}[.)])(?:[ \t]+(.*))?$", RegexOptions.Compiled);
        static readonly Regex QuoteLine = new(@"^ {0,3}> ?(.*)$", RegexOptions.Compiled);

        public static List<Block> Parse(string markdown)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrEmpty(markdown)) return blocks;
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            Block open = null;             // a paragraph, item or quote that the next line may continue
            var text = new StringBuilder();
            var indents = new List<int>();  // the list's indents by depth

            void Close()
            {
                if (open != null)
                {
                    open.Source = text.ToString();
                    open.Spans = ParseInline(open.Source);
                    blocks.Add(open);
                }
                open = null;
                text.Clear();
            }

            foreach (var raw in lines)
            {
                string line = raw.TrimEnd();
                if (line.Trim().Length == 0)
                {
                    Close();
                    continue;
                }
                Match m;
                if ((m = HeadingLine.Match(line)).Success)
                {
                    Close();
                    indents.Clear();
                    blocks.Add(new Block { Kind = BlockKind.Heading, Level = m.Groups[1].Length, Source = m.Groups[2].Value, Spans = ParseInline(m.Groups[2].Value) });
                    continue;
                }
                if (RuleLine.IsMatch(line))
                {
                    Close();
                    indents.Clear();
                    blocks.Add(new Block { Kind = BlockKind.Rule });
                    continue;
                }
                if ((m = ItemLine.Match(line)).Success)
                {
                    Close();
                    int indent = Columns(m.Groups[1].Value);
                    // Depth by indent: deeper than the item before is one level down; back to an
                    // earlier indent is that level again.
                    while (indents.Count > 0 && indents[^1] > indent) indents.RemoveAt(indents.Count - 1);
                    if (indents.Count == 0 || indent > indents[^1]) indents.Add(indent);
                    string marker = m.Groups[2].Value;
                    bool numbered = char.IsDigit(marker[0]);
                    open = new Block
                    {
                        Kind = numbered ? BlockKind.Numbered : BlockKind.Bullet,
                        Level = indents.Count - 1,
                        Number = numbered && int.TryParse(marker.TrimEnd('.', ')'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int nr) ? nr : 0,
                    };
                    text.Append(m.Groups[3].Value);
                    continue;
                }
                if ((m = QuoteLine.Match(line)).Success)
                {
                    if (open?.Kind != BlockKind.Quote)
                    {
                        Close();
                        open = new Block { Kind = BlockKind.Quote };
                    }
                    else text.Append('\n');
                    text.Append(m.Groups[1].Value);
                    continue;
                }
                // An indented line after an item continues it; any other line continues a paragraph.
                if (open != null && (open.Kind == BlockKind.Paragraph || ((open.Kind == BlockKind.Bullet || open.Kind == BlockKind.Numbered) && Columns(line) - Columns(line.TrimStart()) >= 2)))
                {
                    text.Append('\n').Append(StripHardBreak(line.TrimStart()));
                    continue;
                }
                Close();
                indents.Clear();
                open = new Block { Kind = BlockKind.Paragraph };
                text.Append(StripHardBreak(line.TrimStart()));
            }
            Close();
            return blocks;
        }

        static string StripHardBreak(string line) => line.EndsWith('\\') && !line.EndsWith("\\\\") ? line[..^1] : line;

        static int Columns(string s)
        {
            int cols = 0;
            foreach (char c in s)
            {
                if (c == '\t') cols += 4 - cols % 4;
                else if (c == ' ') cols++;
                else break;
            }
            return cols;
        }

        // ------------------------------------------------------------------ inline

        static readonly Regex SpanOpen = new(@"\G<span\s+style\s*=\s*[""']\s*color\s*:\s*(#[0-9a-fA-F]{3}|#[0-9a-fA-F]{6}|[a-zA-Z]+)\s*;?\s*[""']\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex AutoLink = new(@"\G<((?:https?|mailto|ftp):[^\s<>]+)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex BareLink = new(@"\G(?:https?://|www\.)[^\s<]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        static readonly Regex Break = new(@"\G<br\s*/?>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public static List<Span> ParseInline(string text)
        {
            var spans = new List<Span>();
            if (string.IsNullOrEmpty(text)) return spans;
            lookahead = 40L * text.Length + 50_000;
            Inline(text, 0, text.Length, new Span(), spans);
            return spans;
        }

        /// <summary>
        /// What looking ahead for closing delimiters may still cost (characters): text of many openers
        /// that never close would take quadratic time, so once it is spent they read as text.
        /// </summary>
        [ThreadStatic] static long lookahead;

        static void Add(List<Span> spans, Span look, string text)
        {
            if (text.Length == 0) return;
            if (spans.Count > 0 && spans[^1].SameLook(look)) spans[^1].Text += text;
            else spans.Add(look.With(text));
        }

        static void Inline(string s, int from, int to, Span look, List<Span> spans)
        {
            var plain = new StringBuilder();
            void Flush()
            {
                Add(spans, look, plain.ToString());
                plain.Clear();
            }

            int i = from;
            while (i < to)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < to && char.IsAsciiLetterOrDigit(s[i + 1]) == false && s[i + 1] > ' ' && s[i + 1] < 127)
                {
                    plain.Append(s[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '\n')
                {
                    Flush();
                    spans.Add(new Span { LineBreak = true, Text = "\n" });
                    i++;
                    continue;
                }
                if (c == '`')
                {
                    int run = RunLength(s, i, to, '`');
                    int close = FindCodeClose(s, i + run, to, run);
                    if (close >= 0)
                    {
                        Flush();
                        string code = s.Substring(i + run, close - i - run);
                        if (code.Length >= 2 && code[0] == ' ' && code[^1] == ' ' && code.Trim().Length > 0) code = code[1..^1];
                        var cl = look.With("");
                        cl.Code = true;
                        Add(spans, cl, code.Replace('\n', ' '));
                        i = close + run;
                        continue;
                    }
                    plain.Append(s, i, run);
                    i += run;
                    continue;
                }
                if (c == '<')
                {
                    Match m;
                    if ((m = Break.Match(s, i)).Success && m.Index + m.Length <= to)
                    {
                        Flush();
                        spans.Add(new Span { LineBreak = true, Text = "\n" });
                        i += m.Length;
                        continue;
                    }
                    if (StartsWith(s, i, to, "<u>"))
                    {
                        int close = FindTagClose(s, i + 3, to, "<u>", "</u>");
                        if (close >= 0)
                        {
                            Flush();
                            var u = look.With("");
                            u.Underline = true;
                            Inline(s, i + 3, close, u, spans);
                            i = close + 4;
                            continue;
                        }
                    }
                    if ((m = SpanOpen.Match(s, i)).Success && m.Index + m.Length <= to)
                    {
                        int close = FindTagClose(s, i + m.Length, to, "<span", "</span>");
                        if (close >= 0)
                        {
                            Flush();
                            var colored = look.With("");
                            colored.Color = ParseColor(m.Groups[1].Value) ?? look.Color;
                            Inline(s, i + m.Length, close, colored, spans);
                            i = close + 7;
                            continue;
                        }
                    }
                    if ((m = AutoLink.Match(s, i)).Success && m.Index + m.Length <= to)
                    {
                        Flush();
                        var link = look.With("");
                        link.Link = m.Groups[1].Value;
                        Add(spans, link, m.Groups[1].Value);
                        i += m.Length;
                        continue;
                    }
                }
                if (c == '*' || c == '_' || c == '~')
                {
                    if (Emphasis(s, ref i, to, look, spans, plain, Flush)) continue;
                }
                if (c == '[')
                {
                    int closeText = FindBracketClose(s, i + 1, to);
                    if (closeText >= 0 && closeText + 1 < to && s[closeText + 1] == '(')
                    {
                        int closeUrl = FindParenClose(s, closeText + 2, to);
                        if (closeUrl >= 0)
                        {
                            string target = s.Substring(closeText + 2, closeUrl - closeText - 2).Trim();
                            int title = target.IndexOfAny(new[] { ' ', '\t' });
                            if (title > 0) target = target.Substring(0, title);
                            target = target.Trim('<', '>');
                            if (target.Length > 0)
                            {
                                Flush();
                                var link = look.With("");
                                link.Link = target;
                                Inline(s, i + 1, closeText, link, spans);
                                i = closeUrl + 1;
                                continue;
                            }
                        }
                    }
                }
                if ((c == 'h' || c == 'H' || c == 'w' || c == 'W') && look.Link == null && (i == from || !char.IsLetterOrDigit(s[i - 1])))
                {
                    var m = BareLink.Match(s, i);
                    if (m.Success && m.Index + m.Length <= to)
                    {
                        string url = TrimUrl(m.Value);
                        Flush();
                        var link = look.With("");
                        link.Link = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
                        Add(spans, link, url);
                        i += url.Length;
                        continue;
                    }
                }
                plain.Append(c);
                i++;
            }
            Flush();
        }

        /// <summary>*, **, ***, _, __, ___ and ~~ at <paramref name="i"/>: true when it opened and closed.</summary>
        static bool Emphasis(string s, ref int i, int to, Span look, List<Span> spans, StringBuilder plain, Action flush)
        {
            char c = s[i];
            int run = RunLength(s, i, to, c);
            if (c == '~' && run < 2) return false;
            int after = i + run;
            if (after >= to || char.IsWhiteSpace(s[after])) return false;           // not left-flanking
            if (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1])) return false;    // intraword underscores are text
            foreach (int want in Tries(c, run))
            {
                int start = i + want;
                int close = FindDelimiterClose(s, start, to, c, want, 0);
                if (close < 0) continue;
                flush();
                var styled = look.With("");
                if (c == '~') styled.Strike = true;
                else if (want == 3) styled.Bold = styled.Italic = true;
                else if (want == 2) styled.Bold = true;
                else styled.Italic = true;
                Inline(s, start, close, styled, spans);
                i = close + want;
                return true;
            }
            return false;
        }

        static int RunLength(string s, int i, int to, char c)
        {
            int j = i;
            while (j < to && s[j] == c) j++;
            return j - i;
        }

        /// <summary>A closing run of exactly <paramref name="want"/> (or a longer run of stars ending it) after text.</summary>
        static int[] Tries(char c, int run) => c == '~' ? new[] { 2 } : run >= 3 ? new[] { 3, 2, 1 } : run == 2 ? new[] { 2, 1 } : new[] { 1 };

        /// <summary>How deep emphasis nests while looking for a close.</summary>
        const int MaxNesting = 6;

        /// <summary>
        /// Where emphasis opened by <paramref name="want"/> of <paramref name="c"/> closes: a run of at least
        /// as many that can close (after text), taking its first ones; emphasis that opens on the way
        /// (**a *b* c**) is passed over whole. -1 when it doesn't close.
        /// </summary>
        static int FindDelimiterClose(string s, int from, int to, char c, int want, int depth)
        {
            int i = from;
            while (i < to)
            {
                if (--lookahead < 0) return -1;
                char ch = s[i];
                if (ch == '\\') { i += 2; continue; }
                if (ch == '`')
                {
                    int run = RunLength(s, i, to, '`');
                    int close = FindCodeClose(s, i + run, to, run);
                    i = close >= 0 ? close + run : i + run;
                    continue;
                }
                if (ch == c)
                {
                    int run = RunLength(s, i, to, c);
                    bool canClose = i > from && !char.IsWhiteSpace(s[i - 1]) && (c != '_' || i + run >= s.Length || !char.IsLetterOrDigit(s[i + run]));
                    bool canOpen = i + run < to && !char.IsWhiteSpace(s[i + run]) && (c != '_' || i == 0 || !char.IsLetterOrDigit(s[i - 1]));
                    if (canClose && (run == want || (run > want && c != '~')))
                        return i;
                    if (canOpen && depth < MaxNesting && (c != '~' || run >= 2))
                    {
                        int end = -1;
                        foreach (int inner in Tries(c, run))
                        {
                            int close = FindDelimiterClose(s, i + inner, to, c, inner, depth + 1);
                            if (close >= 0)
                            {
                                end = close + inner;
                                break;
                            }
                        }
                        if (end > i)
                        {
                            i = end;
                            continue;
                        }
                    }
                    i += run;
                    continue;
                }
                i++;
            }
            return -1;
        }

        static int FindCodeClose(string s, int from, int to, int run)
        {
            int i = from;
            while (i < to)
            {
                if (--lookahead < 0) return -1;
                if (s[i] == '`')
                {
                    int r = RunLength(s, i, to, '`');
                    if (r == run) return i;
                    i += r;
                    continue;
                }
                i++;
            }
            return -1;
        }

        static int FindTagClose(string s, int from, int to, string open, string close)
        {
            int depth = 0;
            for (int i = from; i < to; i++)
            {
                if (--lookahead < 0) return -1;
                if (s[i] == '\\') { i++; continue; }
                if (StartsWith(s, i, to, close))
                {
                    if (depth == 0) return i;
                    depth--;
                }
                else if (StartsWith(s, i, to, open)) depth++;
            }
            return -1;
        }

        static int FindBracketClose(string s, int from, int to)
        {
            int depth = 0;
            for (int i = from; i < to; i++)
            {
                if (--lookahead < 0) return -1;
                char c = s[i];
                if (c == '\\') { i++; continue; }
                if (c == '`')
                {
                    int run = RunLength(s, i, to, '`');
                    int close = FindCodeClose(s, i + run, to, run);
                    if (close >= 0) i = close + run - 1;
                    continue;
                }
                if (c == '[') depth++;
                else if (c == ']')
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }

        static int FindParenClose(string s, int from, int to)
        {
            int depth = 0;
            for (int i = from; i < to; i++)
            {
                if (--lookahead < 0) return -1;
                char c = s[i];
                if (c == '\\') { i++; continue; }
                if (c == '\n') return -1;
                if (c == '(') depth++;
                else if (c == ')')
                {
                    if (depth == 0) return i;
                    depth--;
                }
            }
            return -1;
        }

        static bool StartsWith(string s, int i, int to, string what) =>
            i + what.Length <= to && string.Compare(s, i, what, 0, what.Length, StringComparison.OrdinalIgnoreCase) == 0;

        /// <summary>A bare URL without the punctuation that ends its sentence (and an unbalanced closing parenthesis).</summary>
        static string TrimUrl(string url)
        {
            while (url.Length > 0)
            {
                char last = url[^1];
                if (".,;:!?'\"*_~".IndexOf(last) >= 0) url = url[..^1];
                else if (last == ')' && url.Count(ch => ch == ')') > url.Count(ch => ch == '(')) url = url[..^1];
                else break;
            }
            return url;
        }

        static readonly Dictionary<string, uint> Named = new(StringComparer.OrdinalIgnoreCase)
        {
            ["red"] = 0xFFFF0000, ["green"] = 0xFF008000, ["blue"] = 0xFF0000FF, ["orange"] = 0xFFFFA500, ["yellow"] = 0xFFFFFF00,
            ["purple"] = 0xFF800080, ["gray"] = 0xFF808080, ["grey"] = 0xFF808080, ["white"] = 0xFFFFFFFF, ["black"] = 0xFF000000,
            ["pink"] = 0xFFFFC0CB, ["cyan"] = 0xFF00FFFF, ["magenta"] = 0xFFFF00FF, ["brown"] = 0xFFA52A2A, ["teal"] = 0xFF008080,
        };

        /// <summary>"#rgb", "#rrggbb" or a basic colour name, as 0xFFRRGGBB.</summary>
        internal static uint? ParseColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            if (value[0] != '#') return Named.TryGetValue(value, out uint named) ? named : null;
            string hex = value.Substring(1);
            if (hex.Length == 3) hex = string.Concat(hex.Select(ch => new string(ch, 2)));
            return hex.Length == 6 && uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb) ? 0xFF000000 | rgb : null;
        }

        // ------------------------------------------------------------------ writing Markdown

        /// <summary>Text with the characters that would start formatting escaped.</summary>
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var sb = new StringBuilder(text.Length + 8);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char prev = i > 0 ? text[i - 1] : ' ';
                char next = i + 1 < text.Length ? text[i + 1] : ' ';
                bool escape = c switch
                {
                    '\\' => next < 127 && char.IsPunctuation(next) || char.IsSymbol(next) && next < 127 || next == ' ' && i + 1 == text.Length,
                    '*' or '`' or '[' or ']' => true,
                    '_' => !char.IsLetterOrDigit(prev) || !char.IsLetterOrDigit(next),
                    '~' => next == '~' || prev == '~',
                    '<' => char.IsAsciiLetter(next) || next == '/' || next == '!',
                    _ => false,
                };
                if (escape) sb.Append('\\');
                sb.Append(c);
            }
            return sb.ToString();
        }

        static readonly Regex BlockStart = new(@"^(\s*)(#{1,6}(?:\s|$)|[-+*](?:\s|$)|>|(\d{1,9})([.)])(?:\s|$))", RegexOptions.Compiled);

        /// <summary>A line of text that would read as a heading, an item or a quote, escaped so it stays text.</summary>
        public static string EscapeLineStart(string line)
        {
            var m = BlockStart.Match(line);
            if (!m.Success) return RuleLine.IsMatch(line) ? "\\" + line : line;
            int at = m.Groups[1].Length;
            if (m.Groups[3].Success) return line.Substring(0, at + m.Groups[3].Length) + "\\" + line.Substring(at + m.Groups[3].Length);
            return line.Substring(0, at) + "\\" + line.Substring(at);
        }

        /// <summary>Plain text: each paragraph and item on its own line, list markers kept, formatting dropped.</summary>
        public static string ToPlainText(string markdown)
        {
            var sb = new StringBuilder();
            foreach (var b in Parse(markdown))
            {
                if (sb.Length > 0) sb.Append('\n');
                string prefix = b.Kind switch
                {
                    BlockKind.Bullet => new string(' ', 2 * b.Level) + "• ",
                    BlockKind.Numbered => new string(' ', 2 * b.Level) + b.Number + ". ",
                    BlockKind.Quote => "> ",
                    BlockKind.Rule => "———",
                    _ => "",
                };
                sb.Append(prefix);
                foreach (var span in b.Spans) sb.Append(span.LineBreak ? "\n" : span.Link != null && span.Text != span.Link ? $"{span.Text} ({span.Link})" : span.Text);
            }
            return sb.ToString();
        }
    }
}
