// Agent server (docs/specs/mcp-server.md §5.2, machine-safety.md §3.5): the song's notes, the
// studio's Song Notes tab. song_notes_read, song_notes_write.

using System;
using System.Linq;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class NotesTools
    {
        /// <summary>The longest notes an agent may write (characters).</summary>
        const int MaxLength = 200_000;

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "song_notes_read",
                Title = "Read the song's notes",
                Description =
                    "The song's notes, as the studio's Song Notes tab shows them: Markdown (**bold**, *italic*, <u>underline</u>, # headings, " +
                    "- and 1. lists, [links](https://…); a line break is a line break). They hold what the person wrote about the song: ideas, " +
                    "lyrics, to-dos, credits. Songs with Info machines (WDE's notes machine) or Buzz info text bring that text in when they open " +
                    "(imported). format=\"text\" gives plain text. Example: song_notes_read().",
                Input = Object(null, Opt("format", String("markdown (default) or plain text", "markdown", "text"))),
                Output = NotesSchema(),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    bool plain = c.String("format") == "text";
                    var r = await host.Engine("song_notes_read", false, _ => Notes(Store(core), plain), c.Cancellation);
                    string notes = r["notes"]!.GetValue<string>();
                    return ToolResult.Ok(notes.Length == 0 ? "The song has no notes." : $"{notes.Length} characters of notes:\n{notes}", r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "song_notes_write",
                Title = "Write the song's notes",
                Description =
                    "Writes the song's notes (the Song Notes tab), in Markdown: **bold**, *italic*, <u>underline</u>, # headings, - and 1. lists, " +
                    "[links](https://…); a line break is a line break, an empty line starts a paragraph. mode=replace (default) replaces them, " +
                    "append adds below them (after an empty line). Saved with the song; one undo step. Leave the person's notes alone unless asked: " +
                    "append, under a heading of your own. Example: song_notes_write(text=\"## Mix notes\\n- vocal 2 dB louder in the chorus\", mode=\"append\").",
                Input = Object(null,
                    Req("text", String("The notes in Markdown (\"\" with mode=replace clears them)")),
                    Opt("mode", String("replace (default) or append", "replace", "append"))),
                Output = NotesSchema(),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string text = c.RequiredString("text");
                    string mode = c.String("mode") ?? "replace";
                    var r = await host.Engine("song_notes_write", true, _ =>
                    {
                        var store = Store(core);
                        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
                        string now = mode == "append" && store.Text.Trim().Length > 0
                            ? store.Text.TrimEnd('\n') + "\n\n" + normalized.Trim('\n')
                            : mode == "append" ? normalized.Trim('\n') : normalized;
                        if (now.Length > MaxLength)
                            throw new ToolError($"The notes would be {now.Length:N0} characters; keep them under {MaxLength:N0}.");
                        store.Write(now);
                        return Notes(store, false);
                    }, c.Cancellation);
                    int length = r["length"]!.GetValue<int>();
                    return ToolResult.Ok(length == 0 ? "The song's notes are empty now." : $"The song's notes are {length} characters now (one undo step).", r);
                },
            });
        }

        static SongNotesStore Store(ReBuzzCore core) => core.SongNotes ?? throw new ToolError("This ReBuzz keeps no song notes.");

        static JsonObject Notes(SongNotesStore store, bool plain)
        {
            string text = plain ? NotesMarkdown.ToPlainText(store.Text) : store.Text;
            var imported = new JsonArray(store.Imported.Select(s => (JsonNode)Source(s)).ToArray());
            var just = new JsonArray(store.JustImported.Select(s => (JsonNode)Source(s)).ToArray());
            return new JsonObject
            {
                ["notes"] = text,
                ["format"] = plain ? "text" : "markdown",
                ["length"] = store.Text.Length,
                ["imported"] = imported,
                ["imported_on_open"] = just,
            };
        }

        static JsonObject Source(SongNotesStore.Source s)
        {
            var o = new JsonObject { ["from"] = s.Kind == SongNotesStore.Source.InfoMachine ? "info_machine" : "song_info" };
            if (s.Machine != null) o["machine"] = s.Machine;
            return o;
        }

        static JsonObject NotesSchema() => Object("The song's notes",
            Req("notes", String("The notes (Markdown, or plain text with format=text)")),
            Req("format", String("markdown or text", "markdown", "text")),
            Req("length", Integer("Characters of Markdown")),
            Req("imported", Array(Object("A source of text in the notes", Req("from", String("info_machine or song_info", "info_machine", "song_info")),
                Opt("machine", String("The Info machine"))), "Text the notes took in from Info machines or the song's info text")),
            Req("imported_on_open", Array(Object("A source", Req("from", String("info_machine or song_info", "info_machine", "song_info")),
                Opt("machine", String("The Info machine"))), "What opening this song brought in (the Song Notes tab marks it)")));
    }
}
