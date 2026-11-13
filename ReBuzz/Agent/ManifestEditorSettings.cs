// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the per-machine setting for the manifest editor.
// When it is on, opening a machine that has no GUI of its own (a double click) shows its manifest
// editor instead of the plain parameter list; the list stays on the machine's menu (Parameters...).
// On by default for gear with a curated manifest and no GUI of its own, off for drafts and for machines with a GUI; kept per gear in the settings
// (RegistryEx: the Windows registry, or the studio's and rebuzz-headless's JSON settings), never in
// songs. Engine side, so MachineCore's double click (ReBuzz.exe) and the studio read the same.

using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace ReBuzz.Agent
{
    internal static class ManifestEditorSettings
    {
        /// <summary>The settings key (under the app's registry root) holding one value per gear: 1 on, 0 off.</summary>
        public const string Key = "ManifestEditor";

        /// <summary>Whether the gear has a manifest to make an editor from (not the Master).</summary>
        public static bool Available(IMachineDLL dll) =>
            dll?.Info != null && dll.Info.Type != MachineType.Master && SongModel.Manifest(dll) != null;

        /// <summary>Whether the machine brings a GUI of its own (a GUI factory: Pedal LFO's target picker, Pedal Chord's and
        /// Pedal Tracker's editors, the Neo editors). A native machine's own double click dialog is handled before this.</summary>
        public static bool HasOwnGui(IMachineDLL dll)
        {
            try
            {
                return dll?.GUIFactory != null;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>The default as a rule: the curated manifest editor wins a double click only for a machine with no GUI of
        /// its own; a machine with one keeps opening it (the editor stays on its menu).</summary>
        public static bool DefaultFor(bool curated, bool hasOwnGui) => curated && !hasOwnGui;

        /// <summary>On for a curated manifest of a machine with no GUI of its own, off for a draft, a generated one or a
        /// machine whose GUI holds its own editors (the Neo machines, Pedal LFO, Chord, Peer Control, Tracker...).</summary>
        public static bool Default(IMachineDLL dll) => DefaultFor(SongModel.Manifest(dll)?.IsCurated == true, HasOwnGui(dll));

        /// <summary>Whether opening the machine (a double click) shows its manifest editor rather than its parameter list.</summary>
        public static bool OpensEditor(IMachineDLL dll)
        {
            if (!Available(dll)) return false;
            return RegistryEx.Read(dll.Name, Default(dll) ? 1 : 0, Key) != 0;
        }

        public static void Set(IMachineDLL dll, bool on)
        {
            if (dll?.Name is { Length: > 0 } name) RegistryEx.Write(name, on ? 1 : 0, Key);
        }
    }
}
