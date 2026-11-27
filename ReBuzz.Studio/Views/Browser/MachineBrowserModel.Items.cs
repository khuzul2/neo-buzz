// Fork (docs/specs/browser.md, BR1): the machine browser's model grows past machines. It stays the one model (the panel on the left and the
// popup on the canvas read it), and now also knows the other kinds a result can be: plug-ins (Neo Plug's scan, by vendor), presets, the
// song's clips, sample and MIDI files of the user's places (BrowserLibrary), the song's own waves; the sources the panel lists
// (Collections, Categories, Places); one search over all of them; and hot-swap (a machine put in another's place).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Browser;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Views.Browser
{
    /// <summary>One result of any kind. <see cref="Tag"/> is the list row's tag: "gear:Name", "preset:Gear/Name", "clip:Machine/Name", "audio:path", "midi:path", "wave:N".</summary>
    internal sealed record BrowserItem(BrowserKind Kind, string Id, string Name, string Group, string Detail, string Tag,
        MachineEntry Machine = null, SampleEntry File = null, IClip Clip = null, string Preset = null)
    {
        public BrowserDragData Drag => new(Kind, Id, Preset, Name);

        /// <summary>The key a favourite or colour tag is kept under.</summary>
        public string Key => BrowserCollections.Key(Kind, Id, Preset);

        public string Path => Kind is BrowserKind.Audio or BrowserKind.Midi && !Id.StartsWith("wave:", StringComparison.Ordinal) ? Id : null;
    }

    /// <summary>A row of the results: a group header (Header, Tag "#Group") or an item.</summary>
    internal sealed record BrowserRow(string Tag, string Text, BrowserItem Item, bool Header);

    /// <summary>A source in the left column.</summary>
    internal sealed record BrowserSource(string Id, string Title, string Section)
    {
        public const string All = "all";
        public const string Favourites = "col:fav";
    }

    internal sealed partial class MachineBrowserModel
    {
        public const int MaxRows = 400;

        public static readonly (string Id, string Title)[] CategorySources =
        {
            ("cat:instruments", "Instruments"), ("cat:drums", "Drums"), ("cat:effects", "Audio Effects"), ("cat:control", "MIDI and Control"),
            ("cat:plugins", "Plug-ins"), ("cat:presets", "Presets"), ("cat:clips", "Clips"), ("cat:samples", "Samples"), ("cat:neoaudio", "Neo Audio"),
        };

        BrowserLibrary library;
        BrowserCollections collections;

        /// <summary>The places, their index and the recent files (a file in the settings folder; the settings keys of the registry).</summary>
        public BrowserLibrary Library => library ??= MakeLibrary();

        BrowserLibrary MakeLibrary()
        {
            string folder = StudioHost.Current?.SettingsFolder ?? StudioHost.DefaultSettingsFolder;
            var lib = new BrowserLibrary(folder,
                key => { try { return registry?.Read(key, "", RegistrySection) ?? ""; } catch { return ""; } },
                (key, value) => { try { registry?.Write(key, value, RegistrySection); } catch { /* a setting that can't be saved is not worth a failed click */ } });
            lib.PlacesChanged += () => Changed?.Invoke();
            return lib;
        }

        /// <summary>Starts the background index and the file watchers (the window does, once).</summary>
        public void StartIndexing() => Library.Start();

        public BrowserCollections Collections => collections ??= MakeCollections();

        BrowserCollections MakeCollections()
        {
            var c = new BrowserCollections(
                () => { try { return registry?.Read(BrowserCollections.SettingsKey, "", RegistrySection) ?? ""; } catch { return ""; } },
                v => { try { registry?.Write(BrowserCollections.SettingsKey, v, RegistrySection); } catch { } });
            c.Changed += () => Changed?.Invoke();
            return c;
        }

        // ------------------------------------------------------------------ favourites and tags, for any item

        static bool IsGearItem(BrowserItem item) => item.Machine != null && item.Kind is BrowserKind.Machine or BrowserKind.Plugin;

        public bool IsFavourite(BrowserItem item) =>
            IsGearItem(item) ? IsFavourite(item.Machine) || Collections.IsFavourite(item.Key) : Collections.IsFavourite(item.Key);

        public void ToggleFavourite(BrowserItem item)
        {
            if (IsGearItem(item)) { ToggleFavourite(item.Machine); Collections.Remove(item.Key); }
            else Collections.ToggleFavourite(item.Key);
        }

        /// <summary>A colour tag 1 to 7 (0: none); it makes the item a favourite.</summary>
        public void SetColour(BrowserItem item, int colour)
        {
            Collections.SetColour(item.Key, colour);
            if (IsGearItem(item) && colour > 0 && !IsFavourite(item.Machine)) ToggleFavourite(item.Machine);
        }

        public int ColourOf(BrowserItem item) => Collections.Colour(item.Key);

        // ------------------------------------------------------------------ items of each kind

        public static BrowserItem MachineItem(MachineEntry e) =>
            new(e.IsPlugin ? BrowserKind.Plugin : BrowserKind.Machine, e.Name, e.Name, e.IsPlugin ? (e.Vendor.Length > 0 ? e.Vendor : "Other vendors") : (e.Category.Length > 0 ? e.Category : Other),
                e.TagLine, "gear:" + e.Name, Machine: e);

        public IEnumerable<BrowserItem> MachineItems() => Entries.Where(e => !e.IsPlugin).Select(MachineItem);

        public IEnumerable<BrowserItem> PluginItems() => Entries.Where(e => e.IsPlugin).Select(MachineItem);

        bool IsNeoAudio(MachineEntry e) => e.Name is "Neo Audio" or "Session Clip Player";

        /// <summary>The items of a category source (Instruments, Drums, Audio Effects, MIDI and Control, Neo Audio).</summary>
        IEnumerable<BrowserItem> OfCategory(string id)
        {
            foreach (var e in Entries.Where(x => !x.IsPlugin))
            {
                var type = e.Gear.Instrument.Type;
                bool drums = e.Category == "Drums";
                bool control = type == InstrumentType.Control || e.Category == "Control";
                bool ok = id switch
                {
                    "cat:drums" => drums,
                    "cat:control" => control && !drums,
                    "cat:instruments" => type == InstrumentType.Generator && !drums && !control && !IsNeoAudio(e),
                    "cat:effects" => type == InstrumentType.Effect && !drums && !control && !IsNeoAudio(e),
                    "cat:neoaudio" => IsNeoAudio(e),
                    _ => false,
                };
                if (ok) yield return MachineItem(e);
            }
        }

        // ---- presets: each machine's factory presets (its .prs.xml in Gear) and the user's own (the settings folder's Presets), together

        Dictionary<string, (string Dir, string Dll)> presetGears;

        static string GearFolder => Path.Combine(Path.GetDirectoryName(typeof(MachineExtensions).Assembly.Location) ?? "", "Gear");

        /// <summary>The installed machines that can have presets, by entry name (plug-ins' presets are not in the scan, so they are not listed). Found once.</summary>
        Dictionary<string, (string Dir, string Dll)> PresetGears()
        {
            if (presetGears != null) return presetGears;
            var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in Entries.Where(x => !x.IsPlugin))
            {
                string dll = e.Gear.Instrument.MachineDLL?.Name;
                if (string.IsNullOrEmpty(dll) || e.Gear.Instrument.Name.Length > 0) continue;
                string dir = Path.Combine(GearFolder, e.Gear.Instrument.Type == InstrumentType.Generator ? "Generators" : e.Gear.Instrument.Type == InstrumentType.Effect ? "Effects" : "");
                map[e.Name] = (dir, dll);
            }
            return presetGears = map;
        }

        public IEnumerable<BrowserItem> PresetItems()
        {
            foreach (var (gear, (dir, dll)) in PresetGears().OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                MachinePresets presets;
                try { presets = PresetStore.For(dir, dll); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var n in presets.Names.Where(x => x != "<default>").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    yield return new BrowserItem(BrowserKind.Preset, gear, n, gear, presets.IsUser(n) ? "User" : "", "preset:" + gear + "/" + n, Preset: n, Machine: Find(gear));
            }
        }

        public IEnumerable<BrowserItem> ClipItems()
        {
            foreach (var m in core.Song.Machines.Where(m => m.DLL.Info.Type != MachineType.Master))
                foreach (var p in m.Clips)
                    yield return new BrowserItem(BrowserKind.Clip, m.Name + "/" + p.Name, p.Name, m.Name, p.Length + " rows", "clip:" + m.Name + "/" + p.Name, Clip: p);
        }

        public IEnumerable<BrowserItem> SongAudioItems()
        {
            foreach (var w in core.Song.Wavetable.Waves.Where(w => w != null && w.Layers.Count > 0))
                yield return new BrowserItem(BrowserKind.Audio, "wave:" + w.Index, w.Name, "This song", "", "wave:" + w.Index);
        }

        public static BrowserItem FileItem(SampleEntry f, string group = null) =>
            new(f.Kind == IndexKind.Midi ? BrowserKind.Midi : BrowserKind.Audio, f.Path, f.Name, group ?? "", f.Describe(), (f.Kind == IndexKind.Midi ? "midi:" : "audio:") + f.Path, File: f);

        /// <summary>The folder a file sits in, relative to its place, for the tree.</summary>
        static string RelativeFolder(SampleEntry f)
        {
            string dir = Path.GetDirectoryName(f.Path) ?? "";
            string rel = dir.Length > f.Root.Length ? dir[f.Root.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : "";
            return Path.GetFileName(f.Root) + (rel.Length > 0 ? Path.DirectorySeparatorChar + rel : "");
        }

        IEnumerable<BrowserItem> FilesIn(string root, string query)
        {
            foreach (var f in Library.Index.Search(query, MaxRows, root: root)) yield return FileItem(f, RelativeFolder(f));
        }

        IEnumerable<BrowserItem> GearSamples()
        {
            string dir = GearFolder;
            if (!Directory.Exists(dir)) yield break;
            foreach (var path in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Where(AudioFileInfo.IsAudio).Take(MaxRows))
            {
                var info = AudioFileInfo.TryRead(path, out var i) ? i : default;
                yield return new BrowserItem(BrowserKind.Audio, path, Path.GetFileNameWithoutExtension(path), "Gear", info.SampleRate > 0 ? $"{info.Seconds:0.0#} s" : "", "audio:" + path);
            }
        }

        IEnumerable<BrowserItem> RecentFileItems()
        {
            foreach (var p in Library.ExistingRecents())
            {
                var e = Library.Index.Find(p);
                if (e != null) yield return FileItem(e, "Recent");
                else yield return new BrowserItem(AudioFileInfo.IsMidi(p) ? BrowserKind.Midi : BrowserKind.Audio, p, Path.GetFileNameWithoutExtension(p), "Recent", "", (AudioFileInfo.IsMidi(p) ? "midi:" : "audio:") + p);
            }
        }

        /// <summary>An item from a favourite's key, or null when what it named is gone.</summary>
        BrowserItem Resolve(string key)
        {
            int colon = key.IndexOf(':');
            if (colon <= 0) return null;
            string kind = key[..colon], id = key[(colon + 1)..];
            switch (kind)
            {
                case "machine":
                case "plugin":
                    return Find(id) is { } e ? MachineItem(e) : null;
                case "preset":
                    int slash = id.LastIndexOf('/');
                    if (slash <= 0) return null;
                    return PresetItems().FirstOrDefault(p => p.Id == id[..slash] && p.Preset == id[(slash + 1)..]);
                case "audio":
                case "midi":
                    if (!File.Exists(id)) return null;
                    var f = Library.Index.Find(id);
                    return f != null ? FileItem(f, "Favourites") : new BrowserItem(kind == "midi" ? BrowserKind.Midi : BrowserKind.Audio, id, Path.GetFileNameWithoutExtension(id), "Favourites", "", key);
                default:
                    return null;
            }
        }

        // ------------------------------------------------------------------ sources

        /// <summary>The sources of the left column, in order: All, Collections (favourites and seven colours), Categories, Places.</summary>
        public List<BrowserSource> Sources()
        {
            var list = new List<BrowserSource> { new(BrowserSource.All, "All", "") };
            list.Add(new(BrowserSource.Favourites, "Favourites", "Collections"));
            for (int i = 1; i <= BrowserCollections.Colours; i++) list.Add(new("col:" + i, BrowserCollections.ColourNames[i - 1], "Collections"));
            foreach (var (id, title) in CategorySources) list.Add(new(id, title, "Categories"));
            foreach (var p in Library.Places) list.Add(new("place:" + p, Path.GetFileName(p).Length > 0 ? Path.GetFileName(p) : p, "Places"));
            list.Add(new("gear:samples", "Gear samples", "Places"));
            list.Add(new("song:audio", "This song's audio", "Places"));
            list.Add(new("recent:files", "Recent", "Places"));
            return list;
        }

        /// <summary>The items of one source (not All), filtered by the words of the query where it has any.</summary>
        public List<BrowserItem> ItemsOf(string source, string query = "")
        {
            query = (query ?? "").Trim();
            IEnumerable<BrowserItem> items;
            switch (source)
            {
                case BrowserSource.Favourites:
                    items = MachineFavouriteItems().Concat(Collections.All().Select(Resolve).Where(i => i != null)).DistinctBy(i => i.Tag);
                    break;
                case var s when s.StartsWith("col:", StringComparison.Ordinal) && int.TryParse(s.AsSpan(4), out int colour):
                    items = Collections.WithColour(colour).Select(Resolve).Where(i => i != null);
                    break;
                case "cat:plugins": items = PluginItems(); break;
                case "cat:presets": items = PresetItems(); break;
                case "cat:clips": items = ClipItems(); break;
                case "cat:samples": items = Library.Places.SelectMany(p => FilesIn(p, query)); break;
                case "gear:samples": items = GearSamples(); break;
                case "song:audio": items = SongAudioItems(); break;
                case "recent:files": items = RecentFileItems(); break;
                case var s when s.StartsWith("cat:", StringComparison.Ordinal): items = OfCategory(s); break;
                case var s when s.StartsWith("place:", StringComparison.Ordinal): items = FilesIn(s[6..], query); break;
                default: items = Enumerable.Empty<BrowserItem>(); break;
            }
            var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 0 && !source.StartsWith("place:") && source != "cat:samples")
                items = items.Where(i => words.All(w => (i.Name + " " + i.Group + " " + i.Detail).Contains(w, StringComparison.OrdinalIgnoreCase)));
            return items.ToList();
        }

        IEnumerable<BrowserItem> MachineFavouriteItems() => FavouriteEntries.Select(MachineItem);

        /// <summary>
        /// One search over everything: machines, plug-ins, presets, the song's clips, sample and MIDI files and the song's waves, best first
        /// within each kind. <paramref name="kind"/> narrows it to one kind.
        /// </summary>
        public List<BrowserItem> SearchAll(string query, BrowserKind? kind = null, int perKind = 50)
        {
            query = (query ?? "").Trim();
            var result = new List<BrowserItem>();
            if (query.Length == 0) return result;
            bool Want(BrowserKind k) => kind == null || kind == k;
            if (Want(BrowserKind.Machine) || Want(BrowserKind.Plugin))
                result.AddRange(Search(query).Where(e => e.IsPlugin ? Want(BrowserKind.Plugin) : Want(BrowserKind.Machine)).Take(perKind * 2).Select(MachineItem));
            var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool Has(BrowserItem i) => words.All(w => (i.Name + " " + i.Group).Contains(w, StringComparison.OrdinalIgnoreCase));
            if (Want(BrowserKind.Preset)) result.AddRange(PresetItems().Where(Has).Take(perKind));
            if (Want(BrowserKind.Clip)) result.AddRange(ClipItems().Where(Has).Take(perKind));
            if (Want(BrowserKind.Audio) || Want(BrowserKind.Midi))
            {
                result.AddRange(Library.Index.Search(query, perKind, kind switch { BrowserKind.Midi => IndexKind.Midi, BrowserKind.Audio => IndexKind.Audio, _ => null })
                    .Select(f => FileItem(f, RelativeFolder(f))));
                if (Want(BrowserKind.Audio)) result.AddRange(SongAudioItems().Where(Has).Take(perKind));
            }
            return result;
        }

        // ------------------------------------------------------------------ hot-swap

        IMachine swapTarget;

        /// <summary>The machine the browser is picking a replacement for (swap mode), or null.</summary>
        public IMachine SwapTarget => swapTarget;

        public event Action SwapChanged;

        /// <summary>The machine's context menu's "Hot-swap": the browser (the panel opens) waits for a double-click on a result.</summary>
        public void BeginSwap(IMachine machine)
        {
            swapTarget = machine;
            SwapChanged?.Invoke();
        }

        public void EndSwap()
        {
            if (swapTarget == null) return;
            swapTarget = null;
            SwapChanged?.Invoke();
        }

        /// <summary>Why an item can't take a machine's place; null when it can.</summary>
        public string WhyNotSwap(IMachine machine, BrowserDragData data)
        {
            if (machine == null) return "Nothing to replace.";
            if (machine.DLL.Info.Type == MachineType.Master) return "The Master can't be replaced.";
            if (!(data.IsGear || data.Kind == BrowserKind.Preset)) return data.Kind switch
            {
                BrowserKind.Audio => "Audio goes on a track: drop it on the Arrangement or the Session.",
                BrowserKind.Midi => "A MIDI file goes on a track: drop it on the Arrangement or the Session.",
                _ => "A clip goes on its machine's track: drop it on the Arrangement or the Session.",
            };
            var e = Find(data.Id);
            if (e == null) return $"“{data.Id}” isn't installed.";
            if (data.Kind == BrowserKind.Preset && machine.DLL.Name == e.Gear.Instrument.MachineDLL.Name) return null;
            var have = machine.DLL.Info.Type;
            var want = e.Gear.Instrument.Type == InstrumentType.Generator ? MachineType.Generator : e.Gear.Instrument.Type == InstrumentType.Effect ? MachineType.Effect : MachineType.Generator;
            if (e.Gear.Instrument.Type == InstrumentType.Control) return have == MachineType.Generator && machine.IsControlMachine ? null : "A control machine can only replace a control machine.";
            if (have != want) return have == MachineType.Generator ? "A generator can only be replaced by a generator." : "An effect can only be replaced by an effect.";
            return null;
        }

        /// <summary>
        /// The machine replaced by the dropped item (the Machine View's "Replace with…" path: connections, position, clips kept where they
        /// fit, one undo step). A preset of the machine's own type loads instead; a preset of another machine replaces it and loads the preset.
        /// Returns the machine now in its place, or null with <paramref name="why"/>.
        /// </summary>
        public IMachine Swap(IMachine machine, BrowserDragData data, out string why)
        {
            why = WhyNotSwap(machine, data);
            if (why != null) return null;
            var e = Find(data.Id);
            string name = machine.Name;
            using (MachineEdits.Group(core))
            {
                if (data.Kind == BrowserKind.Preset)
                {
                    if (machine.DLL.Name != e.Gear.Instrument.MachineDLL.Name)
                    {
                        machine = MachineEditing.HotSwap(core, machine, e.Gear.Instrument.MachineDLL.Name, e.Gear.Instrument.Name) ?? machine;
                    }
                    ApplyPreset(machine, data.Preset);
                }
                else
                {
                    if (machine.DLL.Name == e.Gear.Instrument.MachineDLL.Name && ((machine as MachineCore)?.InstrumentName ?? "") == e.Gear.Instrument.Name)
                    {
                        why = "It already is " + e.Name + ".";
                        return null;
                    }
                    machine = MachineEditing.HotSwap(core, machine, e.Gear.Instrument.MachineDLL.Name, e.Gear.Instrument.Name) ?? machine;
                }
            }
            MarkUsed(e);
            return machine;
        }

        void ApplyPreset(IMachine machine, string preset)
        {
            var p = machine.GetPreset(preset);
            if (p != null) ParameterEdits.ApplyPreset(core, machine, p);
        }

        /// <summary>A dropped or double-clicked gear item or preset on empty canvas: the machine added at the place (a preset loaded into it), one undo step.</summary>
        public IMachine AddDropped(BrowserDragData data, float x, float y, out string why)
        {
            why = null;
            if (!(data.IsGear || data.Kind == BrowserKind.Preset))
            {
                why = "Drop audio, MIDI and clips on the Arrangement or the Session.";
                return null;
            }
            var e = Find(data.Id);
            if (e == null) { why = $"“{data.Id}” isn't installed."; return null; }
            using (MachineEdits.Group(core))
            {
                var made = Add(e, x, y);
                if (made != null && data.Kind == BrowserKind.Preset) ApplyPreset(made, data.Preset);
                return made;
            }
        }
    }
}
