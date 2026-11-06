// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): the keyboard under the windows of machines that play notes,
// headless. It shows under a generator's editor, parameter window and manifest editor, not an effect's or a controller's;
// a click plays the written pitch, heard by YIN in a render, on a machine that takes MIDI (Neo Poly) and on ones that take
// notes only in their Note track (the tests' Keys Test, and Pedal SH101 while it takes no MIDI); where the key is struck is
// the velocity; every note ends (released anywhere,
// the pointer leaving the window, the window closing or losing focus, the keyboard hidden); the toggle hides and shows it
// and persists per machine type, under the Preferences default; Space stays the transport's with the keys focused; keys
// light up for what the machine plays from any source; an armed track records what it plays.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Analysis;
using NeoBuzz.Editor;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Keyboards;
using ReBuzz.Studio.Platform;

namespace ReBuzz.Studio.Tests
{
    // Inside the namespace: the engine's ReBuzz.Control namespace would hide Avalonia's Control.
    using Control = Avalonia.Controls.Control;

    public class MachineKeyboardTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static int Rate => ReBuzzCore.masterInfo.SamplesPerSec;

        static MachineCore Add(string gear, string name, bool toMaster = true)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.4f, 0.4f);
            var m = Core.SongCore.MachinesList.FirstOrDefault(x => x.Name == name);
            Assert.That(m, Is.Not.Null, $"{gear} loads");
            if (toMaster && m!.DLL.Info.Type != MachineType.Master)
            {
                var master = Core.SongCore.MachinesList.First(x => x.DLL.Info.Type == MachineType.Master);
                if (!m.Outputs.Any(o => o.Destination == master))
                    Core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            }
            return m!;
        }

        static void Remove(params MachineCore[] machines)
        {
            foreach (var m in machines.Where(m => m != null))
            {
                Core.UiHost.ReleaseMachine(m);
                Core.SongCore.DeleteMachines(new[] { m });
            }
            Dispatcher.UIThread.RunJobs();
        }

        static void Settle() => MachineViewTests.Settle();

        static Window Gui(MachineCore m)
        {
            Core.UiHost.OpenGui(m, null);
            var w = StudioMachineUiHost.GuiWindowOf(m) ?? StudioMachineUiHost.EditorWindowOf(m) ?? StudioMachineUiHost.ParameterWindowOf(m);
            Assert.That(w, Is.Not.Null, $"{m.Name}: a window opened");
            Settle();
            w!.CaptureRenderedFrame();
            return w;
        }

        static Window Parameters(MachineCore m)
        {
            Core.UiHost.OpenParameterWindow(m, null);
            var w = StudioMachineUiHost.ParameterWindowOf(m);
            Assert.That(w, Is.Not.Null);
            Settle();
            w!.CaptureRenderedFrame();
            return w;
        }

        static MachineKeyboard KeyboardOf(Window w) => StudioMachineUiHost.KeyboardOf(w);

        /// <summary>A point on a key, in the window: across its middle, <paramref name="depth"/> of the way down it.</summary>
        static Point On(Window w, MachineKeyboard kb, int note, double depth = 0.8)
        {
            var key = kb.Keys.ElementOf(note);
            Assert.That(key, Is.Not.Null, $"{KeyboardFace.NoteName(note)} is on the keyboard ({KeyboardFace.NoteName(kb.Keys.Layout.First)}–{KeyboardFace.NoteName(kb.Keys.Layout.Last)})");
            return key!.TranslatePoint(new Point(key.Bounds.Width / 2, key.Bounds.Height * depth), w)!.Value;
        }

        // ------------------------------------------------------------------ audio, offline

        static WorkManager work;

        /// <summary>Renders <paramref name="seconds"/> of the master's output (no audio device here), then runs the UI's queue.</summary>
        static float[] Render(double seconds)
        {
            work ??= new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            int frames = (int)Math.Ceiling(seconds * Rate);
            var mono = new float[frames];
            var buffer = new float[512];
            lock (ReBuzzCore.AudioLock)
            {
                for (int at = 0; at < frames; at += 256)
                {
                    work.MainAudioFillBuffer(buffer, 0, 512);
                    for (int i = 0; i < 256 && at + i < frames; i++) mono[at + i] = buffer[2 * i];
                }
            }
            Dispatcher.UIThread.RunJobs();
            return mono;
        }

        static double Rms(float[] x, int from = 0)
        {
            double s = 0;
            for (int i = from; i < x.Length; i++) s += x[i] * (double)x[i];
            return Math.Sqrt(s / Math.Max(1, x.Length - from));
        }

        static double Db(double r) => 20 * Math.Log10(Math.Max(1e-12, r));

        /// <summary>The pitch heard (MIDI, fractional) over the second half of a render, by YIN.</summary>
        static double PitchOf(float[] audio)
        {
            var tail = audio.Skip(audio.Length / 3).ToArray();
            var track = Yin.Track(tail, Rate);
            Assert.That(track.VoicedShare, Is.GreaterThan(0.5), "a pitch is heard");
            return track.MedianMidi;
        }

        /// <summary>Renders until the machine is quiet (its release over), up to a few seconds.</summary>
        static double QuietAfter(double seconds = 4)
        {
            double level = 0;
            for (double t = 0; t < seconds; t += 0.25)
            {
                level = Db(Rms(Render(0.25)));
                if (level < -70) break;
            }
            return level;
        }

        // ------------------------------------------------------------------ where it shows

        [AvaloniaTest]
        public void TheKeyboardShowsUnderGeneratorsAndNotUnderEffectsOrControllers()
        {
            MachineCore poly = null, verb = null, sh101 = null, eq = null, lfo = null, drums = null, juno = null;
            try
            {
                poly = Add("Neo Poly", "Kb Poly");
                var editor = Gui(poly);
                var kb = KeyboardOf(editor);
                Assert.That(kb, Is.Not.Null, "Neo Poly's editor has the keyboard");
                Assert.That(kb!.Shown, Is.True, "shown by default");
                Assert.That(kb.BadgeText, Is.EqualTo("MIDI"));
                var content = StudioMachineUiHost.ContentOf(editor)!;
                Assert.That(content.GetType().Name, Is.EqualTo("NeoPolyEditor"));
                Assert.That(editor.ClientSize.Height, Is.EqualTo(content.Bounds.Height + MachineKeyboard.ExpandedHeight).Within(1),
                    "the window grew by the keyboard's height (the editor keeps its own)");
                Assert.That(kb.Keys.Layout.Octaves, Is.GreaterThanOrEqualTo(4), $"a wide editor gets more octaves ({editor.ClientSize.Width:0} px)");
                Assert.That(kb.Keys.Layout.Contains(60), Is.True, "around middle C");
                Assert.That(AutomationProperties.GetName(kb.Keys.ElementOf(61)!), Is.EqualTo("C#3"), "keys are named for screen readers (as Preferences name notes: C3 is 60 by default)");
                Studio.Screenshot(editor, "keyboard-neo-poly-editor");

                verb = Add("Neo Verb", "Kb Verb");
                Assert.That(KeyboardOf(Gui(verb)), Is.Null, "an effect has none");
                eq = Add("Pedal EQ", "Kb EQ");
                Assert.That(KeyboardOf(Parameters(eq)), Is.Null, "an effect's parameter window has none");
                lfo = Add("Pedal LFO", "Kb LFO", toMaster: false);
                Assert.That(KeyboardOf(Parameters(lfo)), Is.Null, "a controller has none");

                sh101 = Add("Pedal SH101", "Kb SH101");
                var parameters = Parameters(sh101);
                var pk = KeyboardOf(parameters);
                Assert.That(pk, Is.Not.Null, "a generic parameter window has it");
                Assert.That(pk!.BadgeText, Is.EqualTo(pk.Player.Route.Input == KeyboardInput.Midi ? "MIDI" : "Note tracks"), "it says how the machine gets the notes");
                Assert.That(pk.Keys.Layout.Octaves, Is.EqualTo(2), "a narrow window: two octaves");
                Assert.That(parameters.ClientSize.Width, Is.GreaterThanOrEqualTo(KeyboardLayout.MinWidth));
                Studio.Screenshot(parameters, "keyboard-parameter-window");
                // Its manifest editor too (a double click opens it: curated).
                Assert.That(Core.UiHost.OpenEditor(sh101), Is.True);
                Settle();
                Assert.That(KeyboardOf(StudioMachineUiHost.EditorWindowOf(sh101)!), Is.Not.Null, "its manifest editor has it");

                juno = Add("Keys Test", "Kb Tracks");
                var tw = Parameters(juno);
                Assert.That(KeyboardOf(tw)!.BadgeText, Is.EqualTo("Note tracks"), "a machine that takes notes only in its Note track");
                Assert.That(KeyboardOf(tw)!.Player.Route.Input, Is.EqualTo(KeyboardInput.Tracks));

                drums = Add("Neo Drums", "Kb Drums");
                var dw = Gui(drums);
                var dk = KeyboardOf(dw)!;
                Assert.That(dk.Keys.Layout.First, Is.EqualTo(36), "a drum machine starts at its drums (C2)");
                Assert.That(dk.Keys.ElementOf(38)!.Drum, Is.EqualTo("Snare"));
                Assert.That(AutomationProperties.GetName(dk.Keys.ElementOf(38)!), Is.EqualTo("D1, Snare"));
                Assert.That(dk.Keys.ElementOf(42)!.Drum, Is.EqualTo("Closed hat"));
                Assert.That(dk.Keys.ElementOf(53)!.Drum, Is.Null, "F3 plays no drum");
                Studio.Screenshot(dw, "keyboard-neo-drums-editor");
            }
            finally
            {
                Remove(poly, verb, sh101, eq, lfo, drums, juno);
            }
        }

        [AvaloniaTest]
        public void NeoSamplersKitModeNamesItsKeysByTheirSamples()
        {
            var sampler = Add("Neo Sampler", "Kb Sampler");
            try
            {
                var w = Gui(sampler);
                var kb = KeyboardOf(w)!;
                Assert.That(kb.Keys.Elements.All(e => e.Drum == null), Is.True, "Instrument mode: no names");
                var mode = sampler.ParameterGroups[1].Parameters.First(p => p.Name == "Mode");
                var zone = sampler.ParameterGroups[1].Parameters.First(p => p.Name == "Z1 wave");
                var builtIn = Enumerable.Range(zone.MinValue, zone.MaxValue - zone.MinValue + 1).First(v => zone.DescribeValue(v) == "Built-in Kick");
                mode.SetValue(0, 1);
                zone.SetValue(0, builtIn);
                Render(0.05);                                   // the engine takes them at its tick
                for (int i = 0; i < 16; i++) kb.Poll();          // and the keyboard asks now and then
                Assert.That(kb.Keys.NameOf(36), Is.EqualTo("Kick"), "Kit mode: zone 1 on Kit base (C2) by its sample");
            }
            finally
            {
                Remove(sampler);
            }
        }

        // ------------------------------------------------------------------ playing

        [AvaloniaTest]
        [TestCase("Neo Poly", 69, "Midi")]          // A4, 440 Hz: MIDI
        [TestCase("Keys Test", 57, "Tracks")]       // A3, 220 Hz: its Note track
        [TestCase("Pedal SH101", 57, "")]           // A3: its Note track today, MIDI once it takes it (machines/pedal-synths)
        public void AClickPlaysTheWrittenPitch(string gear, int note, string how)
        {
            var m = Add(gear, "Kb Pitch " + gear);
            try
            {
                var w = gear.StartsWith("Neo") ? Gui(m) : Parameters(m);
                var kb = KeyboardOf(w)!;
                var input = how.Length > 0 ? Enum.Parse<KeyboardInput>(how) : kb.Player.Route.Input;
                Assert.That(kb.Player.Route.Input, Is.EqualTo(input));
                TestContext.Out.WriteLine($"{gear}: played by {input}");
                QuietAfter();
                var at = On(w, kb, note);
                w.MouseDown(at, MouseButton.Left);
                Assert.That(kb.Player.IsHeld(note), Is.True, "the key is held");
                var audio = Render(0.6);
                double heard = PitchOf(audio);
                TestContext.Out.WriteLine($"{gear}: {KeyboardFace.NoteName(note)} clicked, heard MIDI {heard:0.00} ({440 * Math.Pow(2, (heard - 69) / 12):0.0} Hz), {Db(Rms(audio)):0.0} dBFS");
                Assert.That(heard, Is.EqualTo(note).Within(0.15), $"{gear} plays {KeyboardFace.NoteName(note)} ({440 * Math.Pow(2, (note - 69) / 12.0):0.0} Hz)");
                Assert.That(Db(Rms(audio)), Is.GreaterThan(-40), "it sounds");
                if (input == KeyboardInput.Tracks)
                {
                    var noteParameter = m.ParameterGroups[2].Parameters.First(p => p.Type == ParameterType.Note);
                    Assert.That(noteParameter.GetValue(0), Is.EqualTo(BuzzNote.FromMIDINote(note)), "on its first track, as notes_write writes it");
                }

                w.MouseUp(at, MouseButton.Left);
                Assert.That(kb.Player.Keys, Is.Empty, "released");
                Assert.That(QuietAfter(), Is.LessThan(-60), "the note ends");
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void TheStrikesHeightIsItsVelocity()
        {
            MachineCore poly = null, fm = null;
            try
            {
                poly = Add("Neo Poly", "Kb Vel Poly");
                var w = Gui(poly);
                var kb = KeyboardOf(w)!;
                var levels = new List<double>();
                var sent = new List<int>();
                foreach (double depth in new[] { 0.06, 0.5, 0.95 })
                {
                    QuietAfter();
                    var at = On(w, kb, 60, depth);
                    w.MouseDown(at, MouseButton.Left);
                    sent.Add(kb.Player.VelocityOf(60));
                    var audio = Render(0.4);
                    levels.Add(Db(Rms(audio, audio.Length / 2)));
                    kb.Poll();
                    Assert.That(kb.Keys.ElementOf(60)!.Look.Velocity, Is.EqualTo(sent[^1]), "the key shows how hard it was struck");
                    w.MouseUp(at, MouseButton.Left);
                }
                TestContext.Out.WriteLine($"Neo Poly: velocities {string.Join(", ", sent)} → {string.Join(", ", levels.Select(l => l.ToString("0.0")))} dBFS");
                Assert.That(sent[0], Is.LessThan(25), "near the top: soft");
                Assert.That(sent[1], Is.InRange(60, 80), "half way: medium");
                Assert.That(sent[2], Is.GreaterThan(115), "near the bottom: loud");
                Assert.That(levels[2], Is.GreaterThan(levels[0] + 3), "and the machine plays it louder");

                // A machine that takes notes in its tracks gets the velocity beside the note, scaled to its parameter's range
                // (Keys Test: Velocity 1–127, a sine at velocity / 127 of its level).
                fm = Add("Keys Test", "Kb Vel Tracks");
                var pw = Parameters(fm);
                var pk = KeyboardOf(pw)!;
                var velocity = fm.ParameterGroups[2].Parameters.First(p => p.Name == "Velocity");
                var top = On(pw, pk, 60, 0.06);
                pw.MouseDown(top, MouseButton.Left);
                int soft = velocity.GetValue(0);
                var softAudio = Render(0.3);
                pw.MouseUp(top, MouseButton.Left);
                QuietAfter();
                var bottom = On(pw, pk, 62, 0.95);
                pw.MouseDown(bottom, MouseButton.Left);
                int loud = velocity.GetValue(0);
                var loudAudio = Render(0.3);
                pw.MouseUp(bottom, MouseButton.Left);
                Assert.That(soft, Is.LessThan(25));
                Assert.That(loud, Is.GreaterThan(115));
                double gained = Db(Rms(loudAudio, loudAudio.Length / 2)) - Db(Rms(softAudio, softAudio.Length / 2));
                TestContext.Out.WriteLine($"Keys Test: velocities {soft} and {loud}, {gained:0.0} dB apart");
                Assert.That(gained, Is.EqualTo(20 * Math.Log10(loud / (double)soft)).Within(0.5), "the machine plays the velocity it got");
            }
            finally
            {
                Remove(poly, fm);
            }
        }

        [AvaloniaTest]
        public void DraggingGlidesFromKeyToKey()
        {
            var m = Add("Neo Poly", "Kb Glide");
            try
            {
                var w = Gui(m);
                var kb = KeyboardOf(w)!;
                var monitor = m.NoteMonitor!;
                var bytes = new byte[128];
                w.MouseDown(On(w, kb, 60), MouseButton.Left);
                w.MouseMove(On(w, kb, 62));
                w.MouseMove(On(w, kb, 64));
                Assert.That(kb.Player.Keys, Is.EqualTo(new[] { 64 }), "one note, the key under the pointer");
                monitor.Read(bytes);
                Assert.That(Enumerable.Range(0, 128).Where(k => bytes[k] > 0), Is.EqualTo(new[] { 64 }), "the machine got the offs of the keys it left");
                double pitch = PitchOf(Render(0.5));
                Assert.That(pitch, Is.EqualTo(64).Within(0.2), "it plays E4");
                w.MouseUp(On(w, kb, 64), MouseButton.Left);
                monitor.Read(bytes);
                Assert.That(bytes.All(b => b == 0), Is.True);
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void TrackMachinesPlayAChordAcrossTheirTracksAndAMonoSynthTheLastKeyHeld()
        {
            MachineCore juno = null, sh101 = null;
            try
            {
                juno = Add("Keys Test", "Kb Chord");
                juno.TrackCount = 3;                // a voice per track: three for a triad
                Parameters(juno);
                var player = new KeyboardPlayer(juno);
                Assert.That(player.Route.Input, Is.EqualTo(KeyboardInput.Tracks));
                var note = juno.ParameterGroups[2].Parameters.First(p => p.Type == ParameterType.Note);
                foreach (int k in new[] { 60, 64, 67 }) Assert.That(player.On(k, 100), Is.True);
                Assert.That(Enumerable.Range(0, 3).Select(t => note.GetValue(t)), Is.EqualTo(new[] { 60, 64, 67 }.Select(BuzzNote.FromMIDINote)),
                    "a note per track, the lowest free");
                Render(0.3);
                var playing = (int[])juno.ManagedMachine.GetType().GetProperty("Playing")!.GetValue(juno.ManagedMachine)!;
                Assert.That(playing.Take(3), Is.All.EqualTo(100), "the machine plays all three, each at its velocity");
                player.Off(64);
                Assert.That(note.GetValue(1), Is.EqualTo(BuzzNote.Off), "a release lets go of its track");
                player.AllOff();
                Assert.That(new[] { note.GetValue(0), note.GetValue(2) }, Is.All.EqualTo(BuzzNote.Off), "every note ends");

                sh101 = Add("Keys Test", "Kb Mono");          // one track: one note at a time
                Assert.That(sh101.TrackCount, Is.EqualTo(1));
                var mono = new KeyboardPlayer(sh101);
                var monoNote = sh101.ParameterGroups[2].Parameters.First(p => p.Type == ParameterType.Note);
                mono.On(60, 100);
                mono.On(64, 100);
                Assert.That(monoNote.GetValue(0), Is.EqualTo(BuzzNote.FromMIDINote(64)), "one track: the newer key takes it");
                mono.Off(64);
                Assert.That(monoNote.GetValue(0), Is.EqualTo(BuzzNote.FromMIDINote(60)), "the key still held comes back");
                mono.Off(60);
                Assert.That(monoNote.GetValue(0), Is.EqualTo(BuzzNote.Off));
            }
            finally
            {
                Remove(juno, sh101);
            }
        }

        // ------------------------------------------------------------------ every note ends

        [AvaloniaTest]
        public void EveryNoteEnds()
        {
            var m = Add("Neo Poly", "Kb Ends");
            Window other = null;
            try
            {
                var w = Gui(m);
                var kb = KeyboardOf(w)!;
                var monitor = m.NoteMonitor!;
                var bytes = new byte[128];
                bool Silent()
                {
                    monitor.Read(bytes);
                    return bytes.All(b => b == 0) && kb.Player.Keys.Count == 0;
                }

                // Released outside the window: the pointer is the keyboard's while pressed.
                w.MouseDown(On(w, kb, 60), MouseButton.Left);
                Assert.That(Silent(), Is.False);
                w.MouseUp(new Point(-200, -200), MouseButton.Left);
                Assert.That(Silent(), Is.True, "released outside the window");

                // Dragged out of the window: the note ends there.
                w.MouseDown(On(w, kb, 62), MouseButton.Left);
                w.MouseMove(new Point(w.ClientSize.Width + 50, w.ClientSize.Height + 50));
                Assert.That(Silent(), Is.True, "the pointer left the window");
                w.MouseUp(new Point(w.ClientSize.Width + 50, w.ClientSize.Height + 50), MouseButton.Left);

                // Hidden while held (the toggle, Ctrl+K).
                w.MouseDown(On(w, kb, 64), MouseButton.Left);
                kb.Toggle.Press();
                Assert.That(Silent(), Is.True, "hiding the keyboard ends its notes");
                kb.Toggle.Press();
                Settle();
                w.MouseUp(new Point(5, 5), MouseButton.Left);

                // Another window taking the focus.
                w.MouseDown(On(w, kb, 65), MouseButton.Left);
                other = new Window { Width = 100, Height = 100 };
                other.Show();
                other.Activate();
                Settle();
                Assert.That(Silent(), Is.True, "the window lost the focus");
                other.Close();
                other = null;
                w.Activate();
                Settle();
                w.MouseUp(new Point(5, 5), MouseButton.Left);

                // The window closing.
                w = Gui(m);
                kb = KeyboardOf(w)!;
                w.MouseDown(On(w, kb, 67), MouseButton.Left);
                Assert.That(Silent(), Is.False);
                w.Close();
                Settle();
                Assert.That(Silent(), Is.True, "the window closed");
            }
            finally
            {
                other?.Close();
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ toggling

        [AvaloniaTest]
        public void TheToggleHidesAndShowsItForThatMachineTypeAndPersists()
        {
            // Preferences' setting, as the page sets it (and saves it): its value as text, the change told.
            var setting = Global.GeneralSettings.List.First(s => s.Name == nameof(BuzzGUI.Common.Settings.GeneralSettings.MachineKeyboard));
            string before = setting.Value;
            MachineCore a = null, b = null, juno = null;
            using var restore = Studio.AfterTest(() => setting.Value = before);
            try
            {
                a = Add("Pedal SH101", "Kb Toggle A");
                b = Add("Pedal SH101", "Kb Toggle B");
                juno = Add("Pedal Juno106", "Kb Toggle Juno");
                var wa = Parameters(a);
                var wb = Parameters(b);
                var wj = Parameters(juno);
                var ka = KeyboardOf(wa)!;
                double shownHeight = wa.ClientSize.Height;
                var contentHeight = StudioMachineUiHost.ContentOf(wa)!.Bounds.Height;

                ka.Toggle.Press();
                Settle();
                Assert.That(ka.Shown, Is.False);
                Assert.That(ka.Keys.IsVisible, Is.False, "the keys hide; the strip with the toggle stays");
                Assert.That(wa.ClientSize.Height, Is.EqualTo(contentHeight + MachineKeyboard.CollapsedHeight).Within(1), "the window shrinks by the keys");
                Assert.That(StudioMachineUiHost.ContentOf(wa)!.Bounds.Height, Is.EqualTo(contentHeight).Within(1), "the GUI keeps its size");
                Assert.That(KeyboardSettings.Shown(a.DLL), Is.False, "kept for the machine type");
                Assert.That(KeyboardOf(wb)!.Shown, Is.False, "the type's other windows follow");
                Assert.That(KeyboardOf(wj)!.Shown, Is.True, "other machine types keep the default");
                Studio.Screenshot(wa, "keyboard-hidden-parameter-window");

                // A new window of the type opens hidden; Ctrl+K (⌘K) shows it again.
                Core.UiHost.ReleaseMachine(b);
                var wb2 = Parameters(b);
                Assert.That(KeyboardOf(wb2)!.Shown, Is.False, "remembered");
                var command = OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;
                wb2.KeyPressQwerty(PhysicalKey.K, command);
                wb2.KeyReleaseQwerty(PhysicalKey.K, command);
                Settle();
                Assert.That(KeyboardOf(wb2)!.Shown, Is.True, "Ctrl+K shows it");
                Assert.That(ka.Shown, Is.True);
                Assert.That(KeyboardSettings.Choice(a.DLL), Is.Null, "back to the default: no choice kept");
                Assert.That(wa.ClientSize.Height, Is.EqualTo(shownHeight).Within(1), "the window grows back");
                Assert.That(ReBuzz.Studio.Shell.MainWindow.ShortcutsText(mac: true), Does.Contain("⌘K\tshow / hide the keyboard"), "Help ▸ Keyboard shortcuts lists it");

                // Preferences' default: hidden for every type without a choice of its own.
                setting.Value = "False";
                Settle();
                Assert.That(ka.Shown, Is.False, "the default off hides it");
                Assert.That(KeyboardOf(wj)!.Shown, Is.False);
                KeyboardOf(wj)!.Toggle.Press();
                Assert.That(KeyboardSettings.Choice(juno.DLL), Is.True, "a type shown against the default keeps that choice");
                setting.Value = "True";
                Settle();
                Assert.That(ka.Shown, Is.True, "the default on again shows the types without a choice");
                Assert.That(KeyboardOf(wj)!.Shown, Is.True);
            }
            finally
            {
                foreach (var m in new[] { a, juno }.Where(m => m != null)) KeyboardSettings.Set(m.DLL, KeyboardSettings.Default);
                Remove(a, b, juno);
            }
        }

        [AvaloniaTest]
        public void TheKeysAreNamedAsPreferencesNameNotes()
        {
            // Fork (ux-overhaul.md §3.5): the keys, the octave and the readout name notes as the pattern editor and the piano
            // roll do (Preferences ▸ General ▸ Note names), and follow a change at once.
            var naming = NoteNames.Naming;
            MachineCore poly = null;
            try
            {
                NoteNames.Naming = NoteNaming.C3;
                poly = Add("Neo Poly", "Kb Names");
                var kb = KeyboardOf(Gui(poly))!;
                int first = kb.Keys.Layout.First;
                Assert.That(kb.OctaveText, Is.EqualTo(NoteNames.Name(first, NoteNaming.C3)));
                Assert.That(AutomationProperties.GetName(kb.Keys.ElementOf(60)!), Is.EqualTo("C3"));
                NoteNames.Naming = NoteNaming.Scientific;
                Settle();
                Assert.That(kb.OctaveText, Is.EqualTo(NoteNames.Name(first, NoteNaming.Scientific)), "the octave follows");
                Assert.That(AutomationProperties.GetName(kb.Keys.ElementOf(60)!), Is.EqualTo("C4"), "and the keys");
                NoteNames.Naming = NoteNaming.Buzz;
                Settle();
                Assert.That(AutomationProperties.GetName(kb.Keys.ElementOf(61)!), Is.EqualTo("C#5"));
                Assert.That(KeyboardFace.Readout(60, null, 96), Is.EqualTo("C-5 · velocity 96"));
            }
            finally
            {
                NoteNames.Naming = naming;
                Remove(poly);
            }
        }

        [AvaloniaTest]
        public void SongsKeepTheGuisSizeWithoutTheKeyboard()
        {
            var m = Add("Pedal SH101", "Kb Placement");
            try
            {
                Core.UiHost.OpenParameterWindow(m, new WindowPlacement(40, 40, 520, 300));
                Settle();
                var w = StudioMachineUiHost.ParameterWindowOf(m)!;
                Assert.That(w.ClientSize.Height, Is.EqualTo(300 + MachineKeyboard.ExpandedHeight).Within(1), "a song's window opens at its size, the keyboard under it");
                Assert.That(Core.UiHost.GetWindowState(m).ParameterWindow.Height, Is.EqualTo(300).Within(1), "and is saved at it");
                var kb = KeyboardOf(w)!;
                kb.Toggle.Press();
                Settle();
                Assert.That(w.ClientSize.Height, Is.EqualTo(300 + MachineKeyboard.CollapsedHeight).Within(1), "a window of its own size shrinks by the keys");
                Assert.That(Core.UiHost.GetWindowState(m).ParameterWindow.Height, Is.EqualTo(300).Within(1));
                kb.Toggle.Press();
            }
            finally
            {
                KeyboardSettings.Set(m.DLL, KeyboardSettings.Default);
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void AnEditorTallerThanTheScreenHoldsScrollsRatherThanBeingSquashed()
        {
            // Neo Wave's editor is about 1000 px high: with the keyboard it is more than the 1080 px test screen holds (a
            // laptop's or a CI runner's screen is smaller still). The editor keeps its own layout and scrolls above the keys.
            var m = Add("Neo Wave", "Kb Tall");
            try
            {
                var w = Gui(m);
                var content = (Control)StudioMachineUiHost.ContentOf(w)!;
                var view = (Control)MachineKeyboardFrame.TopOf(w.Content)!;
                var kb = KeyboardOf(w)!;
                double shown = view.Bounds.Height, height = content.Bounds.Height;
                Assert.That(shown + kb.ExtraHeight, Is.EqualTo(w.ClientSize.Height).Within(1), "the editor's view and the keys fill the window");
                Assert.That(height, Is.GreaterThan(shown + 1), "the screen can't hold the whole editor and the keys");
                Assert.That(view, Is.InstanceOf<ScrollViewer>(), "the editor scrolls");
                Assert.That(((ScrollViewer)view).Extent.Height, Is.EqualTo(height).Within(1), "all of it can be scrolled to");
                content.Measure(Size.Infinity);
                Assert.That(height, Is.EqualTo(content.DesiredSize.Height).Within(1), "at its own height: never squashed");
                ((ScrollViewer)view).Offset = new Vector(0, height);
                Settle();
                Assert.That(((ScrollViewer)view).Offset.Y, Is.EqualTo(height - shown).Within(1), "scrolled to its bottom");
                var top = kb.TranslatePoint(new Point(0, 0), w)!.Value.Y;
                var bottom = kb.TranslatePoint(new Point(0, kb.Bounds.Height), w)!.Value.Y;
                Assert.That(top, Is.GreaterThanOrEqualTo(shown - 1), "the keys stay under the editor");
                Assert.That(bottom, Is.LessThanOrEqualTo(w.ClientSize.Height + 1), "and in view");
                Studio.Screenshot(w, "keyboard-neo-wave-scrolled");
            }
            finally
            {
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ Space

        [AvaloniaTest]
        public void SpaceTogglesTheTransportWithTheKeysFocused()
        {
            var m = Add("Neo Poly", "Kb Space");
            using var restore = Studio.AfterTest(() => Core.Playing = false);
            try
            {
                var w = Gui(m);
                var kb = KeyboardOf(w)!;
                w.MouseDown(On(w, kb, 60), MouseButton.Left);
                w.MouseUp(On(w, kb, 60), MouseButton.Left);
                Assert.That(kb.Keys.IsFocused, Is.True, "a click puts the focus on the keys");
                Assert.That(Core.PlayRequested, Is.False);
                w.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                w.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Assert.That(Core.PlayRequested, Is.True, "Space plays");
                w.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
                w.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
                Assert.That(Core.PlayRequested, Is.False, "Space again stops");
                Assert.That(kb.Player.Keys, Is.Empty, "and the keyboard played nothing");
            }
            finally
            {
                Core.Playing = false;
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ lights and recording

        [AvaloniaTest]
        public void KeysLightUpForWhatTheMachinePlaysFromAnySource()
        {
            MachineCore poly = null, sh101 = null;
            try
            {
                poly = Add("Neo Poly", "Kb Lights Poly");
                var w = Gui(poly);
                var kb = KeyboardOf(w)!;
                // MIDI from elsewhere (a MIDI keyboard, a clip, the agent's midi_play): as the machine gets it.
                Core.MachineManager.SendMidiInput(poly, 0x90 | (64 << 8) | (90 << 16), false);
                kb.Poll();
                Assert.That(kb.Keys.ElementOf(64)!.Look.Velocity, Is.EqualTo(90), "E4 lights up, as hard as it was played");
                Assert.That(kb.Keys.ElementOf(64)!.Look.Pressed, Is.False, "not this keyboard's");
                Studio.Screenshot(w, "keyboard-lit-neo-poly");
                Core.MachineManager.SendMidiInput(poly, 0x80 | (64 << 8), false);
                kb.Poll();
                Assert.That(kb.Keys.ElementOf(64)!.Look.Velocity, Is.EqualTo(0), "and goes out");

                // A pattern's notes: its Note track as the tick sends it.
                sh101 = Add("Keys Test", "Kb Lights Tracks");
                var pw = Parameters(sh101);
                var pk = KeyboardOf(pw)!;
                var note = sh101.ParameterGroups[2].Parameters.First(p => p.Type == ParameterType.Note);
                note.SetValue(0, BuzzNote.FromMIDINote(57));
                Render(0.2);
                pk.Poll();
                Assert.That(pk.Keys.ElementOf(57)!.Look.Velocity, Is.GreaterThan(0), "A3 lights up while its track plays it");
                note.SetValue(0, BuzzNote.Off);
                Render(0.2);
                pk.Poll();
                Assert.That(pk.Keys.ElementOf(57)!.Look.Velocity, Is.EqualTo(0));
            }
            finally
            {
                Remove(poly, sh101);
            }
        }

        [AvaloniaTest]
        public void AnArmedTrackRecordsWhatItsKeyboardPlaysAndNoOtherOne()
        {
            MachineCore poly = null, other = null;
            SequenceCore seq = null, otherSeq = null;
            try
            {
                poly = Add("Neo Poly", "Kb Armed");
                other = Add("Neo Poly", "Kb Other");
                var song = Core.SongCore;
                song.AddSequence(poly, song.Sequences.Count);
                song.AddSequence(other, song.Sequences.Count);
                seq = song.SequencesList.First(s => s.Machine == poly);
                otherSeq = song.SequencesList.First(s => s.Machine == other);
                Core.SessionCore.SetArmed(seq, true);
                Core.SessionCore.SetArmed(otherSeq, true);
                var w = Gui(poly);
                var kb = KeyboardOf(w)!;
                w.MouseDown(On(w, kb, 62), MouseButton.Left);
                w.MouseUp(On(w, kb, 62), MouseButton.Left);
                // Capture MIDI (stopped): what the armed track was played.
                var played = Core.SessionCore.TakeCapturedWhileStopped(seq);
                Assert.That(played.Select(p => p.Data & 0xFFFF), Is.EqualTo(new[] { 0x90 | (62 << 8), 0x80 | (62 << 8) }), "the armed track keeps what it was played");
                Assert.That(Core.SessionCore.TakeCapturedWhileStopped(otherSeq), Is.Empty, "another armed machine's track doesn't: this keyboard is its machine's");
                var bytes = new byte[128];
                other.NoteMonitor?.Read(bytes);
                Assert.That(bytes.All(b => b == 0), Is.True);
            }
            finally
            {
                if (seq != null) Core.SessionCore.SetArmed(seq, false);
                if (otherSeq != null) Core.SessionCore.SetArmed(otherSeq, false);
                Remove(poly, other);
            }
        }
    }
}
