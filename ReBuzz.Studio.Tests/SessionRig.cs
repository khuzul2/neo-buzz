// Fork (docs/specs/studio-mvp.md S5): a song for the Session View's tests on the shared engine:
// Neo machines as tracks, the view in a window, simulated input, and the engine's ticks driven
// as ReBuzzTests drives them (a WorkManager filling buffers; no audio device runs here).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    internal sealed class SessionRig : IDisposable
    {
        public const int Bar = 16;
        public const int TB = PatternEvent.TimeBase;

        readonly WorkManager work;
        readonly float[] buffer = new float[512];
        readonly double masterVolume;
        readonly bool looping;
        float x = -0.4f;

        public ReBuzzCore Core { get; }
        public SessionLayout Layout => Core.SessionLayout;
        public SessionCore Session => (SessionCore)Core.Session;
        public Window Window { get; private set; }
        public SessionView View { get; private set; }

        /// <summary>A new song with a Neo Poly track for each name, connected to the master.</summary>
        public SessionRig(params string[] tracks)
        {
            Core = Studio.Host.Core;
            // Launches are timed to what is heard: the rendered position plus the wall-clock time
            // since that render. Rendering here isn't in real time, so time stands still between
            // renders (as ReBuzzTests' FreezeClock), and quantization lands where the ticks say.
            SessionCore.Timestamp = () => 1_000_000;
            NewSong();
            masterVolume = Core.MasterVolume;
            looping = Core.Looping;
            foreach (var name in tracks)
                Add("Neo Poly", name);
            Core.Looping = true;
            work = new WorkManager(Core, new WorkThreadEngine(1), 0, BuzzGUI.Common.Global.EngineSettings);
        }

        void NewSong()
        {
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Recording = false;
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
        }

        public MachineCore Master => Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);

        /// <summary>
        /// A machine connected to the master (or to <paramref name="to"/>, or with
        /// <paramref name="connect"/> false to nothing); with a sequence (a track) unless <paramref name="track"/> is false.
        /// </summary>
        public MachineCore Add(string dll, string name, bool track = true, IMachine to = null, bool connect = true)
        {
            var song = Core.SongCore;
            song.CreateMachine(dll, null, name, null, null, null, -1, x, 0.2f);
            x += 0.15f;
            var machine = song.MachinesList.Last(m => m.Name == name);
            if (connect)
                song.ConnectMachines(machine, to ?? Master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            if (track && !song.SequencesList.Any(s => s.Machine == machine))
                song.AddSequence(machine, song.SequencesList.Count);
            return machine;
        }

        public MachineCore Machine(string name) => Core.SongCore.MachinesList.First(m => m.Name == name);

        public PatternCore Pattern(MachineCore machine, string name, int length, params (int Time, int Note)[] notes)
        {
            machine.CreatePattern(name, length);
            var p = machine.PatternsList.Single(x => x.Name == name);
            if (notes.Length > 0)
                Layout.EditNotes(p, null, notes.Select(n => new ClipNote(n.Time * TB, TB, n.Note, 100)).ToArray());
            return p;
        }

        // ------------------------------------------------------------------ the view

        /// <summary>The Session View in a window of its own, laid out.</summary>
        public SessionView Show(int width = 1100, int height = 640)
        {
            View = new SessionView(Core);
            Window = new Window { Width = width, Height = height, Content = View };
            Quiet(Window);
            Window.Show();
            Settle();
            View.Focus();
            return View;
        }

        /// <summary>
        /// No tooltips in a test window: on a slow machine one can open under the pointer between
        /// steps (they open on a timer) and take the next click.
        /// </summary>
        public static void Quiet(Window window) => ToolTip.SetServiceEnabled(window, false);

        /// <summary>Runs what is posted and lays the window out again (after a rebuild).</summary>
        public void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            Window?.UpdateLayout();
        }

        /// <summary>A point of a control in its window, scrolled into view first (as a user would).</summary>
        public Point PointIn(Control control, double fx = 0.5, double fy = 0.5)
        {
            Settle();
            control.BringIntoView();
            Settle();
            var p = control.TranslatePoint(new Point(control.Bounds.Width * fx, control.Bounds.Height * fy), (Visual)TopLevel.GetTopLevel(control));
            Assert.That(p, Is.Not.Null, "the control is on screen");
            return p!.Value;
        }

        public void Click(Control control, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None, double fx = 0.5, double fy = 0.5)
        {
            var top = TopLevel.GetTopLevel(control)!;
            var p = PointIn(control, fx, fy);
            top.MouseDown(p, button, mods);
            top.MouseUp(p, button, mods);
            Settle();
        }

        public void DoubleClick(Control control, RawInputModifiers mods = RawInputModifiers.None, double fx = 0.5, double fy = 0.5)
        {
            var top = TopLevel.GetTopLevel(control)!;
            var p = PointIn(control, fx, fy);
            MachineViewTests.DoubleClick(top, p, MouseButton.Left, mods);
            Settle();
        }

        /// <summary>A left-button drag by (dx, dy) pixels in steps.</summary>
        public void Drag(Control control, double dx, double dy, RawInputModifiers mods = RawInputModifiers.None, double fx = 0.5, double fy = 0.5)
        {
            var top = TopLevel.GetTopLevel(control)!;
            var from = PointIn(control, fx, fy);
            top.MouseDown(from, MouseButton.Left, mods);
            for (int i = 1; i <= 4; i++)
                top.MouseMove(new Point(from.X + dx * i / 4, from.Y + dy * i / 4), mods | RawInputModifiers.LeftMouseButton);
            top.MouseUp(new Point(from.X + dx, from.Y + dy), MouseButton.Left, mods);
            Settle();
        }

        /// <summary>
        /// Picks an item of the menu the view opened last, then lets the edit's rebuild run
        /// (the layout posts Changed: the view's cells are new afterwards, so fetch them again).
        /// </summary>
        public void Menu(string path)
        {
            Assert.That(View.ClickMenuItem(path), Is.True, $"menu item {path}");
            Settle();
        }

        /// <summary>Picks an item of a drop-down, as the user would, and lets the edit's rebuild run.</summary>
        public void Choose(ComboBox box, int index)
        {
            box.SelectedIndex = index;
            Settle();
        }

        public void Key(PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None, TopLevel top = null)
        {
            top ??= Window;
            top.KeyPressQwerty(key, mods);
            top.KeyReleaseQwerty(key, mods);
            Settle();
        }

        public string Screenshot(string name, TopLevel top = null)
        {
            Settle();
            View?.RefreshState();
            return Studio.Screenshot(top ?? Window, name);
        }

        // ------------------------------------------------------------------ the engine

        public int SamplesPerTick => ReBuzzCore.masterInfo.SamplesPerTick;

        /// <summary>Renders whole ticks, as the audio thread would.</summary>
        public void RunTicks(int ticks, bool runJobs = true)
        {
            for (int i = 0; i < ticks; i++)
            {
                var mi = ReBuzzCore.masterInfo;
                int frames = Math.Max(1, mi.SamplesPerTick - mi.PosInTick);
                while (frames > 0)
                {
                    int n = Math.Min(frames, buffer.Length / 2);
                    work.MainAudioFillBuffer(buffer, 0, n * 2);
                    frames -= n;
                }
            }
            if (runJobs) Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Renders up to the start of session tick <paramref name="clock"/>.</summary>
        public void PlayTo(long clock)
        {
            if (!Core.PlayRequested) Core.Playing = true;
            int guard = 0;
            while (Core.Session.Clock < clock && guard++ < 100000)
                RunTicks(1);
            Assert.That(Core.Session.Clock, Is.EqualTo(clock));
        }

        public static int NoteOn(int note, int velocity) => 0x90 | (note << 8) | (velocity << 16);

        public static int NoteOff(int note) => 0x80 | (note << 8);

        public void Dispose()
        {
            Window?.Close();
            SessionCore.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp;
            Dialogs.Responder = null;
            Dialogs.TextResponder = null;
            ArrangementInsertDialog.Responder = null;
            foreach (var w in PianoRollWindow.OpenWindows) w.Close();
            Layout.SetSessionRecord(false);
            Core.MasterVolume = masterVolume;
            Core.Looping = looping;                 // the tests after this one start as they would have
            NewSong();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
