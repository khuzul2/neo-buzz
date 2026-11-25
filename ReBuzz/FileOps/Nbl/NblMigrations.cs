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

        /// <summary>The file being read (a long audio asset remembers it, to make its cache again from the blobs).</summary>
        public string Path { get; init; }

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
        public const int Current = 3;

        readonly SortedDictionary<int, Action<JsonObject, NblReadContext>> steps = new();

        /// <summary>The steps this build knows.</summary>
        public static NblMigrations Default { get; } = Standard();

        /// <summary>The steps of this build. 1 to 2 (neo-audio.md): the "audio" member is new; a version 1 song has none, so nothing changes.</summary>
        static NblMigrations Standard()
        {
            var m = new NblMigrations();
            m.Register(1, (manifest, ctx) => { });
            // 2 to 3 (clips.md §7.4, CU3): a song saved before has its tracker data in the clip editors' blobs, which the open-time migration moves into
            // the store; the manifest itself has nothing to change. (A song of format 3 has its lanes in the store, and its editors' data holds no events.)
            m.Register(2, (manifest, ctx) => { });
            return m;
        }

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
