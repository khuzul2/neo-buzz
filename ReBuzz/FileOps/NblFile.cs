// Fork (docs/specs/native-format.md): the native song file, .nbl. Reads and writes the same in-memory song the BMX
// writer reads and writes: the core sections are the ones NblCoreSections builds and loads (in UTF-8), kept as content-addressed
// chunks; the subsections the stores write (Session View, arrangement, markers, notes…) are manifest fields; the
// embedded history shares the chunk store with the rest.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using BuzzGUI.Common.Settings;
using ReBuzz.MachineManagement;
using ReBuzz.Core;
using ReBuzz.Core.Actions.GraphActions;
using ReBuzz.Core.Notes;
using ReBuzz.Core.Session;
using ReBuzz.Core.SongHistory;
using ReBuzz.FileOps.Nbl;

namespace ReBuzz.FileOps
{
    /// <summary>What a song opened from a .nbl carries that this build doesn't know, to write it back (forward tolerance).</summary>
    internal sealed class NblCarry
    {
        /// <summary>Top-level manifest members this build doesn't know.</summary>
        public JsonObject Members { get; } = new();
        /// <summary>Stores (subsections) this build doesn't know, as the manifest holds them.</summary>
        public JsonObject Stores { get; } = new();
        /// <summary>The blobs those two refer to, as stored.</summary>
        public Dictionary<string, (byte Codec, byte[] Stored)> Blobs { get; } = new();
        public bool IsEmpty => Members.Count == 0 && Stores.Count == 0;
    }

    internal sealed class NblFile : IReBuzzFile
    {
        public const string Extension = ".nbl";
        public const string FormatName = "neobuzz-live-song";
        public const string CoreCodec = "bmx-sections-v1";

        static readonly HashSet<string> KnownMembers = new(StringComparer.Ordinal) { "format", "formatVersion", "minReaderVersion", "writer", "index", "machines", "core", "stores", "history", NblAudio.Member };

        /// <summary>The subsections this build writes or reads by name: the others in a file are carried over untouched.</summary>
        internal static readonly HashSet<string> KnownStores = new(StringComparer.Ordinal)
        {
            SessionDocument.SubSectionName, ArrangementStore.SubSectionName, ArrangementEventStore.SubSectionName,
            SongMarkerStore.SubSectionName, SongNotesStore.SubSectionName, SongMeterStore.SubSectionName, MachinePinStore.SubSectionName, TrackColourStore.SubSectionName, MixerFoldStore.SubSectionName, MidiMapping.SubSectionName, ReBuzz.Core.NoteWires.NoteWireStore.SubSectionName,
            "ModernSequenceEditor", "ModernSequenceEditorHorizontal",
        };

        readonly ReBuzzCore buzz;
        readonly NblCoreSections inner;

        public event Action<FileEventType, string, object> FileEvent;

        public NblFile(ReBuzzCore buzz, string buzzPath, IUiDispatcher dispatcher, IKeyboard keyboard, EngineSettings engineSettings)
        {
            this.buzz = buzz;
            inner = new NblCoreSections(buzz, buzzPath, dispatcher, keyboard, engineSettings);
            inner.FileEvent += (t, s, o) => FileEvent?.Invoke(t, s, o);
        }

        public static bool IsNative(string path) => string.Equals(Path.GetExtension(path), Extension, StringComparison.OrdinalIgnoreCase);

        public Dictionary<string, MemoryStream> GetSubSections() => inner.GetSubSections();

        public void SetSubSections(SaveSongCore ss) => inner.SetSubSections(ss);

        public void EndFileOperation(bool import) => inner.EndFileOperation(import);

        // ------------------------------------------------------------------ load

        public void Load(string path, float x = 0, float y = 0, ImportSongAction importAction = null)
        {
            bool import = importAction != null;
            Stream stream;
            NblCarry carry;
            JsonArray keyedMachines;
            List<ReBuzz.Core.AudioLibrary.AudioAsset> audioAssets;
            using (var reader = new NblContainer.Reader(path))
            {
                var manifest = JsonNode.Parse(reader.Manifest) as JsonObject ?? throw new InvalidDataException("The song file's manifest isn't a JSON object.");
                if ((string)manifest["format"] != FormatName) throw new InvalidDataException("Not a NeoBuzz Live song.");
                var ctx = new NblReadContext(reader) { Path = path };
                if (!import) buzz.OpenedNativeVersion = NblMigrations.VersionOf(manifest);      // before the steps bring it up: 3 says the lanes are in the store
                NblMigrations.Default.Migrate(manifest, ctx);
                audioAssets = NblAudio.Read(manifest, ctx);
                (stream, carry) = ToBmxLayout(manifest, ctx, import);
                keyedMachines = manifest["machines"] as JsonArray;
            }
            if (!import) buzz.NativeCarry = carry;
            // The audio assets come before the sections: the Session View's clips name them (docs/specs/neo-audio.md).
            if (import) foreach (var a in audioAssets) buzz.AudioAssets.Add(a);
            else buzz.AudioAssets.ReplaceWith(audioAssets);
            using (NblCoreSections.Utf8Strings())
                inner.LoadFrom(stream, path, x, y, importAction);
            if (!import && keyedMachines != null) ApplyKeyed(buzz, keyedMachines);
        }

