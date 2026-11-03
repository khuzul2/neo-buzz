// Fork (docs/specs/studio-mvp.md S7): autosave, as ReBuzz.exe's main window runs it
// (MainWindow.StartAutosave): at start-up, an autosave left by a session that didn't close
// normally is offered back; then unsaved changes are autosaved every couple of minutes
// (SongAutosave, the engine's: it writes beside the settings, in Autosave/).

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    internal static class Autosave
    {
        static DispatcherTimer timer;

        /// <summary>
        /// Offers the newest leftover autosave: Yes opens it (unsaved, under its song's name), No
        /// deletes them, Cancel keeps them for next time. True when one was opened.
        /// </summary>
        public static async Task<bool> OfferRecovery(ReBuzzCore core)
        {
            try
            {
                var leftovers = SongAutosave.FindLeftovers();
                if (leftovers.Count == 0) return false;
                var newest = leftovers[0];
                string song = string.IsNullOrEmpty(newest.Song) ? "an untitled song" : "'" + Path.GetFileName(newest.Song) + "'";
                var answer = await Dialogs.Ask(
                    $"ReBuzz didn't close normally last time. There are unsaved changes to {song}, autosaved at {newest.Saved:g}.\n\n" +
                    "Yes: open them (then save to keep them)\nNo: delete them\nCancel: keep them for next time",
                    "Recover unsaved changes", UserButtons.YesNoCancel);
                if (answer == UserAnswer.Yes)
                {
                    bool opened = core.Autosave.Recover(newest);
                    foreach (var older in leftovers.Skip(1))
                        SongAutosave.Delete(older.Path);
                    return opened;
                }
                if (answer == UserAnswer.No)
                    foreach (var leftover in leftovers)
                        SongAutosave.Delete(leftover.Path);
            }
            catch (Exception e)
            {
                core.DCWriteLine("Autosave recovery: " + e.Message, BuzzGUI.Interfaces.DCLogLevel.Warning);
            }
            return false;
        }

        /// <summary>Autosaves unsaved changes (when it is time to) once a minute from now on.</summary>
        public static void Start(ReBuzzCore core)
        {
            if (timer != null) return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            timer.Tick += (s, e) => core.Autosave?.Tick(DateTime.Now);
            timer.Start();
        }
    }
}
