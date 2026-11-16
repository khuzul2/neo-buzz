// Fork (docs/specs/stem-separation.md): Preferences ▸ Stems. Where the stem models are (Gear/Models/Stems, or a folder chosen here), which
// of the three qualities are installed, Install models… (the zip the stem-models workflow makes), and the cache of results.
// The weights are for personal use: they are never part of the app or the repository.

using System;
using System.IO;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ReBuzz.Core.AudioLibrary.Stems;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class StemsPage : PrefsPage
    {
        readonly TextBox folder = new TextBox { Width = 420, Watermark = "Default: Gear/Models/Stems" };
        readonly Button choose = new Button { Content = "Choose…" };
        readonly Button install = new Button { Content = "Install models…" };
        readonly Button clear = new Button { Content = "Clear the cache" };
        readonly TextBlock found = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 };
        readonly TextBlock status = new TextBlock { Foreground = Dim, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        public StemsPage(PrefsModel model) : base(model)
        {
            Heading("Stem models");
            Note("Separate stems… splits a clip or a file into vocals, drums, bass and other with three local models: Fast (Demucs), Balanced (Demucs fine-tuned) and " +
                 "Best (BS-RoFormer). The models are not part of the app: the NeoBuzz Live Stem Models workflow exports them to ONNX, and Install models… takes its zip. " +
                 "Their weights have no clear licence: use them for yourself only, and do not share them.");
            Row("Models folder", folder, "Leave empty for Gear/Models/Stems of this installation (or, where that can't be written, the app's data folder).", choose);
            Row("", install);
            Heading("Installed");
            Body.Children.Add(found);
            Heading("Results");
            Note("A separation is kept per audio and model (up to 2 GB; the oldest go first), so asking again is instant.");
            Row("", clear);
            Body.Children.Add(status);
            AutomationProperties.SetName(folder, "Stem models folder");
            AutomationProperties.SetName(choose, "Choose the stem models folder");
            AutomationProperties.SetName(install, "Install stem models");
            AutomationProperties.SetName(clear, "Clear the stem cache");

            folder.LostFocus += (_, _) => Commit();
            folder.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Commit(); };
            choose.Click += async (_, _) =>
            {
                try
                {
                    var top = TopLevel.GetTopLevel(this);
                    if (top == null) return;
                    var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder with the stem models", AllowMultiple = false });
                    string path = picked.Count > 0 ? picked[0].Path?.LocalPath : null;
                    if (path != null) { folder.Text = path; Commit(); }
                }
                catch (Exception) { /* a picker that didn't open is not worth a failed click */ }
            };
            install.Click += async (_, _) =>
            {
                status.Text = await StemFlow.InstallModels();
                Refresh();
            };
            clear.Click += (_, _) =>
            {
                StemCache.Clear();
                status.Text = "The cache is empty.";
            };
            Refresh();
        }

        public override string Title => "Stems";

        internal TextBox FolderBox => folder;
        internal TextBlock FoundText => found;
        internal Button InstallButton => install;

        void Commit()
        {
            StemModels.UserFolder = folder.Text ?? "";
            Refresh();
        }

        public override void Refresh()
        {
            Filling = true;
            try
            {
                folder.Text = StemModels.UserFolder;
                var lines = Enum.GetValues(typeof(StemQuality)).Cast<StemQuality>().Select(q =>
                {
                    var m = StemModels.For(q);
                    return m != null ? $"{StemModels.Name(q)}: {m.Title} ({m.Id})" : $"{StemModels.Name(q)}: not installed";
                });
                found.Text = string.Join(Environment.NewLine, lines);
            }
            finally
            {
                Filling = false;
            }
        }
    }
}
