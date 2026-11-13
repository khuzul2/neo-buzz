// Fork (docs/specs/ux-overhaul.md P3h, decisions 85-87): a machine's identity colour, the one of its category in the machine
// browser (§3.1.1). The machine's box wears it, and its track and its clips take it as their default colour when the song gives
// them none (§3.4: worked out when drawn, never written into the song until the user picks a colour).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Agent.Guides;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views
{
    internal static class MachineIdentity
    {
        static readonly Dictionary<string, MachineCategory?> catalogued = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The category of a machine, or null for the Master (it has none: its box stays the neutral grey).</summary>
        public static MachineCategory? CategoryOf(IMachine machine)
        {
            var dll = machine?.DLL;
            if (dll?.Info == null || dll.Info.Type == MachineType.Master) return null;
            string name = dll.Name ?? "";
            MachineCategory? known;
            lock (catalogued)
                if (!catalogued.TryGetValue(name, out known))
                {
                    // The catalogue's category (the browser's), by its place in the ten.
                    int index = MachineCatalog.Entry(name) is { } entry ? MachineCatalog.Categories.ToList().IndexOf(entry.Category) : -1;
                    catalogued[name] = known = index >= 0 ? (MachineCategory)index : null;
                }
            if (known != null) return known;
            // A machine the catalogue lacks (Buzz's own): a control machine, a synth or a utility.
            return machine.IsControlMachine ? MachineCategory.Control : Tokens.CategoryOf(dll.Info.Type == MachineType.Generator, null);
        }

        /// <summary>The identity colour of a machine (its category's), or null for the Master.</summary>
        public static Color? ColourOf(IMachine machine) => CategoryOf(machine) is { } c ? StudioTokens.Identity(c) : null;

        /// <summary>A box's body: the identity colour at 25 % over the panel surface, 10 % when muted (the name stays 4.5:1 or more on it).</summary>
        public static Color BodyOf(Color identity, bool muted) => Mix(StudioTokens.Of(Tokens.SurfacePanel), identity, muted ? 0.10 : 0.25);

        /// <summary>A box's band along its top edge: the identity colour whole, dimmed to 45 % when muted.</summary>
        public static Color BandOf(Color identity, bool muted) => muted ? Mix(StudioTokens.Of(Tokens.SurfacePanel), identity, 0.45) : identity;

        public static Color Mix(Color a, Color b, double t)
        {
            byte M(byte x, byte y) => (byte)Math.Round(x + (y - x) * t);
            return Color.FromRgb(M(a.R, b.R), M(a.G, b.G), M(a.B, b.B));
        }
    }
}
