// Fork (docs/specs/browser.md, BR1): the one drag format of the browser. Every result row of the browser panel (and the canvas popup)
// can be dragged, and every drop target (the Machine View's boxes and canvas now; the Arrangement and the Session in DD1) reads the
// same thing: a kind, an id or path, and for a preset the machine it belongs to. It is plain text (JSON) in one application format, so
// it survives the platform's drag layer and a target needs no reference to the browser's own types.

using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ReBuzz.Agent.Browser
{
    /// <summary>What a dragged browser row is.</summary>
    public enum BrowserKind { Machine, Plugin, Preset, Audio, Midi, Clip }

    /// <summary>
    /// A dragged browser item. <see cref="Id"/> is, by kind: the gear's name (machine), the plug-in's gear name "Vendor/Name (VST3)"
    /// (plugin), the file's full path (audio, midi), "Machine/Clip" (clip) or the gear the preset belongs to (preset, with
    /// <see cref="Preset"/> its name). <see cref="Label"/> is what a drag image shows.
    /// </summary>
    public sealed record BrowserDragData(BrowserKind Kind, string Id, string? Preset = null, string? Label = null)
    {
        /// <summary>The application format's name (Avalonia: DataFormat.CreateStringApplicationFormat).</summary>
        public const string FormatName = "neobuzz-browser-item";

        /// <summary>The format's version, in the text, for a later change.</summary>
        public const int Version = 1;

        /// <summary>Machines and plug-ins: what makes a generator or an effect on a track.</summary>
        public bool IsGear => Kind == BrowserKind.Machine || Kind == BrowserKind.Plugin;

        public static string KindName(BrowserKind k) => k.ToString().ToLowerInvariant();

        public static bool TryKind(string? text, out BrowserKind kind) => Enum.TryParse(text, true, out kind) && Enum.IsDefined(kind);

        /// <summary>The text a drag carries.</summary>
        public string Format()
        {
            var o = new JsonObject { ["v"] = Version, ["kind"] = KindName(Kind), ["id"] = Id };
            if (Preset != null) o["preset"] = Preset;
            if (Label != null) o["label"] = Label;
            return o.ToJsonString();
        }

        /// <summary>Reads a drag's text; null for anything else (a file's name dragged from elsewhere, a newer version).</summary>
        public static BrowserDragData? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text[0] != '{') return null;
            try
            {
                if (JsonNode.Parse(text) is not JsonObject o) return null;
                if ((int?)o["v"] != Version) return null;
                if (!TryKind((string?)o["kind"], out var kind)) return null;
                string? id = (string?)o["id"];
                if (string.IsNullOrEmpty(id)) return null;
                return new BrowserDragData(kind, id, (string?)o["preset"], (string?)o["label"]);
            }
            catch (JsonException) { return null; }
            catch (InvalidCastException) { return null; }
            catch (FormatException) { return null; }
        }
    }
}