        /// <summary>
        /// Our versioned machines (machines.md §3.2) save each global parameter as (key, raw value) with the machine's StateVersion:
        /// the values come back through the machine's own MigrateKeyed, so a layout that changed since the song was saved loads by identity.
        /// Machines that aren't versioned (old Buzz machines) keep the positional values the core sections hold.
        /// </summary>
        static JsonArray BuildMachines(ReBuzzCore buzz)
        {
            var list = new JsonArray();
            foreach (var m in buzz.SongCore.MachinesList)
            {
                if (m.ManagedMachine is not IVersionedMachine v) continue;
                var globals = m.ParameterGroupsList[1].ParametersList;
                var keys = v.ParameterKeys;
                var values = new JsonObject();
                for (int i = 0; i < globals.Count && i < keys.Count; i++)
                    if (IsState(globals[i])) values[keys[i]] = globals[i].GetValue(0);
                list.Add(new JsonObject { ["name"] = m.Name, ["stateVersion"] = v.StateVersion, ["params"] = values });
            }
            return list;
        }

        static bool IsState(IParameter p) => p.Flags.HasFlag(ParameterFlags.State) && p.Type != ParameterType.Note;

        static void ApplyKeyed(ReBuzzCore buzz, JsonArray saved)
        {
            foreach (var entry in saved)
            {
                var m = buzz.SongCore.MachinesList.FirstOrDefault(x => x.Name == (string)entry["name"]);
                if (m?.ManagedMachine is not IVersionedMachine v) continue;
                int from = (int?)entry["stateVersion"] ?? 0;
                var raw = new Dictionary<string, int>();
                foreach (var p in (JsonObject)entry["params"]) raw[p.Key] = (int)p.Value;
                IReadOnlyDictionary<string, int> current = from == v.StateVersion ? raw : from < v.StateVersion ? v.MigrateKeyed(from, raw) : null;
                if (current == null)
                {
                    buzz.DCWriteLine($"{m.Name}: its parameters were saved at state version {from}, this build has {v.StateVersion}; they keep what the song's core holds.", DCLogLevel.Warning);
                    continue;
                }
                var globals = m.ParameterGroupsList[1].ParametersList;
                var keys = v.ParameterKeys;
                for (int i = 0; i < globals.Count && i < keys.Count; i++)
                    if (IsState(globals[i]) && current.TryGetValue(keys[i], out int value))
                        globals[i].SetValue(0, value);
            }
        }

        /// <summary>The song's sections laid out as a BMX file has them, for the loader; and the parts this build doesn't know.</summary>
        static (Stream, NblCarry) ToBmxLayout(JsonObject manifest, NblReadContext ctx, bool import)
        {
            var parts = new List<(uint Magic, byte[] Data)>();
            var core = manifest["core"] as JsonObject ?? throw new InvalidDataException("The song file has no core.");
            if ((string)core["codec"] != CoreCodec) throw new InvalidDataException("The song's core uses a storage this version doesn't know: " + core["codec"]);
            foreach (var s in core["sections"] as JsonArray ?? new JsonArray())
                parts.Add((MagicOf((string)s["id"]), Join(s["chunks"] as JsonArray, ctx)));

            var carry = new NblCarry();
            var subs = new List<(string Name, byte[] Data)>();
            if (manifest["stores"] is JsonObject stores)
                foreach (var st in stores)
                {
                    var entry = (JsonObject)st.Value;
                    subs.Add((st.Key, entry["kind"]?.ToString() == "xml" ? NblXml.ToBytes((JsonObject)entry["doc"]) : Join(entry["chunks"] as JsonArray, ctx)));
                    if (!KnownStores.Contains(st.Key))
                    {
                        carry.Stores[st.Key] = entry.DeepClone();
                    }
                }
            parts.Add(((uint)NblCoreSections.SectionType.IUGB, NblCoreSections.BuildSubSections(subs.OrderBy(s => s.Name, StringComparer.Ordinal))));

            if (!import && manifest["history"] is JsonObject history)
                parts.Add(((uint)NblCoreSections.SectionType.HIST, PackHistory(history, ctx)));

            foreach (var m in manifest)
                if (!KnownMembers.Contains(m.Key))
                    carry.Members[m.Key] = m.Value?.DeepClone();
            CollectBlobs(carry.Members, ctx, carry.Blobs);
            CollectBlobs(carry.Stores, ctx, carry.Blobs);

            return (new MemoryStream(NblCoreSections.Assemble(parts), false), carry);
        }

