using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core.Notes
{
    /// <summary>
    /// Fork (docs/specs/machine-safety.md §3.5): the song's notes, rich text written as Markdown
    /// (<see cref="NotesMarkdown"/>). The studio's Song Notes tab edits them and agents read and
    /// write them (song_notes_read, song_notes_write); every change is a step on the song's undo
    /// history and marks the song modified.
    ///
    /// Saved in a "SongNotes" subsection, in .bmx and .bmxml alike; upstream ReBuzz skips it.
    /// Opening a song brings in the notes it had elsewhere, marked as imported, and leaves where
    /// they came from as it is, so the song saves back unchanged but for this subsection:
    /// <list type="bullet">
    /// <item>each Info machine's text (WDE's rich-text notes machine, <see cref="InfoMachineState"/>):
    /// read from the placeholder's kept data off Windows, from the machine's state on Windows;</item>
    /// <item>the song's own info text (Buzz's "BLAH" section, ReBuzz's Info view), as plain text.</item>
    /// </list>
    /// What was imported is remembered (a hash of each text, as Markdown) in the subsection, so it comes
    /// in once; a text changed since (in ReBuzz on Windows) comes in again, below the notes.
    /// </summary>
    internal sealed class SongNotesStore
    {
        public const string SubSectionName = "SongNotes";
        const int FormatVersion = 1;

        /// <summary>Typing that pauses no longer than this is one undo step.</summary>
        internal static TimeSpan TypingWindow = TimeSpan.FromSeconds(1.5);

        /// <summary>The clock typing steps are timed by (tests set it).</summary>
        internal static Func<DateTime> Now = () => DateTime.UtcNow;

        /// <summary>Where notes came from when a song opened.</summary>
        internal sealed record Source(string Kind, string Machine, string Hash, string Title)
        {
            public const string InfoMachine = "info-machine";
            public const string SongInfo = "song-info";
        }

        readonly ReBuzzCore buzz;
        string text = "";
        readonly List<Source> imported = new();         // in the notes already (saved with them)
        readonly List<Source> justImported = new();     // brought in when this song opened
        Edit typing;

        internal SongNotesStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        /// <summary>The notes, Markdown with "\n" line ends ("" when there are none).</summary>
        public string Text => text;

        /// <summary>What opening the song brought into the notes (shown as imported until dismissed).</summary>
        public IReadOnlyList<Source> JustImported => justImported;

        /// <summary>Every source in the notes, this session's and earlier ones.</summary>
        public IReadOnlyList<Source> Imported => imported;

        /// <summary>The notes or the import marks changed (on the song's thread).</summary>
        public event Action Changed;

        // ------------------------------------------------------------------ editing

        /// <summary>One undoable step on the song's history; marks the song modified.</summary>
        sealed class Edit : IAction
        {
            readonly SongNotesStore store;
            public readonly string Before;
            public string After;
            public DateTime At;

            public Edit(SongNotesStore store, string before, string after)
            {
                this.store = store;
                Before = before;
                After = after;
            }

            public void Do() => store.Apply(After);

            public void Undo() => store.Apply(Before);
        }

        void Apply(string value)
        {
            text = value;
            buzz.SetModifiedFlag();
            Changed?.Invoke();
        }

        static string Normalize(string markdown) => (markdown ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>Replaces the notes: one step on the song's undo history.</summary>
        public void Write(string markdown)
        {
            markdown = Normalize(markdown);
            typing = null;
            if (markdown == text) return;
            buzz.SongCore.ActionStack.Do(new Edit(this, text, markdown));
        }

        /// <summary>
        /// The notes as someone types them: one undo step for a run of typing (until a pause of
        /// <see cref="TypingWindow"/>, a new line, or another step on the song's history).
        /// </summary>
        public void Type(string markdown)
        {
            markdown = Normalize(markdown);
            if (markdown == text) return;
            var stack = buzz.SongCore.ActionStack;
            var now = Now();
            if (typing != null && !stack.CanRedo && ReferenceEquals(stack.Actions.LastOrDefault(), typing) && now - typing.At <= TypingWindow &&
                Lines(markdown) == Lines(typing.After))
            {
                typing.After = markdown;
                typing.At = now;
                Apply(markdown);
                return;
            }
            var edit = new Edit(this, text, markdown) { At = now };
            stack.Do(edit);
            typing = edit;
        }

        static int Lines(string s) => s.Count(c => c == '\n');

        /// <summary>The next keystroke starts a new undo step (focus left the editor, a format was applied).</summary>
        public void EndTyping() => typing = null;

        /// <summary>The imported mark is put away (the notes stay).</summary>
        public void DismissImport()
        {
            if (justImported.Count == 0) return;
            justImported.Clear();
            Changed?.Invoke();
        }

        /// <summary>A new song: no notes (not undoable, as a new song's history starts empty).</summary>
        public void Clear()
        {
            typing = null;
            bool had = text.Length > 0 || imported.Count > 0 || justImported.Count > 0;
            text = "";
            imported.Clear();
            justImported.Clear();
            if (had) Changed?.Invoke();
        }

        // ------------------------------------------------------------------ song files

        void OnSaveSong(ISaveSong save)
        {
            var xml = ToXml();
            if (xml == null) return;      // no notes: no subsection, and the file is as upstream writes it
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var bytes = new UTF8Encoding(false).GetBytes(xml.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>The subsection, or null when there are no notes and nothing was imported.</summary>
        internal XElement ToXml()
        {
            if (text.Length == 0 && imported.Count == 0) return null;
            return new XElement("SongNotes",
                new XAttribute("version", FormatVersion),
                new XAttribute("format", "markdown"),
                new XElement("Text", XmlSafe(text)),
                imported.Select(s => new XElement("Imported",
                    new XAttribute("kind", s.Kind),
                    s.Machine == null ? null : new XAttribute("machine", XmlSafe(s.Machine)),
                    new XAttribute("hash", s.Hash))));
        }

        static string XmlSafe(string s) => s.All(XmlConvert.IsXmlChar) ? s : new string(s.Where(XmlConvert.IsXmlChar).ToArray());

        void OnOpenSong(IOpenSong open)
        {
            typing = null;
            text = "";
            imported.Clear();
            justImported.Clear();
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    var root = XDocument.Load(stream).Root;
                    if (root?.Name.LocalName == "SongNotes")
                    {
                        text = Normalize((string)root.Element("Text") ?? "");
                        foreach (var e in root.Elements("Imported"))
                        {
                            string hash = (string)e.Attribute("hash");
                            if (string.IsNullOrEmpty(hash)) continue;
                            imported.Add(new Source((string)e.Attribute("kind") ?? Source.InfoMachine, (string)e.Attribute("machine"), hash, null));
                        }
                    }
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Song notes: " + e.Message, DCLogLevel.Warning);
            }
            try
            {
                Import(Sources());
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Song notes: importing: " + e.Message, DCLogLevel.Warning);
            }
            Changed?.Invoke();
        }

        /// <summary>
        /// Adds the sources not in the notes yet below them, each under a heading when there is more
        /// than one text. Not a step on the history: it is what the song opened with.
        /// </summary>
        void Import(List<(Source Source, string Markdown)> sources)
        {
            var known = new HashSet<string>(imported.Select(s => s.Hash));
            var fresh = sources.Where(s => s.Markdown.Trim().Length > 0 && known.Add(s.Source.Hash)).ToList();
            if (fresh.Count == 0) return;
            bool headings = fresh.Count + (text.Trim().Length > 0 ? 1 : 0) > 1;
            var sb = new StringBuilder(text.TrimEnd('\n'));
            foreach (var (source, markdown) in fresh)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                if (headings) sb.Append("### ").Append(NotesMarkdown.Escape(source.Title)).Append("\n\n");
                sb.Append(markdown.Trim('\n'));
                imported.Add(source);
                justImported.Add(source);
            }
            text = sb.ToString();
        }

        /// <summary>The song's notes kept elsewhere: its info text, then each Info machine's, in machine order.</summary>
        internal List<(Source Source, string Markdown)> Sources()
        {
            var list = new List<(Source, string)>();
            string info = Normalize(buzz.InfoText ?? "").Trim('\n');
            if (info.Trim().Length > 0)
                list.Add((new Source(Source.SongInfo, null, Hash(info), "Song info"), PlainToMarkdown(info)));
            foreach (var m in buzz.SongCore.MachinesList)
            {
                if (m.Hidden || m.DLL?.Name != InfoMachineState.Gear) continue;
                byte[] data;
                try
                {
                    data = m.Data;
                }
                catch (Exception e)
                {
                    buzz.DCWriteLine($"Song notes: the Info machine {m.Name}'s data: {e.Message}", DCLogLevel.Warning);
                    continue;
                }
                var state = InfoMachineState.Read(data);
                if (state == null || string.IsNullOrWhiteSpace(state.Rtf)) continue;
                string markdown;
                try
                {
                    markdown = state.ToMarkdown();
                }
                catch (Exception e)
                {
                    buzz.DCWriteLine($"Song notes: the Info machine {m.Name}'s text: {e.Message}", DCLogLevel.Warning);
                    continue;
                }
                // Known by its text as Markdown: Info's box may write the same notes as RTF a little differently
                // each time ReBuzz on Windows saves it, and that mustn't bring them in again.
                list.Add((new Source(Source.InfoMachine, m.Name, Hash(markdown), $"Info machine “{m.Name}”"), markdown));
            }
            return list;
        }

        /// <summary>Plain text that reads the same as Markdown: each line kept, nothing taken as formatting.</summary>
        internal static string PlainToMarkdown(string plain) =>
            string.Join("\n", Normalize(plain).Split('\n').Select(line => line.Trim().Length == 0 ? "" : NotesMarkdown.EscapeLineStart(NotesMarkdown.Escape(line.TrimEnd()))));

        internal static string Hash(string s)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(s)));
            return Convert.ToHexString(bytes, 0, 8).ToLowerInvariant();
        }
    }
}
