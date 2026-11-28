// Neo Looper (docs/specs/neo-looper.md §5, gate "Save and load"): the loop's audio is saved with the song in the .nbl and comes back bit for bit,
// with the machine's parameters; the reopened machine holds the loop, stopped, ready to play. Headless, through the real song save and open.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class NeoLooperNblTests
    {
        const string Gear = "Neo Looper";
        static ReBuzzCore Core => Studio.Host.Core;

        static void Pump()
        {
            for (int i = 0; i < 3; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
        }

        static void NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            SongFiles.New().Wait();
            Pump();
        }

        static MachineCore Add(string name)
        {
            Core.SongCore.CreateMachine(Gear, null, name, null, null, null, -1, 0.3f, 0.3f);
            var machine = Core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);
            Assert.That(machine, Is.Not.Null, "Neo Looper loads");
            return machine!;
        }

        static object Engine(MachineCore m) =>
            m.ManagedMachine.GetType().GetProperty("Engine", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(m.ManagedMachine)!;

        static T Call<T>(object engine, string method, params object[] args) =>
            (T)engine.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(engine, args)!;

        static IParameter Global(IMachine m, string name) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == name);

        static (float[] L, float[] R) Loop(int frames)
        {
            var l = new float[frames];
            var r = new float[frames];
            var rng = new Random(11);
            for (int i = 0; i < frames; i++)
            {
                l[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 220 * i / 48000.0) + 0.1 * (rng.NextDouble() - 0.5));
                r[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * i / 48000.0) + 0.1 * (rng.NextDouble() - 0.5));
            }
            return (l, r);
        }

        [AvaloniaTest]
        public void ALoopSavesInTheNblAndOpensBitExactWithItsParameters()
        {
            NewSong();
            var machine = Add("Loop Song");
            try
            {
                int frames = 4 * 48000;
                var (l, r) = Loop(frames);
                var engine = Engine(machine);
                Call<object>(engine, "ApplyLimitsNow");
                engine.GetType().GetMethod("Load")!.Invoke(engine, new object[] { l, r, frames, 8.0 });
                Global(machine, "Feedback").SetValue(0, 700);
                Global(machine, "Half speed").SetValue(0, 1);
                Global(machine, "Quantise").SetValue(0, 1);

                string dir = TestTemp.Path($"rebuzz-looper-nbl-{Environment.ProcessId}");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "looper.nbl");
                Core.SaveSongCopy(path);
                long bytes = new FileInfo(path).Length;
                Core.Modified = false;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Core.OpenSongFile(path);
                Pump();

                var opened = Core.SongCore.MachinesList.Single(m => m.Name == "Loop Song");
                Assert.That(opened, Is.Not.SameAs(machine), "the song was opened");
                machine = opened;
                Assert.That(Global(opened, "Feedback").GetValue(0), Is.EqualTo(700));
                Assert.That(Global(opened, "Half speed").GetValue(0), Is.EqualTo(1));
                Assert.That(Global(opened, "Quantise").GetValue(0), Is.EqualTo(1));

                var e2 = Engine(opened);
                var args = new object[] { null!, null!, 0, 0.0 };
                bool ok = (bool)e2.GetType().GetMethod("Snapshot")!.Invoke(e2, args)!;
                Assert.That(ok, Is.True, "the reopened machine holds a loop");
                var l2 = (float[])args[0]!;
                var r2 = (float[])args[1]!;
                Assert.That((int)args[2]!, Is.EqualTo(frames));
                Assert.That((double)args[3]!, Is.EqualTo(8.0), "its length in beats");
                for (int i = 0; i < frames; i++)
                {
                    if (BitConverter.SingleToInt32Bits(l2[i]) != BitConverter.SingleToInt32Bits(l[i])) Assert.Fail($"left, frame {i}");
                    if (BitConverter.SingleToInt32Bits(r2[i]) != BitConverter.SingleToInt32Bits(r[i])) Assert.Fail($"right, frame {i}");
                }
                Assert.That(Call<object>(e2, "get_State").ToString(), Is.EqualTo("Stopped"), "it reopens stopped, ready to play");
                TestContext.Out.WriteLine($"Neo Looper: a 4 s stereo loop saved in a .nbl of {bytes / 1e6:F2} MB ({bytes / 4 / 1e6:F2} MB a second of loop); the audio came back bit for bit.");
            }
            finally
            {
                NewSong();
            }
        }

        [AvaloniaTest]
        public void AnEmptyLooperSavesNoAudio()
        {
            NewSong();
            var machine = Add("Empty Loop");
            try
            {
                string dir = TestTemp.Path($"rebuzz-looper-nbl-{Environment.ProcessId}");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "empty.nbl");
                Core.SaveSongCopy(path);
                long bytes = new FileInfo(path).Length;
                Core.Modified = false;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Core.OpenSongFile(path);
                Pump();
                var opened = Core.SongCore.MachinesList.Single(m => m.Name == "Empty Loop");
                var e2 = Engine(opened);
                Assert.That(Call<object>(e2, "get_State").ToString(), Is.EqualTo("Empty"));
                Assert.That(Call<int>(e2, "get_LoopFrames"), Is.EqualTo(0));
                TestContext.Out.WriteLine($"Neo Looper: an empty looper adds nothing to a .nbl ({bytes / 1e3:F0} KB song).");
            }
            finally
            {
                NewSong();
            }
        }
    }
}
