// Fork (docs/specs/machine-safety.md §3.5): Song Notes' Markdown, as the studio draws it (NotesMarkdown), without the engine.

using System.Text;
using ReBuzz.Core.Notes;
using static ReBuzz.Core.Notes.NotesMarkdown;

namespace ReBuzz.SessionTests
{
    [TestFixture]
    public class NotesFormatTests
    {
        /// <summary>The spans as text: **bold**, *italic*, _underline_, ~strike~, `code`, [text](url), {#rrggbb text}, / for a break.</summary>
        static string Show(IEnumerable<Span> spans)
        {
            var sb = new StringBuilder();
            foreach (var s in spans)
            {
                if (s.LineBreak) { sb.Append('/'); continue; }
                string t = s.Text;
                if (s.Code) t = "`" + t + "`";
                if (s.Italic) t = "*" + t + "*";
                if (s.Bold) t = "**" + t + "**";
                if (s.Underline) t = "_" + t + "_";
                if (s.Strike) t = "~" + t + "~";
                if (s.Link != null) t = "[" + t + "](" + s.Link + ")";
                if (s.Color is uint c) t = "{#" + (c & 0xFFFFFF).ToString("x6") + " " + t + "}";
                sb.Append(t);
            }
            return sb.ToString();
        }

        static string Blocks(string markdown) => string.Join(" | ", Parse(markdown).Select(b => b.Kind switch
        {
            BlockKind.Heading => $"H{b.Level} {Show(b.Spans)}",
            BlockKind.Bullet => $"•{b.Level} {Show(b.Spans)}",
            BlockKind.Numbered => $"{b.Number}.{b.Level} {Show(b.Spans)}",
            BlockKind.Quote => $"> {Show(b.Spans)}",
            BlockKind.Rule => "---",
            _ => $"P {Show(b.Spans)}",
        }));

        // ------------------------------------------------------------------ Markdown

