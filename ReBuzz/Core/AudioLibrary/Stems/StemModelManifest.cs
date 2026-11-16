#nullable disable
// Fork (docs/specs/stem-separation.md): the description of one stem model on disk, a model.json next to its ONNX files. The export
// scripts (tools/stems) write it; the app reads it to know the quality it serves, the rate and segment the network takes, the transform
// around it, and which outputs of which file make each stem. Pure: ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBuzz.Core.AudioLibrary.Stems
{
    internal enum StemQuality { Fast, Balanced, Best }

    internal sealed class StemPass
    {
        /// <summary>The ONNX file, in the model's folder.</summary>
        [JsonPropertyName("file")] public string File { get; set; } = "";

        /// <summary>
        /// For each stem this file gives, the indexes of its outputs that make it (summed: a six-stem model's guitar and piano join "other").
        /// </summary>
        [JsonPropertyName("outputs")] public Dictionary<string, int[]> Outputs { get; set; } = new();
    }

    internal sealed class StemModelManifest
    {
        public const string FileName = "model.json";

        /// <summary>The network around the transform: "htdemucs" (a waveform and a complex-as-channels spectrogram in, two branches out)
        /// or "spec-mask" (a spectrogram in, the separated spectrograms out).</summary>
        public const string FamilyDemucs = "htdemucs", FamilySpecMask = "spec-mask";

        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("quality")] public string Quality { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("family")] public string Family { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("license")] public string License { get; set; } = "";
        [JsonPropertyName("sampleRate")] public int SampleRate { get; set; } = 44100;
        /// <summary>Samples per segment (the network's fixed input length).</summary>
        [JsonPropertyName("segment")] public int Segment { get; set; }
        /// <summary>Changes when the weights or the export change (it is part of the cache's key).</summary>
        [JsonPropertyName("revision")] public string Revision { get; set; } = "1";
        [JsonPropertyName("overlap")] public double Overlap { get; set; } = 0.25;
        [JsonPropertyName("nfft")] public int Nfft { get; set; }
        [JsonPropertyName("hop")] public int Hop { get; set; }
        /// <summary>The stems the model can give, in the order the app lists them.</summary>
        [JsonPropertyName("stems")] public string[] Stems { get; set; } = { "vocals", "drums", "bass", "other" };
        /// <summary>How many separated signals the network returns (its sources, or the second dimension of its outputs).</summary>
        [JsonPropertyName("outputStems")] public int OutputStems { get; set; }
        [JsonPropertyName("passes")] public List<StemPass> Passes { get; set; } = new();

        /// <summary>The folder it was read from.</summary>
        [JsonIgnore] public string Folder { get; set; } = "";

        public StemQuality? QualityLevel =>
            Enum.TryParse<StemQuality>(Quality, true, out var q) ? q : null;

        /// <summary>The frames of one segment's spectrogram.</summary>
        public int Frames => Family == FamilyDemucs ? (Segment + Hop - 1) / Hop : 1 + Segment / Math.Max(1, Hop);

        static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

        /// <summary>Reads a model.json; null and the reason when it isn't a usable one.</summary>
        public static StemModelManifest Load(string path, out string problem)
        {
            problem = null;
            try
            {
                var m = JsonSerializer.Deserialize<StemModelManifest>(File.ReadAllText(path), Options);
                if (m == null) throw new InvalidDataException("empty");
                m.Folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
                problem = m.Validate();
                return problem == null ? m : null;
            }
            catch (Exception e)
            {
                problem = e.Message;
                return null;
            }
        }

        /// <summary>The first thing wrong with the description, or null.</summary>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Id)) return "no id";
            if (QualityLevel == null) return $"quality \"{Quality}\" isn't Fast, Balanced or Best";
            if (Family != FamilyDemucs && Family != FamilySpecMask) return $"family \"{Family}\" is unknown";
            if (Segment <= 0 || Nfft <= 0 || Hop <= 0 || SampleRate <= 0) return "segment, nfft, hop and sampleRate must be positive";
            if ((Nfft & (Nfft - 1)) != 0) return "nfft must be a power of two";
            if (Passes.Count == 0) return "no passes";
            if (OutputStems <= 0) return "outputStems missing";
            foreach (var p in Passes)
            {
                if (string.IsNullOrWhiteSpace(p.File) || p.File.IndexOfAny(new[] { '/', '\\' }) >= 0) return "a pass names a file outside the folder";
                if (p.Outputs.Count == 0) return $"{p.File} gives no stem";
                foreach (var stem in p.Outputs.Keys)
                    if (!Stems.Contains(stem)) return $"{p.File} gives \"{stem}\", which the model doesn't list";
            }
            foreach (var stem in Stems)
                if (Passes.Count(p => p.Outputs.ContainsKey(stem)) != 1) return $"\"{stem}\" must come from exactly one pass";
            return null;
        }

        /// <summary>The ONNX files' full paths.</summary>
        public IEnumerable<string> Files => Passes.Select(p => Path.Combine(Folder, p.File));

        /// <summary>True when every ONNX file is there.</summary>
        public bool Complete => Files.All(f => System.IO.File.Exists(f));
    }
}
