// Fork (docs/specs/machine-safety.md §3.5): the Song Notes tab, headless. Notes typed with the
// keyboard take formats from the toolbar and the keys and read as they look; every edit is a step on
// the song's undo history and marks the song modified; they save and open again in .bmx and .bmxml.
// A song made on Windows with an Info machine (WDE's rich-text notes machine; here a placeholder that
// keeps its data, Fixtures/InfoMachineState.xml being what Info saved on Windows CI) opens with the
// machine's text in the notes, marked as imported, and saves back unchanged in its core sections
// (tools/song-roundtrip.py --compare). The song's own info text comes in too. Agents read and write
// the notes (song_notes_read, song_notes_write). Screenshots: song-notes*.png.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Notes;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class SongNotesTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static SongNotesStore Notes => Core.SongNotes;

        DateTime clock;

        [SetUp]
        public void Fresh()
        {
            clock = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
            SongNotesStore.Now = () => clock;
        }

        [TearDown]
        public void PutBack()
        {
            SongNotesStore.Now = () => DateTime.UtcNow;
            SongFiles.Picker = null;
            SongNotesView.OpenLink = null;
            Dialogs.Responder = null;
        }

        static string Folder(string name)
        {
            string dir = Path.Combine(Path.GetTempPath(), $"rebuzz-studio-notes-{Environment.ProcessId}", name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A new song in a new window, on the Song Notes tab (F10, as Buzz's Info view).</summary>
        static (MainWindow Window, SongNotesView View) OpenNotes()
        {
            var (window, _) = OpenEmpty();
            Key(window, PhysicalKey.F10);
            Assert.That((window.Tabs.SelectedItem as TabItem)?.Header, Is.EqualTo("Song Notes"), "F10 shows Song Notes");
            var view = (SongNotesView)window.Tabs.SelectedContent;
            view.Editor.Focus();
            Settle();
            return (window, view);
        }

        static void Key(MainWindow window, PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
        {
            window.KeyPressQwerty(key, mods);
            window.KeyReleaseQwerty(key, mods);
            Settle();
        }

        static void Type(MainWindow window, string text)
        {
            foreach (char c in text)
            {
                if (c == '\n') Key(window, PhysicalKey.Enter);
                else window.KeyTextInput(c.ToString());
            }
            Settle();
        }

        static void Select(SongNotesView view, string what, int nth = 0)
        {
            string t = view.Editor.Text ?? "";
            int at = -1;
            for (int i = 0; i <= nth; i++) at = t.IndexOf(what, at + 1, StringComparison.Ordinal);
            Assert.That(at, Is.GreaterThanOrEqualTo(0), $"\"{what}\" is in the editor");
            view.Editor.SelectionStart = at;
            view.Editor.SelectionEnd = at + what.Length;
        }

        static void CaretToEnd(SongNotesView view)
        {
            int end = (view.Editor.Text ?? "").Length;
            view.Editor.SelectionStart = view.Editor.SelectionEnd = end;
            view.Editor.CaretIndex = end;
        }

        static void Undo(MainWindow window) => Key(window, PhysicalKey.Z, Cmd);

        static void Redo(MainWindow window)
        {
            if (OperatingSystem.IsMacOS()) Key(window, PhysicalKey.Z, Cmd | RawInputModifiers.Shift);
            else Key(window, PhysicalKey.Y, Cmd);
        }

        /// <summary>Edit ▸ <paramref name="header"/>: the menu bar's entry on a Mac, the window's menu item elsewhere.</summary>
        static void Edit(MainWindow window, string header)
        {
            if (OperatingSystem.IsMacOS())
            {
                var entry = AppMenu.Find(window.MenuModel(mac: true), "Edit/" + header.Replace("_", ""));
                Assert.That(entry, Is.Not.Null, $"the Edit menu has {header}");
                Assert.That(entry!.CanRun?.Invoke() ?? true, Is.True, $"{header} is enabled");
                entry.Run();
            }
            else
            {
                var item = window.GetLogicalDescendants().OfType<MenuItem>().Single(m => m.Header as string == header);
                Assert.That(item.Command!.CanExecute(null), Is.True, $"{header} is enabled");
                item.Command.Execute(null);
            }
            Settle();
        }

        static void Open(MainWindow window, string path)
        {
            SongFiles.Picker = saving => saving ? null : path;
            Key(window, PhysicalKey.O, Cmd);
            Assert.That(Core.SongCore.SongName, Is.EqualTo(path), path + " opened");
            Settle();
        }

        static void SaveAs(MainWindow window, string path)
        {
            if (File.Exists(path)) File.Delete(path);
            SongFiles.Picker = saving => saving ? path : null;
            Key(window, PhysicalKey.S, Cmd | RawInputModifiers.Shift);
            Assert.That(File.Exists(path), Is.True, path + " saved");
            Assert.That(Core.Modified, Is.False, "saved");
        }

        /// <summary>The page's blocks as text: "H2 Mix notes | P Vocal **louder** | • Bass | …".</summary>
        static string Page(SongNotesView view)
        {
            var parts = new List<string>();
            foreach (var c in view.Page.Children)
            {
                switch (c)
                {
                    case SelectableTextBlock t when t.FontWeight == FontWeight.Bold && t.FontSize > 14:
                        parts.Add("H " + Plain(t));
                        break;
                    case SelectableTextBlock t:
                        parts.Add("P " + Plain(t));
                        break;
                    case Grid g:
                        var marker = g.Children.OfType<TextBlock>().First(x => x is not SelectableTextBlock).Text;
                        parts.Add(marker + " " + Plain(g.Children.OfType<SelectableTextBlock>().Single()));
                        break;
                    case TextBlock t:
                        parts.Add("(" + t.Text + ")");
                        break;
                    default:
                        parts.Add(c.GetType().Name);
                        break;
                }
            }
            return string.Join(" | ", parts);
        }

        /// <summary>A text block's runs, with bold as **, italic as *, underline as _, links as [text](url).</summary>
        static string Plain(SelectableTextBlock t)
        {
            var sb = new StringBuilder();
            foreach (var r in t.Inlines!.OfType<Run>())
            {
                string s = r.Text == "\n" ? "/" : r.Text;
                bool link = r.Foreground is ISolidColorBrush b && b.Color == Color.FromRgb(0x6C, 0xB4, 0xFF);
                if (r.FontWeight == FontWeight.Bold && t.FontWeight != FontWeight.Bold) s = "**" + s + "**";
                if (r.FontStyle == FontStyle.Italic) s = "*" + s + "*";
                if (!link && r.TextDecorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true) s = "_" + s + "_";
                if (link) s = "[" + s + "]";
                sb.Append(s);
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ typing and formats

        [AvaloniaTest]
        public void TypedNotesTakeFormatsAndReadAsTheyLook()
        {
            var (window, view) = OpenNotes();
            try
            {
                Assert.That(Notes.Text, Is.Empty, "a new song has no notes");
                Assert.That(Page(view), Is.EqualTo("(The notes appear here as they read.)"));

                Type(window, "Mix notes");
                view.ToolButton("H").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.That(Notes.Text, Is.EqualTo("# Mix notes"), "H makes the line a heading");
                Assert.That(Core.Modified, Is.True, "an edit marks the song");

                CaretToEnd(view);
                Type(window, "\n\nVocal louder in the chorus");
                Select(view, "louder");
                Key(window, PhysicalKey.B, Cmd);
                Assert.That(Notes.Text, Is.EqualTo("# Mix notes\n\nVocal **louder** in the chorus"), "Ctrl/Command+B");
                Select(view, "chorus");
                Key(window, PhysicalKey.I, Cmd);
                Select(view, "Vocal");
                Key(window, PhysicalKey.U, Cmd);

                CaretToEnd(view);
                Type(window, "\nBass\nDrums");
                Select(view, "Bass\nDrums");
                view.ToolButton("•").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                CaretToEnd(view);
                Type(window, "\nReference");
                Select(view, "Reference");
                Key(window, PhysicalKey.K, Cmd);
                Assert.That(view.Editor.SelectedText, Is.EqualTo("https://"), "the address is selected, to type over");
                Type(window, "https://example.com/ref");
                CaretToEnd(view);
                Type(window, "\nThen master it\nOne more pass");
                Select(view, "Then master it\nOne more pass");
                view.ToolButton("1.").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();

                Assert.That(Notes.Text, Is.EqualTo(
                    "# Mix notes\n\n<u>Vocal</u> **louder** in the *chorus*\n- Bass\n- Drums\n[Reference](https://example.com/ref)\n1. Then master it\n2. One more pass"));
                Assert.That(Page(view), Is.EqualTo(
                    "H Mix notes | P _Vocal_ **louder** in the *chorus* | • Bass | • Drums | P [Reference] | 1. Then master it | 2. One more pass"));
                Studio.Screenshot(window, "song-notes");

                // Again takes a format off.
                Select(view, "louder");
                Key(window, PhysicalKey.B, Cmd);
                Assert.That(Notes.Text, Does.Contain("Vocal</u> louder in"), "Ctrl/Command+B again: plain");
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void EditsAreStepsOnTheSongsUndoHistory()
        {
            var (window, view) = OpenNotes();
            try
            {
                Type(window, "first");
                clock += TimeSpan.FromSeconds(0.5);
                Type(window, " take");
                Assert.That(Notes.Text, Is.EqualTo("first take"));
                clock += TimeSpan.FromSeconds(5);           // a pause: the next typing is a step of its own
                Type(window, " here");
                Select(view, "first");
                Key(window, PhysicalKey.B, Cmd);
                Assert.That(Notes.Text, Is.EqualTo("**first** take here"));
                Assert.That(Core.Modified, Is.True);

                Undo(window);
                Assert.That(Notes.Text, Is.EqualTo("first take here"), "Ctrl/Command+Z: the format");
                Assert.That(view.Editor.Text, Is.EqualTo("first take here"), "the editor shows it");
                Undo(window);
                Assert.That(Notes.Text, Is.EqualTo("first take"), "then the typing after the pause");
                Undo(window);
                Assert.That(Notes.Text, Is.Empty, "then the first run of typing, as one step");
                Redo(window);
                Assert.That(Notes.Text, Is.EqualTo("first take"));
                Edit(window, "_Redo");
                Assert.That(Notes.Text, Is.EqualTo("first take here"), "Edit ▸ Redo, with the editor focused");
                Edit(window, "_Undo");
                Assert.That(Notes.Text, Is.EqualTo("first take"), "Edit ▸ Undo is the song's too");

                // The steps are the song's: its history undoes them, whoever asks.
                CaretToEnd(view);
                Type(window, "!");
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Notes.Text, Is.EqualTo("first take"), "the song's history undoes the notes");
                Assert.That(view.Editor.Text, Is.EqualTo("first take"));
                Assert.That(Core.Modified, Is.True);
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ song files

        [AvaloniaTest]
        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        public void NotesSaveWithTheSongAndOpenAgain(string extension)
        {
            const string notes = "# Ideas\n\nA **bold** line, *italic*, <u>under</u>, ~~gone~~ and `code`\nnext line: ü € 音\n\n- one\n  - nested\n1. first\n> quoted\n" +
                                 "[ReBuzz](https://github.com/wasteddesign/ReBuzz) <span style=\"color:#ff8000\">orange</span>";
            var (window, view) = OpenNotes();
            try
            {
                Type(window, "x");
                Notes.Write(notes);
                Settle();
                Assert.That(view.Editor.Text, Is.EqualTo(notes), "the editor follows the notes");
                string path = Path.Combine(Folder("files"), "notes" + extension);
                SaveAs(window, path);

                SongFiles.New().Wait();
                Settle();
                Assert.That(Notes.Text, Is.Empty, "a new song has none");
                Assert.That(view.Editor.Text ?? "", Is.Empty);

                Open(window, path);
                Assert.That(Notes.Text, Is.EqualTo(notes), "the same notes");
                Assert.That(view.Editor.Text, Is.EqualTo(notes));
                Assert.That(Notes.JustImported, Is.Empty, "nothing imported");
                Assert.That(view.Banner.IsVisible, Is.False);
                Assert.That(Core.Modified, Is.False, "opening isn't an edit");
                Assert.That(Page(view), Does.StartWith("H Ideas | P A **bold** line, *italic*, _under_, gone and code/next line: ü € 音 | • one | ◦ nested | 1. first"));
                Studio.Screenshot(window, "song-notes-reopened" + extension.Replace('.', '-'));

                if (extension == ".bmxml")
                {
                    var sub = XDocument.Load(path).Descendants("SubSection").SingleOrDefault(s => (string)s.Element("Name") == SongNotesStore.SubSectionName);
                    Assert.That(sub, Is.Not.Null, "a SongNotes subsection");
                }
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void ASongWithoutNotesSavesNoSubsection()
        {
            var (window, _) = OpenNotes();
            try
            {
                Assert.That(Notes.ToXml(), Is.Null);
                string path = Path.Combine(Folder("files"), "plain.bmxml");
                Core.SaveSongCopy(path);
                Assert.That(XDocument.Load(path).Descendants("SubSection").Any(s => (string)s.Element("Name") == SongNotesStore.SubSectionName), Is.False,
                    "a song without notes saves as upstream does");
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ importing

        /// <summary>The machine data an Info machine saved on Windows (Fixtures/InfoMachineState.xml, with ReBuzz's header).</summary>
        static byte[] InfoData()
        {
            var xml = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "InfoMachineState.xml"));
            var data = new byte[5 + xml.Length];
            data[0] = 2;
            BitConverter.GetBytes(xml.Length).CopyTo(data, 1);
            xml.CopyTo(data, 5);
            return data;
        }

        /// <summary>
        /// A .bmx as ReBuzz on Windows saves a song with an Info machine and no Song Notes: made from a
        /// test machine renamed Info in the .bmxml, opened (a placeholder now) and saved without notes.
        /// </summary>
        static string InfoSong(MainWindow window, string dir, byte[] data, string infoText = null)
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            Core.SongCore.CreateMachine("Guard Test", null, "Info", null, null, null, -1, 0.4f, -0.4f);
            Core.InfoText = infoText ?? "";
            string seed = Path.Combine(dir, "seed.bmxml");
            Core.SaveSongCopy(seed);
            var doc = XDocument.Load(seed);
            var machine = doc.Descendants("Machine").Single(m => (string)m.Element("Name") == "Info");
            machine.Element("Library")!.Value = "Info";
            var dataElement = machine.Element("Data");
            if (dataElement == null) machine.Element("Library")!.AddAfterSelf(dataElement = new XElement("Data"));
            dataElement.Value = Convert.ToBase64String(data);
            // Info's one parameter (a switch it never reads), as its PARA says.
            var global = machine.Descendants("ParameterGroup").Single(g => (string)g.Element("Type") == "Global");
            global.Element("Parameters")!.ReplaceNodes(new XElement("Parameter",
                new XElement("Type", "Switch"), new XElement("Name", "Dummy"), new XElement("Description", "Dummy"),
                new XElement("MinValue", 0), new XElement("MaxValue", 1), new XElement("NoValue", 255), new XElement("Flags", "State"),
                new XElement("DefValue", 0), new XElement("Values", new XElement("Value", new XElement("Track", 0), new XElement("Value", 0)))));
            machine.Descendants("Patterns").ToList().ForEach(p => p.RemoveNodes());
            doc.Save(seed);

            Core.Modified = false;
            Assert.That(Core.TryOpenSongFile(seed), Is.True, "the seed opens");
            Settle();
            var info = Core.SongCore.MachinesList.Single(m => m.Name == "Info");
            Assert.That(info.DLL.IsMissing, Is.True, "Info is a placeholder here");
            Notes.Clear();                  // as ReBuzz on Windows saves it: no SongNotes
            string first = Path.Combine(dir, "windows-first.bmx");
            Core.SaveSongCopy(first);
            // Once more from the .bmx, so it is as a .bmx saved from a .bmx is.
            Core.Modified = false;
            Assert.That(Core.TryOpenSongFile(first), Is.True);
            Settle();
            Notes.Clear();
            string bmx = Path.Combine(dir, "windows.bmx");
            Core.SaveSongCopy(bmx);
            Assert.That(Sections(bmx).Keys, Does.Contain("PARA"));
            Core.Modified = false;
            return bmx;
        }

        /// <summary>A .bmx's sections by their magic ("MACH", "PARA", "IUGB"…), as bytes.</summary>
        internal static Dictionary<string, byte[]> Sections(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var sections = new Dictionary<string, byte[]>();
            int count = BitConverter.ToInt32(bytes, 4);
            for (int i = 0; i < count; i++)
            {
                int at = 8 + 12 * i;
                string magic = Encoding.ASCII.GetString(bytes, at, 4);
                int offset = BitConverter.ToInt32(bytes, at + 4), size = BitConverter.ToInt32(bytes, at + 8);
                sections[magic] = bytes.AsSpan(offset, size).ToArray();
            }
            return sections;
        }

        /// <summary>The section of subsections (IUGB, "BGUI" as the file spells it): the notes are one.</summary>
        const string Subsections = "BGUI";

        static void SameCoreSections(string original, string saved)
        {
            var a = Sections(original);
            var b = Sections(saved);
            Assert.That(b.Keys.Where(k => k != Subsections), Is.EquivalentTo(a.Keys.Where(k => k != Subsections)), "the same sections");
            foreach (var key in a.Keys.Where(k => k != Subsections))
                Assert.That(b[key], Is.EqualTo(a[key]), $"{key} is byte for byte the same");
        }

        const string InfoMarkdown =
            "## Song title\n\n" +
            "Plain, **bold**, *italic*, <u>underlined</u>, <span style=\"color:#ff0000\">red</span> and ü € “quotes” 音 {braces} back\\slash\n\n" +
            "- first\n- second\n1. one\n2. two\n\n" +
            "[ReBuzz](https://github.com/wasteddesign/ReBuzz)\nafter a line break\tand a tab\n" +
            "<span style=\"color:#808080\">Small grey</span>";

        [AvaloniaTest]
        public void AnInfoMachinesTextIsImportedAndTheSongSavesBackUnchanged()
        {
            var (window, view) = OpenNotes();
            string dir = Folder("info");
            var data = InfoData();
            try
            {
                string windows = InfoSong(window, dir, data);
                Open(window, windows);
                window.ShowView("Song Notes");
                Settle();

                Assert.That(Notes.Text, Is.EqualTo(InfoMarkdown), "the Info machine's text, as Markdown");
                Assert.That(Notes.JustImported.Select(s => (s.Kind, s.Machine)), Is.EqualTo(new[] { (SongNotesStore.Source.InfoMachine, "Info") }));
                Assert.That(view.Banner.IsVisible, Is.True, "marked as imported");
                Assert.That(view.BannerText, Does.Contain("the Info machine “Info”").And.Contain("stays in the song"));
                Assert.That(view.Editor.Text, Is.EqualTo(InfoMarkdown));
                Assert.That(Page(view), Does.StartWith("H Song title | P Plain, **bold**, *italic*, _underlined_, red and").And.Contain("• first | • second | 1. one | 2. two | P [ReBuzz]/after"));
                Assert.That(Core.Modified, Is.False, "importing isn't an edit");
                var info = Core.SongCore.MachinesList.Single(m => m.Name == "Info");
                Assert.That(info.DLL.IsMissing, Is.True);
                Assert.That(info.Data, Is.EqualTo(data), "the machine keeps its data, as it was");
                Studio.Screenshot(window, "song-notes-imported");

                // Saved: the core sections as they were; the notes in their subsection.
                string a = Path.Combine(dir, "A.bmx"), b = Path.Combine(dir, "B.bmx");
                SaveAs(window, a);
                SameCoreSections(windows, a);
                Open(window, a);
                Assert.That(Notes.Text, Is.EqualTo(InfoMarkdown), "imported once: not again");
                Assert.That(Notes.JustImported, Is.Empty);
                Assert.That(view.Banner.IsVisible, Is.False);
                Assert.That(Notes.Imported.Single().Machine, Is.EqualTo("Info"), "remembered as imported");
                SaveAs(window, b);
                Assert.That(File.ReadAllBytes(b), Is.EqualTo(File.ReadAllBytes(a)), "A and B are byte for byte the same");
                var (exit, output) = ExitTests.RoundTripCheck("--compare", windows, a, b);
                Assert.That(exit, Is.EqualTo(0), "tools/song-roundtrip.py --compare: " + output);

                // And in BMXML.
                Open(window, windows);
                Assert.That(Notes.JustImported, Has.Count.EqualTo(1));
                string ax = Path.Combine(dir, "A.bmxml"), bx = Path.Combine(dir, "B.bmxml");
                SaveAs(window, ax);
                Open(window, ax);
                Assert.That(Notes.Text, Is.EqualTo(InfoMarkdown));
                Assert.That(Notes.JustImported, Is.Empty);
                Assert.That(Core.SongCore.MachinesList.Single(m => m.Name == "Info").Data, Is.EqualTo(data));
                SaveAs(window, bx);
                (exit, output) = ExitTests.RoundTripCheck("--compare", windows, ax, bx);
                Assert.That(exit, Is.EqualTo(0), "tools/song-roundtrip.py --compare (BMXML): " + output);

                // The mark goes when dismissed; the notes stay.
                Open(window, windows);
                Assert.That(view.Banner.IsVisible, Is.True);
                view.DismissButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Settle();
                Assert.That(view.Banner.IsVisible, Is.False);
                Assert.That(Notes.Text, Is.EqualTo(InfoMarkdown));
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AnInfoTextChangedSinceComesInBelowTheNotes()
        {
            var (window, view) = OpenNotes();
            string dir = Folder("info-changed");
            try
            {
                string windows = InfoSong(window, dir, InfoData());
                Open(window, windows);
                // The person's notes beside the import, saved.
                Notes.Write(Notes.Text + "\n\nMine");
                string a = Path.Combine(dir, "A.bmx");
                SaveAs(window, a);
                Open(window, a);
                Assert.That(Notes.Text, Does.EndWith("\n\nMine"));

                // Edited in ReBuzz on Windows since: its new text comes in, under headings.
                var info = Core.SongCore.MachinesList.Single(m => m.Name == "Info");
                info.Data = InfoMachineState.Write(@"{\rtf1\ansi {\b New} text\par}");
                string b = Path.Combine(dir, "B.bmx");
                SaveAs(window, b);
                Open(window, b);
                Assert.That(Notes.Text, Does.StartWith("## Song title").And.EndWith("Mine\n\n### Info machine “Info”\n\n**New** text"));
                Assert.That(Notes.JustImported, Has.Count.EqualTo(1));
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void TheSongsInfoTextComesInToo()
        {
            var (window, view) = OpenNotes();
            string dir = Folder("blah");
            try
            {
                Core.InfoText = "Made with Buzz\r\n* greetings to the crew\r\n1. first try\r\n\r\n#1 at the party";
                Notes.Clear();
                string path = Path.Combine(dir, "info-text.bmx");
                Core.SaveSongCopy(path);
                Open(window, path);
                Assert.That(Notes.Text, Is.EqualTo("Made with Buzz\n\\* greetings to the crew\n1\\. first try\n\n#1 at the party"), "plain text, kept as it reads");
                Assert.That(Notes.JustImported.Single().Kind, Is.EqualTo(SongNotesStore.Source.SongInfo));
                Assert.That(Page(view), Is.EqualTo("P Made with Buzz/* greetings to the crew/1. first try | P #1 at the party"));
                Assert.That(view.BannerText, Does.Contain("the song's info text"));
                Assert.That(Core.InfoText, Is.EqualTo("Made with Buzz\r\n* greetings to the crew\r\n1. first try\r\n\r\n#1 at the party"), "left as it was");
                string a = Path.Combine(dir, "A.bmx");
                SaveAs(window, a);
                SameCoreSections(path, a);
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ links

        [AvaloniaTest]
        public void LinksInTheNotesOpen()
        {
            var (window, view) = OpenNotes();
            var opened = new List<string>();
            SongNotesView.OpenLink = uri =>
            {
                opened.Add(uri.ToString());
                return true;
            };
            try
            {
                Notes.Write("Go to [ReBuzz](https://github.com/wasteddesign/ReBuzz)\nor www.example.com/notes.");
                Settle();
                window.UpdateLayout();
                Settle();
                var links = view.LinkBounds().ToList();
                Assert.That(links.Select(l => l.Url), Is.EqualTo(new[] { "https://github.com/wasteddesign/ReBuzz", "https://www.example.com/notes" }));
                foreach (var (url, bounds) in links)
                {
                    var at = view.TranslatePoint(bounds.Center, window);
                    Assert.That(at, Is.Not.Null);
                    Click(window, at!.Value);
                }
                Assert.That(opened, Is.EqualTo(new[] { "https://github.com/wasteddesign/ReBuzz", "https://www.example.com/notes" }), "a click on a link opens it");
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ the agent

        /// <summary>A tool call on the studio's agent server (the UI thread runs it meanwhile): its result.</summary>
        static JsonObject Call(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            Assert.That(r["error"], Is.Null, r.ToJsonString());
            return r["result"]!.AsObject();
        }

        static JsonObject Ok(string tool, JsonObject args = null)
        {
            var result = Call(tool, args);
            Assert.That(result["isError"]?.GetValue<bool>() == true, Is.False, result.ToJsonString());
            return result["structuredContent"]!.AsObject();
        }

        [AvaloniaTest]
        public void AgentsReadAndWriteTheNotes()
        {
            var (window, view) = OpenNotes();
            string dir = Folder("agent");
            try
            {
                var empty = Ok("song_notes_read");
                Assert.That(empty["notes"]!.GetValue<string>(), Is.Empty);
                Assert.That(empty["format"]!.GetValue<string>(), Is.EqualTo("markdown"));

                Type(window, "My idea: **big** chorus");
                Core.Modified = false;
                var appended = Ok("song_notes_write", new JsonObject { ["text"] = "## Mix notes\n- vocal 2 dB louder", ["mode"] = "append" });
                Assert.That(appended["notes"]!.GetValue<string>(), Is.EqualTo("My idea: **big** chorus\n\n## Mix notes\n- vocal 2 dB louder"));
                Assert.That(Notes.Text, Is.EqualTo(appended["notes"]!.GetValue<string>()));
                Settle();
                Assert.That(view.Editor.Text, Is.EqualTo(Notes.Text), "the tab shows what the agent wrote");
                Assert.That(Core.Modified, Is.True, "an agent's edit marks the song");
                Studio.Screenshot(window, "song-notes-agent");

                var plain = Ok("song_notes_read", new JsonObject { ["format"] = "text" });
                Assert.That(plain["notes"]!.GetValue<string>(), Is.EqualTo("My idea: big chorus\nMix notes\n• vocal 2 dB louder"));

                Ok("undo");
                Assert.That(Notes.Text, Is.EqualTo("My idea: **big** chorus"), "one undo step");
                Ok("redo");
                Ok("song_notes_write", new JsonObject { ["text"] = "Replaced" });
                Assert.That(Notes.Text, Is.EqualTo("Replaced"));
                var bad = Call("song_notes_write", new JsonObject { ["text"] = new string('x', 200_001) });
                Assert.That(bad["isError"]!.GetValue<bool>(), Is.True, "too long");
                Assert.That(Notes.Text, Is.EqualTo("Replaced"), "a refused call changes nothing");

                // An imported Info machine's text, and where it came from.
                string windows = InfoSong(window, dir, InfoData());
                Open(window, windows);
                var read = Ok("song_notes_read");
                Assert.That(read["notes"]!.GetValue<string>(), Is.EqualTo(InfoMarkdown));
                Assert.That(read["imported_on_open"]!.AsArray().Single()!["machine"]!.GetValue<string>(), Is.EqualTo("Info"));
                Assert.That(read["imported"]!.AsArray().Single()!["from"]!.GetValue<string>(), Is.EqualTo("info_machine"));
            }
            finally
            {
                Close(window);
            }
        }
    }
}
