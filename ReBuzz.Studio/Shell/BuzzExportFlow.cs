// Fork (docs/specs/native-format.md NF2): File ▸ Export for Buzz/ReBuzz… A separate, lossy save of a .bmx that upstream ReBuzz
// reads: pick a path, see what upstream can't hold (the loss report), Export or Cancel, written atomically. The open song is
// untouched: it keeps its name, its modified flag, its undo and its history.

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ReBuzz.Core;
using ReBuzz.FileOps;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    internal static class BuzzExportFlow
    {
        static readonly FilePickerFileType BuzzSong = new FilePickerFileType("Song for Buzz/ReBuzz (.bmx)") { Patterns = new[] { "*.bmx" } };

        /// <summary>Tests choose the file here instead of a picker: the suggested name → a path, or null to cancel.</summary>
        public static Func<string, string> Picker { get; set; }

        /// <summary>Tests answer the report here instead of the dialog: the report → export (true) or cancel.</summary>
        public static Func<ExportReport, bool> Confirmer { get; set; }

        /// <summary>The window's title and the menu's words.</summary>
        public const string Title = "Export for Buzz/ReBuzz";

        /// <summary>The name proposed: the song's own, with .bmx.</summary>
        public static string Suggested(string songName) =>
            string.IsNullOrEmpty(songName) ? "Untitled.bmx" : Path.ChangeExtension(Path.GetFileName(songName), ".bmx");

        /// <summary>Picks a path, shows the loss report, writes. The path written, or null (cancelled, refused or failed).</summary>
        public static async Task<string> Run()
        {
            var core = StudioHost.Current?.Core;
            if (core == null) return null;
            string path = await Pick(Suggested(core.SongCore.SongName));
            if (path == null) return null;
            if (Path.GetExtension(path).Length == 0) path += ".bmx";
            string problem = BuzzExport.TargetProblem(core.SongCore.SongName, path);
            if (problem != null)
            {
                await Dialogs.Info(problem, Title);
                return null;
            }
            var report = core.PreviewBuzzExport();
            if (!report.IsEmpty && !await Confirm(report)) return null;
            try
            {
                core.ExportForBuzz(path);
            }
            catch (Exception e)
            {
                await Dialogs.Error("The song could not be exported to " + path + ".", Title, e.ToString());
                return null;
            }
            return path;
        }

        static async Task<string> Pick(string suggested)
        {
            if (Picker != null) return Picker(suggested);
            var storage = Dialogs.Owner?.StorageProvider;
            if (storage == null) return null;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Title,
                SuggestedFileName = suggested,
                FileTypeChoices = new[] { BuzzSong },
                DefaultExtension = "bmx",
            });
            return file?.TryGetLocalPath();
        }

        static async Task<bool> Confirm(ExportReport report)
        {
            if (Confirmer != null) return Confirmer(report);
            bool export = false;
            var window = Build(report, () => export = true);
            await StudioDialog.Run(window);
            return export;
        }

        /// <summary>The report's window: what upstream can't hold, a line each, and Export (Return) and Cancel (Escape).</summary>
        internal static Window Build(ExportReport report, Action exported)
        {
            var window = StudioDialog.Create(Title);
            var intro = new TextBlock
            {
                Text = "Upstream ReBuzz can't hold everything in this song. The file is written for it anyway; this is what changes there:",
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Tag = "intro",
            };
            var list = new StackPanel { Spacing = 8 };
            foreach (var group in report.Losses.GroupBy(l => l.Category))
            {
                list.Children.Add(new TextBlock { Text = group.Key, FontWeight = Avalonia.Media.FontWeight.SemiBold, Tag = "category" });
                foreach (var loss in group)
                    list.Children.Add(new TextBlock { Text = loss.Item + ": " + loss.Upstream, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0), Tag = "loss" });
            }
            var scroll = new ScrollViewer { Content = list, MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Tag = "losses" };
            var export = StudioDialog.Primary("Export");
            var cancel = StudioDialog.Secondary();
            export.Click += (s, e) => { exported(); window.Close(); };
            cancel.Click += (s, e) => window.Close();
            var body = StudioDialog.Body(intro, scroll, StudioDialog.Buttons(export, cancel));
            body.Width = 520 + 2 * StudioDialog.Margin;
            window.Content = body;
            return window;
        }
    }
}