        [TestCase("plain text", "plain text")]
        [TestCase("**bold** and *italic* and _also_ and __strong__", "**bold** and *italic* and *also* and **strong**")]
        [TestCase("***both***", "***both***")]
        [TestCase("**a *b* c**", "**a *****b***** c**")]
        [TestCase("*a **b** c*", "*a ****b**** c*")]
        [TestCase("**a *b***", "**a *****b***")]
        [TestCase("***a** b*", "***a**** b*")]
        [TestCase("*a **b***", "*a ****b***")]
        [TestCase("snake_case_name and 2 * 3 * 4", "snake_case_name and 2 * 3 * 4")]
        [TestCase("~~gone~~ and ~one~", "~gone~ and ~one~")]
        [TestCase("`co*de*` and `` a`b ``", "`co*de*` and `a`b`")]
        [TestCase(@"\*literal\* \_x\_ \[y\] \\", @"*literal* _x_ [y] \")]
        [TestCase("<u>under</u> <span style=\"color:#f80\">orange</span> <span style='color: red'>red</span>", "_under_ {#ff8800 orange} {#ff0000 red}")]
        [TestCase("one<br>two<br/>three", "one/two/three")]
        [TestCase("[text](https://x.y/a_(b)) [**bold** link](<https://a.b/c d>)", "[text](https://x.y/a_(b)) [**bold**](https://a.b/c)[ link](https://a.b/c)")]
        [TestCase("see <https://a.b/c> and https://a.b/c. and (www.example.com/x)", "see [https://a.b/c](https://a.b/c) and [https://a.b/c](https://a.b/c). and ([www.example.com/x](https://www.example.com/x))")]
        [TestCase("an * alone, ** two, *unclosed", "an * alone, ** two, *unclosed")]
        [TestCase("intra**word**bold and a**b", "intra**word**bold and a**b")]
        public void InlineFormats(string markdown, string expected)
        {
            Assert.That(Show(ParseInline(markdown)), Is.EqualTo(expected));
        }

        [Test]
        public void LinkTextKeepsItsFormatsAndTheAddressItsTitleOut()
        {
            var spans = ParseInline("[**bold** link](https://a.b/c \"title\")");
            Assert.That(spans.Select(s => (s.Text, s.Bold, s.Link)), Is.EqualTo(new[] { ("bold", true, "https://a.b/c"), (" link", false, "https://a.b/c") }));
        }

        [Test]
        public void Blocks_()
        {
            Assert.That(Blocks("# One\n## Two\n### Three\n#### Four\n#notaheading"), Is.EqualTo("H1 One | H2 Two | H3 Three | H4 Four | P #notaheading"));
            Assert.That(Blocks("line one\nline two\n\nnew paragraph"), Is.EqualTo("P line one/line two | P new paragraph"));
            Assert.That(Blocks("- a\n  - b\n    * c\n  - d\n- e\n\n3. three\n4) four"),
                Is.EqualTo("•0 a | •1 b | •2 c | •1 d | •0 e | 3.0 three | 4.0 four"));
            Assert.That(Blocks("- item\n  continued\nnot the item"), Is.EqualTo("•0 item/continued | P not the item"));
            Assert.That(Blocks("> quoted\n> more\n\n---\n***\nafter"), Is.EqualTo("> quoted/more | --- | --- | P after"));
            Assert.That(Blocks("trailing break\\\nnext"), Is.EqualTo("P trailing break/next"));
            Assert.That(Blocks(""), Is.Empty);
            Assert.That(Blocks("\n\n  \n"), Is.Empty);
            Assert.That(Blocks("a\r\nb"), Is.EqualTo("P a/b"));
        }

        [TestCase("plain")]
        [TestCase("stars * and ** and *** here")]
        [TestCase("*not italic* **not bold** _no_ __no__ ~~no~~ `no`")]
        [TestCase("[not a link](x) <u>no</u> <span style=\"color:red\">no</span> <br>")]
        [TestCase(@"back\slash \* \\ ends with \")]
        [TestCase("snake_case and _leading and trailing_")]
        [TestCase("ü € “quotes” 音 {braces}")]
        public void EscapedTextReadsAsItself(string text)
        {
            var spans = ParseInline(Escape(text));
            Assert.That(spans, Has.Count.EqualTo(1), Escape(text));
            Assert.That(spans[0].Text, Is.EqualTo(text));
            Assert.That(spans[0].Bold || spans[0].Italic || spans[0].Underline || spans[0].Strike || spans[0].Code || spans[0].Link != null || spans[0].Color != null, Is.False);
        }

        [TestCase("# not a heading", "\\# not a heading")]
        [TestCase("- not an item", "\\- not an item")]
        [TestCase("  + not either", "  \\+ not either")]
        [TestCase("1. not numbered", "1\\. not numbered")]
        [TestCase("12) nor this", "12\\) nor this")]
        [TestCase("> not quoted", "\\> not quoted")]
        [TestCase("---", "\\---")]
        [TestCase("#hashtag", "#hashtag")]
        [TestCase("1.5 is a number", "1.5 is a number")]
        public void LineStartsStayText(string line, string escaped)
        {
            Assert.That(EscapeLineStart(line), Is.EqualTo(escaped));
            Assert.That(Blocks(escaped), Is.EqualTo("P " + line.TrimStart()));
        }

        [Test]
        public void OddTextParsesQuickly()
        {
            // The longest notes an agent may write, of openers that never close, brackets and backticks.
            foreach (var unit in new[] { "*a ", "**a ", "_a ", "[a ", "`a ", "~~a ", "<u>a ", "a\\" })
            {
                string text = string.Concat(Enumerable.Repeat(unit, 200_000 / unit.Length));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var blocks = Parse(text);
                Assert.That(blocks, Has.Count.EqualTo(1));
                Assert.That(watch.Elapsed.TotalSeconds, Is.LessThan(5), unit);
            }
        }

        [Test]
        public void PlainText()
        {
            Assert.That(ToPlainText("# Title\n\nSome **bold** [link](https://a.b)\n- one\n  - two\n1. first\n> q\n---"),
                Is.EqualTo("Title\nSome bold link (https://a.b)\n• one\n  • two\n1. first\n> q\n———"));
        }
    }
}
