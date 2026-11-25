// Fork (docs/specs/machines.md §3.4): a song saved by an older layout of one of our machines loads its global parameter values through
// the machine's migrations, on the loader thread, instead of by position. Used by the core sections loader.

using System;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.FileOps
{
    internal static class LoadedStateMigration
    {
        /// <summary>
        /// Sets <paramref name="machine"/>'s global state parameters from the song's values when the song saved the machine at another
        /// state version than the build has. False when nothing needs migrating (the caller copies by position as always).
        /// </summary>
        /// <param name="savedCount">How many global values the song saved.</param>
        /// <param name="saved">The saved value at an index, or null when the song has none.</param>
        internal static bool CopyGlobals(MachineCore machine, int savedCount, Func<int, int?> saved, Action<string> log)
        {
            int from = machine.LoadedStateVersion;
            if (from < 0 || machine.ManagedMachine is not IVersionedMachine versioned || versioned.StateVersion == from)
                return false;

            int[] current = null;
            if (from < versioned.StateVersion)
            {
                var values = new int[savedCount];
                for (int i = 0; i < savedCount; i++)
                    values[i] = saved(i) ?? int.MinValue;
                current = versioned.MigrateGlobals(from, values);
            }
            if (current == null)
            {
                log($"{machine.Name}: its parameters were saved at state version {from}, this build has {versioned.StateVersion}; " +
                    (from > versioned.StateVersion ? "the song comes from a newer build, so they keep their defaults." : "the migration failed, so they keep their defaults."));
                return true;
            }

            var parameters = machine.ParameterGroupsList[1].ParametersList;
            for (int i = 0; i < parameters.Count && i < current.Length; i++)
            {
                var p = parameters[i];
                if (p.Flags.HasFlag(ParameterFlags.State) && p.Type != ParameterType.Note)
                    p.SetValue(0, current[i]);
            }
            return true;
        }
    }
}
