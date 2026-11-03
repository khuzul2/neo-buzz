// Fork (docs/specs/machine-safety.md §3.5): Song Notes' formats, without the engine. Markdown as the
// studio draws it (NotesMarkdown), the RTF reader that turns an Info machine's text into it
// (RtfToMarkdown), and Info's saved state (InfoMachineState), with what Info saved on Windows CI
// (ReBuzz.Studio.Tests/Fixtures/InfoMachineState.xml).

using System.Text;
using ReBuzz.Core.Notes;
using static ReBuzz.Core.Notes.NotesMarkdown;

namespace ReBuzz.SessionTests
{
    [TestFixture]
    public class NotesFormatTests
    {
        /// <summary>RTF written with ^ for its backslash.</summary>
        static string R(string text) => text.Replace('^', '\\');

        static string Md(string rtf, uint? foreground = null) => RtfToMarkdown.Convert(R(rtf), foreground);

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

        // ------------------------------------------------------------------ RTF

        [Test]
        public void ParagraphsBreaksAndCharacterFormats()
        {
            Assert.That(Md("{^rtf1^ansi Hello ^b bold^b0  ^i it^i0  ^ul u^ulnone  ^strike s^strike0^par Two^line lines^par}"),
                Is.EqualTo("Hello **bold** *it* <u>u</u> ~~s~~\nTwo\nlines"));
            Assert.That(Md("{^rtf1 {^b ^i both} {^b bold}{^i it}^plain  plain}"), Is.EqualTo("***both*** **bold**_it_ plain"), "neighbouring emphasis doesn't run together");
            Assert.That(Md("{^rtf1 no par at the end}"), Is.EqualTo("no par at the end"));
            Assert.That(Md("{^rtf1 a^par ^par ^par b^par}"), Is.EqualTo("a\n\nb"), "empty paragraphs are empty lines, one between paragraphs at most");
            Assert.That(Md("{^rtf1 spaced^sa240^par next^par}"), Is.EqualTo("spaced\n\nnext"), "space after a paragraph is an empty line");
            Assert.That(Md("{^rtf1 tab^tab here ^{braces^} back^^slash^~nbsp^emdash^endash^bullet^lquote x^rquote ^ldblquote y^rdblquote}"),
                Is.EqualTo("tab\there {braces} back\\slash\u00A0nbsp—–•‘x’“y”"));
        }

        [Test]
        public void UnicodeAndCodePages()
        {
            Assert.That(Md("{^rtf1^ansi^ansicpg1252^uc1 ^u8364^'80 ^u-26637? ^'fc^'80}"), Is.EqualTo("€ 音 ü€"));
            Assert.That(Md("{^rtf1^ansi^uc0 ^u8364 x}"), Is.EqualTo("€x"), "uc0: no fallback");
            Assert.That(Md("{^rtf1^ansi^uc2 ^u8364^'80^'80 x}"), Is.EqualTo("€ x"), "uc2: two fallback characters");
            Assert.That(Md("{^rtf1^ansi^uc1 ^u-10180?^u-8267?}"), Is.EqualTo("🎵"), "a surrogate pair");
            Assert.That(Md("{^rtf1^ansi^ansicpg1251 ^'cf^'f0^'e8^'e2^'e5^'f2}"), Is.EqualTo("Привет"));
            Assert.That(Md("{^rtf1^ansi^ansicpg932 ^'82^'a0^'82^'a2}"), Is.EqualTo("あい"), "double-byte characters together");
            Assert.That(Md("{^rtf1^ansi{^fonttbl{^f0^fcharset0 Arial;}{^f1^fcharset204 Arial Cyr;}}^f1 ^'c4^f0 ^'c4}"), Is.EqualTo("ДÄ"), "a font's character set");
        }

        [Test]
        public void WhatIsntTextIsSkipped()
        {
            Assert.That(Md("{^rtf1{^fonttbl{^f0 Arial;}}{^colortbl;^red255^green0^blue0;}{^stylesheet{^s0 Normal;}}{^info{^title T}{^author A}}" +
                           "{^*^generator Riched20;}{^*^unknown {nested} stuff}{^pict^wmetafile8 0102abcd}{^header H}text}"), Is.EqualTo("text"));
        }

        [Test]
        public void SizesMakeHeadingsAndColoursSpans()
        {
            // The body is the size and colour most of the text has.
            string rtf = "{^rtf1{^colortbl;^red0^green0^blue0;^red255^green0^blue0;^red0^green128^blue0;}^fs24^cf1 " +
                         "{^fs48 Big}^par{^fs36 Bigger than body}^par{^fs29 A bit}^par{^fs26 Hardly}^par Body text in black, {^cf2 red} and {^cf3 green}.^par}";
            Assert.That(Md(rtf), Is.EqualTo("# Big\n\n## Bigger than body\n\n### A bit\n\nHardly\nBody text in black, <span style=\"color:#ff0000\">red</span> and <span style=\"color:#008000\">green</span>."));
            // The default colour (the Info machine's foreground) needs none.
            Assert.That(Md("{^rtf1{^colortbl;^red224^green225^blue226;^red255^green0^blue0;}{^cf2 most of the text is red}, {^cf1 some isn't}}", 0xFFE0E1E2),
                Is.EqualTo("most of the text is red, some isn't"));
        }

