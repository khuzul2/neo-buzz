using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using AtmaFileSystem;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Analysis;
using ReBuzz.Core;
using ReBuzz.Core.Keyboards;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition (machine keyboard, docs/specs/studio-mvp.md §3.12): the keyboard under machine windows in the Windows app.
    /// <c>NeoBuzzLive.exe --agent --keyboard-shots</c> opens machines' windows through the WPF host, draws them (screenshots beside
    /// SessionViewTests', keyboard-wpf-*.png), plays the keyboard through its own pointer handling with the engine rendered
    /// offline, and reads back: a Neo synth's editor (MIDI) and a native machine's parameter window (its Note track, with its
    /// manifest's pitch offset, FSM Infector's +12) both play the written A4 at 440 Hz, heard here by YIN; a strike's height
    /// is its velocity; a glide plays one note; every note ends on release, the window hiding, the keyboard hidden and the
    /// pointer's capture lost; Space with the keys focused plays and stops the song; an effect has no keyboard; a drum
    /// machine names its keys. In process: the routes the engine chooses, and the player's note-offs.
    /// </summary>
    public class MachineKeyboardTests
    {
        static JsonObject? shots;
        static string? folder;

        /// <summary>Runs ReBuzz.exe --keyboard-shots once for the fixture.</summary>
        static JsonObject Shots()
        {
            if (shots != null) return shots;
            string bin = File.ReadLines(TestAssemblyDirectory.Value.AddFileName("ReBuzzLocation.txt").ToString()).First();
            string exe = Path.Combine(bin, ReBuzz.Product.WindowsExe);
            if (!File.Exists(exe)) Assert.Inconclusive($"{ReBuzz.Product.WindowsExe} not built ({exe})");
            folder = Path.Combine(Path.GetTempPath(), "rebuzz-keyboard-shots-" + Environment.ProcessId);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var psi = new ProcessStartInfo(exe, $"--agent --keyboard-shots \"{folder}\"")
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = bin,
            };
            using var process = Process.Start(psi)!;
            var stderr = new List<string>();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.Add(e.Data); };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
            if (!process.WaitForExit(TimeSpan.FromMinutes(4)))
            {
                process.Kill(true);
                Assert.Fail($"{ReBuzz.Product.WindowsExe} --keyboard-shots didn't finish in 4 minutes:\n" + string.Join("\n", stderr.TakeLast(40)));
            }
            string log = string.Join("\n", stderr);
            TestContext.Out.WriteLine(log);
            string json = Path.Combine(folder, "keyboard.json");
            Assert.That(File.Exists(json), Is.True, "keyboard.json written:\n" + log);
            // Screenshots where the tests' others go (published with the run).
            string screenshots = Environment.GetEnvironmentVariable("SESSION_SCREENSHOT_DIR") ?? TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(screenshots);
            foreach (var png in Directory.GetFiles(folder, "*.png")) File.Copy(png, Path.Combine(screenshots, Path.GetFileName(png)), true);
            shots = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
            TestContext.Out.WriteLine(shots.ToJsonString());
            return shots;
        }

        static JsonObject Case(string name)
        {
            var r = Shots()[name]?.AsObject();
            Assert.That(r, Is.Not.Null, name);
            Assert.That(r!["error"], Is.Null, $"{name}: {r.ToJsonString()}");
            return r;
        }

        /// <summary>A WAV --keyboard-shots wrote (32-bit float, mono): its samples and rate.</summary>
        static (float[] Samples, int Rate) ReadWav(string name)
        {
            var bytes = File.ReadAllBytes(Path.Combine(folder!, name));
            int rate = BitConverter.ToInt32(bytes, 24);
            int data = BitConverter.ToInt32(bytes, 40);
            var x = new float[data / 4];
            Buffer.BlockCopy(bytes, 44, x, 0, x.Length * 4);
            return (x, rate);
        }

        static double PitchOf(string wav)
        {
            var (x, rate) = ReadWav(wav);
            var tail = x.Skip(x.Length / 3).ToArray();
            var track = Yin.Track(tail, rate);
            double rms = Math.Sqrt(tail.Sum(v => v * (double)v) / Math.Max(1, tail.Length));
            TestContext.Out.WriteLine($"[notice] {wav}: MIDI {track.MedianMidi:0.00} ({track.MedianHz:0.0} Hz), voiced {track.VoicedShare:P0}, {20 * Math.Log10(Math.Max(1e-12, rms)):0.0} dBFS");
            Assert.That(track.VoicedShare, Is.GreaterThan(0.5), $"{wav}: a pitch is heard");
            return track.MedianMidi;
        }

        [Test]
        public void ANeoSynthsEditorHasTheKeyboardAndAClickPlaysTheWrittenPitch()
        {
            var r = Case("neo-poly");
            Assert.That(r["keyboard"]!.GetValue<string>(), Is.EqualTo("Midi"));
            Assert.That(r["shown"]!.GetValue<bool>(), Is.True);
            Assert.That(r["badge"]!.GetValue<string>(), Is.EqualTo("MIDI"));
            Assert.That(r["octaves"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(4), "a wide editor: more octaves");
            Assert.That(r["frame_height"]!.GetValue<double>(), Is.EqualTo(r["content_height"]!.GetValue<double>() + r["extra_height"]!.GetValue<double>()).Within(1),
                "the window is the editor and the keyboard under it");
            Assert.That(r["key_name"]!.GetValue<string>(), Does.Match(@"^C#\d$"), "keys are named for screen readers");
            Assert.That(r["held"]!.GetValue<bool>(), Is.True);
            Assert.That(PitchOf("keyboard-neo-poly-a4.wav"), Is.EqualTo(69).Within(0.15), "A4 (440 Hz)");
            Assert.That(r["held_after_release"]!.GetValue<int>(), Is.EqualTo(0));
            Assert.That(r["monitor_after_release"]!.GetValue<string>(), Is.Empty, "the machine got the note-off");
            Assert.That(r["quiet_db_after_release"]!.GetValue<double>(), Is.LessThan(-60), "the note ends");
            TestContext.Out.WriteLine($"[notice] Neo Poly in its window: velocities {r["velocity_top"]} (top) and {r["velocity_bottom"]} (bottom), " +
                                      $"{r["quiet_db_after_release"]} dBFS after the release, {r["octaves"]} octaves from {r["first"]}, keyboard {r["extra_height"]} px");
            Assert.That(r["velocity_top"]!.GetValue<int>(), Is.LessThan(25), "struck at the top: soft");
            Assert.That(r["velocity_bottom"]!.GetValue<int>(), Is.GreaterThan(115), "at the bottom: loud");
            Assert.That(r["glide_held"]!.GetValue<string>(), Is.EqualTo("64"), "a glide plays one note, the key under the pointer");
            Assert.That(r["glide_sounding"]!.GetValue<string>(), Is.EqualTo("64"));
        }

        [Test]
        public void EveryNoteEndsInReBuzz()
        {
            var r = Case("neo-poly");
            Assert.That(r["off_on_hide"]!.GetValue<bool>(), Is.True, "the window hidden (closed)");
            Assert.That(r["off_on_toggle"]!.GetValue<bool>(), Is.True, "the keyboard hidden");
            Assert.That(r["hidden_height"]!.GetValue<double>(), Is.EqualTo(NeoBuzz.Editor.MachineKeyboard.CollapsedHeight).Within(0.5), "hidden: the strip with the toggle stays");
            Assert.That(r["off_on_capture_lost"]!.GetValue<bool>(), Is.True, "the pointer's capture lost");
        }

        [Test]
        public void SpaceInAMachineWindowTogglesTheTransportWithTheKeysFocused()
        {
            var r = Case("neo-poly");
            Assert.That(r["keys_focused"]!.GetValue<bool>(), Is.True);
            Assert.That(r["space_plays"]!.GetValue<bool>(), Is.True, "Space plays");
            Assert.That(r["space_stops"]!.GetValue<bool>(), Is.True, "Space again stops");
        }

        [Test]
        public void ANativeMachinesParameterWindowPlaysItsNoteTrackAtTheWrittenPitch()
        {
            var r = Case("fsm-infector");
            Assert.That(r["keyboard"]!.GetValue<string>(), Is.EqualTo("Tracks"), "FSM Infector takes notes in its Note track");
            Assert.That(r["badge"]!.GetValue<string>(), Is.EqualTo("Note tracks"));
            Assert.That(r["offset"]!.GetValue<int>(), Is.EqualTo(12), "its manifest: it plays an octave low");
            Assert.That(r["octaves"]!.GetValue<int>(), Is.GreaterThanOrEqualTo(2));
            Assert.That(r["frame_height"]!.GetValue<double>(), Is.EqualTo(r["content_height"]!.GetValue<double>() + r["extra_height"]!.GetValue<double>()).Within(1));
            // A4 written: A5 sent (+12), heard at 440 Hz.
            Assert.That(r["note_value"]!.GetValue<int>(), Is.EqualTo(BuzzNote.FromMIDINote(69 + 12)), "the Note track gets A4 plus the offset");
            Assert.That(PitchOf("keyboard-fsm-infector-a4.wav"), Is.EqualTo(69).Within(0.25), "the written A4 (440 Hz) plays");
            Assert.That(r["note_value_after_release"]!.GetValue<int>(), Is.EqualTo(BuzzNote.Off), "released: an off on its track");
        }

        [Test]
        public void AnEffectHasNoKeyboardAndADrumMachineNamesItsKeys()
        {
            Assert.That(Case("jeskola-reverb")["keyboard"]!.GetValue<bool>(), Is.False);
            var drums = Case("neo-drums");
            Assert.That(drums["snare"]!.GetValue<string>(), Is.EqualTo("Snare"));
            Assert.That(drums["first"]!.GetValue<int>(), Is.EqualTo(36), "starts at its drums (C2)");
        }

        // ------------------------------------------------------------------ in process

        [Test]
        [Apartment(ApartmentState.STA)]
        public void TheWpfKeyboardPlaysANeoSynthByMidiAndEveryNoteEnds()
        {
            var poly = NeoMachineTests.Poly;
            if (!File.Exists(poly.BuiltPath)) Assert.Inconclusive($"{poly.Name} not built ({poly.BuiltPath})");
            using var driver = new Driver();
            driver.Gear.AddBuiltMachine(poly, AbsoluteFilePath.Value(poly.BuiltPath), true);
            driver.Start();
            var controller = new DynamicMachineController(poly.Name, "Keys Poly");
            driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(controller);
            var m = driver.MachineFor(controller);
            var master = driver.Core.SongCore.MachinesList.Single(x => x.DLL.Info.Type == MachineType.Master);
            Assert.That(KeyboardRoute.For(master).Shows, Is.False, "the Master has none");
            var route = KeyboardRoute.For(m);
            Assert.That(route.Input, Is.EqualTo(KeyboardInput.Midi), "a Neo synth takes MIDI");
            Assert.That((route.Offset, route.Lowest, route.Highest), Is.EqualTo((0, 0, 127)));

            m.NoteMonitor = new NoteMonitor();
            var bytes = new byte[NoteMonitor.Keys];
            string Sounding()
            {
                m.NoteMonitor.Read(bytes);
                return string.Join(",", Enumerable.Range(0, bytes.Length).Where(k => bytes[k] > 0));
            }
            // The WPF control under a stand-in GUI, laid out as a window would.
            var gui = new System.Windows.Controls.Border { Width = 640, Height = 200 };
            var frame = (FrameworkElement)NeoBuzz.Editor.MachineKeyboardFrame.Wrap(m, gui);
            var kb = NeoBuzz.Editor.MachineKeyboardFrame.Of(frame)!;
            Assert.That(kb, Is.Not.Null);
            try
            {
                double height = 200 + kb.ExtraHeight;
                frame.Measure(new Size(640, height));
                frame.Arrange(new Rect(0, 0, 640, height));
                frame.UpdateLayout();
                Assert.That(gui.ActualHeight, Is.EqualTo(200), "the GUI keeps its size");
                Assert.That(kb.Keys.Layout.Octaves, Is.EqualTo(3), "640 px: three octaves");
                NeoBuzz.Editor.NeoRect Key(int note) => kb.Keys.Layout.Rect(note);

                var a4 = Key(69);
                kb.Keys.Down(a4.X + a4.Width / 2, a4.Y + a4.Height * 0.8);
                Assert.That(Sounding(), Is.EqualTo("69"), "the machine got A4");
                var reader = driver.CreateAudioReader(256);
                int rate = ReBuzzCore.masterInfo.SamplesPerSec;
                var audio = new float[(int)(0.6 * rate)];
                for (int at = 0; at < audio.Length; at += 256)
                {
                    int n = Math.Min(256, audio.Length - at);
                    reader.Read(n);
                    for (int i = 0; i < n; i++) audio[at + i] = reader.Buffer[2 * i];
                }
                var track = Yin.Track(audio.Skip(audio.Length / 3).ToArray(), rate);
                TestContext.Out.WriteLine($"[notice] Neo Poly (in process): A4 pressed, heard MIDI {track.MedianMidi:0.00} ({track.MedianHz:0.0} Hz)");
                Assert.That(track.MedianMidi, Is.EqualTo(69).Within(0.15), "A4 (440 Hz)");
                SessionViewTests.Screenshot(frame, "keyboard-wpf-frame", 640, (int)Math.Ceiling(height));
                kb.Keys.Up();
                Assert.That(Sounding(), Is.Empty, "released");

                var c4 = Key(60);
                kb.Keys.Down(c4.X + c4.Width / 2, c4.Y + c4.Height * 0.8);
                kb.Keys.LetGo();
                Assert.That(Sounding(), Is.Empty, "the pointer's capture lost");
                kb.Keys.Down(c4.X + c4.Width / 2, c4.Y + c4.Height * 0.8);
                kb.Toggle.Press();
                Assert.That(Sounding(), Is.Empty, "the keyboard hidden");
                Assert.That(kb.Shown, Is.False);
                kb.Toggle.Press();
                Assert.That(kb.Shown, Is.True);
                var player = new KeyboardPlayer(m);
                player.On(60, 100);
                player.On(64, 100);
                player.On(67, 100);
                Assert.That(Sounding(), Is.EqualTo("60,64,67"));
                player.AllOff();
                Assert.That(Sounding(), Is.Empty, "AllOff lets go of every note");
            }
            finally
            {
                KeyboardSettings.Set(m.DLL, KeyboardSettings.Default);
                driver.Core.Playing = false;
            }
        }
    }
}
