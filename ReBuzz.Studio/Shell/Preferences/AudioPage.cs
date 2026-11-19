// Fork (docs/specs/studio-mvp.md S7): Preferences ▸ Audio. The output device from the PortAudio
// driver factory ("PortAudio: …"), its input, sample rate and buffer (the registry's PortAudio
// section, which PortAudioDriver reads when it opens), and the render threads and work algorithm
// (ReBuzz.exe's Preferences ▸ Engine). Each choice is saved and the device opens again with it,
// as ReBuzz.exe's toolbar device choice (SelectedAudioDriver) and Preferences OK do.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ReBuzz.Audio;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Host;
using ReBuzz.Core;
using ReBuzz.Platform;

namespace ReBuzz.Studio.Shell.Preferences
{
    internal sealed class AudioPage : PrefsPage
    {
        /// <summary>Tests: the output and input devices to offer, instead of PortAudio's.</summary>
        public static Func<IReadOnlyList<string>> OutputSource { get; set; }
        public static Func<IReadOnlyList<string>> InputSource { get; set; }

        /// <summary>Tests: what reopening the device does (the device name), instead of opening it.</summary>
        public static Action<string> ReopenHook { get; set; }

        static readonly int[] Rates = { 0, 44100, 48000, 88200, 96000 };
        static readonly int[] Buffers = { 32, 64, 128, 256, 512, 1024, 2048 };
        static readonly string[] Algorithms = { "Recursive Task Groups", "Recursive Tasks", "Threads" };

        // The engine's defaults where it reads them (AudioEngine, CommonAudioProvider, PortAudioDriver).
        const int DefaultThreads = 4, DefaultAlgorithm = 1, DefaultBuffer = 256;

        readonly ReBuzzCore core;
        readonly IRegistryEx registry;
        readonly ComboBox output = Choices(), input = Choices(), rate = Choices(220), buffer = Choices(220);
        readonly ComboBox threads = Choices(120), algorithm = Choices(220);
        // Disk streaming (neo-audio.md, NA1b): not registry settings (a small file in the cache folder), so they are not in the agent's list.
        static readonly int[] StreamThresholdsMb = { 4, 8, 16, 32, 64, 0 };
        static readonly int[] CacheLimitsGb = { 1, 2, 4, 8, 16, 64 };
        readonly ComboBox streamThreshold = Choices(220), cacheLimit = Choices(220);
        readonly TextBlock cacheText = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap, MaxWidth = 460, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        readonly Button clearCache = new Button { Content = "Clear" };
        readonly TextBlock status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        readonly TextBlock missing;
        Button asioPanel;
        readonly CheckBox exclusive = new CheckBox { Content = "Exclusive (other apps go silent)" }, advanced = new CheckBox { Content = "Show advanced devices (WDM-KS)" };
        string note;
        List<string> listed = new List<string>(), outputs = new List<string>(), inputs = new List<string>();

        public AudioPage(ReBuzzCore core, IRegistryEx registry, PrefsModel model) : base(model)
        {
            this.core = core;
            this.registry = registry;

            Heading("Output");
            missing = Note("PortAudio isn't installed here (macOS: brew install portaudio; Linux: libportaudio2), so there are no devices.", Problem);
            Row("Output device", output, "\"PortAudio: default\" follows the system's output device. On Windows the installed ASIO drivers come first (they use their own buffer size and rate: their control panel), then WASAPI, WDM-KS, DirectSound and MME.");
            if (OperatingSystem.IsWindows())
            {
                // The user's choice for Windows (ux-overhaul.md P6, decision 3): ASIO first (the default when a driver is installed), WASAPI shared
                // with low latency (the default without ASIO), shared, and exclusive behind this switch; WDM-KS under advanced; no DirectSound or MME.
                Row("WASAPI mode", exclusive, "Off: shared, so other apps keep playing. On: the device is yours alone, event-driven, and every other app goes silent while it plays.");
                Row("Advanced", advanced, "Also lists the WDM-KS devices.");
                exclusive.IsCheckedChanged += (s, e) => { if (!Filling) SetSwitch("audio.WasapiExclusive", WindowsHostAudioDriverFactory.ExclusiveKey, exclusive.IsChecked == true, exclusiveChanged: true); };
                advanced.IsCheckedChanged += (s, e) => { if (!Filling) SetSwitch("audio.ShowAdvancedDevices", WindowsHostAudioDriverFactory.AdvancedKey, advanced.IsChecked == true, exclusiveChanged: false); };
            }
            Row("Input", input, "What audio takes record from (the Session View); none plays without input.");
            Row("Sample rate", rate);
            Row("Buffer size", buffer, "Smaller: less latency, more CPU. Dropouts in the status line mean it's too small.");
            var restart = new Button { Content = "Restart audio" };
            restart.Click += (s, e) => Reopen();
            // Windows, an ASIO driver: its own control panel (buffer size and rate are the driver's).
            var panel = new Button { Content = "ASIO control panel…" };
            panel.Click += (s, e) => core.AudioEngine?.ShowControlPanel();
            Row("Now", new StackPanel { Spacing = 6, Children = { status, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { restart, panel } } } });
            asioPanel = panel;

