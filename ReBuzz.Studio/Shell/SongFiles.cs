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
        static readonly FilePickerFileType Songs = new FilePickerFileType("ReBuzz songs") { Patterns = new[] { "*.bmx", "*.bmxml", "*.bmw" } };
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
                SuggestedFileName = suggested ?? "Untitled.bmxml",
                FileTypeChoices = new[] { Bmxml, Bmx },
                DefaultExtension = "bmxml",
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
            var answer = await Dialogs.Ask($"Save changes to {name}?", "ReBuzz", UserButtons.YesNoCancel);
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
            if (core.SongCore.SongName == null) return await SaveAs();
            core.SaveSongFile(core.SongCore.SongName);
            return !core.Modified;
        }

        public static async Task<bool> SaveAs()
        {
            var core = Core;
            if (core == null) return false;
            string path = await PickSave(core.SongCore.SongName == null ? null : System.IO.Path.GetFileName(core.SongCore.SongName));
            if (path == null) return false;
            StudioHost.Current.SaveChoice.Next = path;
            core.SaveSongFile(null);                // the engine takes the name, as from its own dialog
            return !core.Modified;
        }
    }
}
