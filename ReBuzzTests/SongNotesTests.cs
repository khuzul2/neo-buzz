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
            TestContext.Out.WriteLine(again == rtf ? "[notice] Info's RTF is the same when its box saves it again" : "[notice] Info's RTF changes when its box saves it again");
            Assert.That(RtfToMarkdown.Convert(again, 0xFFE0E1E2), Is.EqualTo(Expected), "the same Markdown, so it isn't imported again");

            // The importer's writer (tests, fixtures) writes the elements Info's serializer does, in its order.
            var written = InfoMachineState.Write(rtf, 0xFF202122, 0xFFE0E1E2);
            var ours = XDocument.Parse(Encoding.UTF8.GetString(written, 5, written.Length - 5).TrimStart((char)0xFEFF));
            var theirs = XDocument.Parse(Encoding.UTF8.GetString(data, 5, data.Length - 5).TrimStart((char)0xFEFF));
            Assert.That(ours.Descendants().Select(e => e.Name.LocalName), Is.EqualTo(theirs.Descendants().Select(e => e.Name.LocalName)));
            foreach (var part in new[] { "A", "R", "G", "B", "ScA", "ScR", "ScG", "ScB" })
                Assert.That((string?)ours.Root!.Element("Foreground")!.Element(part), Is.EqualTo((string?)theirs.Root!.Element("Foreground")!.Element(part)), "Foreground " + part);
        }

        static void CopyFolder(string from, string to)
        {
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }

        /// <summary>
        /// A live Info machine (it loads on Windows since its theme fix, machine-safety.md §3.1): its text typed
        /// into its box, the song saved and opened again, the notes have it, from the machine's own state.
        /// </summary>
        [Test]
        public void ALiveInfoMachinesTextIsImported()
        {
            string? dll = InfoDll();
            if (dll == null) Assert.Inconclusive("RunTime/Gear/Generators/Info.NET.dll isn't in this checkout");
            var gear = new NeoMachineTests.NeoInfo("Info", "Info", generator: true);
            // Where the agent rig takes gear from (beside ReBuzz.exe); and the theme Info builds itself from, under
            // ReBuzz's folder (BuzzGUI.Common's, here).
            if (!File.Exists(gear.BuiltPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(gear.BuiltPath)!);
                File.Copy(dll!, gear.BuiltPath, overwrite: true);
            }
            string runTime = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dll!)!)!)!;
            CopyFolder(Path.Combine(runTime, "Themes", "Default", "Gear", "Info"), Path.Combine(BuzzGUI.Common.Global.BuzzPath, "Themes", "Default", "Gear", "Info"));

            using var rig = new AgentServerTests.Rig(gear);
            var (error, text, _) = rig.Call("machine_add", new JsonObject { ["gear"] = "Info", ["name"] = "Notes" });
            if (error)
            {
                TestContext.Out.WriteLine("[notice] A live Info machine didn't load in the test rig: " + text);
                Assert.Inconclusive("Info didn't load here: " + text);
            }
            var core = rig.Driver.Core;
            var machine = core.SongCore.MachinesList.Single(m => m.Name == "Notes");
            Assert.That(machine.DLL.IsMissing, Is.False);
            // Its text, as its window's box would have it.
            var infoMachine = core.MachineManager.ManagedMachines[machine].ManagedMachine;
            var box = infoMachine.GetType().GetField("rTFBox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(infoMachine)!;
            box.GetType().GetMethod("SetRTF")!.Invoke(box, new object[] { DocumentRtf() });
            Assert.That(InfoMachineState.Read(machine.Data)?.ToMarkdown(), Is.EqualTo(Expected), "the machine's data, as ReBuzz saves it");

            string path = TempSong("info.bmx");
            Assert.That(rig.Call("song_save", new JsonObject { ["path"] = path }).Error, Is.False);
            Assert.That(rig.Call("song_new", new JsonObject { ["discard_unsaved"] = true }).Error, Is.False);
            (error, text, _) = rig.Call("song_open", new JsonObject { ["path"] = path, ["discard_unsaved"] = true });
            Assert.That(error, Is.False, text);
            var read = rig.Call("song_notes_read").Data;
            TestContext.Out.WriteLine("[notice] A live Info machine's text, opened again: " + (read["notes"]!.GetValue<string>() == Expected ? "imported" : "not imported: " + read.ToJsonString()));
            Assert.That(read["notes"]!.GetValue<string>(), Is.EqualTo(Expected), "imported from the machine's state");
            Assert.That(read["imported_on_open"]!.AsArray().Single()!["machine"]!.GetValue<string>(), Is.EqualTo("Notes"));
            Assert.That(core.SongCore.MachinesList.Single(m => m.Name == "Notes").DLL.IsMissing, Is.False, "the machine stays, loaded");

            // Saved with the notes, opened again: in once.
            Assert.That(rig.Call("song_save", new JsonObject { ["path"] = path, ["overwrite"] = true }).Error, Is.False);
            Assert.That(rig.Call("song_open", new JsonObject { ["path"] = path, ["discard_unsaved"] = true }).Error, Is.False);
            read = rig.Call("song_notes_read").Data;
            Assert.That(read["notes"]!.GetValue<string>(), Is.EqualTo(Expected));
            Assert.That(read["imported_on_open"]!.AsArray(), Is.Empty, "not again");
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
