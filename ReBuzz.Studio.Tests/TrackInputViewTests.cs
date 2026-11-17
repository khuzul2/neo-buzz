// Fork (docs/specs/neo-audio.md "Inputs and arm"): the views of a track's inputs and arm, in the studio's window: one state, shown by the
// Machine View's dot, the Session's header and I/O row, the Arrangement's header and the Inspector's Input section.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    public class TrackInputViewTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static void Settle() => MachineViewTests.Settle();

        sealed class Windowed : IDisposable
        {
            public MainWindow Window { get; }
            public MachineView Machines { get; }
            public SessionView Session { get; }
            public SequenceView Sequence { get; }
            public MachineCore Voice { get; }
            public MachineCore Lead { get; }

            public Windowed()
            {
                (Window, Machines) = MachineViewTests.OpenEmpty();
                Core.EngineInput.Configure(2, null, 44100, 0);
                var layout = Core.SessionLayout;
                layout.Ensure();
                Voice = (MachineCore)layout.NewAudioTrack("Voice")!;
                var song = Core.SongCore;
                song.CreateMachine("Neo Poly", null, "Lead", null, null, null, -1, 0.3f, -0.3f);
                Lead = song.MachinesList.Last(m => m.Name == "Lead");
                song.ConnectMachines(Lead, song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                song.AddSequence(Lead, song.SequencesList.Count);
                Session = Tab<SessionView>("Session");
                Sequence = Tab<SequenceView>("Arrangement");
                Settle();
            }

            T Tab<T>(string title) where T : class => Window.Tabs.Items.OfType<TabItem>().First(t => (string)t.Header == title).Content as T;

            public void Show(string view)
            {
                Window.ShowView(view);
                Settle();
                Window.RefreshNow();
            }

            public void Dispose()
            {
                Core.EngineInput.Close();
                MachineViewTests.Close(Window);
            }
        }

        static SessionLayout Layout => Core.SessionLayout;

        [AvaloniaTest]
        public void TheMachineBoxesOfArmableMachinesHaveADotThatArmsAndTheMenuHasTheInputs()
        {
            using var w = new Windowed();
            w.Show("Machines");
            var canvas = w.Machines.Canvas;
            var dot = w.Machines.FromCanvas(MachineGeometry.ArmDot(canvas.BoxOf(w.Voice)).Center, w.Window);
            MachineViewTests.Click(w.Window, dot);
            Assert.That(Layout.IsMachineArmed(w.Voice), Is.True, "the dot arms");
            MachineViewTests.Click(w.Window, dot);
            Assert.That(Layout.IsMachineArmed(w.Voice), Is.False, "and disarms");
            Assert.That(Layout.IsMachineArmed(w.Lead), Is.False);

            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            Assert.That(SessionLayout.CanArm(master), Is.False, "the Master has no dot");

            MachineViewTests.Click(w.Window, dot, MouseButton.Right);
            var menu = canvas.OpenMenu;
            Assert.That(menu?.IsOpen, Is.True);
            var headers = menu!.Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
            Assert.That(headers, Is.SupersetOf(new[] { "Audio From", "MIDI From", "Channel", "Monitor" }));
            var audio = menu.Items.OfType<MenuItem>().First(i => (string)i.Header == "Audio From");
            Assert.That(audio.Items.OfType<MenuItem>().Select(i => (string)i.Header), Is.EquivalentTo(new[] { "None", "In 1", "In 2", "In 1/2", "Track: Lead", "Wired inputs" }));
            menu.Close();
            // The Neo Poly has MIDI From only.
            var poly = Views.Session.TrackInputMenu.Build(Core, w.Lead).Items.OfType<MenuItem>().Select(i => (string)i.Header).ToList();
            Assert.That(poly, Does.Not.Contain("Audio From"));
            Assert.That(poly, Does.Contain("MIDI From"));
        }

        static TrackInputView[] InputViews(Visual root) => root.GetVisualDescendants().OfType<TrackInputView>().ToArray();

        [AvaloniaTest]
        public void ArmFromEachViewShowsInTheOthers()
        {
            using var w = new Windowed();
            // Machine View dot.
            w.Show("Machines");
            MachineViewTests.Click(w.Window, w.Machines.FromCanvas(MachineGeometry.ArmDot(w.Machines.Canvas.BoxOf(w.Voice)).Center, w.Window));
            Assert.That(Layout.IsMachineArmed(w.Voice), Is.True);

            w.Show("Session");
            w.Session.RefreshState();
            int track = Layout.FirstTrackOf(w.Voice);
            Assert.That(track, Is.GreaterThanOrEqualTo(0));
            Assert.That(w.Session.Headers[track].Arm.IsActive, Is.True, "the Session header's dot");

            // Session header: off.
            MachineViewTests.Click(w.Window, w.Session.Headers[track].Arm.TranslatePoint(new Point(10, 10), w.Window)!.Value);
            Assert.That(Layout.IsMachineArmed(w.Voice), Is.False);

            // The Inspector's Input section arms.
            StudioSelection.For(Core).Set(w.Voice, null, this);
            Settle();
            w.Window.Inspector.Open();
            Settle();
            var inspector = InputViews(w.Window.Inspector).Single();
            MachineViewTests.Click(w.Window, inspector.ArmButton.TranslatePoint(new Point(8, 8), w.Window)!.Value);
            Assert.That(Layout.IsMachineArmed(w.Voice), Is.True, "the Inspector's dot");
            inspector.Refresh();

            // The Arrangement's R.
            w.Show("Arrangement");
            var r = w.Sequence.GetVisualDescendants().OfType<ToggleButton>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Arm Voice for recording");
            Assert.That(r.IsChecked, Is.True, "the Arrangement's R follows");
            MachineViewTests.Click(w.Window, r.TranslatePoint(new Point(10, 10), w.Window)!.Value);
            Assert.That(Layout.IsMachineArmed(w.Voice), Is.False, "the R disarms");

            w.Show("Machines");
            Assert.That(Layout.IsMachineArmed(w.Voice), Is.False);
            // A machine with no track gets one when its dot arms it.
            var extra = Core.SongCore.MachinesList.First(m => m.Name == "Lead");
            Assert.That(Layout.SetMachineArmed(extra, true), Is.True);
            Assert.That(Layout.IsMachineArmed(extra), Is.True);
        }

        [AvaloniaTest]
        public void TheSessionIORowChoosesInputsAndShowsTheLiveMeter()
        {
            using var w = new Windowed();
            w.Show("Session");
            Assert.That(InputViews(w.Session), Is.Empty, "hidden until the I/O button");
            MachineViewTests.Click(w.Window, w.Session.IOButton.TranslatePoint(new Point(10, 8), w.Window)!.Value);
            Assert.That(Layout.Document.ShowIO, Is.True);
            w.Session.RefreshState();
            Settle();
            var rows = InputViews(w.Session);
            Assert.That(rows.Length, Is.EqualTo(2), "one for each track that can be armed");
            var voice = rows.First(r => r.AudioBox.IsVisible && r.AudioBox.Parent != null);
            Assert.That(voice.AudioBox.SelectedIndex, Is.GreaterThanOrEqualTo(0));
            Assert.That(voice.MonitorButtons.Count, Is.EqualTo(3));

            voice.AudioBox.SelectedIndex = 1 + 1;                               // None, In 1, In 2...
            Assert.That(Layout.TrackInputOf(w.Voice).Audio, Is.EqualTo(AudioFrom.Mono(1)), "In 2");
            voice = InputViews(w.Session).First(r => r.AudioBox.Parent != null);
            voice.MidiBox.SelectedIndex = voice.MidiBox.ItemCount - 1;
            Assert.That(Layout.TrackInputOf(w.Voice).Midi, Is.EqualTo(MidiFromKind.Keyboard), "the last choice is the computer keyboard");
            voice = InputViews(w.Session).First(r => r.AudioBox.Parent != null);
            voice.ChannelBox.SelectedIndex = 4;
            Assert.That(Layout.TrackInputOf(w.Voice).Channel, Is.EqualTo(4));
            voice = InputViews(w.Session).First(r => r.AudioBox.Parent != null);
            MachineViewTests.Click(w.Window, voice.MonitorButtons[0].TranslatePoint(new Point(8, 8), w.Window)!.Value);
            Assert.That(Layout.MonitorOf(w.Voice), Is.EqualTo(1), "In");
        }

        [AvaloniaTest]
        public void TheIORowsMeterMovesWithTheInputWhileArmed()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var cleanup = Studio.AfterTest(() => core.EngineInput.Close());
            core.EngineInput.Configure(2, null, 44100, 0);
            var layout = rig.Layout;
            layout.Ensure();
            var voice = (MachineCore)layout.NewAudioTrack("Voice")!;
            layout.SetMachineArmed(voice, true);
            var view = rig.Show();
            layout.SetShowIO(true);
            view.RefreshState();
            rig.Settle();
            var row = InputViews(view).Single();
            long fed = 0;
            void Feed(int frames, Func<long, float> signal)
            {
                var data = new float[frames * 2];
                for (int i = 0; i < frames; i++) data[2 * i] = data[2 * i + 1] = signal(fed + i);
                core.EngineInput.Push(data, frames, 2);
                fed += frames;
                rig.RunFrames(frames);
            }
            Feed(8000, f => 0f);
            System.Threading.Thread.Sleep(30);
            Assert.That(row.Meter.Level, Is.EqualTo(0).Within(0.01));
            Feed(8000, f => 0.5f * (float)Math.Sin(f * 0.1));
            System.Threading.Thread.Sleep(60);
            rig.Settle();
            Assert.That(row.Meter.Level, Is.GreaterThan(0.5), "the meter shows the input while the track is armed");
            rig.Screenshot("session-io-row");
            layout.SetMachineArmed(voice, false);
            Feed(8000, f => 0.5f * (float)Math.Sin(f * 0.1));
            System.Threading.Thread.Sleep(60);
            rig.Settle();
            Assert.That(row.Meter.Level, Is.LessThan(0.5), "and goes quiet when it is neither armed nor monitored");
        }
        }
    }
}
