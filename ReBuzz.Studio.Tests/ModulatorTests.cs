// Fork (docs/specs/neo-patch.md §3, phase NM): the canvas modulators (Neo LFO, Neo Envelope, Neo Step, Neo Macro) on the real engine, as the
// device's thread drives it. The engine renders here the way AudioHygieneTests has it (the same audio thread), and these tests check:
//  - an LFO moves a parameter around the value it had, within its range, no step bigger than the write glide allows (smoothness), with no undo
//    step from the modulation, and removing the target puts the value back;
//  - the master volume is never pushed past 0 dB, and the master's tempo is refused;
//  - a target machine renamed is followed, one deleted is shown as missing and nothing breaks;
//  - a song saved and opened again keeps the targets, the base and the state of the modulators;
//  - 4 modulators with 8 targets each on a song of 8 playing machines allocate nothing on the audio thread or the render threads, with each
//    work algorithm (the rest of the big song's budget is ReBuzzTests');
//  - the studio: the dashed wire on the machine canvas and the live range ring in the device view.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Device;
using ReBuzz.Studio.Views.Machines;
using static ReBuzz.Studio.Tests.AudioHygieneTests;

namespace ReBuzz.Studio.Tests
{
    public class ModulatorTests
    {
        const int TB = PatternEvent.TimeBase;

        static ReBuzzCore Core => AudioHygieneTests.Core;

