#nullable disable
// Neo Plug's versioned state (docs/specs/machines.md §3.4). Its 64 slots are positional by design (what a slot drives is saved in
// PlugState.SlotIds by the plug-in's own parameter id), so its layout is the same at every version: version 0, no steps.
// Keys "slot1" … "slot64" are the slots' identity for the native song format; the plug-in's own parameter ids come with PlugState.
// "output" is the global after them (which output bus feeds the machine; index 64, appended by H1b): appending needs no version (§3.2),
// so a song saved without it (64 globals, or no "output" key in a .nbl) loads with Output at its default, 0.

using System;
using System.Collections.Generic;
using System.Linq;
using Buzz.MachineInterface;

namespace NeoBuzz.Plug
{
    public partial class NeoPlugMachine : IVersionedMachine
    {
        const string OutputKey = "output";
        const int OutputDefault = 0;

        static readonly string[] slotKeys = Enumerable.Range(1, PlugCore.Slots).Select(i => "slot" + i).Append(OutputKey).ToArray();

        public int StateVersion => 0;

        public IReadOnlyList<string> ParameterKeys => slotKeys;

        public int[] MigrateGlobals(int fromVersion, int[] saved)
        {
            if (fromVersion != 0) return null;
            var r = new int[slotKeys.Length];
            for (int i = 0; i < r.Length; i++)
                r[i] = saved != null && i < saved.Length && saved[i] != int.MinValue ? saved[i] : i == PlugCore.Slots ? OutputDefault : 0;
            return r;
        }

        public IReadOnlyDictionary<string, int> MigrateKeyed(int fromVersion, IReadOnlyDictionary<string, int> saved) =>
            fromVersion == 0 ? slotKeys.ToDictionary(k => k, k => saved != null && saved.TryGetValue(k, out int v) ? v : k == OutputKey ? OutputDefault : 0) : null;

        public byte[] MigrateData(int fromVersion, byte[] data) => fromVersion == 0 ? data : null;

        public int MapParameterIndex(int fromVersion, int savedIndex) => fromVersion == 0 && savedIndex >= 0 && savedIndex < slotKeys.Length ? savedIndex : -1;
    }
}
