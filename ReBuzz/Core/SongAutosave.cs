using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ReBuzz.Core
{
    /// <summary>
    /// Fork addition (docs/specs/session-view.md 0.4.1): autosave of unsaved changes, so a crash
    /// during a session loses at most a couple of minutes. ReBuzz's own backups copy the last
    /// saved file; this saves the song as it is in memory.
    ///
    /// The main window calls <see cref="Tick"/> every minute on the UI thread. It saves a copy
    /// when the song has unsaved changes, the transport is stopped (saving takes UI time and
    /// talks to native machines, so not during a performance) and the last autosave is at least
    /// <see cref="Interval"/> old. The copy goes to <see cref="Folder"/> with a text file naming
    /// the song it belongs to; the song's own file, name and modified flag are untouched.
    /// Saving, opening another song, a new song and a normal exit delete the copy
    /// (<see cref="Discard"/>). One left over at start means ReBuzz didn't close normally:
    /// the main window offers to recover it.
    /// </summary>
    internal sealed class SongAutosave
    {
        public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

        /// <summary>Where autosaves go (tests point it elsewhere).</summary>
        internal static string Folder { get; set; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ReBuzz", "Autosave");

        const string Marker = ".autosave";

        readonly ReBuzzCore buzz;
        DateTime lastSave = DateTime.MinValue;
        string current;

        internal SongAutosave(ReBuzzCore buzz)
        {
            this.buzz = buzz;
        }

        /// <summary>The autosave written for the current song, if any.</summary>
        public string Current => current;

        /// <summary>Autosave path for a song (null: an untitled one).</summary>
        public static string PathFor(string songName)
        {
            string title = string.IsNullOrEmpty(songName) ? "Untitled" : Path.GetFileNameWithoutExtension(songName);
            string ext = !string.IsNullOrEmpty(songName) && Path.GetExtension(songName).Equals(".bmxml", StringComparison.OrdinalIgnoreCase) ? ".bmxml" : ".bmx";
            string id = string.IsNullOrEmpty(songName) ? "0" : StableHash(Path.GetFullPath(songName).ToLowerInvariant()).ToString("x8");
            foreach (char c in Path.GetInvalidFileNameChars())
                title = title.Replace(c, '_');
            return Path.Combine(Folder, $"{title}-{id}{Marker}{ext}");
        }

        static uint StableHash(string s)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(s))
                h = (h ^ b) * 16777619;
            return h;
        }

        /// <summary>UI thread, every minute or so: autosaves if it is time to. Returns whether it saved.</summary>
        public bool Tick(DateTime now)
        {
            if (!buzz.Modified || buzz.PlayRequested) return false;
            if (now - lastSave < Interval) return false;
            return SaveNow(now);
        }

        /// <summary>Writes the autosave now (UI thread). Returns false if it failed (logged).</summary>
        public bool SaveNow(DateTime now)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string songName = buzz.SongCore.SongName;
                string path = PathFor(songName);
                // Same extension (it picks the file format), and not matched by FindLeftovers.
                string temp = Path.Combine(Folder, Path.GetFileNameWithoutExtension(path) + ".partial" + Path.GetExtension(path));
                buzz.SaveSongCopy(temp);
                File.Move(temp, path, overwrite: true);         // never leave a half-written autosave
                File.WriteAllText(path + ".txt", songName ?? "");
                if (current != null && current != path)
                    Delete(current);
                current = path;
                lastSave = now;
                return true;
            }
            catch (Exception e)
            {
                buzz.DCWriteLine("Autosave failed: " + e.Message, DCLogLevel.Warning);
                return false;
            }
        }

        /// <summary>The song was saved or closed on purpose: its autosave is no longer needed.</summary>
        public void Discard()
        {
            if (current != null)
                Delete(current);
            current = null;
            lastSave = DateTime.MinValue;
        }

        /// <summary>An autosave left over from a session that didn't close normally.</summary>
        internal sealed record Leftover(string Path, string Song, DateTime Saved);

        /// <summary>Autosaves in <see cref="Folder"/>, newest first.</summary>
        public static IReadOnlyList<Leftover> FindLeftovers()
        {
            try
            {
                if (!Directory.Exists(Folder)) return Array.Empty<Leftover>();
                return Directory.GetFiles(Folder, "*" + Marker + ".*")
                    .Where(p => (p.EndsWith(".bmx", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".bmxml", StringComparison.OrdinalIgnoreCase)) &&
                                !p.Contains(".partial.", StringComparison.OrdinalIgnoreCase))
                    .Select(p => new Leftover(p, File.Exists(p + ".txt") ? File.ReadAllText(p + ".txt").Trim() : "", File.GetLastWriteTime(p)))
                    .OrderByDescending(l => l.Saved)
                    .ToList();
            }
            catch (Exception)
            {
                return Array.Empty<Leftover>();
            }
        }

        /// <summary>
        /// Opens a leftover autosave as the song it belongs to, with unsaved changes, then
        /// deletes it (the open song is autosaved again from here). If it can't be opened, it is
        /// kept (0.6.1), so it can be tried again or opened by hand. Returns whether it opened.
        /// </summary>
        public bool Recover(Leftover leftover)
        {
            // OpenSongFile discards the current song's autosave, which may be this very file:
            // open a copy.
            string copy = Path.Combine(Path.GetTempPath(), "ReBuzz-recovered-" + Guid.NewGuid().ToString("N") + Path.GetExtension(leftover.Path));
            File.Copy(leftover.Path, copy, overwrite: true);
            bool opened;
            try
            {
                opened = buzz.TryOpenSongFile(copy);
                if (opened)
                {
                    buzz.SongCore.SongName = string.IsNullOrEmpty(leftover.Song) ? null : leftover.Song;
                    // Loading clears the modified flag from the dispatcher once it is done: set it after that.
                    buzz.dispatcher.BeginInvoke(() => buzz.Modified = true);
                }
            }
            finally
            {
                TryDeleteFile(copy);
            }
            if (opened)
                Delete(leftover.Path);
            else
                buzz.DCWriteLine("Autosave not recovered (it couldn't be opened); kept at " + leftover.Path, DCLogLevel.Warning);
            return opened;
        }

        public static void Delete(string path)
        {
            TryDeleteFile(path);
            TryDeleteFile(path + ".txt");
        }

        static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception)
            {
            }
        }
    }
}
