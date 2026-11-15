#nullable disable
// What a song saves for a Neo Plug machine (XML through ReBuzz's MachineState).

using System;
using System.IO;
using System.IO.Compression;

namespace NeoBuzz.Plug
{
    public class PlugState
    {
        public int Version { get; set; } = 1;
        /// <summary>"vst3" or "clap".</summary>
        public string Format { get; set; }
        /// <summary>The module the song was saved with (the plug-in is looked up by id and name when it has moved).</summary>
        public string Path { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Vendor { get; set; }
        /// <summary>The Gear menu name it had ("Vendor/Name (VST3)").</summary>
        public string Display { get; set; }
        /// <summary>The plug-in's own state (neoplug's envelope), deflated.</summary>
        public byte[] Blob { get; set; }
        /// <summary>Which plug-in parameter each of the 64 slots drives: ids separated by commas, empty for a free slot.</summary>
        public string SlotIds { get; set; }
        public int MidiChannel { get; set; }
        /// <summary>The machine that gets the MIDI the plug-in plays (by name); empty for none.</summary>
        public string MidiOut { get; set; }

        public static byte[] Pack(byte[] raw)
        {
            if (raw == null || raw.Length == 0) return null;
            using var ms = new MemoryStream();
            using (var z = new DeflateStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        public static byte[] Unpack(byte[] packed)
        {
            if (packed == null || packed.Length == 0) return null;
            try
            {
                using var input = new MemoryStream(packed);
                using var z = new DeflateStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                z.CopyTo(output);
                return output.ToArray();
            }
            catch (Exception) { return null; }
        }

        public PlugState Copy() => (PlugState)MemberwiseClone();
    }
}