        static MachineCore Add(string gear, string name, float x = 0.5f, float y = 0.5f)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, x, y);
            return Core.SongCore.MachinesList.Last(m => m.Name == name);
        }

        static IParameter Param(IMachine m, string name) =>
            m.ParameterGroups[1].Parameters.First(p => p.Name == name);

        static IModulator Mod(IMachine m) => ModulationBoard.Of(m) ?? throw new AssertionException(m.Name + " is not a modulator");

        /// <summary>A Neo Poly playing a pattern (so the song has sound and the transport runs), a Neo Filter after it.</summary>
        static (MachineCore Poly, MachineCore Filter) PlayingFilter()
        {
            var poly = AddPoly("Mod Poly");
            poly.CreatePattern("00", 16);
            var pattern = (PatternCore)poly.Patterns.First();
            Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(i => new ClipNote(i * TB, TB / 2, 48 + (i * 5) % 12, 60 + i * 4)).ToArray());
            Arrange(poly, pattern);
            var filter = InsertFilter(poly, "Mod Filter");
            return (poly, filter);
        }

        static ModulationInfo Info(int depth, bool bipolar = true, int curve = 0) => new ModulationInfo { Depth = depth, Bipolar = bipolar, Curve = curve };

        /// <summary>"250 ms" or "1.5 s" as seconds.</summary>
        static double Seconds(string text)
        {
            var parts = text.Split(' ');
            double v = double.Parse(parts[0], CultureInfo.InvariantCulture);
            return parts.Length > 1 && parts[1] == "ms" ? v / 1000 : v;
        }

        static void Wait(Func<bool> until, int ms = 6000)
        {
            var end = DateTime.UtcNow.AddMilliseconds(ms);
            while (!until() && DateTime.UtcNow < end)
            {
                Pump();
                Thread.Sleep(20);
            }
        }

        // ------------------------------------------------------------------ an LFO on a filter

        [AvaloniaTest]
        public void AnLfoMovesAParameterSmoothlyAndRemovingItBringsTheBaseBack()
        {
            NewSong();
            try
            {
                var (poly, filter) = PlayingFilter();
                var lfo = Add("Neo LFO", "Mod LFO");
                var mod = Mod(lfo);
                var cutoff = Param(filter, "Cutoff");
                int baseValue = (cutoff.MinValue + cutoff.MaxValue) / 2;
                cutoff.SetValue(1 << 16, baseValue);
                Param(lfo, "Rate").SetValue(1 << 16, 700);        // fast, so a short run covers cycles
                Assert.That(mod.SetTarget(-1, filter, cutoff, 0, Info(40), out int slot), Is.Null);
                Assert.That(slot, Is.EqualTo(0));

                var stack = Core.SongCore.ActionStack;
                bool couldUndo = stack.CanUndo;
                double glide = Seconds(Param(lfo, "Write glide").DescribeValue(Param(lfo, "Write glide").GetValue(0)));
                int span = cutoff.MaxValue - cutoff.MinValue;
                double bufferSeconds = Frames / (double)ReBuzzCore.masterInfo.SamplesPerSec;
                var seen = new List<int>();
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                audio.Read(100);
                audio.Read(400, () => seen.Add(cutoff.GetValue(0)));
                lock (ReBuzzCore.AudioLock) Core.Playing = false;

                Assert.That(seen.Min(), Is.LessThan(baseValue - span / 50), "it swings below the base");
                Assert.That(seen.Max(), Is.GreaterThan(baseValue + span / 50), "and above it");
                Assert.That(seen.All(v => v >= cutoff.MinValue && v <= cutoff.MaxValue), Is.True, "always inside the range");
                // Smoothness: the glide is the time to cross the whole range; a buffer is at most 4 sub-blocks of it, with rounding.
                double allowed = span * bufferSeconds / glide * 1.6 + 2;
                int worst = seen.Zip(seen.Skip(1), (a, b) => Math.Abs(b - a)).Max();
                Assert.That(worst, Is.LessThanOrEqualTo(allowed), $"no step bigger than the glide allows ({glide * 1000:F0} ms over {span}: {allowed:F0})");
                Assert.That(stack.CanUndo, Is.EqualTo(couldUndo), "modulation makes no undo step");
                var t = mod.Targets.Single();
                Assert.That(t.State, Is.EqualTo(ModulationState.Active));
                Assert.That(t.Base, Is.EqualTo(baseValue), "the base is what was set");
                Assert.That(t.Low, Is.LessThan(baseValue).And.GreaterThanOrEqualTo(cutoff.MinValue));
                Assert.That(t.High, Is.GreaterThan(baseValue).And.LessThanOrEqualTo(cutoff.MaxValue));

                // A hand moves the base: the modulation rides on the new value.
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                cutoff.SetValue(1 << 16, baseValue / 2);
                audio.Read(50);
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Assert.That(mod.Targets.Single().Base, Is.EqualTo(baseValue / 2), "an edit moves the base");

                mod.RemoveTarget(slot);
                Assert.That(cutoff.GetValue(0), Is.EqualTo(baseValue / 2), "the base is back");
                Assert.That(mod.Targets, Is.Empty);
                audio.Read(50);
                Assert.That(cutoff.GetValue(0), Is.EqualTo(baseValue / 2), "and stays");
                Assert.That(stack.CanUndo, Is.EqualTo(couldUndo), "removing it is no undo step either");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ safety

        [AvaloniaTest]
        public void TheMasterVolumeIsNeverPushedPastZeroDbAndTheMastersTempoIsRefused()
        {
            NewSong();
            try
            {
                var (_, _) = PlayingFilter();
                var master = Core.SongCore.MachinesList[0];
                var volume = Param(master, "Volume");
                var lfo = Add("Neo LFO", "Mod LFO");
                var mod = Mod(lfo);
                Param(lfo, "Rate").SetValue(1 << 16, 800);
                volume.SetValue(1 << 16, 0);                       // 0 dB: the top
                Assert.That(mod.SetTarget(-1, master, volume, 0, Info(100), out _), Is.Null, "the volume can be modulated");
                var seen = new List<int>();
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                audio.Read(100);
                audio.Read(300, () => seen.Add(volume.GetValue(0)));
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Assert.That(seen.Min(), Is.GreaterThanOrEqualTo(0), "never above 0 dB (raw 0 is 0 dB)");
                Assert.That(seen.Max(), Is.LessThanOrEqualTo(0x4000));
                Assert.That(seen.Distinct().Count(), Is.GreaterThan(5), "downwards it moves");
                mod.RemoveTarget(0);
                Assert.That(volume.GetValue(0), Is.EqualTo(0), "back at 0 dB");

                foreach (var name in new[] { "BPM", "TPB" })
                    Assert.That(mod.SetTarget(-1, master, Param(master, name), 0, Info(50), out _), Is.Not.Null, name + " is refused");
                Assert.That(mod.SetTarget(-1, lfo, Param(lfo, "Rate"), 0, Info(50), out _), Is.Not.Null, "a modulator doesn't move itself");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ renamed, deleted

        [AvaloniaTest]
        public void ATargetRenamedIsFollowedAndOneDeletedIsMissing()
        {
            NewSong();
            try
            {
                var (_, filter) = PlayingFilter();
                var lfo = Add("Neo LFO", "Mod LFO");
                var mod = Mod(lfo);
                Param(lfo, "Rate").SetValue(1 << 16, 700);
                var cutoff = Param(filter, "Cutoff");
                Assert.That(mod.SetTarget(-1, filter, cutoff, 0, Info(40), out _), Is.Null);
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                audio.Read(100);
                Assert.That(mod.Targets.Single().State, Is.EqualTo(ModulationState.Active));

                filter.Name = "Renamed Filter";
                Wait(() => mod.Targets.Single().Machine == "Renamed Filter");
                audio.Read(100);
                var t = mod.Targets.Single();
                Assert.That(t.Machine, Is.EqualTo("Renamed Filter"), "the target follows the rename");
                Assert.That(t.State, Is.EqualTo(ModulationState.Active));
                Assert.That(ModulationBoard.Incoming(filter).Count, Is.EqualTo(1), "and the machine lists it as incoming");

                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Core.SongCore.DeleteMachines(new[] { filter });
                Pump();
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                Wait(() => mod.Targets.Count == 0 || mod.Targets.Single().State != ModulationState.Active);
                audio.Read(100);                                    // the modulator keeps working
                Assert.That(mod.Targets.All(x => x.State == ModulationState.MachineMissing), Is.True, "a deleted target is missing, not an error");
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ save and open

        [AvaloniaTest]
        public void ASongKeepsItsModulatorsTargetsAndBases()
        {
            NewSong();
            string path = Path.Combine(Path.GetTempPath(), "neo-modulators-" + Guid.NewGuid().ToString("N") + ".bmx");
            try
            {
                var (_, filter) = PlayingFilter();
                var step = Add("Neo Step", "Mod Step");
                var mod = Mod(step);
                var cutoff = Param(filter, "Cutoff");
                var reso = Param(filter, "Resonance");
                int baseValue = (cutoff.MinValue + cutoff.MaxValue) / 3;
                cutoff.SetValue(1 << 16, baseValue);
                Assert.That(mod.SetTarget(-1, filter, cutoff, 0, new ModulationInfo { Depth = -35, Bipolar = false, Curve = 20 }, out _), Is.Null);
                Assert.That(mod.SetTarget(-1, filter, reso, 0, new ModulationInfo { Depth = 60, Bipolar = true, RangeMin = reso.MinValue, RangeMax = reso.MaxValue / 2 }, out _), Is.Null);
                using (var audio = new AudioThread(1))
                {
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    audio.Read(200);
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
                Assert.That(mod.Targets.Select(t => t.Base).First(), Is.EqualTo(baseValue));

                Core.SaveSongCopy(path);
                Core.Modified = false;
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Core.OpenSongFile(path);
                Pump();
                var opened = Core.SongCore.MachinesList.Single(m => m.Name == "Mod Step");
                var again = Mod(opened);
                Assert.That(again, Is.Not.SameAs(mod), "the song was opened");
                using (var audio = new AudioThread(1))
                {
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    audio.Read(200);
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
                Wait(() => again.Targets.Count == 2 && again.Targets.All(t => t.State == ModulationState.Active));
                var targets = again.Targets;
                Assert.That(targets.Count, Is.EqualTo(2), "both targets are back");
                Assert.That(targets[0].Param, Is.EqualTo("Cutoff"));
                Assert.That(targets[0].Depth, Is.EqualTo(-35));
                Assert.That(targets[0].Bipolar, Is.False);
                Assert.That(targets[0].Curve, Is.EqualTo(20));
                Assert.That(targets[0].Base, Is.EqualTo(baseValue), "the base came back (the song holds the modulated value, the modulator the base)");
                Assert.That(targets[1].Param, Is.EqualTo("Resonance"));
                Assert.That(targets[1].Depth, Is.EqualTo(60));
                Assert.That(targets[1].RangeMax, Is.EqualTo(reso.MaxValue / 2));
                Assert.That(targets.All(t => t.State == ModulationState.Active), Is.True);
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Dialogs.Responder = null;
                try { File.Delete(path); } catch (IOException) { }
                NewSong();
            }
        }

        // ------------------------------------------------------------------ allocation

        /// <summary>The first <paramref name="n"/> parameters of a machine's globals that take enough values to hear a swing in.</summary>
        static List<IParameter> Movable(IMachine m, int n) =>
            m.ParameterGroups[1].Parameters.Where(p => p.MaxValue - p.MinValue >= 100 && p.Name != "Volume" && p.Name != "Note").Take(n).ToList();

        [AvaloniaTest]
        public void FourModulatorsWithEightTargetsEachAllocateNothingOnTheAudioThread()
        {
            NewSong();
            try
            {
                var polys = new List<MachineCore>();
                for (int i = 0; i < 8; i++)
                {
                    var poly = AddPoly("Mod Poly " + i);
                    poly.CreatePattern("00", 16);
                    var pattern = (PatternCore)poly.Patterns.First();
                    Core.SessionLayout.EditNotes(pattern, null, Enumerable.Range(0, 16).Select(k => new ClipNote(k * TB, TB / 2, 48 + (k * 5 + i) % 12, 60 + k * 4)).ToArray());
                    Arrange(poly, pattern);
                    polys.Add(poly);
                }
                var mods = new[] { Add("Neo LFO", "Mod LFO"), Add("Neo Envelope", "Mod Env"), Add("Neo Step", "Mod Step"), Add("Neo Macro", "Mod Macro") };
                Param(mods[0], "Rate").SetValue(1 << 16, 800);
                Param(mods[1], "Mode").SetValue(1 << 16, 1);                       // Follower: the output level of the machine it listens to
                Param(mods[2], "Random").SetValue(1 << 16, 400);
                var targets = new List<(IParameter P, int Base)>();
                for (int k = 0; k < mods.Length; k++)
                {
                    var mod = Mod(mods[k]);
                    if (k == 1) mod.ListenMachine = polys[0].Name;
                    var chosen = Movable(polys[k], 8);
                    Assert.That(chosen.Count, Is.EqualTo(8), "Neo Poly has parameters to move");
                    foreach (var p in chosen)
                    {
                        p.SetValue(1 << 16, (p.MinValue + p.MaxValue) / 2);
                        Assert.That(mod.SetTarget(-1, polys[k], p, 0, Info(30), out _), Is.Null, $"{mods[k].Name} → {p.Name}");
                        targets.Add((p, (p.MinValue + p.MaxValue) / 2));
                    }
                }
                Param(mods[3], "Value").SetValue(1 << 16, 0);
                int n = 0;
                foreach (int algorithm in new[] { 0, 1, 2 })
                {
                    using var audio = new AudioThread(algorithm);
                    lock (ReBuzzCore.AudioLock) Core.Playing = true;
                    var cost = Steady(audio, perBuffer: () => Param(mods[3], "Value").SetValue(1 << 16, (n++ % 50) * 20));   // the macro's knob, moved every buffer
                    AssertNoAllocation(cost, $"4 modulators with 8 targets each over 8 playing machines (algorithm {algorithm})");
                    lock (ReBuzzCore.AudioLock) Core.Playing = false;
                }
                int moved = targets.Count(t => t.P.GetValue(0) != t.Base);
                Assert.That(moved, Is.GreaterThan(targets.Count / 2), $"the targets move ({moved} of {targets.Count})");
                foreach (var m in mods) foreach (var t in Mod(m).Targets) Assert.That(t.State, Is.EqualTo(ModulationState.Active), m.Name);
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                NewSong();
            }
        }

        // ------------------------------------------------------------------ the studio

        [AvaloniaTest]
        public void TheCanvasDrawsADashedWireAndTheDeviceViewARing()
        {
            var (window, view) = MachineViewTests.OpenEmpty();
            try
            {
                var (_, filter) = PlayingFilter();
                var lfo = Add("Neo LFO", "Mod LFO", 0.2f, 0.2f);
                var mod = Mod(lfo);
                Param(lfo, "Rate").SetValue(1 << 16, 600);
                var cutoff = Param(filter, "Cutoff");
                cutoff.SetValue(1 << 16, (cutoff.MinValue + cutoff.MaxValue) / 2);
                Assert.That(mod.SetTarget(-1, filter, cutoff, 0, Info(50), out _), Is.Null);
                using var audio = new AudioThread(1);
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                audio.Read(150);
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Wait(() => mod.Targets.Any(t => t.State == ModulationState.Active) && mod.Targets[0].High > mod.Targets[0].Low);
                MachineViewTests.Settle();

                var canvas = window.GetVisualDescendants().OfType<MachineCanvas>().First();
                var link = canvas.ModLinks.Single();
                Assert.That(link.Source, Is.SameAs(lfo));
                Assert.That(link.Target, Is.SameAs(filter));
                Assert.That(link.Label, Does.Contain("Cutoff").And.Contain("+50"));
                Assert.That(MachineIdentity.CategoryOf(lfo), Is.EqualTo(NeoBuzz.Editor.MachineCategory.Control), "the wire wears the Control identity colour");

                // The device view of the target: the ring.
                var device = new DeviceView(filter);
                var host = new Window { Width = 760, Height = 700, Content = device };
                host.Show();
                MachineViewTests.Settle();
                var row = device.Layout.All.First(p => p.Parameter == cutoff);
                device.List.Reveal(row);
                MachineViewTests.Settle();
                Assert.That(device.List.IsModulated(row), Is.True, "the modulated parameter has a ring");
                Assert.That(device.List.IsModulated(device.Layout.All.First(p => p.Parameter == Param(filter, "Resonance"))), Is.False, "another one has none");
                host.Close();

                mod.RemoveTarget(0);
                Wait(() => !canvas.ModLinks.Any());
                Assert.That(canvas.ModLinks, Is.Empty, "no target, no wire");
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                MachineViewTests.Close(window);
                NewSong();
            }
        }
    }
}
