#nullable disable
// Agent server (docs/specs/mcp-server.md §6): the craft guides. One source, three ways in: the
// `guide` tool, resources rebuzz://guides/<topic>, and (later) prompts. The guides are markdown
// files embedded in this assembly (Guides/*.md).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.Guides
{
    public static class GuideLibrary
    {
        public const string UriPrefix = "rebuzz://guides/";

        static readonly (string Topic, string Title, string Description)[] Topics =
        {
            ("workflow", "Working in ReBuzz", "The loop (build, render, measure, adjust), which tool for what, undo, and the checks before calling something done. Read first."),
            ("concepts", "ReBuzz in five minutes", "Machines and the graph, parameters and tracks, patterns and the arrangement, beats, pitches and levels."),
        };

        public static IEnumerable<string> TopicNames => Topics.Select(t => t.Topic);

        /// <summary>A guide's markdown, or null.</summary>
        public static string Get(string topic)
        {
            if (!Topics.Any(t => t.Topic == topic)) return null;
            using var stream = typeof(GuideLibrary).Assembly.GetManifestResourceStream($"ReBuzz.Agent.Guides.{topic}.md");
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public static IEnumerable<ResourceDef> Resources() =>
            Topics.Select(t => new ResourceDef
            {
                Uri = UriPrefix + t.Topic,
                Name = t.Topic,
                Title = t.Title,
                Description = t.Description,
                Read = () => Get(t.Topic) ?? "",
            });

        public static void Register(ToolRegistry tools)
        {
            tools.Add(new ToolDef
            {
                Name = "guide",
                Title = "Read a guide",
                Description =
                    "Returns a guide as markdown: 'workflow' (how to work here; read it first), 'concepts' (machines, patterns, the arrangement, " +
                    "beats, pitches, levels). Without a topic, lists the topics. Read-only. Example: guide(topic=\"workflow\").",
                Input = Object(null, Opt("topic", String("The guide to read", TopicNames.ToArray()))),
                Output = Object(null,
                    Opt("topic", String("The guide returned")),
                    Opt("markdown", String("Its text")),
                    Req("topics", Array(Object(null, Req("topic", String("Its name")), Req("title", String("Its title")), Req("about", String("What it covers"))), "Every guide"))),
                Annotations = ToolAnnotations.Read,
                Handler = c =>
                {
                    var list = new JsonArray(Topics.Select(t => (JsonNode)new JsonObject { ["topic"] = t.Topic, ["title"] = t.Title, ["about"] = t.Description }).ToArray());
                    string topic = c.String("topic");
                    if (topic == null)
                        return System.Threading.Tasks.Task.FromResult(ToolResult.Ok(
                            "Guides: " + string.Join("; ", Topics.Select(t => $"{t.Topic} ({t.Description})")), new JsonObject { ["topics"] = list }));
                    string text = Get(topic) ?? throw new ToolError($"No guide \"{topic}\". Topics: {string.Join(", ", TopicNames)}.");
                    return System.Threading.Tasks.Task.FromResult(ToolResult.Ok(text, new JsonObject { ["topic"] = topic, ["markdown"] = text, ["topics"] = list }));
                },
            });
        }
    }
}
