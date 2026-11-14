// Fork (docs/specs/ux-overhaul.md P5b, §9 parity: "the wavetable, as the Samples view"): the song's wavetable. The slots as Buzz shows them
// (hex from 01) with their names, and for the one chosen: name, length, rate, channels, root note, loop (on, start, end), a waveform, and
// Load / Replace, Clear, Play. A file dragged in goes to the chosen slot (to the first empty ones when it is filled). Everything is an undo
// step on the song's history (WaveEdits, which shares the agent's wave_import). WPF keeps its own wavetable window.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Samples
{
    using Control = Avalonia.Controls.Control;

    internal sealed class SamplesWindow : Window
    {
        static SamplesWindow current;

        readonly ListBox slots = new ListBox { MinWidth = 240, Width = 260 };
        readonly TextBox name = new TextBox { MinHeight = 0, Height = 26, Padding = new Thickness(8, 0), VerticalContentAlignment = VerticalAlignment.Center, FontSize = StudioTokens.FontBody };
        readonly TextBlock info = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap };
        readonly TextBlock message = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap };
        readonly CheckBox loop = new CheckBox { Content = "Loop" };
        readonly NumericUpDown loopStart = Spin(), loopEnd = Spin(), root = Spin(0, 127);
        readonly TextBlock rootName = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };
        readonly Button load = new Button { Content = "Load…" }, clear = new Button { Content = "Clear" }, play = new Button { Content = "Play" };
        readonly WaveformView waveform = new WaveformView();
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        bool filling;
        int shown = -1;
        readonly Action<int> changed;

        /// <summary>The file chooser (tests replace it: the picked paths, or null for none).</summary>
        internal Func<IReadOnlyList<string>> Chooser { get; set; }

        static NumericUpDown Spin(int min = 0, int max = int.MaxValue / 2) =>
            new NumericUpDown { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 140, MinHeight = 0, Height = 26, FontSize = StudioTokens.FontBody };

        static ReBuzzCore Core => StudioHost.Current?.Core;

        /// <summary>Opens the Samples view (or brings it to the front).</summary>
        public static SamplesWindow Open(Window owner = null)
        {
            if (current == null)
            {
                current = new SamplesWindow();
                current.Closed += (s, e) => current = null;
                if (owner != null) current.Show(owner); else current.Show();
            }
            else current.Activate();
            current.slots.Focus();
            return current;
        }

        internal static SamplesWindow Current => current;

        SamplesWindow()
        {
            Title = "Samples";
            Width = 900;
            Height = 560;
            MinWidth = 640;
            MinHeight = 420;
            Background = StudioTokens.SurfaceViewBrush;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            AutomationProperties.SetName(this, "Samples");
            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, OnDrop);

            AutomationProperties.SetName(slots, "Wavetable slots");
            AutomationProperties.SetName(name, "Name");
            AutomationProperties.SetName(loop, "Loop");
            AutomationProperties.SetName(loopStart, "Loop start (samples)");
            AutomationProperties.SetName(loopEnd, "Loop end (samples)");
            AutomationProperties.SetName(root, "Root note (MIDI)");
            AutomationProperties.SetName(load, "Load");
            AutomationProperties.SetName(clear, "Clear");
            AutomationProperties.SetName(play, "Play");
            ToolTip.SetTip(load, "Loads an audio file (WAV, AIFF, FLAC, OGG) into the chosen slot. Dragging a file into this window does too.");
            ToolTip.SetTip(clear, "Empties the slot. Undo brings the wave back.");
            ToolTip.SetTip(play, "Plays the wave through the master, once. Press again to stop.");
            ToolTip.SetTip(root, "The MIDI note the wave plays at unchanged (60 is middle C)");

            slots.SelectionChanged += (s, e) => { if (!filling) ShowSlot(slots.SelectedIndex); };
            slots.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete || e.Key == Key.Back) { ClearNow(); e.Handled = true; }
                else if (e.Key == Key.Enter) { _ = LoadNow(); e.Handled = true; }
            };
            name.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { RenameNow(); e.Handled = true; }
                else if (e.Key == Key.Escape) { ShowSlot(shown); e.Handled = true; }
            };
            name.LostFocus += (s, e) => RenameNow();
            load.Click += async (s, e) => await LoadNow();
            clear.Click += (s, e) => ClearNow();
            play.Click += (s, e) => TogglePlay();
            loop.IsCheckedChanged += (s, e) => { if (!filling) LoopNow(); };
            loopStart.ValueChanged += (s, e) => { if (!filling && loop.IsChecked == true) LoopNow(); };
            loopEnd.ValueChanged += (s, e) => { if (!filling && loop.IsChecked == true) LoopNow(); };
            root.ValueChanged += (s, e) => { if (!filling) RootNow(); };

            var rootRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Caption("Root note"), root, rootName } };
            var loopRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { loop, Caption("Start"), loopStart, Caption("End"), loopEnd } };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { load, clear, play } };
            var details = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(16, 12, 16, 12),
                Children = { Caption("Name"), name, info, waveform, loopRow, rootRow, buttons, message },
            };
            var root2 = new DockPanel();
            DockPanel.SetDock(slots, Dock.Left);
            root2.Children.Add(slots);
            root2.Children.Add(new ScrollViewer { Content = details, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            Content = root2;
            KeyDown += (s, e) => { if (e.Key == Key.Escape && !name.IsKeyboardFocusWithin) { Close(); e.Handled = true; } };

            changed = _ => Dispatcher.UIThread.Post(RefreshList);
            Opened += (s, e) =>
            {
                if (Core?.SongCore?.Wavetable != null) Core.SongCore.Wavetable.WaveChanged += changed;
                poll.Start();
                RefreshList();
                slots.SelectedIndex = Math.Max(0, FirstFilled());
            };
            Closed += (s, e) =>
            {
                poll.Stop();
                if (Core?.SongCore?.Wavetable != null) Core.SongCore.Wavetable.WaveChanged -= changed;
                if (Core != null) WaveEdits.StopPreview(Core);
            };
            poll.Tick += (s, e) => UpdatePlay();
        }

        static TextBlock Caption(string text) => new TextBlock { Text = text, FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };

        // ------------------------------------------------------------------ the slots

        int Count => Core?.SongCore?.Wavetable?.Waves.Count ?? 0;

        int FirstFilled()
        {
            for (int i = 0; i < Count; i++) if (WaveEdits.Wave(Core, i) != null) return i;
            return -1;
        }

        int FirstEmpty()
        {
            for (int i = 0; i < Count; i++) if (WaveEdits.Wave(Core, i) == null) return i;
            return -1;
        }

        /// <summary>The slot's line: its hex number and its name ("01  Kick"), the number alone while it is empty.</summary>
        internal static string SlotText(int index, string waveName) => WaveEdits.Hex(index) + (waveName == null ? "" : "  " + (waveName.Length > 0 ? waveName : "(unnamed)"));

        void RefreshList()
        {
            if (Core == null) return;
            filling = true;
            int keep = slots.SelectedIndex;
            var items = new List<string>(Count);
            for (int i = 0; i < Count; i++) items.Add(SlotText(i, WaveEdits.Wave(Core, i)?.Name));
            slots.ItemsSource = items;
            slots.SelectedIndex = keep >= 0 && keep < items.Count ? keep : 0;
            filling = false;
            ShowSlot(slots.SelectedIndex);
        }

        void ShowSlot(int index)
        {
            if (Core == null) return;
            filling = true;
            shown = index;
            var wave = WaveEdits.Wave(Core, index);
            var layer = wave?.LayersList.FirstOrDefault();
            bool has = layer != null;
            if (!name.IsKeyboardFocusWithin || wave == null) name.Text = wave?.Name ?? "";
            name.IsEnabled = has;
            clear.IsEnabled = has;
            play.IsEnabled = has;
            loop.IsEnabled = has;
            loopStart.IsEnabled = has;
            loopEnd.IsEnabled = has;
            root.IsEnabled = has;
            load.Content = has ? "Replace…" : "Load…";
            AutomationProperties.SetName(load, has ? "Replace" : "Load");
            if (has)
            {
                int rate = Math.Max(1, layer.SampleRate);
                int midi = Pitch.FromBuzz(layer.RootNote);
                if (midi < 0) midi = 60;
                info.Text = $"{layer.SampleCount / (double)rate:0.###} s · {layer.SampleCount:N0} samples · {rate} Hz · {(layer.ChannelCount > 1 ? "stereo" : "mono")}"
                          + (wave.LayersList.Count > 1 ? $" · {wave.LayersList.Count} layers (the first is shown)" : "");
                loopStart.Maximum = layer.SampleCount;
                loopEnd.Maximum = layer.SampleCount;
                bool on = (wave.Flags & WaveFlags.Loop) != 0 && layer.LoopEnd > layer.LoopStart;
                loop.IsChecked = on;
                loopStart.Value = layer.LoopStart;
                loopEnd.Value = layer.LoopEnd;
                root.Value = midi;
                rootName.Text = NoteNames.Name(midi);
                waveform.Show(layer, on);
            }
            else
            {
                info.Text = index >= 0 ? $"Slot {WaveEdits.Hex(index)} is empty. Load a file, or drag one in." : "";
                loop.IsChecked = false;
                loopStart.Value = 0;
                loopEnd.Value = 0;
                rootName.Text = "";
                waveform.Show(null, false);
            }
            UpdatePlay();
            filling = false;
        }

        internal int Selected
        {
            get => slots.SelectedIndex;
            set => slots.SelectedIndex = value;
        }

        // ------------------------------------------------------------------ the edits

        string Say(string text)
        {
            message.Text = text ?? "";
            return text;
        }

        /// <summary>Loads a file into the chosen slot (Replace when filled): null when it worked, else why not.</summary>
        internal string LoadFile(string path, bool toFirstEmpty = false)
        {
            if (Core == null) return Say("No song is open.");
            int index = toFirstEmpty ? FirstEmpty() : slots.SelectedIndex;
            if (index < 0) return Say(toFirstEmpty ? "The wavetable is full." : "Choose a slot first.");
            bool replace = WaveEdits.Wave(Core, index) != null && !toFirstEmpty;
            string refusal = WaveEdits.Load(Core, path, index, null, replace, out int placed);
            if (refusal == null)
            {
                RefreshList();
                slots.SelectedIndex = placed;
                Say($"Loaded {Path.GetFileName(path)} into slot {WaveEdits.Hex(placed)}. Undo takes it back.");
            }
            else Say(refusal);
            return refusal;
        }

        async Task LoadNow()
        {
            IReadOnlyList<string> files = Chooser?.Invoke();
            if (files == null && Chooser == null && StorageProvider != null)
            {
                var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Load a sample",
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType("Audio files") { Patterns = WaveEdits.Extensions.Select(e => "*" + e).ToList() } },
                });
                files = picked.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
            }
            if (files == null || files.Count == 0) return;
            LoadFile(files[0]);
        }

        internal void ClearNow()
        {
            if (Core == null || shown < 0) return;
            string refusal = WaveEdits.Clear(Core, shown);
            if (refusal == null) { RefreshList(); Say($"Slot {WaveEdits.Hex(shown)} is empty. Undo brings the wave back."); }
            else Say(refusal);
        }

        internal string RenameNow()
        {
            if (filling || Core == null || shown < 0 || WaveEdits.Wave(Core, shown) is not { } wave) return null;
            string text = (name.Text ?? "").Trim();
            if (text == wave.Name) return null;
            string refusal = WaveEdits.Rename(Core, shown, text);
            Say(refusal);
            if (refusal != null) ShowSlot(shown);
            return refusal;
        }

        void LoopNow()
        {
            if (Core == null || shown < 0) return;
            bool on = loop.IsChecked == true;
            int start = (int)(loopStart.Value ?? 0), end = (int)(loopEnd.Value ?? 0);
            var layer = WaveEdits.Wave(Core, shown)?.LayersList.FirstOrDefault();
            if (on && layer != null && end <= start) { start = 0; end = layer.SampleCount; }   // turning it on with no points: the whole wave
            string refusal = WaveEdits.SetLoop(Core, shown, on, start, end);
            Say(refusal);
            ShowSlot(shown);
        }

        void RootNow()
        {
            if (Core == null || shown < 0) return;
            int midi = (int)(root.Value ?? 60);
            string refusal = WaveEdits.SetRoot(Core, shown, midi);
            Say(refusal);
            rootName.Text = NoteNames.Name(midi);
        }

        // ------------------------------------------------------------------ preview

        internal void TogglePlay()
        {
            if (Core == null || shown < 0) return;
            if (WaveEdits.IsPreviewing(Core)) WaveEdits.StopPreview(Core);
            else WaveEdits.Preview(Core, shown);
            UpdatePlay();
        }

        void UpdatePlay()
        {
            bool on = Core != null && WaveEdits.IsPreviewing(Core);
            string text = on ? "Stop" : "Play";
            if ((string)play.Content != text)
            {
                play.Content = text;
                AutomationProperties.SetName(play, text);
            }
        }

        internal bool IsPlaying => Core != null && WaveEdits.IsPreviewing(Core);

        // ------------------------------------------------------------------ files dragged in

        static IEnumerable<string> FilesOf(DragEventArgs e) =>
            (e.DataTransfer?.TryGetFiles() ?? Array.Empty<IStorageItem>()).Select(f => f.TryGetLocalPath()).Where(p => p != null && WaveEdits.IsAudioFile(p));

        void OnDragOver(object sender, DragEventArgs e)
        {
            e.DragEffects = FilesOf(e).Any() ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDrop(object sender, DragEventArgs e)
        {
            DropFiles(FilesOf(e).ToList());
            e.Handled = true;
        }

        /// <summary>Files dragged in: the first into the chosen slot (the first empty one when it is filled), the rest into the empty slots after it.</summary>
        internal void DropFiles(IReadOnlyList<string> files)
        {
            bool first = true;
            foreach (var f in files)
            {
                bool filled = WaveEdits.Wave(Core, slots.SelectedIndex) != null;
                string refusal = LoadFile(f, toFirstEmpty: !first || filled);
                first = false;
                if (refusal != null) break;
            }
        }

        // Tests
        internal ListBox SlotList => slots;
        internal TextBox NameBox => name;
        internal CheckBox LoopBox => loop;
        internal NumericUpDown LoopStartBox => loopStart;
        internal NumericUpDown LoopEndBox => loopEnd;
        internal NumericUpDown RootBox => root;
        internal Button LoadButton => load;
        internal Button ClearButton => clear;
        internal Button PlayButton => play;
        internal WaveformView Waveform => waveform;
        internal string Message => message.Text;
        internal string InfoText => info.Text;
        internal void Reload() => RefreshList();
        internal void Commit() { RenameNow(); }
        internal void ApplyLoop() => LoopNow();
        internal void ApplyRoot() => RootNow();
    }
}
