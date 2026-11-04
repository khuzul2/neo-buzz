// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the per-machine setting for the manifest editor.
// When it is on, opening a machine that has no GUI of its own (a double click) shows its manifest
// editor instead of the plain parameter list; the list stays on the machine's menu (Parameters...).
// On by default for gear with a curated manifest, off for drafts; kept per gear in the settings
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

        /// <summary>On for a curated manifest, off for a draft or a generated one (the Neo machines have their own editors).</summary>
        public static bool Default(IMachineDLL dll) => SongModel.Manifest(dll)?.IsCurated == true;

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
