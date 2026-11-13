// Agent server (docs/specs/agent-knowledge.md §5, K3): export_audio. The song (or its loop, a range or a marker's
// section) rendered to a WAV file at a path the agent names, at 16, 24 or 32-bit (float) with dither, with
// stems: each machine's own output beside it. It reuses `render` (the same engine path, faster than real time,
// the song untouched) and writes the files itself; it never overwrites a file unless overwrite=true.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    internal static class ExportTools
    {
        public static void Register(ToolRegistry tools, IAppServices app)
        {
            tools.Add(new ToolDef
            {
                Name = "export_audio",
                Title = "Export audio",
                Description =
                    "Writes the song as a stereo WAV file at path: the whole song, range=\"loop\", from/to in beats or \"bar.beat\", or a marker's section. bits is 16, 24 (default) or 32 " +
                    "(float); 16 and 24 are rounded with dither (tpdf default, off, light or psychoacoustic). stems=\"generators\" or \"all\" (or machines=[…]) also writes each machine's own output " +
                    "(after its connection gain) beside it as <name>-<machine>.wav. tail_seconds lets reverbs ring out (default 2). Renders as `render` does: faster than real time, " +
                    "the song unchanged, playback stopped. Never overwrites unless overwrite=true. Returns each file with its size and the master's loudness and peaks. " +
                    "Example: export_audio(path=\"C:/Music/demo.wav\", bits=24, stems=\"generators\").",
                Input = Object(null,
                    Req("path", String("Full path of the WAV file (.wav); its folder is made if missing")),
                    Opt("range", String("song (default) or loop", "song", "loop")),
                    Opt("from", AnyOf("Start", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("to", AnyOf("End (exclusive)", Number("Beats", 0), String("\"bar.beat\""))),
                    Opt("section", String("A marker's name: from it to the next marker (arrangement_markers)")),
                    Opt("tail_seconds", Number("Seconds rendered after the end (default 2)", 0, 30)),
                    Opt("bits", Integer("16 or 24 bit PCM, or 32 for float (default 24)", 16, 32)),
                    Opt("dither", String("With 16 or 24 bits: tpdf (default), off, light or psychoacoustic", "off", "tpdf", "light", "psychoacoustic")),
                    Opt("stems", String("Also each machine's output: none (default), generators or all (every machine but Master)", "none", "generators", "all")),
                    Opt("machines", Array(String("A machine"), "Stems of exactly these machines (instead of stems)")),
                    Opt("overwrite", Boolean("Replace files that exist (default false: the call fails and writes nothing)"))),
                Output = Object(null,
                    Req("files", Array(Object(null,
                        Req("role", String("master or stem", "master", "stem")),
                        Opt("machine", String("The stem's machine")),
                        Req("path", String("The file")),
                        Req("bytes", Integer("Its size"))), "The files written, the master first")),
                    Req("bits", Integer("Bit depth written")),
                    Opt("dither", String("Dither used (16 and 24 bit)")),
                    Req("sample_rate", Integer("Samples per second")),
                    Req("seconds", Number("Length, with the tail")),
                    Req("measure", new JsonObject { ["type"] = "object", ["description"] = "The master as written: loudness and peaks, as analyze reports them (silent, seconds, loudness, peaks, reads)" }),
                    Opt("warnings", Array(String("A warning"), "Things to know"))),
                Annotations = new ToolAnnotations(),
                Handler = async c => await Export(tools, c),
            });
        }

        static async Task<ToolResult> Export(ToolRegistry tools, ToolContext c)
        {
            string path = c.RequiredString("path").Trim();
            if (!Path.IsPathRooted(path)) throw new ToolError("path: give the full path of the file, e.g. C:/Music/demo.wav or /Users/me/demo.wav.");
            if (Directory.Exists(path)) throw new ToolError($"{path} is a folder; name the .wav file.");
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new ToolError("path: the file must end in .wav.");
            int bits = c.Int("bits") ?? 24;
            if (bits is not (16 or 24 or 32)) throw new ToolError("bits: 16, 24 or 32 (float).");
            string? dither = c.String("dither");
            if (dither != null && bits == 32) throw new ToolError("dither goes with 16 or 24 bits; 32-bit float is written as is.");
            if (bits != 32) Requantize.Validate(dither);
            bool overwrite = c.Bool("overwrite") == true;
            string stems = c.String("stems") ?? "none";
            var explicitMachines = c.List("machines")?.Select(m => m?.GetValue<string>() ?? "").Where(m => m.Length > 0).ToList();

            var render = tools.Find("render") ?? throw new ToolError("This host can't render, so it can't export.");
            List<string> stemMachines;
            if (explicitMachines is { Count: > 0 }) stemMachines = explicitMachines.Distinct().ToList();
            else if (stems is "generators" or "all")
            {
                var list = tools.Find("machine_list") ?? throw new ToolError("This host can't list machines, so it can't make stems.");
                var listed = await list.Handler(new ToolContext("machine_list", new JsonObject(), c.Cancellation));
                var machines = listed.Structured?["machines"] as JsonArray ?? new JsonArray();
                stemMachines = machines.Select(m => (Name: m?["name"]?.GetValue<string>() ?? "", Kind: m?["kind"]?.GetValue<string>() ?? ""))
                    .Where(m => m.Name.Length > 0 && m.Kind != "master" && (stems == "all" || m.Kind == "generator"))
                    .Select(m => m.Name).ToList();
                if (stemMachines.Count == 0) throw new ToolError("The song has no " + (stems == "all" ? "machines" : "generators") + " to make stems of.");
            }
            else stemMachines = new List<string>();

            string folder = Path.GetDirectoryName(path)!;
            string stem = Path.GetFileNameWithoutExtension(path);
            var targets = new List<(string Role, string? Machine, string File)> { ("master", null, path) };
            foreach (var m in stemMachines) targets.Add(("stem", m, Path.Combine(folder, $"{stem}-{SafeName(m)}.wav")));
            if (targets.Select(t => t.File).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
                throw new ToolError("Two stems would get the same file name (machine names that differ only in punctuation). Export them one by one, or rename a machine.");
            if (!overwrite)
            {
                var existing = targets.Where(t => File.Exists(t.File)).Select(t => t.File).ToList();
                if (existing.Count > 0)
                    throw new ToolError($"{(existing.Count == 1 ? "A file exists" : existing.Count + " files exist")} at {string.Join(", ", existing.Take(3))}{(existing.Count > 3 ? ", …" : "")}: nothing written. " +
                                        "Pick another path, or pass overwrite=true to replace them.");
            }

            var args = new JsonObject { ["name"] = "export" };
            foreach (var key in new[] { "range", "from", "to", "section", "tail_seconds" })
                if (c.Arguments[key] != null) args[key] = c.Arguments[key]!.DeepClone();
            if (stemMachines.Count > 0) args["probes"] = new JsonArray(stemMachines.Select(m => (JsonNode)m).ToArray());
            var rendered = await render.Handler(new ToolContext("render", args, c.Cancellation, (d, t, m) => c.Progress(d, t, m)));
            var r = rendered.Structured ?? throw new ToolError("The render gave no result.");
            var probes = (r["probes"] as JsonArray)?.ToDictionary(p => p!["machine"]!.GetValue<string>(), p => p!["file"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase)
                         ?? new Dictionary<string, string>();

            Directory.CreateDirectory(folder);
            var files = new JsonArray();
            JsonObject? measure = null;
            int rate = 0;
            double seconds = 0;
            var warnings = new JsonArray();
            foreach (var t in targets)
            {
                string source = t.Role == "master" ? r["file"]!.GetValue<string>()
                    : probes.TryGetValue(t.Machine!, out var pf) ? pf : throw new ToolError($"The render has no output for {t.Machine}.");
                var audio = Wav.Read(source);
                if (bits != 32) audio = Requantize.To(audio, bits, dither);
                Write(t.File, audio, bits);
                if (t.Role == "master")
                {
                    measure = AudioReport.Measure(audio, new[] { "loudness", "peaks" });
                    rate = audio.Rate;
                    seconds = Math.Round(audio.Seconds, 3);
                }
                var o = new JsonObject { ["role"] = t.Role, ["path"] = t.File, ["bytes"] = new FileInfo(t.File).Length };
                if (t.Machine != null) o["machine"] = t.Machine;
                files.Add(o);
            }
            if (r["warnings"] is JsonArray rw) foreach (var w in rw) warnings.Add(w!.DeepClone());
            if (r["diverged"] is JsonArray d && d.Count > 0)
                warnings.Add("Machines diverged during the render and are silent from there: " + string.Join(", ", d.Select(x => x!["machine"]!.GetValue<string>())) + ".");

            var result = new JsonObject
            {
                ["files"] = files,
                ["bits"] = bits,
                ["sample_rate"] = rate,
                ["seconds"] = seconds,
                ["measure"] = measure,
                ["warnings"] = warnings,
            };
            if (bits != 32) result["dither"] = dither ?? "tpdf";
            string line = measure != null ? AudioReport.Line(measure) : "";
            return ToolResult.Ok($"Exported {seconds} s at {bits}{(bits == 32 ? "-bit float" : "-bit" + $", {dither ?? "tpdf"} dither")} to {path}" +
                                 (stemMachines.Count > 0 ? $" with {stemMachines.Count} stem{(stemMachines.Count == 1 ? "" : "s")} ({string.Join(", ", stemMachines.Take(6))}{(stemMachines.Count > 6 ? ", …" : "")})" : "") +
                                 $". {line}." + (warnings.Count > 0 ? " " + string.Join(" ", warnings.Select(w => w!.GetValue<string>())) : ""), result)
                .WithLink(new Uri(path).AbsoluteUri, Path.GetFileName(path), "audio/wav", "The exported master");
        }

        static string SafeName(string name)
        {
            var chars = name.Trim().Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-').Take(60).ToArray();
            return chars.Length == 0 ? "machine" : new string(chars);
        }

        static void Write(string file, StereoAudio audio, int bits)
        {
            var interleaved = new float[audio.Frames * 2];
            for (int i = 0; i < audio.Frames; i++)
            {
                interleaved[2 * i] = audio.Left[i];
                interleaved[2 * i + 1] = audio.Right?[i] ?? audio.Left[i];
            }
            if (bits == 32) Wav.WriteStereoFloat(file, interleaved, audio.Frames, audio.Rate);
            else Wav.WriteStereoPcm(file, interleaved, audio.Frames, audio.Rate, bits);
        }
    }
}
