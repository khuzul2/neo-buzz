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
        /// Offers the newest leftover autosave: Restore opens it (unsaved, under its song's name),
        /// Discard… deletes them once confirmed, Decide Later keeps them for next time (Return restores,
        /// Escape decides later: UX review). True when one was opened.
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
                    $"{Product.Name} didn't close normally last time. There are unsaved changes to {song}, autosaved at {newest.Saved:g}.\n\n" +
                    "Restore opens them (then save to keep them); Decide Later keeps them for next time.",
                    "Recover unsaved changes", UserButtons.YesNoCancel, DialogChoices.Recover);
                if (answer == UserAnswer.Yes)
                {
                    bool opened = core.Autosave.Recover(newest);
                    foreach (var older in leftovers.Skip(1))
                        SongAutosave.Delete(older.Path);
                    return opened;
                }
                if (answer == UserAnswer.No &&
                    await Dialogs.Ask($"Discard the unsaved changes to {song}? They can't be brought back.", "Discard unsaved changes", UserButtons.YesNo,
                        new DialogChoices { Labels = new() { [UserAnswer.Yes] = "Discard", [UserAnswer.No] = "Keep" }, Default = UserAnswer.Yes, Safe = UserAnswer.No }) == UserAnswer.Yes)
                    foreach (var leftover in leftovers)
                        SongAutosave.Delete(leftover.Path);
            }
            catch (Exception e)
            {
                core.DCWriteLine("Autosave recovery: " + e.Message, BuzzGUI.Interfaces.DCLogLevel.Warning);
            }
            return false;
        }

        /// <summary>
        /// Offers the takes a crash left (audio that was recording to its temp file): Recover puts them as clips on a new "Recovered" track,
        /// Discard… deletes them once confirmed, Decide Later keeps them for next time. True when they were recovered.
        /// </summary>
        public static async Task<bool> OfferTakeRecovery(ReBuzzCore core)
        {
            try
            {
                var takes = core.Arrangement?.Takes.LeftoverTakes();
                if (takes == null || takes.Count == 0) return false;
                string seconds = string.Join(", ", takes.Take(4).Select(t => $"{t.Name} ({t.Frames / (double)Math.Max(1, t.SampleRate):0.#} s)")) + (takes.Count > 4 ? ", ..." : "");
                var answer = await Dialogs.Ask(
                    $"{Product.Name} didn't close normally while audio was recording. {takes.Count} take{(takes.Count == 1 ? "" : "s")} can be recovered: {seconds}.\n\n" +
                    "Recover puts them on a new track in the song; Decide Later keeps them for next time.",
                    "Recover takes", UserButtons.YesNoCancel, DialogChoices.Recover);
                if (answer == UserAnswer.Yes) return core.Arrangement.Takes.Recover(takes) > 0;
                if (answer == UserAnswer.No &&
                    await Dialogs.Ask($"Discard {takes.Count} recorded take{(takes.Count == 1 ? "" : "s")}? They can't be brought back.", "Discard takes", UserButtons.YesNo,
                        new DialogChoices { Labels = new() { [UserAnswer.Yes] = "Discard", [UserAnswer.No] = "Keep" }, Default = UserAnswer.Yes, Safe = UserAnswer.No }) == UserAnswer.Yes)
                    foreach (var take in takes)
                        ReBuzz.Core.Clips.TakeFile.Discard(take);
            }
            catch (Exception e)
            {
                core.DCWriteLine("Take recovery: " + e.Message, BuzzGUI.Interfaces.DCLogLevel.Warning);
            }
            return false;
        }

        /// <summary>Autosaves unsaved changes (when it is time to) once a minute from now on.</summary>
        public static void Start(ReBuzzCore core)
        {
            if (timer != null) return;
            timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            // Not while Export Audio renders: the render has the song's loop, mutes and position (ux-overhaul.md P4a).
            timer.Tick += (s, e) => { if (ExportSheet.Rendering) return; core.Autosave?.Tick(DateTime.Now); core.Snapshots?.Tick(DateTime.UtcNow); };
            timer.Start();
        }
    }
}