        static byte[] Join(JsonArray chunks, NblReadContext ctx)
        {
            var ms = new MemoryStream();
            foreach (var c in chunks ?? new JsonArray())
            {
                var b = ctx.Get((string)c["blob"]);
                ms.Write(b, 0, b.Length);
            }
            return ms.ToArray();
        }

        static void CollectBlobs(JsonNode node, NblReadContext ctx, Dictionary<string, (byte, byte[])> into)
        {
            if (node is JsonObject o)
            {
                foreach (var p in o)
                {
                    if (p.Key == "blob" && p.Value is JsonValue v && v.TryGetValue<string>(out var h) && h.Length == 64 && ctx.Has(h) && !into.ContainsKey(h))
                        into[h] = ctx.GetStored(h);
                    else CollectBlobs(p.Value, ctx, into);
                }
            }
            else if (node is JsonArray a)
                foreach (var e in a) CollectBlobs(e, ctx, into);
        }

        /// <summary>The embedded history in the layout the snapshots read (SongSnapshots.ImportEmbedded).</summary>
        static byte[] PackHistory(JsonObject history, NblReadContext ctx)
        {
            var ms = new MemoryStream();
            using var w = new BinaryWriter(ms, Encoding.UTF8, true);
            var manifests = history["manifests"] as JsonArray ?? new JsonArray();
            w.Write((byte)1);
            w.Write(manifests.Count);
            foreach (var m in manifests)
            {
                var json = Encoding.UTF8.GetBytes(m.ToJsonString());
                w.Write(json.Length);
                w.Write(json);
            }
            var chunks = history["chunks"] as JsonArray ?? new JsonArray();
            w.Write(chunks.Count);
            foreach (var c in chunks)
            {
                string hash = (string)c;
                var (codec, stored) = ctx.GetStored(hash);
                if (codec == 0) stored = Deflate(stored);
                w.Write(Convert.FromHexString(hash));
                w.Write(stored.Length);
                w.Write(stored);
            }
            w.Flush();
            return ms.ToArray();
        }

