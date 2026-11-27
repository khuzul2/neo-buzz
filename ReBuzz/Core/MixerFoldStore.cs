using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (docs/specs/ux-overhaul.md §11, MX1): which groups the mixer shows folded to one strip, kept with the song. A group is its id
    /// in the track model ("group:Drum bus"). Kept in a "MixerFolds" subsection, in .bmx, .bmxml and .nbl alike (upstream ReBuzz skips
    /// it), versioned: a newer version than this build knows is left unread. Folding is a view state: it is not an undo step and does not
    /// mark the song modified by itself; it is saved with the song the next time the song is saved. A renamed group keeps its state.
    /// </summary>
    internal sealed class MixerFoldStore
    {
        public const string SubSectionName = "MixerFolds";
        public const int FormatVersion = 1;

        readonly ReBuzzCore buzz;
        readonly HashSet<string> folded = new(StringComparer.Ordinal);

        internal MixerFoldStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        /// <summary>A group was folded or opened, or a song was opened or closed.</summary>
        public event Action Changed;

        /// <summary>The ids of the folded groups.</summary>
        public IReadOnlyCollection<string> Folded => folded;

        public bool IsFolded(string groupId) => groupId != null && folded.Contains(groupId);

        /// <summary>Folds or opens a group. False when it was so already.</summary>
        public bool Set(string groupId, bool fold)
        {
            if (string.IsNullOrEmpty(groupId)) return false;
            if (!(fold ? folded.Add(groupId) : folded.Remove(groupId))) return false;
            Changed?.Invoke();
            return true;
        }

        /// <summary>A machine renamed: its group's state follows.</summary>
        public void Rename(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
            if (!folded.Remove("group:" + oldName)) return;
            folded.Add("group:" + newName);
            Changed?.Invoke();
        }

        /// <summary>A new song: none.</summary>
        public void Clear()
        {
            if (folded.Count == 0) return;
            folded.Clear();
            Changed?.Invoke();
        }

        void OnSaveSong(ISaveSong save)
        {
            // Only groups still in the song (a machine of that name); none, no subsection.
            var names = new HashSet<string>(save.Song.Machines.Select(m => "group:" + m.Name), StringComparer.Ordinal);
            var live = folded.Where(names.Contains).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (live.Count == 0) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var root = new XElement("MixerFolds", new XAttribute("version", FormatVersion),
                live.Select(id => new XElement("Group", new XAttribute("id", id))));
            var bytes = new UTF8Encoding(false).GetBytes(root.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            folded.Clear();
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    var root = XDocument.Load(stream).Root;
                    int version = int.TryParse((string)root.Attribute("version"), out int v) ? v : 1;
                    if (version > FormatVersion)
                        buzz.DCWriteLine($"Mixer folds: saved by a newer version ({version}); not read.", DCLogLevel.Warning);
                    else
                        foreach (var g in root.Elements("Group"))
                        {
                            string id = (string)g.Attribute("id");
                            if (!string.IsNullOrEmpty(id)) folded.Add(id);
                        }
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Mixer folds: " + e.Message, DCLogLevel.Warning);
            }
            Changed?.Invoke();
        }
    }
}
