// Fork (docs/specs/daw-modes.md §8.9, NM1): what the core adds to BuzzGUI.Interfaces.NoteMaps: the manifest's declared map (note_map in a
// .machine.json, or a profiled drum map) and the UI thread the change event is raised on. Installed when the engine's core is made.

using System;
using System.Collections.Generic;
using System.Linq;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;

namespace ReBuzz.Core.Notes
{
    internal static class NoteMapSources
    {
        /// <summary>Hooks the manifest reader and the dispatcher into <see cref="NoteMaps"/> (once per core; the last core made wins, as the one UI thread does).</summary>
        public static void Install(IUiDispatcher dispatcher)
        {
            NoteMaps.Manifest = ManifestEntries;
            NoteMaps.Marshal = action =>
            {
                if (dispatcher == null || dispatcher.CheckAccess()) action();
                else dispatcher.BeginInvoke(action);
            };
        }

        /// <summary>The keys a machine's manifest declares, in order (a key listed twice keeps its last name), or null when it declares none.</summary>
        public static IReadOnlyList<NoteMapEntry> ManifestEntries(IMachine machine)
        {
            var manifest = SongModel.Manifest(machine);
            if (manifest == null || manifest.NoteMap.Count == 0) return null;
            return manifest.NoteMap
                .Select(k => new NoteMapEntry(k.Key, k.Name.Length > 0 ? k.Name : null, k.Group.Length > 0 ? k.Group : null, k.Color))
                .ToList();
        }
    }
}
