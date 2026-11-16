// Fork (docs/specs/neo-audio.md): machines that were renamed. A song (.bmx, .bmxml or .nbl) that names an old machine library loads
// as the machine that replaced it, when the old one isn't installed (its layout is the new machine's version 0).

using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace ReBuzz.MachineManagement
{
    internal static class MachineAliases
    {
        /// <summary>Old library name to the machine that replaced it.</summary>
        static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Session Clip Player"] = "Neo Audio",
        };

        /// <summary>
        /// The library a song's machine loads as: <paramref name="library"/> itself when it is installed, else its replacement when
        /// that is. <paramref name="aliased"/> says it was replaced (the song's machine type must not overwrite the installed one's).
        /// </summary>
        public static string Resolve(IDictionary<string, IMachineDLL> installed, string library, out bool aliased)
        {
            aliased = false;
            if (library == null || installed.ContainsKey(library)) return library;
            if (Names.TryGetValue(library, out var now) && installed.ContainsKey(now))
            {
                aliased = true;
                return now;
            }
            return library;
        }
    }
}
