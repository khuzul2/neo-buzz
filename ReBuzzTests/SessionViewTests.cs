using BuzzGUI.Interfaces;
using ReBuzz.AppViews.Session;
using ReBuzz.Core;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: Session View UI (docs/specs/session-view.md, 0.1). Drives the layout
    /// (SessionLayout) and the grid control (SessionView) against a real ReBuzzCore, and
    /// renders the grid to PNG files so CI can show what it looks like. Screenshots go to
    /// SESSION_SCREENSHOT_DIR when set, else to the test's work directory.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionViewTests
    {
        const int Bar = 16;

        internal sealed class Rig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public MachineCore Drums { get; }
            public MachineCore Bass { get; }
            public MachineCore Lead { get; }

            public Rig()
            {
                var drums = SynthController.NewInstance("Drums");
                var bass = SynthController.NewInstance("Bass");
                var lead = SynthController.NewInstance("Lead");
                Driver.Gear.AddDynamicGenerator(SynthController.Info);
                Driver.Start();
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(drums);
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(bass);
                Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(lead);
                Drums = Driver.MachineFor(drums);
                Bass = Driver.MachineFor(bass);
                Lead = Driver.MachineFor(lead);

                foreach (var m in new[] { Drums, Bass, Lead })
                {
                    var song = Core.SongCore;
                    if (!song.SequencesList.Any(s => s.Machine == m))
                        song.AddSequence(m, song.SequencesList.Count);
                }
                Core.Looping = true;
            }

            public PatternCore Pattern(MachineCore machine, string name, int length)
            {
                machine.CreatePattern(name, length);
                return machine.PatternsList.Single(p => p.Name == name);
            }

            public void RunTicks(int ticks)
            {
                for (int i = 0; i < ticks; i++)
                {
                    var mi = ReBuzzCore.masterInfo;
                    Driver.ReadStereoSamples(Math.Max(1, mi.SamplesPerTick - mi.PosInTick));
                }
            }

            public void Dispose()
            {
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        internal static string Screenshot(FrameworkElement element, string name, int width = 880, int height = 400)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            var dir = Environment.GetEnvironmentVariable("SESSION_SCREENSHOT_DIR");
            if (string.IsNullOrEmpty(dir))
                dir = TestContext.CurrentContext.WorkDirectory;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, name + ".png");
            using (var stream = File.Create(path))
                encoder.Save(stream);
            TestContext.Out.WriteLine("Screenshot: " + path);
            return path;
        }

        [Test]
        public void LayoutIsAutoFilledFromTheSongOnFirstUse()
        {
            using var rig = new Rig();
            rig.Pattern(rig.Drums, "Beat", Bar);
            rig.Pattern(rig.Drums, "Fill", Bar);
            rig.Pattern(rig.Bass, "Line", 2 * Bar);
            var layout = rig.Core.SessionLayout;

            Assert.That(layout.Document, Is.Null, "a song gets a layout only once the session is used");
            var doc = layout.Ensure();

            Assert.That(doc.Tracks.Select(t => t.Machine), Is.EqualTo(new[] { "Drums", "Bass", "Lead" }));
            Assert.That(layout.PatternAt(0, 0)?.Name, Is.EqualTo("Beat"));
            Assert.That(layout.PatternAt(0, 1)?.Name, Is.EqualTo("Fill"));
            Assert.That(layout.PatternAt(1, 0)?.Name, Is.EqualTo("Line"));
            Assert.That(layout.PatternAt(2, 0), Is.Null);
            Assert.That(layout.SceneCount, Is.GreaterThanOrEqualTo(8));
        }

        [Test]
        public void LayoutFollowsRenamesAndDeletedPatterns()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var fill = rig.Pattern(rig.Drums, "Fill", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();

            beat.Name = "Groove";
            rig.Drums.Name = "Kit";
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(beat));
            Assert.That(layout.Document!.Tracks[0].Machine, Is.EqualTo("Kit"));

            rig.Drums.DeletePattern(fill);
            Assert.That(layout.PatternAt(0, 1), Is.Null);
            Assert.That(layout.PatternAt(0, 0), Is.SameAs(beat));
        }

        [Test]
        public void SceneLaunchStartsEveryFilledSlotOfTheRow()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var line = rig.Pattern(rig.Bass, "Line", 2 * Bar);
            var hook = rig.Pattern(rig.Lead, "Hook", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.ClearSlot(2, 0);
            layout.SetSlot(2, 1, hook);

            layout.LaunchScene(0);
            rig.RunTicks(2);

            var session = rig.Core.Session;
            Assert.That(session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Playing));
            Assert.That(session.GetState(layout.SequenceOf(1)!, line), Is.EqualTo(ClipState.Playing));
            Assert.That(session.GetPlaying(layout.SequenceOf(2)!), Is.Null);
        }

        [Test]
        public void NewSongDropsTheLayout()
        {
            using var rig = new Rig();
            rig.Pattern(rig.Drums, "Beat", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.SetBeatsPerBar(3);

            rig.Core.Modified = false;   // otherwise NewFile asks whether to save
            rig.Driver.NewFile();

            Assert.That(layout.Document, Is.Null);
            Assert.That(rig.Core.Session.BeatsPerBar, Is.EqualTo(4));
        }

        [Test]
        public void RendersTheGrid()
        {
            using var rig = new Rig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            rig.Pattern(rig.Drums, "Fill", Bar);
            rig.Pattern(rig.Drums, "Break", 2 * Bar);
            var line = rig.Pattern(rig.Bass, "Line A", 2 * Bar);
            var lineB = rig.Pattern(rig.Bass, "Line B", 2 * Bar);
            rig.Pattern(rig.Lead, "Hook", Bar);
            rig.Pattern(rig.Lead, "Answer", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.RenameScene(0, "Intro");
            layout.RenameScene(1, "Verse");
            layout.RenameScene(2, "Drop");

            var view = new SessionView(rig.Core);
            view.Rebuild();
            view.RefreshState();
            Screenshot(view, "session-view-stopped");

            // Scene 1 playing for a while, then Line B queued on the bass track.
            layout.LaunchScene(0);
            rig.RunTicks(6);
            rig.Core.Session.Launch(layout.SequenceOf(1)!, lineB);
            view.RefreshState();
            Screenshot(view, "session-view-playing");

            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Playing));
            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(1)!, line), Is.EqualTo(ClipState.Playing));
            Assert.That(rig.Core.Session.GetState(layout.SequenceOf(1)!, lineB), Is.EqualTo(ClipState.Queued));
        }

        [Test]
        public void RendersRecordingAndTakenOverTracks()
        {
            using var rig = new Rig();
            rig.Pattern(rig.Drums, "Beat", Bar);
            rig.Pattern(rig.Bass, "Line", Bar);
            rig.Pattern(rig.Lead, "Hook", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.ClearSlot(2, 0);

            var view = new SessionView(rig.Core);
            view.Rebuild();
            rig.Core.Recording = true;
            layout.LaunchScene(0);
            rig.RunTicks(3);
            view.RefreshState();
            Screenshot(view, "session-view-recording");

            Assert.That(rig.Core.Session.IsAnyArrangementOverridden, Is.True);
            rig.Core.Recording = false;
        }
    }
}
