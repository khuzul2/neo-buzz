// Fork (docs/specs/native-format.md §6): load-time migrations. A step takes a manifest of version v to v+1 and may read
// and add blobs through the context. Steps are chained until the manifest is at the version this build writes.

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace ReBuzz.FileOps.Nbl
{
    /// <summary>What a migration step can reach: the blobs of the file being read, and blobs of its own.</summary>
    internal sealed class NblReadContext
    {
        readonly NblContainer.Reader reader;
        readonly Dictionary<string, byte[]> added = new();

        public NblReadContext(NblContainer.Reader reader) { this.reader = reader; }

        public byte[] Get(string hash) => added.TryGetValue(hash, out var b) ? b : reader.Get(hash);

        /// <summary>A blob a step writes (the manifest then names it by the returned hash).</summary>
        public string Add(byte[] data)
        {
            string hash = ReBuzz.Core.SongHistory.ChunkStore.HashOf(data);
            added[hash] = data;
            return hash;
        }

        public bool Has(string hash) => added.ContainsKey(hash) || reader.Has(hash);

        internal (byte Codec, byte[] Stored) GetStored(string hash)
        {
            if (added.TryGetValue(hash, out var b)) return (0, b);
            return reader.GetStored(hash);
        }
    }

    internal sealed class NblMigrations
    {
        /// <summary>The manifest version this build writes.</summary>
        public const int Current = 1;

        readonly SortedDictionary<int, Action<JsonObject, NblReadContext>> steps = new();

        /// <summary>The steps this build knows (none yet: version 1 is the first).</summary>
        public static NblMigrations Default { get; } = new NblMigrations();

        public void Register(int from, Action<JsonObject, NblReadContext> step) => steps[from] = step;

        public static int VersionOf(JsonObject manifest) => (int?)manifest["formatVersion"] ?? throw new System.IO.InvalidDataException("The song file has no format version.");

        /// <summary>Brings the manifest to <paramref name="target"/> (a newer one is left as it is: it is read as far as it is known).</summary>
        public void Migrate(JsonObject manifest, NblReadContext ctx, int target = Current)
        {
            int version = VersionOf(manifest);
            int minReader = (int?)manifest["minReaderVersion"] ?? 1;
            if (minReader > target)
                throw new System.IO.InvalidDataException($"This song needs a newer NeoBuzz Live (it needs song format {minReader}, this one reads up to {target}).");
            while (version < target)
            {
                if (!steps.TryGetValue(version, out var step))
                    throw new System.IO.InvalidDataException($"No way to bring a song of format {version} to {version + 1}.");
                step(manifest, ctx);
                version++;
                manifest["formatVersion"] = version;
            }
        }
    }
}