            Heading("Rendering");
            Row("Render threads", threads, "With Multithreading on (Advanced ▸ Engine flags). Takes effect after a restart.");
            Row("Work algorithm", algorithm, $"The default is {Algorithms[DefaultAlgorithm]}. Takes effect after a restart.");

            Heading("Disk streaming");
            Row("Audio cache", new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { cacheText, clearCache } },
                "Long audio is decoded once into the cache folder and read from there while it plays, so it does not sit in memory; the cache is shared by songs and made again from the song when a file is lost. Clear removes what this song is not using.");
            Row("Stream audio longer than", streamThreshold, "Shorter audio stays in memory. Takes effect for audio added after the change.");
            Row("Cache size limit", cacheLimit, "The oldest files this song is not using go first.");
            clearCache.Click += (s, e) => { AudioCache.Clear(); ShowCache(); };
            OnPick(streamThreshold, i => { StreamingPolicy.ThresholdBytes = StreamThresholdsMb[i] == 0 ? long.MaxValue / 4 : StreamThresholdsMb[i] * 1024L * 1024; StreamingPolicy.Save(); });
            OnPick(cacheLimit, i => { StreamingPolicy.CacheLimitBytes = CacheLimitsGb[i] * 1024L * 1024 * 1024; StreamingPolicy.Save(); AudioCache.Trim(); ShowCache(); });

