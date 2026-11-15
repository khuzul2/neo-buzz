// Fork (docs/specs/ux-overhaul.md P6b, §9): View ▸ Hard Disk Recorder, after BuzzGUI.MachineView's HDRecorderWindow: records the Master
// to a wave file in real time (MasterRecorder), either at once ("Record") or over the song's loop ("Record loop": starts on the loop's
// first tick and stops after its last). The file's name is the song's, in the folder last used (remembered), numbered
// (name-0001.wav) so a take never overwrites another; the bit depth is a choice. Offline rendering is File ▸ Export Audio.
// It records only while an audio output is open (as there), and stops when the window closes.

using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class HardDiskRecorderWindow : ToolWindow
    {
        public const string PlacementName = "HardDiskRecorder";
        const string FolderKey = "HDRecorderPath";
        static HardDiskRecorderWindow current;

        internal static readonly (string Name, WavFormat Format)[] Depths =
        {
            ("16 bit", WavFormat.Pcm16), ("24 bit", WavFormat.Pcm24), ("32 bit integer", WavFormat.Pcm32), ("32 bit float", WavFormat.Float32),
        };

        internal readonly TextBox PathBox = new TextBox { MinWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch };
        internal readonly ComboBox Bits = new ComboBox { MinWidth = 140 };
        internal readonly Button Record = new Button { Content = "Record" };
        internal readonly Button RecordLoop = new Button { Content = "Record loop" };
        internal readonly Button Stop = new Button { Content = "Stop" };
        internal readonly Button Browse = new Button { Content = "Choose..." };
        internal readonly TextBlock Status = Label("", secondary: true);
        readonly LoopBar progress = new LoopBar();
        MasterRecorder recorder;

        /// <summary>How far through the loop a loop recording is (a plain bar: no animation, nothing to wait for).</summary>
        sealed class LoopBar : Avalonia.Controls.Control
        {
            double value;
            public LoopBar() { Height = 8; HorizontalAlignment = HorizontalAlignment.Stretch; }
            public double Value { get => value; set { if (this.value != value) { this.value = value; InvalidateVisual(); } } }
            public override void Render(Avalonia.Media.DrawingContext ctx)
            {
                var r = new Rect(Bounds.Size);
                ctx.DrawRectangle(StudioTokens.ControlTrackBrush, null, r, 2, 2);
                ctx.DrawRectangle(StudioTokens.AccentBrush, null, new Rect(0, 0, r.Width * Math.Clamp(value, 0, 1), r.Height), 2, 2);
            }
        }

        /// <summary>Tests: answers "Choose..." without a picker.</summary>
        internal Func<string> Chooser { get; set; }

        HardDiskRecorderWindow(StudioHost host) : base(host, "Hard Disk Recorder", PlacementName, 560, 230, refreshMs: 100)
        {
            AutomationProperties.SetName(this, "Hard Disk Recorder");
            Bits.ItemsSource = Array.ConvertAll(Depths, d => d.Name);
            Bits.SelectedIndex = 1;
            AutomationProperties.SetName(PathBox, "File to record to");
            AutomationProperties.SetName(Bits, "Bit depth");
            AutomationProperties.SetName(progress, "Loop progress");
            ToolTip.SetTip(Record, "Record the Master now, until Stop");
            ToolTip.SetTip(RecordLoop, "Play the song's loop once and record it exactly");
            ToolTip.SetTip(Browse, "Choose the file and the folder");
            PathBox.Text = MasterRecorder.NextFree(DefaultPath());
            Record.Click += (s, e) => Begin(loop: false);
            RecordLoop.Click += (s, e) => Begin(loop: true);
            Stop.Click += (s, e) => Finish();
            Browse.Click += async (s, e) => await ChooseFile();
            var file = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
            var fileLabel = Label("File");
            Grid.SetColumn(PathBox, 1);
            Grid.SetColumn(Browse, 2);
            file.Children.Add(fileLabel);
            file.Children.Add(PathBox);
            file.Children.Add(Browse);
            var depth = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Label("Format"), Bits, Label("WAV", secondary: true) } };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Record, RecordLoop, Stop } };
            Content = new StackPanel { Margin = new Thickness(16), Spacing = 12, Children = { file, depth, buttons, progress, Status } };
            Closed += (s, e) => { Finish(); if (current == this) current = null; };
        }

        string DefaultPath()
        {
            string folder = Host?.Registry?.Read(FolderKey, "") ?? "";
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string song = Core?.SongCore?.SongName;
            string name = string.IsNullOrEmpty(song) ? "NeoBuzz Live" : Path.GetFileNameWithoutExtension(song);
            return Path.Combine(folder, name + ".wav");
        }

        /// <summary>Opens the recorder (or brings the open one to the front).</summary>
        public static HardDiskRecorderWindow Open(StudioHost host, Window owner = null)
        {
            if (current != null)
            {
                current.Activate();
                return current;
            }
            current = new HardDiskRecorderWindow(host);
            ShowOver(current, owner);
            return current;
        }

        internal static HardDiskRecorderWindow Current => current;

        internal bool HasOutput => Core?.AudioEngine?.SelectedOutDevice != null;
        internal bool IsRecording => recorder?.Active == true;
        internal MasterRecorder Recorder => recorder;

        async Task ChooseFile()
        {
            string path = Chooser != null ? Chooser() : null;
            if (Chooser == null && StorageProvider != null)
            {
                var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Record to",
                    SuggestedFileName = Path.GetFileName(PathBox.Text ?? "NeoBuzz Live.wav"),
                    FileTypeChoices = new[] { new FilePickerFileType("WAV audio") { Patterns = new[] { "*.wav" } } },
                    DefaultExtension = "wav",
                });
                path = file?.TryGetLocalPath();
            }
            if (string.IsNullOrEmpty(path)) return;
            PathBox.Text = path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? path : path + ".wav";
            Host?.Registry?.Write(FolderKey, Path.GetDirectoryName(PathBox.Text) ?? "");
        }

        /// <summary>Starts a take (the Record buttons).</summary>
        internal bool Begin(bool loop)
        {
            if (IsRecording || !HasOutput) return false;
            string path = (PathBox.Text ?? "").Trim();
            if (path.Length == 0) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                path = MasterRecorder.NextFree(path);
                var r = new MasterRecorder(Core, loop);
                r.Start(path, Depths[Math.Max(0, Bits.SelectedIndex)].Format);
                recorder = r;
                PathBox.Text = path;
            }
            catch (Exception e)
            {
                Status.Text = "Can't record: " + e.Message;
                recorder = null;
                return false;
            }
            Refresh();
            return true;
        }

        /// <summary>Ends the take and closes the file (the Stop button; also a loop's end and the window closing).</summary>
        internal void Finish()
        {
            var r = recorder;
            if (r == null) return;
            r.Stop();
            string path = r.Path;
            long bytes = r.Bytes;
            Status.Text = $"Saved {Path.GetFileName(path)}: {r.Elapsed:mm\\:ss\\.f}, {Size(bytes)}" + (r.Dropped > 0 ? $" - {r.Dropped} blocks lost (the disk was too slow)" : "");
            recorder = null;
            PathBox.Text = MasterRecorder.NextFree(path);
            Host?.Registry?.Write(FolderKey, Path.GetDirectoryName(path) ?? "");
            Refresh(keepStatus: true);
        }

        static string Size(long bytes) => bytes >= 1 << 20 ? (bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.InvariantCulture) + " MB" : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

        internal override void Tick()
        {
            if (recorder?.LoopFinished == true) Finish();
            Refresh();
        }

        void Refresh(bool keepStatus = false)
        {
            bool rec = IsRecording;
            bool can = HasOutput && !rec;
            Record.IsEnabled = can;
            RecordLoop.IsEnabled = can;
            Stop.IsEnabled = rec;
            PathBox.IsEnabled = !rec;
            Bits.IsEnabled = !rec;
            Browse.IsEnabled = !rec;
            progress.Value = rec ? recorder.Progress : 0;
            if (rec)
                Status.Text = (recorder.Now == MasterRecorder.State.WaitingForLoop ? "Waiting for the loop to start..." : "Recording " + recorder.Elapsed.ToString("mm\\:ss\\.f", CultureInfo.InvariantCulture))
                    + $" - {Size(recorder.Bytes)}" + (recorder.Dropped > 0 ? $" - {recorder.Dropped} blocks lost" : "");
            else if (!HasOutput)
                Status.Text = "No audio output is open: choose one in Preferences, Audio, to record.";
            else if (!keepStatus && (Status.Text?.StartsWith("Recording") == true || Status.Text?.StartsWith("No audio") == true || string.IsNullOrEmpty(Status.Text)))
                Status.Text = "Ready.";
        }
    }
}
