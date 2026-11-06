// Fork (machine keyboard, docs/specs/studio-mvp.md §3.12): `ReBuzz.exe --agent --keyboard-shots <folder>`, the keyboard
// under machine windows as ReBuzz.exe shows it (WpfMachineUiHost), for ReBuzzTests on the Windows package run (the
// natives load only in ReBuzz.exe's engine, which the tests run as a process, as --editor-shots does). The engine starts
// headless as for --agent; machines are added and their windows opened; the keyboard is checked, drawn
// (<folder>/keyboard-wpf-*.png), played through its own pointer handling (a press at a key's place, the release, a glide)
// with the engine rendered offline (<folder>/keyboard-*.wav, for the tests to hear the pitch), and its notes checked to end
// when the window hides, the keyboard is hidden or the pointer's capture is lost; Space in the window plays and stops the
// song with the keys focused. keyboard.json holds what was found; then ReBuzz exits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Agent;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Keyboards;

namespace ReBuzz.AppViews
{
    internal static class KeyboardShots
    {
        public const string Switch = "--keyboard-shots";

        /// <summary>The folder after --keyboard-shots, or null.</summary>
        public static string Folder(string[] args)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        /// <summary>Opens, draws, plays and checks the keyboards; on the UI thread, the engine started.</summary>
        public static void Run(ReBuzzCore core, string folder, Action<string> log)
        {
            Directory.CreateDirectory(folder);
            var results = new JsonObject();
            var work = new WorkManager(core, new WorkThreadEngine(1), 0, core.EngineSettings);
            void Case(string name, Action<JsonObject> check)
            {
                var r = new JsonObject();
                try { check(r); }
                catch (Exception e) { r["error"] = e.GetType().Name + ": " + e.Message + " " + e.StackTrace?.Split('\n').FirstOrDefault()?.Trim(); }
                results[name] = r;
                log($"keyboard {name}: " + (r["error"]?.GetValue<string>() ?? "done"));
            }

            // A Neo synth's editor (its GUI): MIDI.
            Case("neo-poly", r => WithMachine(core, "Neo Poly", m =>
            {
                core.UiHost.OpenGui(m, null);
                var window = WpfMachineUiHost.GuiWindowOf(m) ?? throw new InvalidOperationException("no GUI window");
                var kb = Opened(window, r);
                r["png"] = Shoot((FrameworkElement)window.Content, Path.Combine(folder, "keyboard-wpf-neo-poly-editor.png"));
                Quiet(work);
                Press(kb, 69, 0.8);
                r["held"] = kb.Player.IsHeld(69);
                WriteWav(Path.Combine(folder, "keyboard-neo-poly-a4.wav"), Render(work, 0.8));
                Release(kb);
                r["held_after_release"] = kb.Player.Keys.Count;
                r["quiet_db_after_release"] = Quiet(work);
                r["monitor_after_release"] = Sounding(m);

                // The strike's height is its velocity.
                Press(kb, 60, 0.06);
                r["velocity_top"] = kb.Player.VelocityOf(60);
                Release(kb);
                Press(kb, 60, 0.95);
                r["velocity_bottom"] = kb.Player.VelocityOf(60);
                Release(kb);

                // A glide: one note, the key under the pointer.
                Press(kb, 60, 0.8);
                MoveTo(kb, 62);
                MoveTo(kb, 64);
                r["glide_held"] = string.Join(",", kb.Player.Keys);
                r["glide_sounding"] = Sounding(m);
                Release(kb);

                // Every note ends: the window hidden (closing hides it), the keyboard hidden, the pointer's capture lost.
                Press(kb, 60, 0.8);
                window.Hide();
                r["off_on_hide"] = kb.Player.Keys.Count == 0 && Sounding(m) == "";
                window.Show();
                window.UpdateLayout();
                Press(kb, 62, 0.8);
                kb.Toggle.Press();
                r["off_on_toggle"] = kb.Player.Keys.Count == 0 && Sounding(m) == "";
                r["hidden_height"] = kb.ExtraHeight;
                kb.Toggle.Press();
                window.UpdateLayout();
                Press(kb, 64, 0.8);
                kb.Keys.LetGo();
                r["off_on_capture_lost"] = kb.Player.Keys.Count == 0 && Sounding(m) == "";

                // Space with the keys focused plays and stops the song.
                kb.Keys.Focus();
                r["keys_focused"] = kb.Keys.IsKeyboardFocused;
                SpaceIn(window);
                r["space_plays"] = core.PlayRequested;
                SpaceIn(window);
                r["space_stops"] = !core.PlayRequested;
                core.Playing = false;
                KeyboardSettings.Set(m.DLL, KeyboardSettings.Default);
            }));

            // A native machine's generic parameter window: its Note track, with its pitch offset (FSM Infector: +12).
            Case("fsm-infector", r => WithMachine(core, "FSM Infector", m =>
            {
                core.UiHost.OpenParameterWindow(m, null);
                var window = WpfMachineUiHost.ParameterWindowOf(m) ?? throw new InvalidOperationException("no parameter window");
                var kb = Opened(window, r);
                r["offset"] = kb.Player.Route.Offset;
                r["png"] = Shoot((FrameworkElement)window.Content, Path.Combine(folder, "keyboard-wpf-parameter-window.png"));
                Quiet(work);
                var note = m.ParameterGroups[2].Parameters.First(p => p.Type == ParameterType.Note);
                Press(kb, 69, 0.8);
                r["note_value"] = note.GetValue(0);
                WriteWav(Path.Combine(folder, "keyboard-fsm-infector-a4.wav"), Render(work, 0.8));
                Release(kb);
                r["note_value_after_release"] = note.GetValue(0);
                Render(work, 0.3);
                window.Hide();
            }));

            // A drum machine: its keys by their drums.
            Case("neo-drums", r => WithMachine(core, "Neo Drums", m =>
            {
                core.UiHost.OpenGui(m, null);
                var window = WpfMachineUiHost.GuiWindowOf(m) ?? throw new InvalidOperationException("no GUI window");
                var kb = Opened(window, r);
                r["snare"] = kb.Keys.NameOf(38);
                r["png"] = Shoot((FrameworkElement)window.Content, Path.Combine(folder, "keyboard-wpf-neo-drums-editor.png"));
            }));

            // An effect: none.
            Case("jeskola-reverb", r => WithMachine(core, "Jeskola Reverb", m =>
            {
                core.UiHost.OpenParameterWindow(m, null);
                var window = WpfMachineUiHost.ParameterWindowOf(m) ?? throw new InvalidOperationException("no parameter window");
                r["keyboard"] = WpfMachineUiHost.KeyboardOf(window) != null;
                window.Hide();
            }));

            File.WriteAllText(Path.Combine(folder, "keyboard.json"), results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        static void WithMachine(ReBuzzCore core, string gear, Action<MachineCore> check)
        {
            var before = SongModel.Machines(core).ToHashSet();
            core.SongCore.CreateMachine(gear, null, "Keys " + gear, null, null, null, -1, 0.3f, 0.3f);
            var m = SongModel.Machines(core).FirstOrDefault(x => !before.Contains(x)) ?? throw new InvalidOperationException(gear + " not created");
            try
            {
                var master = core.SongCore.MachinesList.First(x => x.DLL.Info.Type == MachineType.Master);
                if (m.DLL.Info.Type == MachineType.Generator)
                    core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                check(m);
            }
            finally
            {
                try
                {
                    core.UiHost?.CloseWindows(m);
                    core.UiHost?.ReleaseMachine(m);
                    core.SongCore.DeleteMachines(new IMachine[] { m });
                }
                catch (Exception) { }
            }
        }

        /// <summary>The window's keyboard, laid out: what it shows, and the heights (the GUI keeps its own).</summary>
        static MachineKeyboard Opened(Window window, JsonObject r)
        {
            window.UpdateLayout();
            var kb = WpfMachineUiHost.KeyboardOf(window) ?? throw new InvalidOperationException("no keyboard under the window");
            var content = (FrameworkElement)WpfMachineUiHost.ContentOf(window);
            r["keyboard"] = kb.Player.Route.Input.ToString();
            r["shown"] = kb.Shown;
            r["badge"] = kb.BadgeText;
            r["first"] = kb.Keys.Layout?.First ?? -1;
            r["octaves"] = kb.Keys.Layout?.Octaves ?? 0;
            r["frame_height"] = ((FrameworkElement)window.Content).ActualHeight;
            r["content_height"] = content.ActualHeight;
            r["keyboard_height"] = kb.ActualHeight;
            r["extra_height"] = kb.ExtraHeight;
            r["key_name"] = kb.Keys.ElementOf(kb.Keys.Layout.First + 1) is { } key ? System.Windows.Automation.AutomationProperties.GetName(key) : null;
            return kb;
        }

        static Point At(MachineKeyboard kb, int note, double depth)
        {
            var rect = kb.Keys.Layout.Rect(note);
            return new Point(rect.X + rect.Width / 2, rect.Y + rect.Height * depth);
        }

        static void Press(MachineKeyboard kb, int note, double depth)
        {
            var p = At(kb, note, depth);
            kb.Keys.Down(p.X, p.Y);
        }

        static void MoveTo(MachineKeyboard kb, int note)
        {
            var p = At(kb, note, 0.8);
            kb.Keys.Move(p.X, p.Y);
        }

        static void Release(MachineKeyboard kb) => kb.Keys.Up();

        /// <summary>Space pressed in a window, as the keyboard sends it (the window's handlers first).</summary>
        static void SpaceIn(Window window)
        {
            var source = PresentationSource.FromVisual(window);
            var target = Keyboard.FocusedElement as IInputElement ?? window;
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(down);
            if (!down.Handled) target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
            target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Space) { RoutedEvent = Keyboard.PreviewKeyUpEvent });
        }