        [Test]
        public void ListsAndLinks()
        {
            // As Word and WordPad write them: \ls and \ilvl, with \listtext.
            string word = "{^rtf1{^*^listtable{^list{^listlevel{^leveltext ^'01^'b7;}}}}" +
                          "^pard^ls1^ilvl0{^listtext ^'b7^tab}one^par{^listtext ^'b7^tab}two^par^pard^ls1^ilvl1{^listtext o^tab}deeper^par" +
                          "^pard^ls2{^listtext 1.^tab}first^par{^listtext 2.^tab}second^line more^par^pard after^par}";
            Assert.That(Md(word), Is.EqualTo("- one\n- two\n  - deeper\n1. first\n2. second\n   more\n\nafter"));
            // Hyperlink fields, with and without \*.
            Assert.That(Md("{^rtf1 see {^field{^*^fldinst{ HYPERLINK \"https://a.b/c\" ^^o \"tip\"}}{^fldrslt{^ul^cf1 the site}}} and " +
                           "{^field{^fldinst HYPERLINK https://x.y}{^fldrslt x}} and {^field{^*^fldinst PAGE}{^fldrslt 3}}}"),
                Is.EqualTo("see [the site](https://a.b/c) and [x](https://x.y) and 3"));
        }

        [Test]
        public void TextThatLooksLikeMarkdownStaysText()
        {
            Assert.That(Md("{^rtf1 # not a heading^par - not a list^par 1. not numbered^par a *star* and [brackets] and <u>^par}"),
                Is.EqualTo("\\# not a heading\n\\- not a list\n1\\. not numbered\na \\*star\\* and \\[brackets\\] and \\<u>"));
            Assert.That(ToPlainText(Md("{^rtf1 # not a heading^par a *star* and [brackets] and <u>^par}")), Is.EqualTo("# not a heading\na *star* and [brackets] and <u>"));
        }

        [Test]
        public void BrokenRtfGivesWhatItCan()
        {
            Assert.That(Md(""), Is.Empty);
            Assert.That(Md("{^rtf1 unclosed {^b group"), Is.EqualTo("unclosed **group**"));
            Assert.That(Md("{^rtf1 too many}}} closings}"), Is.EqualTo("too many closings"));
            Assert.That(Md("{^rtf1 bad hex ^'zz and ^'4"), Does.StartWith("bad hex"));
        }

        // ------------------------------------------------------------------ Info's saved state

        static string Fixture
        {
            get
            {
                string? dir = AppContext.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "ReBuzz.sln"))) dir = Path.GetDirectoryName(dir);
                Assume.That(dir, Is.Not.Null, "the repository");
                return Path.Combine(dir!, "ReBuzz.Studio.Tests", "Fixtures", "InfoMachineState.xml");
            }
        }

        static byte[] WithHeader(byte[] xml)
        {
            var data = new byte[5 + xml.Length];
            data[0] = 2;
            BitConverter.GetBytes(xml.Length).CopyTo(data, 1);
            xml.CopyTo(data, 5);
            return data;
        }

        [Test]
        public void WhatInfoSavedOnWindowsReadsAsMarkdown()
        {
            var xml = File.ReadAllBytes(Fixture);
            var state = InfoMachineState.Read(WithHeader(xml));
            Assert.That(state, Is.Not.Null);
            Assert.That(state!.Rtf, Does.StartWith(R("{^rtf1^ansi^ansicpg1252^uc1^htmautsp")));
            Assert.That(state.Background, Is.EqualTo(0xFF202122));
            Assert.That(state.Foreground, Is.EqualTo(0xFFE0E1E2));
            Assert.That(state.ToMarkdown(), Is.EqualTo(
                "## Song title\n\n" +
                "Plain, **bold**, *italic*, <u>underlined</u>, <span style=\"color:#ff0000\">red</span> and ü € “quotes” 音 {braces} back\\slash\n\n" +
                "- first\n- second\n1. one\n2. two\n\n" +
                "[ReBuzz](https://github.com/wasteddesign/ReBuzz)\nafter a line break\tand a tab\n" +
                "<span style=\"color:#808080\">Small grey</span>"));
            Assert.That(InfoMachineState.Read(xml)!.Rtf, Is.EqualTo(state.Rtf), "the XML alone reads too");
        }

        [Test]
        public void StateWrittenHereReadsBack()
        {
            string rtf = R("{^rtf1^ansi {^b Notes} & <more>^par}");
            var data = InfoMachineState.Write(rtf, 0xFF102030, 0xFFF0E0D0);
            Assert.That(data[0], Is.EqualTo(2), "the version");
            Assert.That(BitConverter.ToInt32(data, 1), Is.EqualTo(data.Length - 5), "the size");
            var state = InfoMachineState.Read(data)!;
            Assert.That(state.Rtf, Is.EqualTo(rtf));
            Assert.That((state.Background, state.Foreground), Is.EqualTo((0xFF102030u, 0xFFF0E0D0u)));
            Assert.That(state.ToMarkdown(), Is.EqualTo("**Notes** & \\<more>"));
            // The same elements, in the same order, as Info's own (XmlSerializer of WDE.Info.InfoMachine+State).
            var ours = System.Xml.Linq.XDocument.Parse(Encoding.UTF8.GetString(data, 5, data.Length - 5).TrimStart((char)0xFEFF));
            var theirs = System.Xml.Linq.XDocument.Load(Fixture);
            Assert.That(ours.Descendants().Select(e => e.Name.LocalName), Is.EqualTo(theirs.Descendants().Select(e => e.Name.LocalName)));
        }

        [Test]
        public void DataThatIsntInfosReadsAsNothing()
        {
            Assert.That(InfoMachineState.Read(null), Is.Null);
            Assert.That(InfoMachineState.Read(Array.Empty<byte>()), Is.Null);
            Assert.That(InfoMachineState.Read(new byte[] { 2, 0, 0, 0, 0 }), Is.Null);
            Assert.That(InfoMachineState.Read(WithHeader(Encoding.UTF8.GetBytes("<Other><Text>x</Text></Other>"))), Is.Null);
            Assert.That(InfoMachineState.Read(new byte[] { 1, 2, 3, 4, 5, 6, 7 }), Is.Null);
        }
    }
}
