using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using ReBuzz.AppViews.Session;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Linq;
using System.Threading;
using System.Windows.Input;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: Session View mixer (docs/specs/session-view.md 0.4). The mixer edits the
    /// machine graph itself, so these tests check connections: volume and pan are the output
    /// connection's amp and pan (both ways), sends and cue are connections to the return and cue
    /// machines, meters read the post-fader peak, and machine groups get a column that launches
    /// their members' clips.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionMixerTests
    {
        const int Bar = 16;

        /// <summary>
        /// Drums and Bass play into a Bus effect (their machine group's output), Lead and the Bus
        /// into the master, a Reverb effect (return) into the master; Phones (the cue output) is
        /// connected to nothing, as it would play to a second sound card output.
        /// </summary>
        sealed class MixRig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public SynthController DrumsSynth { get; } = SynthController.NewInstance("Drums");
            public SynthController BassSynth { get; } = SynthController.NewInstance("Bass");
            public SynthController LeadSynth { get; } = SynthController.NewInstance("Lead");
            public MachineCore Drums { get; }
            public MachineCore Bass { get; }
            public MachineCore Lead { get; }
            public MachineCore Bus { get; }
            public MachineCore Reverb { get; }
            public MachineCore Phones { get; }

            public MixRig(bool groupBus = true)
            {
                var bus = EffectController.NewInstance("Bus");
                var reverb = EffectController.NewInstance("Reverb");
                var phones = EffectController.NewInstance("Phones");
                Driver.Gear.AddDynamicGenerator(SynthController.Info);
                Driver.Gear.AddDynamicEffect(EffectController.Info);
                Driver.Start();

                var graph = Driver.MachineGraph;
                graph.InsertMachineInstanceConnectedToMasterFor(bus);
                graph.InsertMachineInstanceConnectedToMasterFor(reverb);
                graph.InsertMachineInstanceFor(phones);
                graph.InsertMachineInstanceFor(DrumsSynth);
                graph.InsertMachineInstanceFor(BassSynth);
                graph.InsertMachineInstanceConnectedToMasterFor(LeadSynth);
                graph.Connect(DrumsSynth, bus);
                graph.Connect(BassSynth, bus);

                Drums = Driver.MachineFor(DrumsSynth);
                Bass = Driver.MachineFor(BassSynth);
                Lead = Driver.MachineFor(LeadSynth);
                Bus = Driver.MachineFor(bus);
                Reverb = Driver.MachineFor(reverb);
                Phones = Driver.MachineFor(phones);

                var song = Core.SongCore;
                foreach (var m in new[] { Drums, Bass, Lead })
                    if (!song.SequencesList.Any(s => s.Machine == m))
                        song.AddSequence(m, song.SequencesList.Count);
                Core.Looping = true;

                if (groupBus)
                {
                    song.CreateMachineGroup("Rhythm", 0, 0);
                    var group = song.MachineGroups.Single();
                    foreach (var m in new[] { Drums, Bass, Bus })
                        song.AddMachineToGroup(m, group);
                    group.MainOutputMachine = Bus;
                }
            }

            public IMachineGroup Group => Core.SongCore.MachineGroups.Single();

            public PatternCore Pattern(MachineCore machine, string name, int length)
            {
                machine.CreatePattern(name, length);
                return machine.PatternsList.Single(p => p.Name == name);
            }

            public void Play(SynthController synth, float left, float right) =>
                Driver.MachineGraph.ExecuteMachineCommand(synth.SetStereoSampleValueTo(new Sample(left, right)));

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

        static IMachineConnection Output(IMachine from, IMachine to) => from.Outputs.Single(o => o.Destination == to);

        // ------------------------------------------------------------------ scales

        [Test]
        public void FaderScaleIsTheMachineViewsAmpKnob()
        {
            Assert.That(SessionMixer.AmpOf(SessionMixer.UnityFader), Is.EqualTo(SessionMixer.UnityAmp));
            Assert.That(SessionMixer.UnityFader, Is.EqualTo(66 / (66 + 20 * Math.Log10((double)0xFFFE / 0x4000))).Within(1e-9));
            Assert.That(SessionMixer.AmpOf(0), Is.EqualTo(0));
            Assert.That(SessionMixer.AmpOf(1), Is.EqualTo(SessionMixer.MaxAmp));
            Assert.That(SessionMixer.FaderOf(0), Is.EqualTo(0));
            foreach (int amp in new[] { 0x100, 0x1000, 0x2000, 0x3000, 0x4000, 0x8000, 0xC000 })
                Assert.That(SessionMixer.AmpOf(SessionMixer.FaderOf(amp)), Is.EqualTo(amp).Within(1), $"amp {amp:X}");

            Assert.That(SessionMixer.FormatAmp(0x4000), Is.EqualTo("0.0"));
            Assert.That(SessionMixer.FormatAmp(0x2000), Is.EqualTo("-6.0"));
            Assert.That(SessionMixer.FormatAmp(0x8000), Is.EqualTo("+6.0"));
            Assert.That(SessionMixer.FormatAmp(0), Is.EqualTo("-inf"));
            Assert.That(SessionMixer.FormatPan(0), Is.EqualTo("100L"));
            Assert.That(SessionMixer.FormatPan(0x4000), Is.EqualTo("C"));
            Assert.That(SessionMixer.FormatPan(0x6000), Is.EqualTo("50R"));
            Assert.That(SessionMixer.PanValue(SessionMixer.PanOf(0x2000)), Is.EqualTo(0x2000));

            Assert.That(SessionMixer.MasterGainOf(SessionMixer.MasterFaderOf(0.5)), Is.EqualTo(0.5).Within(1e-9));
            Assert.That(SessionMixer.MasterFaderOf(1), Is.EqualTo(1));
            Assert.That(SessionMixer.MeterLevel(32768), Is.EqualTo(SessionMixer.LevelOfDb(0)).Within(1e-9));
            Assert.That(SessionMixer.MeterLevel(0), Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ volume and pan

        [Test]
        public void VolumeAndPanAreTheOutputConnectionsAmpAndPan()
        {
            using var rig = new MixRig();
            var doc = rig.Core.SessionLayout.Ensure();
            var toMaster = Output(rig.Lead, rig.Core.SongCore.MachinesList.First(m => m.Name == "Master"));

            SessionMixer.SetAmp(doc, rig.Lead, 0x2000);
            Assert.That(toMaster.Amp, Is.EqualTo(0x2000), "the session's fader sets the connection");
            toMaster.Amp = 0x1000;                                     // as the Machine View's knob does
            Assert.That(SessionMixer.GetAmp(doc, rig.Lead), Is.EqualTo(0x1000), "and reads it back");

            SessionMixer.SetPan(doc, rig.Lead, SessionMixer.PanValue(-0.5));
            Assert.That(toMaster.Pan, Is.EqualTo(0x2000));
            toMaster.Pan = 0x8000;
            Assert.That(SessionMixer.GetPan(doc, rig.Lead), Is.EqualTo(0x8000));

            // The Drums' fader is its connection into the Bus, not the master.
            SessionMixer.SetAmp(doc, rig.Drums, 0x3000);
            Assert.That(Output(rig.Drums, rig.Bus).Amp, Is.EqualTo(0x3000));
        }

        [Test]
        public void StripsFollowTheMachineViewAndWriteToIt()
        {
            using var rig = new MixRig();
            rig.Core.SessionLayout.Ensure();
            var view = new SessionView(rig.Core);
            view.Rebuild();
            var strip = view.MixerStrips.Single(s => s.Kind == SessionView.StripKind.Track && s.Machine == rig.Lead);
            var toMaster = rig.Lead.Outputs.Single();

            toMaster.Amp = 0x2000;
            toMaster.Pan = 0x6000;
            view.RefreshState();
            Assert.That(strip.Fader.Value, Is.EqualTo(SessionMixer.FaderOf(0x2000)).Within(1e-9));
            Assert.That(strip.DbText, Is.EqualTo("-6.0 dB"));
            Assert.That(strip.PanBar.ValueText, Is.EqualTo("50R"));

            strip.Fader.SetByUser(SessionMixer.FaderOf(0x1000));
            Assert.That(toMaster.Amp, Is.EqualTo(0x1000).Within(1));
            strip.PanBar.SetByUser(0.5);
            Assert.That(toMaster.Pan, Is.EqualTo(0x4000));

            var master = view.MixerStrips.Single(s => s.Kind == SessionView.StripKind.Master);
            master.Fader.SetByUser(SessionMixer.MasterFaderOf(0.5));
            Assert.That(rig.Core.MasterVolume, Is.EqualTo(0.5).Within(1e-6));
        }

        [Test]
        public void SeveralMainOutputsKeepTheirBalance()
        {
            using var rig = new MixRig();
            var doc = rig.Core.SessionLayout.Ensure();
            rig.Core.SongCore.ConnectMachines(rig.Lead, rig.Bus, new[] { 0 }, new[] { 0 }, 0x2000, 0x4000);
            var toMaster = Output(rig.Lead, rig.Core.SongCore.MachinesList.First(m => m.Name == "Master"));
            var toBus = Output(rig.Lead, rig.Bus);

            SessionMixer.SetAmp(doc, rig.Lead, 0x2000);           // -6 dB on both

            Assert.That(toMaster.Amp, Is.EqualTo(0x2000));
            Assert.That(toBus.Amp, Is.EqualTo(0x1000));
        }

        // ------------------------------------------------------------------ sends and cue

        [Test]
        public void SendsAreConnectionsToReturnMachines()
        {
            using var rig = new MixRig();
            var layout = rig.Core.SessionLayout;
            var doc = layout.Ensure();
            layout.AddReturn(rig.Reverb);
            Assert.That(layout.ReturnMachines(), Is.EqualTo(new[] { rig.Reverb }));

            Assert.That(SessionMixer.CanSend(rig.Lead, rig.Reverb), Is.True);
            Assert.That(SessionMixer.GetSend(rig.Lead, rig.Reverb), Is.EqualTo(0));
            SessionMixer.SetSend(rig.Lead, rig.Reverb, 0x2000);
            Assert.That(Output(rig.Lead, rig.Reverb).Amp, Is.EqualTo(0x2000), "raising a send connects the track to the return");
            Assert.That(SessionMixer.MainOutputs(doc, rig.Lead).Select(o => o.Destination.Name), Is.EqualTo(new[] { "Master" }), "a send is not a main output");

            SessionMixer.SetAmp(doc, rig.Lead, 0x1000);
            Assert.That(Output(rig.Lead, rig.Reverb).Amp, Is.EqualTo(0x2000), "the fader leaves sends alone");
            Assert.That(SessionMixer.CanSend(rig.Reverb, rig.Lead), Is.False, "no send into a generator");

            rig.Core.SongCore.Undo();                                 // the Machine View's undo
            Assert.That(SessionMixer.ConnectionTo(rig.Lead, rig.Reverb), Is.Null);

            layout.Undo();                                            // the session's undo
            Assert.That(layout.ReturnMachines(), Is.Empty);
        }

        [Test]
        public void CueConnectsToTheCueMachine()
        {
            using var rig = new MixRig();
            var layout = rig.Core.SessionLayout;
            var doc = layout.Ensure();
            layout.SetCueMachine(rig.Phones);
            layout.AddReturn(rig.Phones);
            Assert.That(doc.Returns, Is.Empty, "the cue output can't also be a return");

            Assert.That(SessionMixer.SetCue(rig.Lead, rig.Phones, true), Is.True);
            Assert.That(Output(rig.Lead, rig.Phones).Amp, Is.EqualTo(SessionMixer.UnityAmp));
            Assert.That(SessionMixer.IsCued(rig.Lead, rig.Phones), Is.True);
            Assert.That(SessionMixer.MainOutputs(doc, rig.Lead).Count, Is.EqualTo(1), "cue is not a main output");

            SessionMixer.SetCue(rig.Lead, rig.Phones, false);
            Assert.That(SessionMixer.IsCued(rig.Lead, rig.Phones), Is.False);
        }

        // ------------------------------------------------------------------ meters

        [Test]
        public void MetersShowThePostFaderPeak()
        {
            using var rig = new MixRig(groupBus: false);
            var doc = rig.Core.SessionLayout.Ensure();
            var toMaster = (MachineConnectionCore)rig.Lead.Outputs.Single();
            rig.Play(rig.LeadSynth, 16384, -8192);

            rig.Driver.ReadStereoSamples(256);
            Assert.That(toMaster.TakePeak(), Is.EqualTo((0f, 0f)), "nothing is measured while no meter is shown");

            SessionMixer.UseMeters(true);
            try
            {
                rig.Driver.ReadStereoSamples(256);
                var peak = SessionMixer.MainPeak(doc, rig.Lead);
                Assert.That(peak.Left, Is.EqualTo(16384).Within(1));
                Assert.That(peak.Right, Is.EqualTo(8192).Within(1));
                Assert.That(SessionMixer.MeterLevel(peak.Left), Is.EqualTo(SessionMixer.LevelOfDb(-6.02)).Within(0.001));

                SessionMixer.SetAmp(doc, rig.Lead, 0x2000);           // -6 dB, ramped over a few blocks
                rig.Driver.ReadStereoSamples(1500);
                toMaster.TakePeak();
                rig.Driver.ReadStereoSamples(256);
                var after = toMaster.TakePeak();
                Assert.That(after.Left, Is.EqualTo(8192).Within(1), "post-fader");
                Assert.That(after.Right, Is.EqualTo(4096).Within(1));
            }
            finally
            {
                SessionMixer.UseMeters(false);
            }
        }

        [Test]
        public void AmpChangesRampSmoothly()
        {
            using var rig = new MixRig(groupBus: false);
            var toMaster = rig.Lead.Outputs.Single();
            rig.Play(rig.LeadSynth, 16384, 16384);
            float before = rig.Driver.ReadStereoSamples(256).Values[0];

            toMaster.Amp = 0x2000;                                     // -6 dB: ramps over a few blocks
            var left = rig.Driver.ReadStereoSamples(1536).Values.Where((v, i) => i % 2 == 0).ToArray();

            for (int i = 1; i < left.Length; i++)
                Assert.That(left[i], Is.LessThanOrEqualTo(left[i - 1] + Math.Abs(before) * 1e-4), $"sample {i} rises: a jump");
            Assert.That(left.Max(), Is.LessThanOrEqualTo(before * 1.0001f), "no overshoot above the old level");
            Assert.That(left[^1], Is.EqualTo(before / 2).Within(Math.Abs(before) * 1e-3), "ends at the new level");
        }

        // ------------------------------------------------------------------ groups

        [Test]
        public void GroupsGetAColumnThatLaunchesTheirClipsAndFolds()
        {
            using var rig = new MixRig();
            var beat = rig.Pattern(rig.Drums, "Beat", Bar);
            var line = rig.Pattern(rig.Bass, "Line", Bar);
            var hook = rig.Pattern(rig.Lead, "Hook", Bar);
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.AddReturn(rig.Reverb);
            var view = new SessionView(rig.Core);
            view.Rebuild();

            Assert.That(view.DescribeColumns(), Is.EqualTo("[Rhythm] Drums Bass Lead A:Reverb"));
            var groupStrip = view.MixerStrips.Single(s => s.Kind == SessionView.StripKind.Group);
            Assert.That(groupStrip.Machine, Is.SameAs(rig.Bus), "the group's fader is its output machine's");

            view.LaunchGroupSlot(view.GroupSlots.Single(c => c.Scene == 0));
            rig.RunTicks(2);
            var session = rig.Core.Session;
            Assert.That(session.GetState(layout.SequenceOf(0)!, beat), Is.EqualTo(ClipState.Playing));
            Assert.That(session.GetState(layout.SequenceOf(1)!, line), Is.EqualTo(ClipState.Playing));
            Assert.That(session.GetPlaying(layout.SequenceOf(2)!), Is.Null, "Lead is not in the group");

            layout.SetGroupCollapsed("Rhythm", true);
            view.Rebuild();
            Assert.That(view.DescribeColumns(), Is.EqualTo("[Rhythm ▸] Lead A:Reverb"));

            // The keyboard skips folded tracks: the first selection is Lead's slot.
            view.HandleKey(Key.Down, ModifierKeys.None);
            view.HandleKey(Key.Enter, ModifierKeys.None);
            Assert.That(session.GetState(layout.SequenceOf(2)!, hook), Is.EqualTo(ClipState.Queued));

            view.HandleKey(Key.M, ModifierKeys.Control | ModifierKeys.Alt);
            Assert.That(layout.Document!.ShowMixer, Is.False);
        }

        [Test]
        public void MixerSettingsSurviveUndoAndRenames()
        {
            using var rig = new MixRig();
            var layout = rig.Core.SessionLayout;
            layout.Ensure();
            layout.AddReturn(rig.Reverb);                             // undoable
            layout.InsertScene(0);                                    // undoable
            layout.SetCueMachine(rig.Phones);                         // settings
            layout.SetGroupCollapsed("Rhythm", true);
            layout.Undo();                                            // back to before InsertScene

            var doc = layout.Document!;
            Assert.That(doc.Returns, Is.EqualTo(new[] { "Reverb" }));
            Assert.That(doc.CueMachine, Is.EqualTo("Phones"), "cue output and folding are settings, not undo steps");
            Assert.That(doc.CollapsedGroups, Does.Contain("Rhythm"));

            rig.Reverb.Name = "Hall";
            Assert.That(layout.Document!.Returns, Is.EqualTo(new[] { "Hall" }), "returns follow renames");
        }

        // ------------------------------------------------------------------ look

        [Test]
        public void RendersTheMixer()
        {
            using var rig = new MixRig();
            rig.Pattern(rig.Drums, "Beat", Bar);
            rig.Pattern(rig.Drums, "Fill", Bar);
            rig.Pattern(rig.Bass, "Line", 2 * Bar);
            rig.Pattern(rig.Lead, "Hook", Bar);
            var layout = rig.Core.SessionLayout;
            var doc = layout.Ensure();
            layout.RenameScene(0, "Intro");
            layout.AddReturn(rig.Reverb);
            layout.SetCueMachine(rig.Phones);

            SessionMixer.SetAmp(doc, rig.Bass, SessionMixer.AmpOf(SessionMixer.FaderOf(0x4000) - 0.06));
            SessionMixer.SetPan(doc, rig.Bass, SessionMixer.PanValue(-0.3));
            SessionMixer.SetSend(rig.Drums, rig.Reverb, 0x1800);
            SessionMixer.SetSend(rig.Lead, rig.Reverb, 0x2800);
            SessionMixer.SetCue(rig.Lead, rig.Phones, true);
            rig.Play(rig.DrumsSynth, 14000, 13000);
            rig.Play(rig.BassSynth, 7000, 5000);
            rig.Play(rig.LeadSynth, 22000, 30000);
            rig.Core.VUMeterLevel = Tuple.Create(0.93, 0.95);         // the master meter's timer doesn't run in tests

            var view = new SessionView(rig.Core);
            view.Rebuild();
            layout.LaunchScene(0);
            SessionMixer.UseMeters(true);
            try
            {
                rig.RunTicks(2);
                view.RefreshState();
                view.RefreshMeters();
            }
            finally
            {
                SessionMixer.UseMeters(false);
            }
            SessionViewTests.Screenshot(view, "session-view-mixer", 980, 560);

            var lead = view.MixerStrips.Single(s => s.Machine == rig.Lead && s.Kind == SessionView.StripKind.Track);
            Assert.That(lead.Meter.Left, Is.GreaterThan(0.8), "Lead's meter shows its level");
            Assert.That(lead.Sends.Single().ValueText, Is.EqualTo(SessionMixer.FormatAmp(0x2800)));
        }
    }
}