        /// <summary>The keys the machine sounds now, from its monitor ("60,64"; "" for none).</summary>
        static string Sounding(MachineCore m)
        {
            var bytes = new byte[NoteMonitor.Keys];
            m.NoteMonitor?.Read(bytes);
            return string.Join(",", Enumerable.Range(0, bytes.Length).Where(k => bytes[k] > 0));
        }

        static float[] Render(WorkManager work, double seconds)
        {
            int rate = ReBuzzCore.masterInfo.SamplesPerSec;
            int frames = (int)Math.Ceiling(seconds * rate);
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
            return mono;
        }

        /// <summary>Renders until quiet (up to 4 s); the level of the last quarter second, dBFS.</summary>
        static double Quiet(WorkManager work)
        {
            double db = 0;
            for (int i = 0; i < 16; i++)
            {
                var x = Render(work, 0.25);
                double sum = 0;
                foreach (var v in x) sum += v * (double)v;
                db = 20 * Math.Log10(Math.Max(1e-12, Math.Sqrt(sum / x.Length)));
                if (db < -70) break;
            }
            return Math.Round(db, 1);
        }

        static void WriteWav(string path, float[] x)
        {
            int rate = ReBuzzCore.masterInfo.SamplesPerSec;
            using var w = new BinaryWriter(File.Create(path));
            int bytes = x.Length * 4;
            w.Write("RIFF"u8);
            w.Write(36 + bytes);
            w.Write("WAVE"u8);
            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)3);          // IEEE float
            w.Write((short)1);
            w.Write(rate);
            w.Write(rate * 4);
            w.Write((short)4);
            w.Write((short)32);
            w.Write("data"u8);
            w.Write(bytes);
            foreach (var v in x) w.Write(v);
        }

        static string Shoot(FrameworkElement element, string path)
        {
            element.UpdateLayout();
            int w = Math.Max(1, (int)Math.Ceiling(element.ActualWidth)), h = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
                dc.DrawRectangle(new VisualBrush(element), null, new Rect(0, 0, w, h));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            return Path.GetFileName(path);
        }
    }
}
