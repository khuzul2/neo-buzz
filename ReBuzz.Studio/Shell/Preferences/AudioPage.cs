// Fork (docs/specs/studio-mvp.md S7): Preferences ▸ Audio. The output device from the PortAudio
// driver factory ("PortAudio: …"), its input, sample rate and buffer (the registry's PortAudio
// section, which PortAudioDriver reads when it opens), and the render threads and work algorithm
// (ReBuzz.exe's Preferences ▸ Engine). Each choice is saved and the device opens again with it,
// as ReBuzz.exe's toolbar device choice (SelectedAudioDriver) and Preferences OK do.
// Fork (machine-safety.md §3.2–3.3): Safety: the safety limiter on the output (on by default; off
// asks first), its ceiling, the clip indicator's threshold, and the guard on every machine (off only
// for this song, after asking; never saved off).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using ReBuzz.Audio;
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
        readonly CheckBox limiter = new CheckBox { Content = "Safety limiter on the output" };
        readonly ComboBox ceiling = Choices(160), threshold = Choices(160);
        readonly CheckBox guard = new CheckBox { Content = "Guard every machine" };
        List<double> ceilings = new List<double>(), thresholds = new List<double>();
        static readonly double[] Ceilings = { 0, -0.1, -0.3, -1, -3, -6 };
        static readonly double[] Thresholds = { -6, -3, -1, 0, 1, 2, 3, 6, 12 };
        readonly TextBlock status = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        readonly TextBlock missing;
        List<string> listed = new List<string>(), outputs = new List<string>(), inputs = new List<string>();

        public AudioPage(ReBuzzCore core, IRegistryEx registry)
        {
            this.core = core;
            this.registry = registry;

            Heading("Output");
            missing = Note("PortAudio isn't installed here (macOS: brew install portaudio; Linux: libportaudio2), so there are no devices.", Problem);
            Row("Output device", output, "\"PortAudio: default\" follows the system's output device.");
            Row("Input", input, "What audio takes record from (the Session View); none plays without input.");
            Row("Sample rate", rate);
            Row("Buffer size", buffer, "Smaller: less latency, more CPU. Dropouts in the status line mean it's too small.");
            var restart = new Button { Content = "Restart audio" };
            restart.Click += (s, e) => Reopen();
            Row("Now", new StackPanel { Spacing = 6, Children = { status, restart } });

            Heading("Rendering");
            Row("Render threads", threads, "With Multithreading on (General ▸ Engine). Recursive algorithms take a new count at the next start.");
            Row("Work algorithm", algorithm, $"The default is {Algorithms[DefaultAlgorithm]}.");

            Heading("Safety");
            Row("", limiter, "A zero-latency limiter on what goes to the speakers: nothing above its ceiling reaches them, and below it the sound " +
                             "is untouched. Renders and exports get the true signal.");
            Row("Limiter ceiling", ceiling);
            Row("Clip indicator", threshold, "The master meter turns red above this, measured before the limiter, and keeps the highest over until you click it.");
            Row("", guard, "A machine putting out NaN, infinity or more than +60 dBFS is silenced, stopped and muted (Reset and unmute on its menu). " +
                           "Off only for this song: a new or opened song turns it on again.");
            limiter.IsCheckedChanged += (s, e) => { if (!Filling) _ = SetLimiter(limiter.IsChecked == true); };
            guard.IsCheckedChanged += (s, e) => { if (!Filling) _ = SetGuard(guard.IsChecked == true); };
            OnPick(ceiling, i => { core.SafeMaster.CeilingDb = ceilings[i]; core.SafeMaster.Save(registry); Refresh(); });
            OnPick(threshold, i => { core.SafeMaster.ClipThresholdDb = thresholds[i]; core.SafeMaster.Save(registry); Refresh(); });

            OnPick(output, i => Choose("AudioDriver", outputs[i], "Settings"));
            OnPick(input, i => Choose("Input", inputs[i], PortAudioDriver.Section));
            OnPick(rate, i => Choose("SampleRate", Rates[i], PortAudioDriver.Section));
            OnPick(buffer, i => Choose("BufferSize", Buffers[i], PortAudioDriver.Section));
            OnPick(threads, i => Choose("AudioThreads", i + 1, "Settings"));
            OnPick(algorithm, i => Choose("WorkAlgorithm", i, "Settings"));
        }

        public override string Title => "Audio";

        public ComboBox OutputBox => output;
        public ComboBox InputBox => input;
        public ComboBox RateBox => rate;
        public ComboBox BufferBox => buffer;
        public ComboBox ThreadsBox => threads;
        public ComboBox AlgorithmBox => algorithm;
        public CheckBox LimiterBox => limiter;
        public ComboBox CeilingBox => ceiling;
        public ComboBox ThresholdBox => threshold;
        public CheckBox GuardBox => guard;
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
                output.ItemsSource = outputs.Select(n => listed.Contains(n) || !devicesListed ? n : n + " (not connected)").ToList();
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

                threads.ItemsSource = Enumerable.Range(1, 8).Select(n => n.ToString()).ToList();
                threads.SelectedIndex = Math.Clamp(registry.Read("AudioThreads", DefaultThreads, "Settings"), 1, 8) - 1;
                algorithm.ItemsSource = Algorithms;
                algorithm.SelectedIndex = Math.Clamp(registry.Read("WorkAlgorithm", DefaultAlgorithm, "Settings"), 0, Algorithms.Length - 1);

                status.Text = DeviceStatus(core);

                var safe = core.SafeMaster;
                limiter.IsChecked = safe.LimiterOn;
                ceilings = Ceilings.Append(safe.CeilingDb).Distinct().OrderByDescending(v => v).ToList();
                ceiling.ItemsSource = ceilings.Select(Dbfs).ToList();
                ceiling.SelectedIndex = ceilings.IndexOf(safe.CeilingDb);
                ceiling.IsEnabled = safe.LimiterOn;
                thresholds = Thresholds.Append(safe.ClipThresholdDb).Distinct().OrderBy(v => v).ToList();
                threshold.ItemsSource = thresholds.Select(Dbfs).ToList();
                threshold.SelectedIndex = thresholds.IndexOf(safe.ClipThresholdDb);
                guard.IsChecked = MachineGuard.Enabled;
            }
            finally
            {
                Filling = false;
            }
        }

        static string Dbfs(double db) => db == 0 ? "0 dBFS" : $"{db:+0.##;−0.##} dBFS";

        /// <summary>The safety limiter on or off; off asks first.</summary>
        async Task SetLimiter(bool on)
        {
            var safe = core.SafeMaster;
            if (!on && safe.LimiterOn)
            {
                Refresh();      // stays on until confirmed
                var answer = await Dialogs.Ask("Turn the safety limiter off? Then nothing stops a loud or broken signal from reaching your " +
                                               "speakers or headphones at full scale and beyond.", "Safety limiter", UserButtons.YesNo, DialogChoices.TurnOff);
                if (answer != UserAnswer.Yes) return;
            }
            safe.LimiterOn = on;
            safe.Save(registry);
            Refresh();
        }

        /// <summary>The guard on every machine on or off (this song only); off asks first.</summary>
        async Task SetGuard(bool on)
        {
            if (!on && MachineGuard.Enabled)
            {
                Refresh();
                var answer = await Dialogs.Ask("Turn the machine guard off for this song? A machine that goes wild (NaN, infinity or more than " +
                                               "+60 dBFS) then plays on into the rest of the song; only the safety limiter stands between it and " +
                                               "your speakers. A new or opened song turns the guard on again.", "Machine guard", UserButtons.YesNo, DialogChoices.TurnOff);
                if (answer != UserAnswer.Yes) return;
            }
            MachineGuard.Enabled = on;
            Refresh();
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
            if (engine.SelectedInDevice != null) text += $"; input from {engine.SelectedInDevice.Name}";
            return text + ".";
        }

        void Choose<T>(string key, T value, string section)
        {
            registry.Write(key, value, section);
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
            Reopen(core, device);
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
                return core.AudioEngine?.AudioDevices().Where(d => d.Type == Audio.Engine.AudioEngine.AudioOutType.PortAudio).Select(d => d.Name).ToList()
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
