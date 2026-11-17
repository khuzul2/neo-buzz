#nullable disable
// Fork (docs/specs/stem-separation.md): where the stem models are, which one serves a quality, and installing them from the zip the
// "NeoBuzz Live Stem Models" workflow makes. The weights are never part of the repository or the package (their licences are unclear:
// personal use); they live in Gear/Models/Stems, or in a folder chosen in Preferences, one subfolder per model with its model.json.
// Pure: ReBuzz.SessionTests links this file.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace ReBuzz.Core.AudioLibrary.Stems
{
    internal static class StemModels
    {
        /// <summary>Gear/Models/Stems of the installation (ReBuzzCore sets it from the app's folder).</summary>
        public static string GearFolder { get; set; } = Path.Combine(AppContext.BaseDirectory, "Gear", "Models", "Stems");

        /// <summary>Where the app keeps what it makes for the user (the models folder setting, the result cache).</summary>
        public static string DataFolder { get; set; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ReBuzz", "stems");

        static string SettingFile => Path.Combine(DataFolder, "models-folder.txt");

        /// <summary>The models folder chosen in Preferences ("" when none).</summary>
        public static string UserFolder
        {
            get
            {
                try { return File.Exists(SettingFile) ? File.ReadAllText(SettingFile).Trim() : ""; }
                catch (Exception) { return ""; }
            }
            set
            {
                try
                {
                    Directory.CreateDirectory(DataFolder);
                    if (string.IsNullOrWhiteSpace(value)) File.Delete(SettingFile);
                    else File.WriteAllText(SettingFile, value.Trim());
                }
                catch (Exception) { /* the setting is not kept */ }
            }
        }

        /// <summary>The folders searched, in order: the user's, the installation's, and the data folder the installer falls back to.</summary>
        public static IReadOnlyList<string> SearchFolders()
        {
            var list = new List<string>();
            string user = UserFolder;
            if (user.Length > 0) list.Add(user);
            list.Add(GearFolder);
            list.Add(FallbackFolder);
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static string FallbackFolder => Path.Combine(DataFolder, "models");

        /// <summary>Every model description found (a folder with a model.json, directly or one level down), complete or not.</summary>
        public static List<StemModelManifest> All(IEnumerable<string> folders = null)
        {
            var found = new List<StemModelManifest>();
            foreach (var folder in folders ?? SearchFolders())
            {
                if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
                var files = new List<string>();
                string own = Path.Combine(folder, StemModelManifest.FileName);
                if (File.Exists(own)) files.Add(own);
                try
                {
                    foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                    {
                        string f = Path.Combine(dir, StemModelManifest.FileName);
                        if (File.Exists(f)) files.Add(f);
                    }
                }
                catch (Exception) { /* a folder that can't be listed has none */ }
                foreach (var f in files)
                {
                    var m = StemModelManifest.Load(f, out _);
                    if (m != null && !found.Any(x => x.Id == m.Id && x.Complete)) found.Add(m);
                }
            }
            return found;
        }

        /// <summary>The model that serves a quality: the first complete one in the search folders. Null when there is none.</summary>
        public static StemModelManifest For(StemQuality quality, IEnumerable<string> folders = null) =>
            All(folders).FirstOrDefault(m => m.QualityLevel == quality && m.Complete);

        /// <summary>The quality's usual name, for menus and messages.</summary>
        public static string Name(StemQuality q) => q.ToString();

        /// <summary>What a quality does, for a tooltip.</summary>
        public static string Describe(StemQuality q) => q switch
        {
            StemQuality.Fast => "Fast: Demucs (htdemucs), four stems",
            StemQuality.Balanced => "Balanced: Demucs fine-tuned (htdemucs_ft), four stems, four times the work",
            _ => "Best: BS-RoFormer (six stems folded into four), the slowest",
        };

        /// <summary>Why a quality can't be used, for a greyed menu item or an error; null when it can.</summary>
        public static string Missing(StemQuality quality, IEnumerable<string> folders = null)
        {
            if (For(quality, folders) != null) return null;
            return $"The {Name(quality)} model isn't installed. Install models… takes the \"NeoBuzz Live Stem Models\" zip (made by the stem-models workflow), " +
                   $"or put its folders in {GearFolder} or in the folder set in Preferences.";
        }

        /// <summary>The folder Install puts models in: the Preferences folder, else Gear/Models/Stems when it can be written, else the data folder's.</summary>
        public static string InstallFolder()
        {
            string user = UserFolder;
            if (user.Length > 0) return user;
            try
            {
                Directory.CreateDirectory(GearFolder);
                string probe = Path.Combine(GearFolder, ".write-test-" + Environment.ProcessId);
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return GearFolder;
            }
            catch (Exception) { return FallbackFolder; }
        }

        /// <summary>
        /// Installs the models of a zip: every folder that holds a model.json (at any depth up to three) is extracted into
        /// <paramref name="destination"/> under its own name, replacing an older copy. Returns the ids installed; throws with a reason a
        /// person can read when the zip holds none.
        /// </summary>
        public static List<string> Install(string zipPath, string destination = null)
        {
            destination ??= InstallFolder();
            using var zip = ZipFile.OpenRead(zipPath);
            var roots = new List<string>();                              // entry folders that hold a model.json
            foreach (var e in zip.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (!name.EndsWith("/" + StemModelManifest.FileName, StringComparison.Ordinal) && name != StemModelManifest.FileName) continue;
                string dir = name.Length > StemModelManifest.FileName.Length ? name.Substring(0, name.Length - StemModelManifest.FileName.Length) : "";
                if (dir.Count(c => c == '/') <= 3) roots.Add(dir);
            }
            if (roots.Count == 0) throw new InvalidDataException("the zip holds no stem model (no model.json in it)");
            Directory.CreateDirectory(destination);
            string full = Path.GetFullPath(destination);
            var installed = new List<string>();
            foreach (var root in roots)
            {
                string folderName = root.TrimEnd('/').Split('/').LastOrDefault();
                if (string.IsNullOrEmpty(folderName))
                {
                    var manifest = zip.GetEntry(StemModelManifest.FileName);
                    using var s = manifest.Open();
                    using var r = new StreamReader(s);
                    folderName = System.Text.Json.JsonDocument.Parse(r.ReadToEnd()).RootElement.GetProperty("id").GetString();
                }
                if (string.IsNullOrWhiteSpace(folderName) || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folderName == "." || folderName == "..")
                    throw new InvalidDataException("a model's folder has no usable name");
                string target = Path.GetFullPath(Path.Combine(full, folderName));
                if (!target.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidDataException("a model's folder leaves the destination");
                string temp = target + ".installing";
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);
                foreach (var e in zip.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    if (!name.StartsWith(root, StringComparison.Ordinal) || name.EndsWith("/")) continue;
                    string rest = name.Substring(root.Length);
                    if (rest.Contains('/') || rest.Length == 0) continue;        // the model's own files only
                    string file = Path.GetFullPath(Path.Combine(temp, rest));
                    if (!file.StartsWith(Path.GetFullPath(temp) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new InvalidDataException("a file leaves the destination");
                    e.ExtractToFile(file, true);
                }
                var loaded = StemModelManifest.Load(Path.Combine(temp, StemModelManifest.FileName), out string problem);
                if (loaded == null)
                {
                    Directory.Delete(temp, true);
                    throw new InvalidDataException($"{folderName}: {problem}");
                }
                if (!loaded.Complete)
                {
                    Directory.Delete(temp, true);
                    throw new InvalidDataException($"{folderName}: some of its ONNX files are not in the zip");
                }
                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.Move(temp, target);
                installed.Add(loaded.Id);
            }
            return installed;
        }
    }
}
