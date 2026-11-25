// Fork (docs/specs/stem-separation.md): "Separate stems…" in the studio: the dialog (quality, which stems, Install models…), the job on a
// worker with its progress and Cancel, and the result as new Neo Audio tracks (one undo step). Reached from a Session clip's menu, the
// arrangement's, a sample in the browser, File ▸ Separate Stems from File… and Preferences ▸ Stems.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.AudioLibrary.Stems;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    using Control = Avalonia.Controls.Control;

    /// <summary>What the dialog asks for.</summary>
    internal readonly record struct StemChoice(StemQuality Quality, string[] Stems);

    internal static class StemFlow
    {
        /// <summary>Tests answer here instead of the dialog: (what is separated) → the choice, or null for cancel.</summary>
        public static Func<string, StemChoice?> Responder { get; set; }

        /// <summary>Tests choose the models zip / the audio file here instead of a picker.</summary>
        public static Func<string> ZipPicker { get; set; }
        public static Func<string> FilePicker { get; set; }

        /// <summary>What the last separation did, or why it didn't (tests, and a line for the status).</summary>
        public static string LastSummary { get; private set; } = "";

        /// <summary>The outcomes of the last separation (tests).</summary>
        public static IReadOnlyList<StemOutcome> LastOutcomes { get; private set; } = Array.Empty<StemOutcome>();

        static ReBuzzCore Core => StudioHost.Current?.Core;

        static readonly FilePickerFileType AudioFiles = new FilePickerFileType("Audio files") { Patterns = AudioImporter.Extensions.Select(e => "*" + e).ToList() };
        static readonly FilePickerFileType Zip = new FilePickerFileType("NeoBuzz Live Stem Models (.zip)") { Patterns = new[] { "*.zip" } };

        // ------------------------------------------------------------------ entry points

        /// <summary>An audio clip of the song.</summary>
        public static async Task SeparateClip(IClip clip)
        {
            var layout = Core?.SessionLayout;
            if (layout == null || clip == null) return;
            var source = layout.StemSourceOfClip(clip, out string error);
            if (source == null) { await Say(error); return; }
            await Run(layout, source);
        }

        /// <summary>An audio file (the browser's item, or one the person picks when <paramref name="path"/> is null).</summary>
        public static async Task SeparateFile(string path = null)
        {
            var layout = Core?.SessionLayout;
            if (layout == null) return;
            if (path == null)
            {
                path = await PickFile();
                if (path == null) return;
            }
            var source = layout.StemSourceOfFile(path, out string error);
            if (source == null) { await Say(error); return; }
            await Run(layout, source);
        }

        static async Task<string> PickFile()
        {
            if (FilePicker != null) return FilePicker();
            var storage = Dialogs.Owner?.StorageProvider;
            if (storage == null) return null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Separate stems from an audio file", FileTypeFilter = new[] { AudioFiles }, AllowMultiple = false });
            return files.FirstOrDefault()?.TryGetLocalPath();
        }

        /// <summary>Install models…: takes the zip the stem-models workflow made. Returns what it did, as a sentence.</summary>
        public static async Task<string> InstallModels()
        {
            string zip;
            if (ZipPicker != null) zip = ZipPicker();
            else
            {
                var storage = Dialogs.Owner?.StorageProvider;
                if (storage == null) return "";
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Install stem models", FileTypeFilter = new[] { Zip }, AllowMultiple = false });
                zip = files.FirstOrDefault()?.TryGetLocalPath();
            }
            if (zip == null) return "";
            try
            {
                string folder = StemModels.InstallFolder();
                var ids = await Task.Run(() => StemModels.Install(zip, folder));
                return $"Installed {string.Join(", ", ids)} in {folder}.";
            }
            catch (Exception e)
            {
                return "Couldn't install the models: " + e.Message;
            }
        }

        // ------------------------------------------------------------------ the job

        static async Task Say(string text)
        {
            LastSummary = text;
            await Dialogs.Ask(text, Product.Name, UserButtons.Ok);
        }

        static async Task Run(SessionLayout layout, StemSource source)
        {
            if (!layout.CanMakeAudioTracks)
            {
                await Say($"The {SessionClipHost.MachineName} machine isn't installed, so there are no audio tracks to put stems on.");
                return;
            }
            if (Responder != null)
            {
                var answer = Responder(source.Name);
                if (answer == null) return;
                try
                {
                    var result = await layout.SeparateAsync(source, answer.Value.Quality, answer.Value.Stems, null, CancellationToken.None);
                    Apply(layout, source, result);
                }
                catch (Exception e)
                {
                    LastSummary = "Couldn't separate: " + e.Message;
                }
                return;
            }
            var window = new StemWindow(layout, source);
            await StudioDialog.Run(window);
        }

        /// <summary>Puts a result in the song and sets <see cref="LastSummary"/>.</summary>
        internal static bool Apply(SessionLayout layout, StemSource source, StemResult result)
        {
            var outcomes = layout.ApplyStems(source, result, null, null, out string error);
            if (outcomes == null)
            {
                LastSummary = "Couldn't put the stems in the song: " + error;
                LastOutcomes = Array.Empty<StemOutcome>();
                return false;
            }
            LastOutcomes = outcomes;
            LastSummary = $"Separated {source.Name} into {string.Join(", ", outcomes.Select(o => o.Stem))}" +
                          (result.FromCache ? " (from the cache)." : $" in {result.Seconds:0.#} s.");
            return true;
        }

        // ------------------------------------------------------------------ the window

        /// <summary>The dialog, then the same window as a progress view while the job runs.</summary>
        sealed class StemWindow : Window
        {
            readonly SessionLayout layout;
            readonly StemSource source;
            readonly Dictionary<StemQuality, RadioButton> qualities = new();
            readonly Dictionary<StemQuality, TextBlock> hints = new();
            readonly Dictionary<string, CheckBox> stems = new();
            readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = StudioTokens.TextSecondaryBrush };
            readonly Button go = StudioDialog.Primary("Separate");
            readonly Button cancel = StudioDialog.Secondary();
            readonly Button install = new Button { Content = "Install models…" };
            readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, MinWidth = 380 };
            readonly CancellationTokenSource cts = new();
            bool running;

            public StemWindow(SessionLayout layout, StemSource source)
            {
                this.layout = layout;
                this.source = source;
                Title = "Separate stems";
                SizeToContent = SizeToContent.WidthAndHeight;
                CanResize = false;
                ShowInTaskbar = false;

                var quality = new StackPanel { Spacing = 4 };
                foreach (StemQuality q in Enum.GetValues(typeof(StemQuality)))
                {
                    var radio = new RadioButton { Content = StemModels.Describe(q), GroupName = "quality", Tag = q.ToString() };
                    var hint = new TextBlock { Foreground = StudioTokens.TextSecondaryBrush, FontSize = 12, Margin = new Thickness(26, 0, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 400 };
                    qualities[q] = radio;
                    hints[q] = hint;
                    quality.Children.Add(radio);
                    quality.Children.Add(hint);
                }
                var stemRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
                foreach (var s in StemSeparator.AllStems)
                {
                    var box = new CheckBox { Content = char.ToUpperInvariant(s[0]) + s.Substring(1), IsChecked = true, Tag = s };
                    stems[s] = box;
                    stemRow.Children.Add(box);
                }
                RefreshModels();
                install.Click += async (_, _) =>
                {
                    status.Text = await InstallModels();
                    RefreshModels();
                };
                go.Click += (_, _) => _ = Start();
                cancel.Click += (_, _) =>
                {
                    cts.Cancel();
                    if (!running) Close();
                };
                bar.IsVisible = false;
                Content = StudioDialog.Body(
                    new TextBlock
                    {
                        Text = $"Splits \"{source.Name}\" into stems on this computer, one new audio track each, aligned with the source. It takes a while: " +
                               "Fast is the quickest, Balanced is four times the work, Best the most. A result is kept, so asking again is instant.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 420,
                    },
                    quality,
                    stemRow,
                    install,
                    status,
                    bar,
                    StudioDialog.Buttons(go, cancel));
            }

            /// <summary>The first installed quality is the choice; the others that aren't installed are greyed with the hint.</summary>
            void RefreshModels()
            {
                StemQuality? first = null;
                foreach (var (q, radio) in qualities)
                {
                    string missing = StemModels.Missing(q);
                    radio.IsEnabled = missing == null;
                    hints[q].Text = missing == null ? "" : "Not installed: Install models… takes the NeoBuzz Live Stem Models zip.";
                    hints[q].IsVisible = missing != null;
                    if (missing == null && first == null) first = q;
                    if (missing != null && radio.IsChecked == true) radio.IsChecked = false;
                }
                if (qualities.Values.All(r => r.IsChecked != true) && first != null) qualities[first.Value].IsChecked = true;
                go.IsEnabled = first != null;
            }

            async Task Start()
            {
                var q = qualities.FirstOrDefault(p => p.Value.IsChecked == true).Key;
                var wanted = StemSeparator.AllStems.Where(s => stems[s].IsChecked == true).ToArray();
                if (wanted.Length == 0) { status.Text = "Pick at least one stem."; return; }
                running = true;
                foreach (var c in qualities.Values) c.IsEnabled = false;
                foreach (var c in stems.Values) c.IsEnabled = false;
                go.IsEnabled = false;
                install.IsEnabled = false;
                bar.IsVisible = true;
                status.Text = "Separating… (Cancel stops it)";
                try
                {
                    var result = await layout.SeparateAsync(source, q, wanted, f => Dispatcher.UIThread.Post(() => bar.Value = f), cts.Token);
                    Apply(layout, source, result);
                    status.Text = LastSummary;
                }
                catch (OperationCanceledException)
                {
                    LastSummary = "Stem separation cancelled.";
                }
                catch (Exception e)
                {
                    LastSummary = "Couldn't separate: " + e.Message;
                    await Dialogs.Ask(LastSummary, Product.Name, UserButtons.Ok);
                }
                running = false;
                Close();
            }
        }
    }
}