        static byte[] Deflate(byte[] raw)
        {
            var ms = new MemoryStream();
            using (var z = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                z.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        // ------------------------------------------------------------------ save

        public void Save(string filename)
        {
            inner.BeginExternalSave(filename);
            try
            {
                List<(uint Magic, byte[] Data)> parts;
                // Fork (clips.md §7.2, CU3): a native song holds its clips as lanes in the clip columns, so its clip editors' data holds no events.
                using (NblCoreSections.Utf8Strings())
                    parts = inner.BuildSections(true);
                var blobs = new NblBlobSet();
                var manifest = BuildManifest(buzz, parts, blobs, buzz.NativeCarry);
                NblContainer.WriteAtomic(filename, Encoding.UTF8.GetBytes(manifest.ToJsonString(ManifestJson)), blobs);
                // The audio of the song is in this file now: it is where a cache file lost later is made again from (unless an earlier file still is).
                foreach (var a in buzz.SessionLayout?.ReferencedAssets() ?? Array.Empty<ReBuzz.Core.AudioLibrary.AudioAsset>())
                    if (a.Recipe == null || !a.Recipe.Available) a.Recipe = new NblRecipe(filename, a.Id);
            }
            finally
            {
                inner.EndFileOperation(false);
            }
        }

        static readonly JsonSerializerOptions ManifestJson = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        /// <summary>The manifest and its blobs for the sections a save built. Deterministic: the same parts give the same bytes.</summary>
        internal static JsonObject BuildManifest(ReBuzzCore buzz, IReadOnlyList<(uint Magic, byte[] Data)> parts, NblBlobSet blobs, NblCarry carry)
        {
            var root = new JsonObject
            {
                ["format"] = FormatName,
                ["formatVersion"] = NblMigrations.Current,
                ["minReaderVersion"] = 1,
                ["writer"] = new JsonObject
                {
                    ["app"] = "NeoBuzz Live",
                    ["build"] = typeof(NblFile).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "",
                },
            };
            root["index"] = BuildIndex(buzz);
            root["machines"] = BuildMachines(buzz);

            var sections = new JsonArray();
            var stores = new JsonObject();
            JsonObject history = null;
            foreach (var (magic, data) in parts)
            {
                if (magic == (uint)NblCoreSections.SectionType.IUGB)
                {
                    foreach (var sub in NblCoreSections.ParseSubSections(data).OrderBy(s => s.Name, StringComparer.Ordinal))
                        stores[sub.Name] = StoreEntry(sub.Data, blobs);
                }
                else if (magic == (uint)NblCoreSections.SectionType.HIST)
                    history = HistoryEntry(data, blobs);
                else
                    sections.Add(new JsonObject { ["id"] = IdOf(magic), ["size"] = data.Length, ["chunks"] = Refs(blobs.AddChunked(data)) });
            }
            root["core"] = new JsonObject { ["codec"] = CoreCodec, ["strings"] = "utf-8", ["sections"] = sections };

            if (carry != null)
            {
                foreach (var s in carry.Stores)
                    if (!stores.ContainsKey(s.Key)) stores[s.Key] = s.Value.DeepClone();
                foreach (var b in carry.Blobs)
                    blobs.AddStored(b.Key, b.Value.Codec, b.Value.Stored);
            }
            root["stores"] = new JsonObject(stores.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => KeyValuePair.Create(s.Key, s.Value.DeepClone())));
            // The audio the clips play (neo-audio.md): a song with none stays readable by a version 1 reader.
            var audio = NblAudio.Build(buzz.SessionLayout?.ReferencedAssets() ?? Array.Empty<ReBuzz.Core.AudioLibrary.AudioAsset>(), blobs);
            if (audio != null)
            {
                root[NblAudio.Member] = audio;
                root["minReaderVersion"] = 2;
            }
            // Fork (clips.md §7.2, CU3): a song whose tracker data is lanes in the store (which an older build does not play: its editor data holds no events)
            // needs a reader of format 3: an older build refuses and says so, rather than open the song without them.
            if (ClipColumnCore.PlaysEveryLane && ClipStore.HoldsTrackerLanes(buzz.SongCore))
                root["minReaderVersion"] = Math.Max((int)root["minReaderVersion"], 3);
            if (history != null) root["history"] = history;
            if (carry != null)
                foreach (var m in carry.Members.OrderBy(m => m.Key, StringComparer.Ordinal))
                    if (!root.ContainsKey(m.Key)) root[m.Key] = m.Value?.DeepClone();
            return root;
        }

        static JsonObject StoreEntry(byte[] data, NblBlobSet blobs)
        {
            var doc = NblXml.TryToJson(data);
            if (doc != null) return new JsonObject { ["kind"] = "xml", ["doc"] = doc };
            return new JsonObject { ["kind"] = "binary", ["size"] = data.Length, ["chunks"] = Refs(blobs.AddChunked(data)) };
        }

        static JsonArray Refs(IEnumerable<BlobRef> refs)
        {
            var a = new JsonArray();
            foreach (var r in refs) a.Add(new JsonObject { ["blob"] = r.Hash, ["size"] = r.Size });
            return a;
        }

        /// <summary>The history section (SongSnapshots.BuildEmbedded) as manifests plus chunks in the shared blob table.</summary>
        static JsonObject HistoryEntry(byte[] packed, NblBlobSet blobs)
        {
            using var r = new BinaryReader(new MemoryStream(packed));
            if (r.ReadByte() != 1) return null;
            var manifests = new JsonArray();
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
                manifests.Add(JsonNode.Parse(r.ReadBytes(r.ReadInt32())));
            var hashes = new JsonArray();
            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string hash = Convert.ToHexString(r.ReadBytes(32)).ToLowerInvariant();
                blobs.AddStoredDeflate(hash, r.ReadBytes(r.ReadInt32()));
                hashes.Add(hash);
            }
            return new JsonObject { ["manifests"] = manifests, ["chunks"] = hashes };
        }

        /// <summary>A derived summary for tools that list a song without loading it. Ignored on load; the core is the truth.</summary>
        static JsonObject BuildIndex(ReBuzzCore buzz)
        {
            var song = buzz.SongCore;
            var machines = new JsonArray();
            foreach (var m in song.MachinesList)
            {
                var pos = m.Position;
                machines.Add(new JsonObject
                {
                    ["name"] = m.Name,
                    ["dll"] = m.DLL?.Name,
                    ["x"] = pos?.Item1 ?? 0f,
                    ["y"] = pos?.Item2 ?? 0f,
                    ["tracks"] = m.TrackCount,
                });
            }
            return new JsonObject
            {
                ["bpm"] = buzz.BPM,
                ["tpb"] = buzz.TPB,
                ["songEnd"] = song.SongEnd,
                ["loopStart"] = song.LoopStart,
                ["loopEnd"] = song.LoopEnd,
                ["machines"] = machines,
            };
        }

        // BMX section magics are four letters read low byte first: 0x4843414D is "MACH".
        static string IdOf(uint magic) => new string(new[] { (char)(magic & 0xFF), (char)((magic >> 8) & 0xFF), (char)((magic >> 16) & 0xFF), (char)(magic >> 24) });

        static uint MagicOf(string id)
        {
            if (id == null || id.Length != 4) throw new InvalidDataException("A section of the song has no valid id.");
            return ((uint)id[3] << 24) | ((uint)id[2] << 16) | ((uint)id[1] << 8) | id[0];
        }
    }
}