            OnPick(output, i => Choose("audio.Device", "AudioDriver", outputs[i], "Settings"));
            OnPick(input, i => Choose("audio.Input", "Input", inputs[i], PortAudioDriver.Section));
            OnPick(rate, i => Choose("audio.SampleRate", "SampleRate", Rates[i], PortAudioDriver.Section));
            OnPick(buffer, i => Choose("audio.BufferSize", "BufferSize", Buffers[i], PortAudioDriver.Section));
            OnPick(threads, i => Choose("audio.AudioThreads", "AudioThreads", i + 1, "Settings"));
            OnPick(algorithm, i => Choose("audio.WorkAlgorithm", "WorkAlgorithm", i, "Settings"));
        }

        public override string Title => "Audio";

        /// <summary>Restore Defaults: the render threads and the work algorithm (the device is the system's to choose, so it stays).</summary>
        public override void RestoreOwn()
        {
            Model?.Restore("audio");
            Refresh();
        }

        public ComboBox OutputBox => output;
        public TextBlock CacheText => cacheText;
        public Button ClearCacheButton => clearCache;
        public ComboBox StreamThresholdBox => streamThreshold;
        public ComboBox CacheLimitBox => cacheLimit;

        /// <summary>The cache's size, its files, and what the song streams.</summary>
        public void ShowCache()
        {
            long bytes = AudioCache.SizeBytes();
            int used = ReBuzz.Core.AudioLibrary.StreamPrefetcher.OpenIds().Count;
            string size = bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1L << 20):0.#} MB";
            cacheText.Text = $"{size} in {AudioCache.FileCount()} file{(AudioCache.FileCount() == 1 ? "" : "s")}, {used} in use by this song; " +
                $"{StreamStats.Underruns} underrun{(StreamStats.Underruns == 1 ? "" : "s")} since the last reset.";
        }
        public ComboBox InputBox => input;
        public ComboBox RateBox => rate;
        public ComboBox BufferBox => buffer;
        public ComboBox ThreadsBox => threads;
        public ComboBox AlgorithmBox => algorithm;
        public string StatusText => status.Text;

        /// <summary>The device the settings name (the first one, as the engine falls back to, when none is saved).</summary>
        string SavedOutput => registry.Read("AudioDriver", "", "Settings") is string s && s.Length > 0 ? s : listed.FirstOrDefault() ?? PortAudioDriverFactory.DefaultName;

        protected override void OnShown()
        {
            if (LeaveOutSlowState)
            {
                // The devices once the window has drawn (PortAudio may take a while to list them); the rest now.
                Refresh();
                return;
            }
            listed = (OutputSource?.Invoke() ?? Outputs()).ToList();
            inputs = new List<string> { "", "default" };
            inputs.AddRange((InputSource?.Invoke() ?? Inputs()).Where(n => !inputs.Contains(n)));
            devicesListed = true;
            Refresh();
        }

        bool devicesListed;

        /// <summary>Whether the page says PortAudio isn't installed (only once it has asked for the devices).</summary>
        internal bool SaysPortAudioIsMissing => missing.IsVisible;

        public override void Refresh()
        {
            Filling = true;
            try
            {
                missing.IsVisible = devicesListed && listed.Count == 0;
                string saved = SavedOutput;
                outputs = listed.ToList();
                if (!outputs.Contains(saved)) outputs.Add(saved);
                // "Not connected" only once the devices have been listed (the window draws before it lists them).
                output.ItemsSource = outputs.Select(n => WasapiChoice.Label(n) + (listed.Contains(n) || !devicesListed ? "" : " (not connected)")).ToList();
                output.SelectedIndex = outputs.IndexOf(saved);

                string savedInput = registry.Read("Input", "", PortAudioDriver.Section) ?? "";
                if (!inputs.Contains(savedInput)) inputs.Add(savedInput);
                input.ItemsSource = inputs.Select(n => n == "" ? "None" : n == "default" ? "Default input" : n).ToList();
                input.SelectedIndex = inputs.IndexOf(savedInput);

                int deviceRate = core.AudioEngine?.SelectedOutDevice != null ? core.SelectedAudioDriverSampleRate : 0;
                rate.ItemsSource = Rates.Select(r => r == 0 ? (deviceRate > 0 ? $"The device's own ({deviceRate} Hz)" : "The device's own") : $"{r} Hz").ToList();
                rate.SelectedIndex = Math.Max(0, Array.IndexOf(Rates, registry.Read("SampleRate", 0, PortAudioDriver.Section)));

                double msRate = deviceRate > 0 ? deviceRate : 48000;
                int savedBuffer = registry.Read("BufferSize", DefaultBuffer, PortAudioDriver.Section);
                buffer.ItemsSource = Buffers.Select(b => $"{b} frames ({b * 1000.0 / msRate:0.#} ms)").ToList();
                buffer.SelectedIndex = Array.IndexOf(Buffers, savedBuffer) is int bi && bi >= 0 ? bi : Array.IndexOf(Buffers, DefaultBuffer);

                streamThreshold.ItemsSource = StreamThresholdsMb.Select(m => m == 0 ? "Never (keep all audio in memory)" : $"{m} MB of audio (about {Math.Round(m * 1048576 / 8.0 / 48000)} s of stereo)").ToList();
                long thresholdMb = StreamingPolicy.ThresholdBytes / (1024 * 1024);
                streamThreshold.SelectedIndex = StreamingPolicy.ThresholdBytes > 1L << 40 ? StreamThresholdsMb.Length - 1 : Math.Max(0, Array.IndexOf(StreamThresholdsMb, (int)thresholdMb));
                cacheLimit.ItemsSource = CacheLimitsGb.Select(g => $"{g} GB").ToList();
                cacheLimit.SelectedIndex = Math.Max(0, Array.IndexOf(CacheLimitsGb, (int)(StreamingPolicy.CacheLimitBytes >> 30)));
                ShowCache();

                threads.ItemsSource = Enumerable.Range(1, 8).Select(n => n.ToString()).ToList();
                threads.SelectedIndex = Math.Clamp(registry.Read("AudioThreads", DefaultThreads, "Settings"), 1, 8) - 1;
                algorithm.ItemsSource = Algorithms;
                algorithm.SelectedIndex = Math.Clamp(registry.Read("WorkAlgorithm", DefaultAlgorithm, "Settings"), 0, Algorithms.Length - 1);

                exclusive.IsChecked = registry.Read(WindowsHostAudioDriverFactory.ExclusiveKey, 0, "Settings") == 1;
                advanced.IsChecked = registry.Read(WindowsHostAudioDriverFactory.AdvancedKey, 0, "Settings") == 1;
                // A WASAPI endpoint that is unplugged: the system default plays instead, and the page says so (it plays on the endpoint again once plugged in).
                string gone = devicesListed ? WasapiChoice.MissingNote(saved, listed) : null;
                string notice = note ?? (gone != null ? gone : WasapiChoice.Notice);
                status.Text = notice == null ? DeviceStatus(core) : notice + " " + DeviceStatus(core);
                if (asioPanel != null) asioPanel.IsVisible = core.AudioEngine?.SelectedOutDevice?.Type == Audio.Engine.AudioEngine.AudioOutType.ASIO;
            }
            finally
            {
                Filling = false;
            }
        }

        /// <summary>What plays now, for the page and the tests.</summary>
        public static string DeviceStatus(ReBuzzCore core)
        {
            var engine = core.AudioEngine;
            var device = engine?.SelectedOutDevice;
            if (device == null) return "No audio output is open.";
            var driver = engine.CurrentDriver;
            string text = $"Playing through {device.Name} at {core.SelectedAudioDriverSampleRate} Hz, {driver?.BufferSize} frames";
            if (driver is PortAudioDriver pa && core.SelectedAudioDriverSampleRate > 0)
                text += $" ({pa.LatencyFrames * 1000.0 / core.SelectedAudioDriverSampleRate:0.#} ms to the speakers)";
            else if (core.SelectedAudioDriverSampleRate > 0 && core.OutputLatencySamples > 0)
                text += $" ({core.OutputLatencySamples * 1000.0 / core.SelectedAudioDriverSampleRate:0.#} ms to the speakers)";
            if (engine.SelectedInDevice != null) text += $"; input from {engine.SelectedInDevice.Name}";
            return text + ".";
        }

        /// <summary>A choice, saved where the engine reads it and logged in settings_history (as Manual) under the agent's key for it.</summary>
        void Choose<T>(string settingKey, string key, T value, string section)
        {
            string before = Model?.Get(settingKey) ?? "";
            registry.Write(key, value, section);
            Model?.Record(settingKey, before, Model.Get(settingKey));
            Reopen();
            Refresh();
        }

        /// <summary>Opens the chosen device again with the settings as they are now.</summary>
        public void Reopen()
        {
            string device = SavedOutput;
            if (ReopenHook != null)
            {
                ReopenHook(device);
                return;
            }
            if (OperatingSystem.IsWindows() && core.AudioEngine?.SelectedOutDevice != null && core.AudioEngine.SelectedOutDevice.Name != device)
            {
                _ = ReopenChecked(device);
                return;
            }
            note = null;
            Reopen(core, device);
        }

        /// <summary>
        /// A new device, with the way back audio_set has (AudioSwitch, ReBuzz.Agent): if it doesn't open, no audio flows or the dropouts
        /// spike, the one that played is put back and the status says why.
        /// </summary>
        async System.Threading.Tasks.Task ReopenChecked(string device)
        {
            var audio = new ReBuzz.Agent.AudioBackend(core);
            var previous = audio.Current();
            var wanted = new ReBuzz.Agent.App.AudioConfig(device, previous.BufferFrames, previous.SampleRate);
            var done = await ReBuzz.Agent.App.AudioSwitch.Apply(audio, (work, ct) => System.Threading.Tasks.Task.FromResult(work()), previous, wanted, default);
            note = done.Applied ? null : $"Not applied: {done.Reason} {previous.Device} is back." + done.RevertNote;
            if (!done.Applied) registry.Write("AudioDriver", previous.Device, "Settings");
            if (done.Applied && core.AudioEngine.SelectedOutDevice != null)
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
            Refresh();
        }

        /// <summary>A switch of the audio section (WASAPI exclusive, advanced devices): saved, the device list made again, and the device chosen again where the mode changed.</summary>
        void SetSwitch(string settingKey, string key, bool on, bool exclusiveChanged)
        {
            string before = Model?.Get(settingKey) ?? "";
            registry.Write(key, on ? 1 : 0, "Settings");
            Model?.Record(settingKey, before, Model.Get(settingKey));
            listed = (OutputSource?.Invoke() ?? Outputs()).ToList();
            if (exclusiveChanged && WasapiModes.IsWasapi(SavedOutput))
            {
                registry.Write("AudioDriver", on ? WasapiModes.Exclusive : WasapiModes.SharedLowLatency, "Settings");
                Reopen();
            }
            Refresh();
        }

        /// <summary>
        /// The device, opened again: a new device as ReBuzz.exe's toolbar chooses one
        /// (SelectedAudioDriver: it opens, plays and is remembered); the same one closed and opened
        /// again, so the driver reads its settings anew.
        /// </summary>
        public static void Reopen(ReBuzzCore core, string device)
        {
            if (core.SelectedAudioDriver != device)
                core.SelectedAudioDriver = device;
            else
            {
                core.AudioEngine.CreateAudioOut(device);       // stops and releases the old driver first
                core.AudioEngine.Play();
            }
            if (core.AudioEngine.SelectedOutDevice != null)
                System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;   // as EngineHost does
        }

        /// <summary>PortAudio's output devices, as the engine names them.</summary>
        IReadOnlyList<string> Outputs()
        {
            try
            {
                return core.AudioEngine?.AudioDevices().Select(d => d.Name).ToList()     // the same list audio_devices gives (every type, WASAPI endpoints included)
                       ?? new List<string>();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>PortAudio's devices with an input, as the PortAudio section's Input names them.</summary>
        static IReadOnlyList<string> Inputs()
        {
            var list = new List<string>();
            if (!PortAudio.Available) return list;
            try
            {
                PortAudio.Acquire();
                try
                {
                    list.AddRange(PortAudio.Devices().Where(d => d.Inputs >= 1).Select(PortAudioDriverFactory.Label));
                }
                finally
                {
                    PortAudio.Release();
                }
            }
            catch (Exception)
            {
            }
            return list;
        }
    }
}
