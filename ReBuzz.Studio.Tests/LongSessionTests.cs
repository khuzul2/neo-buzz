// Fork (fix/long-session-slowdown): a long process doesn't slow down. The agent's render makes a WorkManager of its
// own per render; each one subscribed to the engine's PrebuildWorkOrder and never let go, so every render left one
// behind with its cached work order (and every machine in it, deleted ones too), and each later topology change
// rebuilt the order of all of them. The machine profiler (145 machines, thousands of renders in one server) ran
// 3 to 5 times slower late in the run. Now a render's manager unsubscribes when it ends.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class LongSessionTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        /// <summary>How many work managers listen for topology changes.</summary>
        static int Listeners() => Core.PrebuildWorkOrder?.GetInvocationList().Length ?? 0;

        static void Render()
        {
            RenderTools.Render(Core, null, 0, 16, 0.0, new HashSet<MachineCore>(), new HashSet<MachineCore>(), Array.Empty<MachineCore>(),
                CancellationToken.None, null, new List<string>());
        }

        /// <summary>One cycle: a machine added into the Master, a render, the machine deleted.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static (double Ms, WeakReference Machine) Cycle()
        {
            var song = Core.SongCore;
            song.CreateMachine("Neo Poly", null, "Cycle", null, null, null, -1, 0.3f, 0.3f);
            var m = song.MachinesList.First(x => x.Name == "Cycle");
            var master = song.MachinesList.First(x => x.DLL.Info.Type == MachineType.Master);
            song.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            var weak = new WeakReference(m);
            var clock = Stopwatch.StartNew();
            Render();
            double ms = clock.Elapsed.TotalMilliseconds;
            song.DeleteMachines(new[] { (IMachine)m });
            return (ms, weak);
        }

        static void Collect()
        {
            for (int i = 0; i < 4; i++)
            {
                MachineViewTests.Settle();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        [AvaloniaTest]
        public void ManyRendersAndDeletedMachinesLeaveNothingBehind()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            try
            {
                SongFiles.New().Wait();
                MachineViewTests.Settle();
                Cycle();    // the first of everything (work instances, JIT)
                Collect();
                long heapBase = GC.GetTotalMemory(true);
                int listeners = Listeners(), machines = Core.SongCore.MachinesList.Count;
                const int N = 60;
                var times = new List<double>();
                var weak = new List<WeakReference>();
                for (int i = 0; i < N; i++)
                {
                    var (ms, w) = Cycle();
                    times.Add(ms);
                    weak.Add(w);
                }
                Collect();
                long heapHeld = GC.GetTotalMemory(true) - heapBase;
                var history = Core.SongCore.ActionStack;

                Assert.That(Listeners(), Is.EqualTo(listeners), "a render's work manager no longer listens for topology changes once it is done");
                Assert.That(Core.SongCore.MachinesList.Count, Is.EqualTo(machines), "the machines added are gone");
                // The undo history keeps the last deleted machine's step (one still alive here); the machines before it are free.
                Assert.That(weak.Take(N - 1).Count(w => w.IsAlive), Is.Zero, "a deleted machine is collectable: neither the history nor the engine keeps it");
                // The history says what it holds (its cap is in those bytes): a deleted machine's saved parameters are ~0.3 MB.
                Assert.That(history.UsedBytes, Is.GreaterThan(N * 100_000L), "a deleted machine counts for what its saved parameters take");
                Assert.That(heapHeld, Is.LessThan(history.UsedBytes * 3 + 10_000_000), $"what the session holds ({heapHeld / 1048576.0:0.0} MB more than at the start) is within 3x of what the history counts ({history.UsedBytes / 1048576.0:0.0} MB)");
                double early = times.Take(10).Order().ElementAt(5), late = times.Skip(N - 10).Order().ElementAt(5);
                Assert.That(late, Is.LessThan(early * 2 + 20), $"a render takes no longer after {N} cycles (median {early:0.0} ms of the first ten, {late:0.0} ms of the last ten)");
            }
            finally
            {
                Dialogs.Responder = null;
            }
        }
    }
}
