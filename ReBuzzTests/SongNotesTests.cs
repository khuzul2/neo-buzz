using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Serialization;
using ReBuzz.Core.Notes;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork (docs/specs/machine-safety.md §3.5): Song Notes on Windows, with the Info machine itself
    /// (WDE's Info.NET.dll, in RunTime). Its rich-text box's RTF and its State, serialized as ReBuzz
    /// saves a managed machine's state behind MachineManager's header, read as the importer reads a
    /// placeholder's data in the studio. And the notes an agent writes save with the song and open again.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SongNotesTests
    {
        static string? InfoDll()
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "RunTime", "Gear", "Generators", "Info.NET.dll");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        /// <summary>A document with what Info's toolbar makes: a large bold title, formats, a colour, lists, a link.</summary>
        static string DocumentRtf()
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph(new Run("Song title") { FontSize = 24, FontWeight = FontWeights.Bold }));
            var p = new Paragraph();
            p.Inlines.Add(new Run("Plain, "));
            p.Inlines.Add(new Bold(new Run("bold")));
            p.Inlines.Add(new Run(", "));
            p.Inlines.Add(new Italic(new Run("italic")));
            p.Inlines.Add(new Run(", "));
            p.Inlines.Add(new Underline(new Run("underlined")));
            p.Inlines.Add(new Run(", "));
            p.Inlines.Add(new Run("red") { Foreground = Brushes.Red });
            p.Inlines.Add(new Run(" and ü € “quotes” 音 {braces} back\\slash"));
            doc.Blocks.Add(p);
            var bullets = new List { MarkerStyle = TextMarkerStyle.Disc };
            bullets.ListItems.Add(new ListItem(new Paragraph(new Run("first"))));
            bullets.ListItems.Add(new ListItem(new Paragraph(new Run("second"))));
            doc.Blocks.Add(bullets);
            var numbers = new List { MarkerStyle = TextMarkerStyle.Decimal };
            numbers.ListItems.Add(new ListItem(new Paragraph(new Run("one"))));
            numbers.ListItems.Add(new ListItem(new Paragraph(new Run("two"))));
            doc.Blocks.Add(numbers);
            var link = new Paragraph();
            link.Inlines.Add(new Hyperlink(new Run("ReBuzz")) { NavigateUri = new Uri("https://github.com/wasteddesign/ReBuzz") });
            link.Inlines.Add(new LineBreak());
            link.Inlines.Add(new Run("after a line break\tand a tab"));
            doc.Blocks.Add(link);
            doc.Blocks.Add(new Paragraph(new Run("Small grey") { FontSize = 9, Foreground = Brushes.Gray }));
            using var ms = new MemoryStream();
            new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.Rtf);
            ms.Position = 0;
            return new StreamReader(ms).ReadToEnd();
        }

        const string Expected =
            "## Song title\n\n" +
            "Plain, **bold**, *italic*, <u>underlined</u>, <span style=\"color:#ff0000\">red</span> and ü € “quotes” 音 {braces} back\\slash\n\n" +
            "- first\n- second\n1. one\n2. two\n\n" +
            "[ReBuzz](https://github.com/wasteddesign/ReBuzz)\nafter a line break\tand a tab\n" +
            "<span style=\"color:#808080\">Small grey</span>";

        /// <summary>The machine's state as ManagedMachineHost.MachineState serializes it, behind MachineManager.GetMachineData's header.</summary>
        static byte[] MachineData(object state)
        {
            using var ms = new MemoryStream();
            var settings = new XmlWriterSettings { NamespaceHandling = NamespaceHandling.OmitDuplicates, NewLineOnAttributes = false, Indent = true };
            using (var w = XmlWriter.Create(ms, settings))
                new XmlSerializer(state.GetType()).Serialize(w, state);
            var xml = ms.ToArray();
            var data = new byte[5 + xml.Length];
            data[0] = 2;
            BitConverter.GetBytes(xml.Length).CopyTo(data, 1);
            xml.CopyTo(data, 5);
            return data;
        }

        [Test]
        public void InfosOwnStateReadsAsMarkdown()
        {
            string? dll = InfoDll();
            if (dll == null) Assert.Inconclusive("RunTime/Gear/Generators/Info.NET.dll isn't in this checkout");
            var asm = Assembly.LoadFrom(dll!);

            // The notes as Info's box holds them: its own SetRTF and GetRTF.
            var boxType = asm.GetType("WDE.Info.RTFBoxInfo", true)!;
            var box = Activator.CreateInstance(boxType)!;
            boxType.GetMethod("SetRTF")!.Invoke(box, new object[] { DocumentRtf() });
            string rtf = (string)boxType.GetMethod("GetRTF")!.Invoke(box, null)!;

            var stateType = asm.GetType("WDE.Info.InfoMachine+State", true)!;
            var state = RuntimeHelpers.GetUninitializedObject(stateType);     // its constructor reads the theme's colours
            stateType.GetProperty("Text")!.SetValue(state, rtf);
            stateType.GetProperty("Background")!.SetValue(state, Color.FromRgb(0x20, 0x21, 0x22));
            stateType.GetProperty("Foreground")!.SetValue(state, Color.FromRgb(0xE0, 0xE1, 0xE2));
            var data = MachineData(state);

            var read = InfoMachineState.Read(data);
            Assert.That(read, Is.Not.Null, "the importer reads Info's state");
            Assert.That(read!.Rtf, Is.EqualTo(rtf.Replace("\r\n", "\n")), "the RTF (XML keeps line ends as \\n)");
            Assert.That((read.Background, read.Foreground), Is.EqualTo(((uint?)0xFF202122, (uint?)0xFFE0E1E2)));
            Assert.That(read.ToMarkdown(), Is.EqualTo(Expected));

            // Saved again by Info (ReBuzz on Windows saves it each time): the same notes.
            boxType.GetMethod("SetRTF")!.Invoke(box, new object[] { rtf });
            string again = (string)boxType.GetMethod("GetRTF")!.Invoke(box, null)!;
            TestContext.Out.WriteLine(again == rtf ? "Info's RTF is the same when saved again" : "Info's RTF changes when saved again");
            Assert.That(RtfToMarkdown.Convert(again, 0xFFE0E1E2), Is.EqualTo(Expected), "the same Markdown, so it isn't imported again");

            // The importer's writer (tests, fixtures) writes the elements Info's serializer does, in its order.
            var ours = XDocument.Parse(Encoding.UTF8.GetString(InfoMachineState.Write(rtf), 5, InfoMachineState.Write(rtf).Length - 5).TrimStart((char)0xFEFF));
            var theirs = XDocument.Parse(Encoding.UTF8.GetString(data, 5, data.Length - 5).TrimStart((char)0xFEFF));
            Assert.That(ours.Descendants().Select(e => e.Name.LocalName), Is.EqualTo(theirs.Descendants().Select(e => e.Name.LocalName)));
            Assert.That((string)ours.Root!.Element("Background")!.Element("ScR"), Is.EqualTo((string)theirs.Root!.Element("Background")!.Element("ScR")));
        }

        static string TempSong(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), "ReBuzzSongNotesTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        public void TheAgentsNotesSaveWithTheSongAndOpenAgain(string extension)
        {
            using var rig = new AgentServerTests.Rig();
            const string notes = "# Ideas\n\n- **louder** chorus\n- [reference](https://example.com)\n\nü € 音";
            var (error, text, written) = rig.Call("song_notes_write", new JsonObject { ["text"] = notes });
            Assert.That(error, Is.False, text);
            Assert.That(written["notes"]!.GetValue<string>(), Is.EqualTo(notes));
            Assert.That(rig.Driver.Core.Modified, Is.True, "an edit marks the song");
            (error, text, _) = rig.Call("song_notes_write", new JsonObject { ["text"] = "Agent's", ["mode"] = "append" });
            Assert.That(error, Is.False, text);
            Assert.That(rig.Driver.Core.SongNotes.Text, Is.EqualTo(notes + "\n\nAgent's"));
            (error, text, _) = rig.Call("undo");
            Assert.That(error, Is.False, text);
            Assert.That(rig.Driver.Core.SongNotes.Text, Is.EqualTo(notes), "one undo step");

            string path = TempSong("notes" + extension);
            (error, text, _) = rig.Call("song_save", new JsonObject { ["path"] = path });
            Assert.That(error, Is.False, text);
            (error, text, _) = rig.Call("song_new", new JsonObject { ["discard_unsaved"] = true });
            Assert.That(error, Is.False, text);
            Assert.That(rig.Call("song_notes_read").Data["notes"]!.GetValue<string>(), Is.Empty, "a new song has none");
            (error, text, _) = rig.Call("song_open", new JsonObject { ["path"] = path, ["discard_unsaved"] = true });
            Assert.That(error, Is.False, text);
            var read = rig.Call("song_notes_read").Data;
            Assert.That(read["notes"]!.GetValue<string>(), Is.EqualTo(notes));
            Assert.That(read["imported_on_open"]!.AsArray(), Is.Empty);
            Assert.That(rig.Call("song_notes_read", new JsonObject { ["format"] = "text" }).Data["notes"]!.GetValue<string>(),
                Is.EqualTo("Ideas\n• louder chorus\n• reference (https://example.com)\nü € 音"));
        }
    }
}
