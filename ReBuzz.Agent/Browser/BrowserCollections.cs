// Fork (docs/specs/browser.md, BR1): the browser's Collections, as Ableton's: an item of any kind (machine, plug-in, preset, a sample file,
// a folder) can be a favourite, and a favourite can carry one of up to seven colour tags; each colour is a collection of its own. Kept in the
// settings (key BrowserCollections), as lines of "colour<TAB>kind:id". The machine star of the machine browser (MachineFavourites) is
// the same thing for machines and stays where it was; the browser shows both.

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Agent.Browser
{
    public sealed class BrowserCollections
    {
        public const string SettingsKey = "BrowserCollections";
        public const int Colours = 7;

        /// <summary>The seven tags' names, in the order of the colours the panel draws.</summary>
        public static readonly string[] ColourNames = { "Red", "Orange", "Yellow", "Green", "Blue", "Violet", "Grey" };

        readonly Func<string> read;
        readonly Action<string> write;
        Dictionary<string, int>? items;

        public BrowserCollections(Func<string> read, Action<string> write)
        {
            this.read = read;
            this.write = write;
        }

        public event Action? Changed;

        /// <summary>The key of an item: "kind:id" (a preset: "preset:Gear/Name").</summary>
        public static string Key(BrowserKind kind, string id, string? preset = null) =>
            BrowserDragData.KindName(kind) + ":" + (preset == null ? id : id + "/" + preset);

        public static string FolderKey(string path) => "folder:" + path;

        Dictionary<string, int> Items
        {
            get
            {
                if (items != null) return items;
                items = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var line in (read() ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || !int.TryParse(line.AsSpan(0, tab), out int c) || c < 0 || c > Colours) continue;
                    items[line[(tab + 1)..]] = c;
                }
                return items;
            }
        }

        public bool IsFavourite(string key) => Items.ContainsKey(key);

        /// <summary>The tag 1 to 7, or 0 for a favourite with none (and for what is not one).</summary>
        public int Colour(string key) => Items.TryGetValue(key, out var c) ? c : 0;

        /// <summary>Everything marked: every favourite.</summary>
        public IReadOnlyList<string> All() => Items.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The items with one tag (1 to 7).</summary>
        public IReadOnlyList<string> WithColour(int colour) => Items.Where(i => i.Value == colour).Select(i => i.Key).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

        public void ToggleFavourite(string key)
        {
            if (!Items.Remove(key)) Items[key] = 0;
            Save();
        }

        /// <summary>Tags an item (making it a favourite); 0 takes the tag off and leaves the item a favourite.</summary>
        public void SetColour(string key, int colour)
        {
            if (colour < 0 || colour > Colours) throw new ArgumentOutOfRangeException(nameof(colour));
            Items[key] = colour;
            Save();
        }

        public void Remove(string key)
        {
            if (Items.Remove(key)) Save();
        }

        void Save()
        {
            write(string.Join("\n", Items.Select(i => i.Value + "\t" + i.Key)));
            Changed?.Invoke();
        }

        public void Reload()
        {
            items = null;
            Changed?.Invoke();
        }
    }
}
