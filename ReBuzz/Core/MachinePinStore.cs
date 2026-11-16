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
    /// The same subsection keeps the parameters a person locked (left out of Randomize and Morph): a "Lock" element beside the "Pin"s of a
    /// machine, still version 1 (a reader that doesn't know it skips it). A rename carries a machine's pins and locks to its new name.
    /// </summary>
    internal sealed class MachinePinStore
    {
        public const string SubSectionName = "DevicePins";
        public const int FormatVersion = 1;

        /// <summary>The most parameters one machine keeps pinned.</summary>
        public const int Max = 8;

        readonly ReBuzzCore buzz;
        readonly Dictionary<string, List<string>> pins = new(StringComparer.Ordinal);
        readonly Dictionary<string, List<string>> locks = new(StringComparer.Ordinal);

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

        /// <summary>The machine's locked parameters (their keys: a global's name, "T:" and a track parameter's name), in the order locked.</summary>
        public IReadOnlyList<string> GetLocks(string machine) =>
            machine != null && locks.TryGetValue(machine, out var list) ? list.ToArray() : Array.Empty<string>();

        public bool IsLocked(string machine, string key) => machine != null && locks.TryGetValue(machine, out var list) && list.Contains(key, StringComparer.Ordinal);

        /// <summary>Locks or unlocks parameters together: one undo step. False when nothing changed.</summary>
        public bool SetLocked(string machine, IEnumerable<string> keys, bool locked)
        {
            if (string.IsNullOrEmpty(machine)) return false;
            var before = GetLocks(machine).ToList();
            var after = before.ToList();
            foreach (var key in keys.Where(k => !string.IsNullOrEmpty(k)))
            {
                if (locked) { if (!after.Contains(key, StringComparer.Ordinal)) after.Add(key); }
                else after.RemoveAll(k => string.Equals(k, key, StringComparison.Ordinal));
            }
            if (after.SequenceEqual(before)) return false;
            buzz.SongCore.ActionStack.Do(new LockChange(this, machine, before, after));
            return true;
        }

        public bool SetLocked(string machine, string key, bool locked) => SetLocked(machine, new[] { key }, locked);

        internal void ReplaceLocks(string machine, IEnumerable<string> list)
        {
            var next = list.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
            if (next.Count == 0) locks.Remove(machine);
            else locks[machine] = next;
            buzz.SetModifiedFlag();
            Changed?.Invoke();
        }

        /// <summary>
        /// A machine was renamed: its pins and locks follow it (no undo step of their own: the rename's undo renames it back, and they
        /// come back with it). What the new name had is replaced; a machine with none leaves the new name's alone.
        /// </summary>
        public void Rename(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName) || string.IsNullOrEmpty(newName) || string.Equals(oldName, newName, StringComparison.Ordinal)) return;
            bool moved = false;
            foreach (var map in new[] { pins, locks })
                if (map.TryGetValue(oldName, out var list))
                {
                    map.Remove(oldName);
                    if (list.Count > 0) map[newName] = list;
                    moved = true;
                }
            if (!moved) return;
            buzz.SetModifiedFlag();
            Changed?.Invoke();
        }

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
            if (pins.Count == 0 && locks.Count == 0) return;
            pins.Clear();
            locks.Clear();
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

        [BuzzGUI.Common.HistoryName("Lock parameter")]
        sealed class LockChange : IAction
        {
            readonly MachinePinStore store;
            readonly string machine;
            readonly List<string> before, after;

            public LockChange(MachinePinStore store, string machine, List<string> before, List<string> after)
            {
                this.store = store;
                this.machine = machine;
                this.before = before;
                this.after = after;
            }

            public void Do() => store.ReplaceLocks(machine, after);

            public void Undo() => store.ReplaceLocks(machine, before);
        }

        void OnSaveSong(ISaveSong save)
        {
            // Only machines still in the song; none, no subsection.
            var names = new HashSet<string>(save.Song.Machines.Select(m => m.Name), StringComparer.Ordinal);
            var live = pins.Keys.Concat(locks.Keys).Distinct(StringComparer.Ordinal)
                .Where(n => names.Contains(n) && (Get(n).Count > 0 || GetLocks(n).Count > 0)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            if (live.Count == 0) return;
            var stream = save.CreateSubSection(SubSectionName);
            if (stream == null) return;
            var root = new XElement("DevicePins", new XAttribute("version", FormatVersion),
                live.Select(n => new XElement("Machine", new XAttribute("name", n),
                    Get(n).Select(p => new XElement("Pin", new XAttribute("param", p))), GetLocks(n).Select(p => new XElement("Lock", new XAttribute("param", p))))));
            var bytes = new UTF8Encoding(false).GetBytes(root.ToString(SaveOptions.DisableFormatting));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        void OnOpenSong(IOpenSong open)
        {
            pins.Clear();
            locks.Clear();
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
                            var held = m.Elements("Lock").Select(p => (string)p.Attribute("param")).Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.Ordinal).ToList();
                            if (!string.IsNullOrEmpty(name) && held.Count > 0) locks[name] = held;
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
