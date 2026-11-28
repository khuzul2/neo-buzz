// SYN1a (docs/sdk/tempo-sync.md): what the shared tempo-time control shows and writes, decided in the toolkit-free TimingModel.

using System.Linq;
using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NUnit.Framework;

namespace NeoBuzz.MachineTests
{
    public class TimingModelTests
    {
        static TimingModel Lfo() => new TimingModel(NeoBuzz.LfoMachine.Params.Table[NeoBuzz.LfoMachine.P.Sync], NeoBuzz.LfoMachine.Params.Table[NeoBuzz.LfoMachine.P.Feel], null, outputHz: true);
        static TimingModel Arp() => new TimingModel(NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Rate], NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Feel], NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Sync], outputHz: false);
        static TimingModel Filter() => new TimingModel(NeoBuzz.Filter.Params.Table[NeoBuzz.Filter.P.LfoSync], NeoBuzz.Filter.Params.Table[NeoBuzz.Filter.P.LfoFeel], null, outputHz: true);
        static TimingModel Delay() => new TimingModel(NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Sync], NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Feel], null, outputHz: false, maxSeconds: NeoBuzz.Delay.DelayDesign.MaxSeconds);

        [Test]
        public void EveryMachineShowsTheSameDivisionsInTheSameOrder()
        {
            var want = new[] { "8 bars", "4 bars", "2 bars", "1 bar", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64" };
            string[] Labels(TimingModel m) => m.Divisions.Select(e => TempoSync.Label(e.DivisionBeats, 0)).ToArray();
            Assert.That(Labels(Lfo()), Is.EqualTo(want), "stored long to short");
            Assert.That(Labels(Delay()), Is.EqualTo(want), "stored short to long, then 4 and 8 bars appended");
            Assert.That(Labels(Arp()), Is.EqualTo(want), "stored long to short, bars appended at the end");
            Assert.That(Labels(new TimingModel(NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L1Sync], NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L1Feel], null, true)), Is.EqualTo(want), "inline T entries are not offered");
            Assert.That(Labels(new TimingModel(NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Rate], NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Feel], NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Sync], false)), Is.EqualTo(want));
            Assert.That(Labels(new TimingModel(NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoRate], NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoFeel], NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoSync], false)), Is.EqualTo(want));
            // The longer entries Neo Filter and Neo Ensemble have kept come last, after 1/64.
            Assert.That(Labels(Filter()), Is.EqualTo(want.Concat(new[] { "16 bars" })));
            Assert.That(Labels(new TimingModel(NeoBuzz.Ensemble.Params.Table[NeoBuzz.Ensemble.P.Sync], NeoBuzz.Ensemble.Params.Table[NeoBuzz.Ensemble.P.Feel], null, true)), Is.EqualTo(want.Concat(new[] { "16 bars" })));
            Assert.That(Lfo().FeelNames, Is.EqualTo(new[] { "Straight", "Dotted", "Triplet", "Quintuplet" }));
        }

        [Test]
        public void FreeIsAnEntryOfTheListOrASwitch()
        {
            var a = Lfo();
            Assert.That(a.IsFree(0, 0), Is.True);
            Assert.That(a.IsFree(7, 0), Is.False);
            Assert.That(a.DivisionText(0, 0, 0), Is.EqualTo("Free"));
            var b = Arp();
            Assert.That(b.IsFree(4, 1), Is.True, "Free time");
            Assert.That(b.IsFree(4, 0), Is.False, "Song tempo");
            Assert.That(a.PickFree().Division, Is.EqualTo(0));
            Assert.That(b.PickFree().Mode, Is.EqualTo(1));
            Assert.That(b.PickSync(4, 0).Mode, Is.EqualTo(0));
        }

        [Test]
        public void SyncGoesBackToTheLastDivisionOrAQuarterNote()
        {
            var a = Lfo();
            Assert.That(a.PickSync(0, 8).Division, Is.EqualTo(8), "the last one it had");
            Assert.That(a.PickSync(0, int.MinValue).Division, Is.EqualTo(6), "1/4 in the stored list");
            Assert.That(a.PickSync(5, 8).Division, Is.Null, "already synced: nothing to write");
        }

        [Test]
        public void AnOldInlineEntryReadsAsItsDivisionAndFeel()
        {
            var f = Filter();
            // Stored 1/16T (index 2) with Straight: the picker says 1/16, the Feel says Triplet.
            Assert.That(f.DivisionText(2, 0, 0), Is.EqualTo("1/16"));
            Assert.That(f.ShownFeel(2, 0), Is.EqualTo(2));
            Assert.That(f.Shown(2, 0, out _).Index, Is.EqualTo(3), "1/16 plain");
            // Stored 1/8D (index 7).
            Assert.That(f.DivisionText(7, 0, 0), Is.EqualTo("1/8"));
            Assert.That(f.ShownFeel(7, 0), Is.EqualTo(1));
            // The time is unchanged by showing it this way.
            Assert.That(f.Beats(2, 0, 0), Is.EqualTo(0.25 * (2.0 / 3.0)).Within(0));
        }

        [Test]
        public void SettingAFeelOnAnInlineEntryFirstMakesItPlain()
        {
            var f = Filter();
            var w = f.PickFeel(2, 0, 3);                                     // 1/16T, then Quintuplet
            Assert.That(w.Division, Is.EqualTo(3), "1/16 plain");
            Assert.That(w.Feel, Is.EqualTo(3));
            Assert.That(f.Beats(3, 3, 0), Is.EqualTo(0.25 * 0.8).Within(0), "1/16 quintuplet, not triplet x quintuplet");
            var w2 = f.PickFeel(3, 0, 1);                                    // a plain entry: only the Feel
            Assert.That(w2.Division, Is.Null);
            Assert.That(w2.Feel, Is.EqualTo(1));
        }

        [Test]
        public void PickingADivisionKeepsTheFeelTheOldEntryHad()
        {
            var f = Filter();
            var eighth = f.Divisions.First(e => e.Beats == 0.5);
            var w = f.PickDivision(2, 0, eighth);                            // 1/16T, then 1/8
            Assert.That(w.Division, Is.EqualTo(eighth.Index));
            Assert.That(w.Feel, Is.EqualTo(2), "still a triplet: 1/8 triplet");
            var plain = f.PickDivision(3, 1, eighth);                        // 1/16 + Dotted, then 1/8: the Feel stays as it is
            Assert.That(plain.Feel, Is.Null);
        }

        [Test]
        public void PickingADivisionInSongTempoSwitchesFreeOff()
        {
            var a = Arp();
            var q = a.Divisions.First(e => e.Beats == 1);
            Assert.That(a.PickDivision(4, 0, q).Mode, Is.EqualTo(0));
        }

        [Test]
        public void TheReadoutSaysWhatItComesTo()
        {
            var a = Lfo();
            int eighth = 7;
            Assert.That(a.Readout(eighth, 1, 0, 120, "2.00 Hz"), Is.EqualTo("1/8 dotted = 2.67 Hz at 120 BPM"));
            Assert.That(a.Readout(0, 0, 0, 120, "2.00 Hz"), Is.EqualTo("2.00 Hz"), "Free: the knob's own value");
            Assert.That(a.Readout(eighth, 3, 0, 100, ""), Does.StartWith("1/8 quintuplet = "));
            var d = Delay();
            int dotted = 1;
            Assert.That(d.Readout(4, dotted, 0, 128, "x"), Does.StartWith("1/8 dotted = 352 ms"), "the editor's own example: 0.3515625 s at 128 BPM");
        }

        [Test]
        public void ALongDivisionThatCannotFitIsGreyedAndClamped()
        {
            var d = Delay();
            var eightBars = d.Divisions.First(e => e.Beats == 32);
            var oneBar = d.Divisions.First(e => e.Beats == 4);
            Assert.That(d.Fits(eightBars, 0, 120), Is.False, "16 s at 120 BPM against a 10 s line");
            Assert.That(d.Fits(oneBar, 0, 120), Is.True);
            Assert.That(d.Fits(eightBars, 0, 200), Is.True, "9.6 s at 200 BPM");
            Assert.That(d.Fits(eightBars, 3, 200), Is.True, "x 4/5 is shorter");
            Assert.That(d.Clamped(10, 0, 0, 120), Is.True);
            Assert.That(d.Readout(10, 0, 0, 120, ""), Does.Contain("clamped to 10.0 s"));
            Assert.That(Lfo().Fits(Lfo().Divisions.First(), 0, 40), Is.True, "no longest time: always fits");
        }
    }
}
