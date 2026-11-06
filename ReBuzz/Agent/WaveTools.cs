// Agent server (docs/specs/mcp-server.md §5.6, A2): the wavetable. wave_list, wave_import.
// Slots are numbered from 1, as ReBuzz shows them. Roots are MIDI pitches (60 = C4), read and
// written through Buzz notes as the rest of the tools do. Fork (Neo Sampler, machines.md §5):
// wave_import's layer adds a file to a filled slot as another layer (a multisample or round robin).

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent
{
    internal static class WaveTools
    {
        static readonly string[] Extensions = { ".wav", ".aif", ".aiff", ".flac", ".ogg" };

        public static void Register(ToolRegistry tools, AgentHost host)
        {
            var core = host.Core;

            tools.Add(new ToolDef
            {
                Name = "wave_list",
                Title = "List waves",
                Description =
                    "The wavetable's samples: slot (from 1), name, length in seconds, channels, sample rate, root pitch and loop. Samplers and " +
                    "drum machines play waves by slot. Example: wave_list().",
                Input = Object(null),
                Output = Object(null, Req("waves", Array(Object(null, Req("slot", Integer("Slot, from 1")), Req("name", String("Name")), Req("seconds", Number("Length"))), "Filled slots")),
                    Req("free_slot", Integer("The first empty slot"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var r = await host.Engine("wave_list", false, _ =>
                    {
                        var waves = core.SongCore.Wavetable.Waves;
                        var list = new JsonArray();
                        for (int i = 0; i < waves.Count; i++)
                            if (waves[i] != null) list.Add(WaveJson(waves[i], i));
                        return new JsonObject { ["waves"] = list, ["free_slot"] = FreeSlot(core) + 1, ["slots"] = waves.Count };
                    }, c.Cancellation);
                    var ws = r["waves"]!.AsArray();
                    return ToolResult.Ok(ws.Count == 0 ? "The wavetable is empty." :
                        string.Join("; ", ws.Take(30).Select(w => $"{w!["slot"]}: {w["name"]} ({w["seconds"]} s)")) + (ws.Count > 30 ? "; …" : ""), r);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "wave_import",
                Title = "Import a sample",
                Description =
                    "Loads an audio file (WAV, AIFF, FLAC, OGG) into a wavetable slot: the first empty one, or slot. Won't replace a filled slot " +
                    "unless replace is true; layer=true adds it to the filled slot as another layer instead (its own root: Neo Sampler splits " +
                    "a zone's keys by the layers' roots and alternates layers of one root). root_note sets the pitch it plays at unchanged " +
                    "(without it, C3). One undo step. Example: wave_import(path=\"C:/Samples/kick.wav\", name=\"Kick\").",
                Input = Object(null,
                    Req("path", String("Full path of the file")),
                    Opt("slot", Integer("Slot, from 1 (default: the first empty)", 1)),
                    Opt("name", String("Its name (default: the file name)")),
                    Opt("root_note", AnyOf("Root pitch", Integer("MIDI number", 0, 127), String("Name: \"C4\""))),
                    Opt("replace", Boolean("Replace a filled slot")),
                    Opt("layer", Boolean("Add the file to the filled slot as another layer"))),
                Output = Object(null, Req("wave", Object(null, Req("slot", Integer("Slot")), Req("name", String("Name"))))),
                Annotations = ToolAnnotations.Write,
                Handler = async c =>
                {
                    string path = Path.GetFullPath(c.RequiredString("path").Trim().Trim('"'));
                    if (!File.Exists(path)) throw new ToolError($"No file at {path}.");
                    if (!Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                        throw new ToolError($"{Path.GetFileName(path)}: use a WAV, AIFF, FLAC or OGG file.");
                    int? root = c.Arguments["root_note"] != null ? NoteList.ParsePitch(c.Arguments["root_note"], "root_note") : null;
                    var r = await host.Engine("wave_import", true, messages =>
                    {
                        var table = core.SongCore.Wavetable;
                        int index = c.Int("slot") is int s ? s - 1 : FreeSlot(core);
                        if (index < 0 || index >= table.Waves.Count) throw new ToolError($"slot: the wavetable has slots 1–{table.Waves.Count}.");
                        var old = table.Waves[index];
                        string backup = null, oldName = old?.Name;
                        bool layer = c.Bool("layer") == true && old != null && old.Layers.Count > 0;
                        if (c.Bool("layer") == true && c.Bool("replace") == true) throw new ToolError("Pass layer or replace, not both.");
                        if (old != null && !layer)
                        {
                            if (c.Bool("replace") != true)
                                throw new ToolError($"Slot {index + 1} holds \"{old.Name}\". Pass replace=true, layer=true to add another layer, or leave slot out for the first empty one (slot {FreeSlot(core) + 1}).");
                            if (old.Layers.Count != 1) throw new ToolError($"Slot {index + 1} has {old.Layers.Count} layers; replacing it couldn't be undone. Use another slot.");
                            backup = Path.Combine(Path.GetTempPath(), "ReBuzzAgent", $"wave-{Guid.NewGuid():N}.wav");
                            Save(old.Layers[0], backup);
                        }
                        string name = layer ? oldName : c.String("name")?.Trim() is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(path);
                        var layerBefore = old?.Layers[0];
                        int oldRoot = layerBefore?.RootNote ?? 0, oldLoopStart = layerBefore?.LoopStart ?? 0, oldLoopEnd = layerBefore?.LoopEnd ?? 0;
                        int layersBefore = old?.Layers.Count ?? 0;
                        void Load()
                        {
                            table.LoadWave(index, path, name, layer);
                            var layers = table.Waves[index]?.Layers;
                            if (root is int midi && layers?.Count > 0) layers[layer ? layers.Count - 1 : 0].RootNote = Pitch.ToBuzz(midi);
                        }
                        void Restore()
                        {
                            if (layer)
                            {
                                // The added layer goes (the slot's others stay as they are).
                                lock (ReBuzzCore.AudioLock)
                                {
                                    var w = core.SongCore.WavetableCore.WavesList[index];
                                    while (w != null && w.LayersList.Count > layersBefore)
                                    {
                                        var added = w.LayersList[^1];
                                        w.LayersList.RemoveAt(w.LayersList.Count - 1);
                                        added.Release();
                                    }
                                    w?.Invalidate();
                                }
                                return;
                            }
                            if (backup == null) { table.LoadWave(index, null, null, false); return; }
                            table.LoadWave(index, backup, oldName, false);
                            var restored = table.Waves[index]?.Layers.FirstOrDefault();
                            if (restored != null) { restored.RootNote = oldRoot; restored.LoopStart = oldLoopStart; restored.LoopEnd = oldLoopEnd; }
                        }
                        try { AgentActions.Do(core, new DelegateAction(Load, Restore)); }
                        catch (Exception e) { throw new ToolError($"Couldn't load {Path.GetFileName(path)}: {e.Message}"); }
                        var wave = table.Waves[index] ?? throw new ToolError($"{Product.Name} didn't load {Path.GetFileName(path)}." + (messages.Count > 0 ? " " + string.Join(" | ", messages) : ""));
                        if (layer && wave.Layers.Count <= layersBefore) throw new ToolError($"{Product.Name} didn't add {Path.GetFileName(path)} as a layer." + (messages.Count > 0 ? " " + string.Join(" | ", messages) : ""));
                        return new JsonObject { ["wave"] = WaveJson(wave, index), ["replaced"] = layer ? null : oldName, ["layered"] = layer };
                    }, c.Cancellation);
                    var w = r["wave"]!;
                    if ((bool?)r["layered"] == true)
                        return ToolResult.Ok($"Added a layer to slot {w["slot"]} (\"{w["name"]}\"): it has {w["layers"]} layers now.", r);
                    return ToolResult.Ok($"Loaded \"{w["name"]}\" into slot {w["slot"]}: {w["seconds"]} s, {w["channels"]} channel(s), root {w["root"]!["name"]}." +
                                         (r["replaced"] != null ? $" It replaced \"{r["replaced"]}\"." : ""), r);
                },
            });
        }

        static int FreeSlot(ReBuzzCore core)
        {
            var waves = core.SongCore.Wavetable.Waves;
            for (int i = 0; i < waves.Count; i++) if (waves[i] == null) return i;
            throw new ToolError("The wavetable is full.");
        }

        static JsonObject WaveJson(IWave w, int index)
        {
            var layer = w.Layers.FirstOrDefault();
            var o = new JsonObject { ["slot"] = index + 1, ["name"] = w.Name ?? "" };
            if (layer != null)
            {
                int rate = Math.Max(1, layer.SampleRate);
                o["seconds"] = Math.Round(layer.SampleCount / (double)rate, 3);
                o["channels"] = layer.ChannelCount;
                o["sample_rate"] = layer.SampleRate;
                int midi = Pitch.FromBuzz(layer.RootNote);
                o["root"] = midi >= 0 ? new JsonObject { ["pitch"] = midi, ["name"] = Pitch.Name(midi) } : new JsonObject { ["pitch"] = 60, ["name"] = "C4" };
                if (layer.LoopEnd > layer.LoopStart && (w.Flags & WaveFlags.Loop) != 0)
                    o["loop_seconds"] = new JsonArray(Math.Round(layer.LoopStart / (double)rate, 3), Math.Round(layer.LoopEnd / (double)rate, 3));
            }
            if (w.Layers.Count > 1)
            {
                o["layers"] = w.Layers.Count;
                o["layer_roots"] = new JsonArray(w.Layers.Select(l => (JsonNode)Pitch.Name(Pitch.FromBuzz(l.RootNote) is int m && m >= 0 ? m : 48)).ToArray());
            }
            return o;
        }

        /// <summary>A layer as a float WAV (for undo of a replace).</summary>
        static void Save(IWaveLayer layer, string path)
        {
            int n = layer.SampleCount, ch = Math.Max(1, layer.ChannelCount);
            var l = new float[n];
            var r = new float[n];
            layer.GetDataAsFloat(l, 0, 1, 0, 0, n);
            if (ch > 1) layer.GetDataAsFloat(r, 0, 1, 1, 0, n); else System.Array.Copy(l, r, n);
            var interleaved = new float[n * 2];
            for (int i = 0; i < n; i++) { interleaved[2 * i] = l[i]; interleaved[2 * i + 1] = r[i]; }
            Wav.WriteStereoFloat(path, interleaved, n, Math.Max(1, layer.SampleRate));
        }
    }
}
