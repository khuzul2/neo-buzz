using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork (docs/specs/ux-overhaul.md, the parameter panel): the parameters a person pinned to a machine's card in the Inspector, up to
    /// <see cref="Max"/> per machine, in the order they were pinned. A pin is a global parameter's name; the machine is its name in the
    /// song. Buzz songs have no such thing, so the pins are kept in a "DevicePins" subsection, in .bmx, .bmxml and .nbl alike (upstream
    /// ReBuzz skips it), versioned: a newer version than this build knows is left unread (and the song saves without it). Every change
    /// is a step on the song's undo history and marks the song modified.
    /// </summary>
    internal sealed class MachinePinStore
    {
        public const string SubSectionName = "DevicePins";
        public const int FormatVersion = 1;

        /// <summary>The most parameters one machine keeps pinned.</summary>
        public const int Max = 8;

        readonly ReBuzzCore buzz;
        readonly Dictionary<string, List<string>> pins = new(StringComparer.Ordinal);

        internal MachinePinStore(ReBuzzCore buzz)
        {
            this.buzz = buzz;
            buzz.SaveSong += OnSaveSong;
            buzz.OpenSong += OnOpenSong;
        }

        /// <summary>Raised when any machine's pins changed (by a person, an agent, undo or a song opening).</summary>
        public event Action Changed;

        /// <summary>The machine's pinned parameters, in order.</summary>
        public IReadOnlyList<string> Get(string machine) =>
            machine != null && pins.TryGetValue(machine, out var list) ? list.ToArray() : Array.Empty<string>();

        public bool IsPinned(string machine, string param) => Get(machine).Contains(param, StringComparer.Ordinal);

        /// <summary>Every machine with a pin, for the song.</summary>
        public IReadOnlyList<string> Machines => pins.Where(p => p.Value.Count > 0).Select(p => p.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Pins or unpins a global parameter: one undo step. False when nothing changed: it was already so, or the machine has
        /// <see cref="Max"/> pins and this would be one more.
        /// </summary>
        public bool Set(string machine, string param, bool pinned)
        {
            if (string.IsNullOrEmpty(machine) || string.IsNullOrEmpty(param)) return false;
            var before = Get(machine).ToList();
            var after = before.ToList();
            if (pinned)
            {
                if (after.Contains(param, StringComparer.Ordinal) || after.Count >= Max) return false;
                after.Add(param);
            }
            else if (after.RemoveAll(p => string.Equals(p, param, StringComparison.Ordinal)) == 0) return false;
            buzz.SongCore.ActionStack.Do(new PinChange(this, machine, before, after));
            return true;
        }

        /// <summary>Replaces a machine's pins (undo and redo, and a machine's pins carried to its renamed or copied self).</summary>
        internal void Replace(string machine, IEnumerable<string> list)
        {
            var next = list.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).Take(Max).ToList();
            if (next.Count == 0) pins.Remove(machine);
            else pins[machine] = next;
            buzz.SetModifiedFlag();
            Changed?.Invoke();
        }

        /// <summary>A new song: no pins (no step, no modified flag).</summary>
        public void Clear()
        {
            if (pins.Count == 0) return;
            pins.Clear();
            Changed?.Invoke();
        }

        [BuzzGUI.Common.HistoryName("Pin parameter")]
        sealed class PinChange : IAction
        {
            readonly MachinePinStore store;
            readonly string machine;
            readonly List<string> before, after;

            public PinChange(MachinePinStore store, string machine, List<string> before, List<string> after)
            {
                this.store = store;
                this.machine = machine;
                this.before = before;
                this.after = after;
            }

            public void Do() => store.Replace(machine, after);

            public void Undo() => store.Replace(machine, before);
        }

        void OnSaveSong(ISaveSong save)
        {
            // Only machines still in the song; none, no subsection.
            var names = new HashSet<string>(save.Song.Machines.Select(m => m.Name), StringComparer.Ordinal);
            var live = pins.Where(p => p.Value.Count > 0 && names.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
            if (live.Count == 0) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var root = new XElement("DevicePins", new XAttribute("version", FormatVersion),
                live.Select(p => new XElement("Machine", new XAttribute("name", p.Key), p.Value.Select(n => new XElement("Pin", new XAttribute("param", n))))));
            var bytes = new UTF8Encoding(false).GetBytes(root.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            pins.Clear();
            try
            {
                var stream = open.GetSubSection(SubSectionName);
                if (stream != null)
                {
                    var root = XDocument.Load(stream).Root;
                    int version = int.TryParse((string)root.Attribute("version"), out int v) ? v : 1;
                    if (version > FormatVersion)
                        buzz.DCWriteLine($"Device pins: saved by a newer version ({version}); not read.", DCLogLevel.Warning);
                    else
                        foreach (var m in root.Elements("Machine"))
                        {
                            string name = (string)m.Attribute("name");
                            var list = m.Elements("Pin").Select(p => (string)p.Attribute("param")).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).Take(Max).ToList();
                            if (!string.IsNullOrEmpty(name) && list.Count > 0) pins[name] = list;
                        }
                }
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Device pins: " + e.Message, DCLogLevel.Warning);
            }
            Changed?.Invoke();
        }
    }
}
