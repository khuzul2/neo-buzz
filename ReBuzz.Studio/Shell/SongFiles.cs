// Fork (docs/specs/studio-mvp.md S0): new, open, save and save as, with the studio's own
// questions (async) before the engine acts, so the engine never has to ask.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    internal static class SongFiles
    {
        static readonly FilePickerFileType Songs = new FilePickerFileType("Songs (.nbl, .bmx, .bmxml, .bmw)") { Patterns = new[] { "*.nbl", "*.bmx", "*.bmxml", "*.bmw" } };
        static readonly FilePickerFileType Nbl = new FilePickerFileType("NeoBuzz Live song (.nbl)") { Patterns = new[] { "*.nbl" } };
        static readonly FilePickerFileType Bmx = new FilePickerFileType("Song with waves (.bmx)") { Patterns = new[] { "*.bmx" } };
        static readonly FilePickerFileType Bmxml = new FilePickerFileType("ReBuzz XML (.bmxml)") { Patterns = new[] { "*.bmxml" } };

        /// <summary>Tests choose here instead of a picker: (saving?) → a path, or null to cancel.</summary>
        public static System.Func<bool, string> Picker { get; set; }

        static ReBuzzCore Core => StudioHost.Current?.Core;

        static IStorageProvider Storage => Dialogs.Owner?.StorageProvider;

        public static async Task<string> PickOpen()
        {
            if (Picker != null) return Picker(false);
            if (Storage == null) return null;
            var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open song", FileTypeFilter = new[] { Songs }, AllowMultiple = false });
            return files.FirstOrDefault()?.TryGetLocalPath();
        }

        public static async Task<string> PickSave(string suggested)
        {
            if (Picker != null) return Picker(true);
            if (Storage == null) return null;
            var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save song",
                SuggestedFileName = suggested ?? "Untitled.nbl",
                FileTypeChoices = new[] { Nbl, Bmxml, Bmx },
                DefaultExtension = "nbl",
            });
            return file?.TryGetLocalPath();
        }

        /// <summary>
        /// Unsaved changes: save them, drop them or stay. True when the caller may go on (and the
        /// engine then won't ask again).
        /// </summary>
        public static async Task<bool> ConfirmDiscard()
        {
            var core = Core;
            if (core == null || !core.Modified) return true;
            string name = core.SongCore.SongName == null ? "Untitled" : System.IO.Path.GetFileName(core.SongCore.SongName);
            var answer = await Dialogs.Ask($"Save changes to {name}?", Product.Name, UserButtons.YesNoCancel, DialogChoices.SaveChanges);
            if (answer == UserAnswer.Cancel) return false;
            if (answer == UserAnswer.Yes) return await Save();
            core.Modified = false;
            return true;
        }

        public static async Task New()
        {
            if (Core == null || !await ConfirmDiscard()) return;
            Core.ExecuteCommand(BuzzGUI.Interfaces.BuzzCommand.NewFile);
        }

        public static async Task<bool> Open(string path = null)
        {
            if (Core == null || !await ConfirmDiscard()) return false;
            path ??= await PickOpen();
            if (path == null) return false;
            return Core.TryOpenSongFile(path);
        }

        public static async Task<bool> Save()
        {
            var core = Core;
            if (core == null) return false;
            // A song opened from a .bmx, .bmw or .bmxml is saved as a new .nbl: the original is never overwritten without asking (native-format.md §7).
            if (core.SongCore.SongName == null || (core.LegacyOrigin != null && core.LegacyOrigin == core.SongCore.SongName)) return await SaveAs();
            core.SaveSongFile(core.SongCore.SongName);
            return !core.Modified;
        }

        public static async Task<bool> SaveAs()
        {
            var core = Core;
            if (core == null) return false;
            string path = await PickSave(core.SongCore.SongName == null ? null
                : core.LegacyOrigin != null ? System.IO.Path.ChangeExtension(System.IO.Path.GetFileName(core.SongCore.SongName), ".nbl")
                : System.IO.Path.GetFileName(core.SongCore.SongName));
            if (path == null) return false;
            StudioHost.Current.SaveChoice.Next = path;
            core.SaveSongFile(null);                // the engine takes the name, as from its own dialog
            return !core.Modified;
        }
    }
}
