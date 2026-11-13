// Fork (docs/specs/ux-overhaul.md P3j, decisions 91-96): File ▸ Export Audio… (⇧⌘R). A sheet over the window: the range (song or loop), the
// bit depth (16, 24, 32 float), dither (Neo Dither's, on for 16 bit), the sample rate, normalise (off), and stems. It runs the agent's own
// `export_audio` tool (ExportTools, one code path, so the file is the one the tool writes with the same settings), shows its progress,
// can be cancelled, never replaces a file without asking, and opens the folder when it is done.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ExportSheet : Window
    {
        /// <summary>Tests: what happens instead of opening the folder.</summary>
        public static Action<string> FolderOpener { get; set; }

        /// <summary>Tests: the path the Choose… button gives instead of a picker (null: cancelled).</summary>
        public static Func<string> Chooser { get; set; }

        /// <summary>Tests: called with the seconds rendered at each progress report (the render's, once a second of audio).</summary>
        internal static Action<double> OnProgress { get; set; }

        static string lastFolder;

        readonly ReBuzzCore core;
        CancellationTokenSource cancel;

        internal readonly ComboBox Range = new ComboBox { MinWidth = 220 };
        internal readonly ComboBox Bits = new ComboBox { MinWidth = 220 };
        internal readonly CheckBox Dither = new CheckBox { Content = "Dither (Neo Dither's TPDF)", IsChecked = true };
        internal readonly ComboBox Rate = new ComboBox { MinWidth = 220 };
        internal readonly CheckBox Normalise = new CheckBox { Content = "Normalise to -1 dBFS peak" };
        internal readonly ComboBox Stems = new ComboBox { MinWidth = 220 };
        internal readonly TextBox PathBox = new TextBox { MinWidth = 300 };
        internal readonly Button Choose = new Button { Content = "Choose…" };
        internal readonly Button Export = new Button { Content = "Export", MinWidth = 88, IsDefault = true };
        internal readonly Button CancelButton = new Button { Content = "Cancel", MinWidth = 88, IsCancel = true };
        internal readonly ProgressBar Bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, IsVisible = false };
        internal readonly TextBlock Status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, MinHeight = 18 };

        /// <summary>The song at the engine's own rate, 44100 and 48000 first: the choices of the rate box (0 is the engine's).</summary>
        internal static readonly int[] Rates = { 0, 44100, 48000, 88200, 96000 };
        internal static readonly int[] BitDepths = { 16, 24, 32 };

        public bool Running { get; private set; }

        /// <summary>The result of the last export (tests): the tool's answer, or null.</summary>
        public JsonObject Result { get; private set; }

        public ExportSheet(ReBuzzCore core)
        {
            this.core = core;
            Title = "Export Audio";
            SizeToContent = SizeToContent.WidthAndHeight;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            bool hasLoop = core.SongCore.LoopEnd > core.SongCore.LoopStart;
            Range.ItemsSource = new[] { "The whole song", hasLoop ? "The loop" : "The loop (none set)" };
            Range.SelectedIndex = 0;
            Bits.ItemsSource = new[] { "16 bit", "24 bit", "32 bit float" };
            Bits.SelectedIndex = 1;
            int engineRate = ReBuzzCore.masterInfo?.SamplesPerSec ?? 44100;
            Rate.ItemsSource = Rates.Select(r => r == 0 ? $"{engineRate} Hz (the song's)" : r + " Hz").ToList();
            Rate.SelectedIndex = 0;
            Stems.ItemsSource = new[] { "No stems", "A file for each generator", "A file for each machine" };
            Stems.SelectedIndex = 0;
            Dither.IsChecked = false;           // 24 bit is selected first; 16 bit turns it on
            Bits.SelectionChanged += (s, e) =>
            {
                int bits = BitDepths[Math.Max(0, Bits.SelectedIndex)];
                Dither.IsEnabled = bits != 32;
                if (bits == 16) Dither.IsChecked = true;            // on for 16 bit (the choice stays with the person afterwards)
                else if (bits == 32) Dither.IsChecked = false;
            };
            Dither.IsEnabled = true;
            PathBox.Text = DefaultPath(core);
            Avalonia.Automation.AutomationProperties.SetName(Range, "Range");
            Avalonia.Automation.AutomationProperties.SetName(Bits, "Bit depth");
            Avalonia.Automation.AutomationProperties.SetName(Rate, "Sample rate");
            Avalonia.Automation.AutomationProperties.SetName(Stems, "Stems");
            Avalonia.Automation.AutomationProperties.SetName(PathBox, "File");
            Avalonia.Automation.AutomationProperties.SetName(Bar, "Export progress");
            Export.Classes.Add("accent");

            Choose.Click += async (s, e) => await ChooseFile();
            Export.Click += async (s, e) => await Run();
            CancelButton.Click += (s, e) =>
            {
                if (Running) { cancel?.Cancel(); Status.Text = "Cancelling…"; }
                else Close();
            };

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowSpacing = 8, ColumnSpacing = 12 };
            int row = 0;
            void Row(string label, Control c)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = StudioTokens.TextSecondaryBrush };
                Grid.SetRow(l, row); Grid.SetRow(c, row); Grid.SetColumn(c, 1);
                grid.Children.Add(l); grid.Children.Add(c);
                row++;
            }
            var file = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(Choose, Dock.Right);
            Choose.Margin = new Thickness(8, 0, 0, 0);
            file.Children.Add(Choose);
            file.Children.Add(PathBox);
            Row("Range", Range);
            Row("Bit depth", Bits);
            Row("", Dither);
            Row("Sample rate", Rate);
            Row("", Normalise);
            Row("Stems", Stems);
            Row("File", file);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { CancelButton, Export } };
            if (OperatingSystem.IsMacOS()) { buttons.Children.Clear(); buttons.Children.Add(CancelButton); buttons.Children.Add(Export); }
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 12, MinWidth = 480, Children = { grid, Bar, Status, buttons } };
        }

        /// <summary>The window's own close button while rendering is a Cancel: the render must end (and put the song back) first.</summary>
        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (Running)
            {
                e.Cancel = true;
                cancel?.Cancel();
                Status.Text = "Cancelling…";
            }
            base.OnClosing(e);
        }

        static string DefaultPath(ReBuzzCore core)
        {
            string song = core.SongCore.SongName;
            string name = string.IsNullOrEmpty(song) ? "Untitled" : Path.GetFileNameWithoutExtension(song);
            string folder = lastFolder
                ?? (string.IsNullOrEmpty(song) ? null : Path.GetDirectoryName(song))
                ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (string.IsNullOrEmpty(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(folder)) folder = Path.GetTempPath();
            return Path.Combine(folder, name + ".wav");
        }

        /// <summary>Opens the sheet over the active window; completes when it closes.</summary>
        public static async Task Open(ReBuzzCore core)
        {
            if (core == null) return;
            var sheet = new ExportSheet(core);
            var owner = Dialogs.Owner;
            if (owner != null) await sheet.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                sheet.Closed += (s, e) => closed.TrySetResult();
                sheet.Show();
                await closed.Task;
            }
        }

        async Task ChooseFile()
        {
            string path = Chooser != null ? Chooser() : null;
            if (Chooser == null && StorageProvider != null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export audio to",
                    SuggestedFileName = Path.GetFileName(PathBox.Text ?? "Untitled.wav"),
                    FileTypeChoices = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } },
                    DefaultExtension = "wav",
                });
                path = file?.TryGetLocalPath();
            }
            if (!string.IsNullOrEmpty(path)) PathBox.Text = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? path : path + ".wav";
        }

        /// <summary>The arguments of the `export_audio` call these settings make.</summary>
        internal JsonObject Arguments()
        {
            int bits = BitDepths[Math.Max(0, Bits.SelectedIndex)];
            var a = new JsonObject { ["path"] = (PathBox.Text ?? "").Trim(), ["bits"] = bits };
            if (Range.SelectedIndex == 1) a["range"] = "loop";
            if (bits != 32) a["dither"] = Dither.IsChecked == true ? "tpdf" : "off";
            int rate = Rates[Math.Max(0, Rate.SelectedIndex)];
            if (rate != 0) a["sample_rate"] = rate;
            if (Normalise.IsChecked == true) a["normalize_db"] = -1.0;
            a["stems"] = Stems.SelectedIndex switch { 1 => "generators", 2 => "all", _ => "none" };
            return a;
        }

        /// <summary>The files the export would make (the master first), for the overwrite question.</summary>
        IEnumerable<string> Targets(JsonObject args)
        {
            string path = args["path"]!.GetValue<string>();
            yield return path;
            string stems = args["stems"]!.GetValue<string>();
            if (stems == "none") yield break;
            foreach (var m in core.SongCore.Machines)
            {
                if (m.DLL.Info.Type == MachineType.Master) continue;
                if (stems == "generators" && m.DLL.Info.Type != MachineType.Generator) continue;
                yield return ExportTools.StemFile(path, m.Name);
            }
        }

        /// <summary>True while a sheet renders: the song is in the render's hands (the autosave and the snapshot timer wait).</summary>
        public static bool Rendering { get; private set; }

        readonly List<Window> blocked = new();

        void Lock(bool running)
        {
            Running = running;
            Rendering = running;
            if (running)
            {
                // Block edits: every other window of the studio (the main window is under this sheet already; tool windows are not).
                var others = new List<Window>();
                if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
                    others.AddRange(desktop.Windows);
                if (Owner is Window owner && !others.Contains(owner)) others.Add(owner);
                foreach (var w in others)
                    if (w != this && w.IsEnabled) { w.IsEnabled = false; blocked.Add(w); }
            }
            else
            {
                foreach (var w in blocked) w.IsEnabled = true;
                blocked.Clear();
            }
            foreach (Control c in new Control[] { Range, Bits, Dither, Rate, Normalise, Stems, PathBox, Choose, Export }) c.IsEnabled = !running;
            if (!running) Dither.IsEnabled = BitDepths[Math.Max(0, Bits.SelectedIndex)] != 32;
            Bar.IsVisible = running;
            CancelButton.Content = running ? "Cancel export" : "Cancel";
        }

        /// <summary>Exports with the sheet's settings: true when the files were written.</summary>
        public async Task<bool> Run()
        {
            if (Running) return false;
            var args = Arguments();
            string path = args["path"]!.GetValue<string>();
            if (!Path.IsPathRooted(path)) { Status.Text = "Give the full path of the file, e.g. " + DefaultPath(core); return false; }
            var existing = Targets(args).Where(File.Exists).ToList();
            if (existing.Count > 0)
            {
                string what = existing.Count == 1 ? Path.GetFileName(existing[0]) + " already exists" : existing.Count + " files already exist";
                var answer = await Dialogs.Ask($"{what} in {Path.GetDirectoryName(existing[0])}. Replace {(existing.Count == 1 ? "it" : "them")}?", "Export Audio", UserButtons.YesNo,
                    new DialogChoices { Default = UserAnswer.No, Safe = UserAnswer.No, Labels = new Dictionary<UserAnswer, string> { [UserAnswer.Yes] = "Replace", [UserAnswer.No] = "Keep" }, Destructive = UserAnswer.Yes });
                if (answer != UserAnswer.Yes) { Status.Text = "Nothing was written."; return false; }
                args["overwrite"] = true;
            }
            var tool = StudioHost.Current?.Agent?.Host.Server.Tools.Find("export_audio");
            if (tool == null) { Status.Text = "This build can't render."; return false; }

            cancel = new CancellationTokenSource();
            Lock(true);
            Bar.IsIndeterminate = true;
            Status.Text = "Rendering…";
            Result = null;
            bool done = false;
            try
            {
                // The render runs on a worker thread of its own (Background), so this thread draws the sheet and hears its Cancel button
                // as it always does. The song is not safe to edit while it renders (the render sets the loop, the mutes and the position,
                // and puts them back): edits are blocked, not worked around (every other window is disabled, the autosave and the
                // snapshot timer wait, an agent call queues behind it), and the cancel stops it within a block.
                var context = new ToolContext("export_audio", args, cancel.Token, (seconds, total, message) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!Running) return;
                        Bar.IsIndeterminate = total is not > 0;
                        if (total is > 0) Bar.Value = Math.Min(1, seconds / total.Value);
                        Status.Text = message == "seconds rendered" ? $"Rendering… {seconds:0} of {total:0} s" : message == null ? "Rendering…" : char.ToUpper(message[0]) + message.Substring(1) + "…";
                        OnProgress?.Invoke(seconds);
                    }, DispatcherPriority.Input);
                }) { Background = true };
                var result = await tool.Handler(context);
                if (result.IsError)
                {
                    Status.Text = result.Summary;
                }
                else
                {
                    Result = result.Structured;
                    done = true;
                    lastFolder = Path.GetDirectoryName(path);
                    Bar.IsIndeterminate = false;
                    Bar.Value = 1;
                    Status.Text = "Exported " + Path.GetFileName(path);
                }
            }
            catch (OperationCanceledException)
            {
                Status.Text = "Cancelled. Nothing was written.";
            }
            catch (ToolError e)
            {
                Status.Text = e.Message;
            }
            catch (Exception e)
            {
                Status.Text = "The export failed: " + e.Message;
            }
            finally
            {
                Lock(false);
                cancel.Dispose();
                cancel = null;
            }
            if (done)
            {
                OpenFolderOf(path);
                Close();
            }
            return done;
        }

        static void OpenFolderOf(string path)
        {
            string folder = Path.GetDirectoryName(path);
            if (FolderOpener != null) { FolderOpener(folder); return; }
            try
            {
                if (OperatingSystem.IsMacOS()) Process.Start(new ProcessStartInfo("open", new[] { "-R", path }) { UseShellExecute = false });
                else if (OperatingSystem.IsWindows()) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else Process.Start(new ProcessStartInfo("xdg-open", new[] { folder }) { UseShellExecute = false });
            }
            catch (Exception)
            {
                // No file manager to open: the sheet's "Exported …" line has said where it went.
            }
        }
    }
}
