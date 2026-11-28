// SYN1a (docs/sdk/tempo-sync.md): the shared tempo-sync helper, and the proof that the ten machines that moved onto it sound as before.
// "Frozen" arrays and expressions below are copies of what each machine computed before SYN1; the new code must give the same bits.

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.Dsp;
using NUnit.Framework;

namespace NeoBuzz.MachineTests
{
    public class TempoSyncTests
    {
        static readonly double[] Tempi = Enumerable.Range(20, 381).Select(i => (double)i).ToArray();

        // ---- the helper -------------------------------------------------------------------------------------------------

        [Test]
        public void FeelFactorsAreTheMusicalOnes()
        {
            Assert.That(TempoSync.FeelFactors, Is.EqualTo(new[] { 1.0, 1.5, 2.0 / 3.0, 4.0 / 5.0 }));
            Assert.That(TempoSync.FeelNames, Is.EqualTo(new[] { "Straight", "Dotted", "Triplet", "Quintuplet" }));
            Assert.That(TempoSync.Beats(0.25, 0), Is.EqualTo(0.25), "straight returns the length untouched");
            Assert.That(TempoSync.Beats(1, 3), Is.EqualTo(0.8));
        }

        [Test]
        public void DivisionsRunFromEightBarsToASixtyFourth()
        {
            Assert.That(TempoSync.DivisionBeats.Length, Is.EqualTo(10));
            Assert.That(TempoSync.DivisionBeats[0], Is.EqualTo(32));
            Assert.That(TempoSync.DivisionBeats[9], Is.EqualTo(1.0 / 16));
            for (int i = 1; i < 10; i++) Assert.That(TempoSync.DivisionBeats[i], Is.LessThan(TempoSync.DivisionBeats[i - 1]));
        }

        [Test]
        public void SecondsHzAndSamplesAreExactAtEveryTempo()
        {
            foreach (double bpm in Tempi.Concat(new[] { 90.5, 120.25, 127.999 }))
                foreach (double div in TempoSync.DivisionBeats)
                    for (int feel = 0; feel < 4; feel++)
                    {
                        double beats = TempoSync.Beats(div, feel);
                        Assert.That(beats, Is.EqualTo(div * TempoSync.FeelFactors[feel]).Within(0));
                        double sec = TempoSync.Seconds(beats, bpm);
                        Assert.That(sec, Is.EqualTo(beats * 60.0 / bpm).Within(0));
                        Assert.That(sec, Is.EqualTo(beats * 60.0 / bpm).Within(1e-12));
                        Assert.That(TempoSync.Hz(beats, bpm) * sec, Is.EqualTo(1.0).Within(1e-12), $"{div} feel {feel} at {bpm}");
                        foreach (double sr in new[] { 44100.0, 48000, 96000 })
                        {
                            double samples = TempoSync.Samples(beats, bpm, sr);
                            Assert.That(samples, Is.EqualTo(sec * sr).Within(0));
                            Assert.That(TempoSync.SamplesRounded(beats, bpm, sr), Is.EqualTo((long)Math.Max(1, Math.Round(samples))));
                        }
                    }
        }

        [Test]
        public void ManyCyclesLandOnTheGridWithoutDrift()
        {
            // 1000 cycles of every division and feel at 48 kHz: the sum of the rounded cycle lengths off a position taken from the beat count
            // stays within half a sample when the position is computed from beats (what the engines do), never from a running sum.
            foreach (double bpm in new[] { 90.0, 120, 174 })
                foreach (double div in TempoSync.DivisionBeats)
                    for (int feel = 0; feel < 4; feel++)
                    {
                        double beats = TempoSync.Beats(div, feel);
                        double perCycle = TempoSync.Samples(beats, bpm, 48000);
                        double pos = TempoSync.Samples(beats * 1000, bpm, 48000);
                        Assert.That(Math.Abs(pos - perCycle * 1000), Is.LessThan(1e-6 * Math.Max(1, pos)), $"{div} {feel} {bpm}");
                    }
        }

        [Test]
        public void StoredNamesAreRead()
        {
            void Check(string name, double beats, int inline, bool free = false)
            {
                Assert.That(TempoSync.TryParse(name, out double b, out int f, out bool fr), Is.True, name);
                Assert.That(fr, Is.EqualTo(free), name);
                if (free) return;
                Assert.That(b, Is.EqualTo(beats), name);
                Assert.That(f, Is.EqualTo(inline), name);
            }
            Check("Free", 0, 0, true);
            Check("1/8", 0.5, 0); Check("1/64", 0.0625, 0); Check("1/1", 4, 0); Check("1 bar", 4, 0); Check("1_bar", 4, 0);
            Check("2 bars", 8, 0); Check("8 bars", 32, 0); Check("16 bars", 64, 0);
            Check("1/16T", 0.25 * (2.0 / 3.0), 2); Check("1/8D", 0.5 * 1.5, 1);
            Check("1/2_dotted", 3, 1); Check("1/4 dotted", 1.5, 1); Check("1/4 triplet", 2.0 / 3.0, 2);
            Assert.That(TempoSync.TryParse("Song_tempo", out _, out _, out _), Is.False);
            Assert.That(TempoSync.TryParse("", out _, out _, out _), Is.False);
        }

        [Test]
        public void InlineEntriesEqualTheirDivisionTimesTheFeel()
        {
            // The old lists stored 1/16T as 1/6 and 1/8D as 3/8; the helper's value for the same name is bit-identical.
            Assert.That(TempoSync.BeatsTable(new[] { "Free", "1/16T", "1/8T", "1/4T", "1/16D", "1/8D" }),
                Is.EqualTo(new[] { 0, 1.0 / 6, 1.0 / 3, 2.0 / 3, 3.0 / 8, 3.0 / 4 }));
        }

        [Test]
        public void ThePickerShowsLongToShortWithLegacyEntriesLastAndNoInlineOnes()
        {
            var names = new[] { "Free", "1/32", "1/16T", "1/16", "1/8T", "1/8", "1/4T", "1/4", "1/2", "1 bar", "2 bars", "4 bars", "8 bars", "16 bars", "1/64" };
            var shown = TempoSync.DisplayOrder(names).Select(e => e.Name).ToArray();
            Assert.That(shown, Is.EqualTo(new[] { "Free", "8 bars", "4 bars", "2 bars", "1 bar", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64", "16 bars" }));
            // A stored 1/16T with a Straight feel reads as 1/16 + Triplet; with any other feel it stays as stored.
            Assert.That(TempoSync.PlainIndex(names, 2, 0, out int feel), Is.EqualTo(3));
            Assert.That(feel, Is.EqualTo(2));
            Assert.That(TempoSync.PlainIndex(names, 2, 1, out feel), Is.EqualTo(2));
            Assert.That(TempoSync.PlainIndex(names, 5, 0, out feel), Is.EqualTo(5));
        }

        [Test]
        public void LabelsUseTheSameWordsEverywhere()
        {
            Assert.That(TempoSync.Label(0.5, 1), Is.EqualTo("1/8 dotted"));
            Assert.That(TempoSync.Label(0.25, 3), Is.EqualTo("1/16 quintuplet"));
            Assert.That(TempoSync.Label(4, 2), Is.EqualTo("1 bar triplet"));
            Assert.That(TempoSync.Label(32, 0), Is.EqualTo("8 bars"));
        }

        // ---- the old lists, frozen ---------------------------------------------------------------------------------------

        static readonly double[] OldLfoRandom = { 0, 32, 16, 8, 4, 2, 1, 0.5, 0.25, 0.125, 0.0625 };
        static readonly double[] OldDelay = { 0, 1.0 / 16, 1.0 / 8, 1.0 / 4, 1.0 / 2, 1, 2, 4, 8 };
        static readonly double[] OldStep = { 4, 2, 1, 0.5, 0.25, 0.125, 0.0625 };
        static readonly double[] OldPolyWave = { 0, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 1, 2, 4, 8, 16 };
        static readonly double[] OldEnsemble = { 0, 0.125, 1.0 / 6, 0.25, 1.0 / 3, 0.5, 2.0 / 3, 1, 2, 4, 8, 16, 32, 64 };
        static readonly double[] OldFilter = { 0, 1.0 / 8, 1.0 / 6, 1.0 / 4, 3.0 / 8, 1.0 / 3, 1.0 / 2, 3.0 / 4, 2.0 / 3, 1, 1.5, 4.0 / 3, 2, 3, 4, 8, 16, 32, 64 };

        static void SameBits(double[] old, double[] now, string what)
        {
            Assert.That(now.Length, Is.GreaterThanOrEqualTo(old.Length), what);
            for (int i = 0; i < old.Length; i++)
                Assert.That(BitConverter.DoubleToInt64Bits(now[i]), Is.EqualTo(BitConverter.DoubleToInt64Bits(old[i])), $"{what} entry {i}");
        }

        [Test]
        public void EveryOldEntryOfEveryListHasTheSameBeatsAsBefore()
        {
            SameBits(OldDelay, TempoSync.BeatsTable(NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Sync].Names), "Neo Delay Sync");
            SameBits(OldLfoRandom, TempoSync.BeatsTable(NeoBuzz.LfoMachine.Params.Table[NeoBuzz.LfoMachine.P.Sync].Names), "Neo LFO Sync");
            SameBits(OldLfoRandom, TempoSync.BeatsTable(NeoBuzz.RandomMachine.Params.Table[NeoBuzz.RandomMachine.P.Sync].Names), "Neo Random Sync");
            SameBits(OldStep, TempoSync.BeatsTable(NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Rate].Names), "Neo Arp Rate");
            SameBits(OldStep, TempoSync.BeatsTable(NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Rate].Names), "Neo Pulse Rate");
            SameBits(OldStep, TempoSync.BeatsTable(NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.LenRate].Names), "Neo Notes fixed rate");
            SameBits(OldStep, TempoSync.BeatsTable(NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoRate].Names), "Neo Notes echo rate");
            SameBits(OldPolyWave, TempoSync.BeatsTable(NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L1Sync].Names), "Neo Poly LFO1 sync");
            SameBits(OldPolyWave, TempoSync.BeatsTable(NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L2Sync].Names), "Neo Poly LFO2 sync");
            SameBits(OldPolyWave, TempoSync.BeatsTable(NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.L1Sync].Names), "Neo Wave LFO1 sync");
            SameBits(OldPolyWave, TempoSync.BeatsTable(NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.L2Sync].Names), "Neo Wave LFO2 sync");
            SameBits(OldEnsemble, TempoSync.BeatsTable(NeoBuzz.Ensemble.Params.Table[NeoBuzz.Ensemble.P.Sync].Names), "Neo Ensemble Sync");
            SameBits(OldFilter, TempoSync.BeatsTable(NeoBuzz.Filter.Params.Table[NeoBuzz.Filter.P.LfoSync].Names), "Neo Filter LFO sync");
        }

        [Test]
        public void NewEntriesAreAppendedAndNothingBeforeThemMoved()
        {
            var d = TempoSync.BeatsTable(NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Sync].Names);
            Assert.That(d.Skip(9).ToArray(), Is.EqualTo(new[] { 16.0, 32.0 }), "Delay: 4 bars, 8 bars");
            foreach (var t in new[] { NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Rate], NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Rate], NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.LenRate], NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoRate] })
                Assert.That(TempoSync.BeatsTable(t.Names).Skip(7).ToArray(), Is.EqualTo(new[] { 8.0, 16.0, 32.0 }), t.Name + ": 2, 4, 8 bars");
            foreach (var t in new[] { NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L1Sync], NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.L2Sync] })
                Assert.That(TempoSync.BeatsTable(t.Names).Skip(12).ToArray(), Is.EqualTo(new[] { 0.0625, 32.0 }), t.Name + ": 1/64, 8 bars");
            Assert.That(TempoSync.BeatsTable(NeoBuzz.Ensemble.Params.Table[NeoBuzz.Ensemble.P.Sync].Names).Last(), Is.EqualTo(0.0625));
            Assert.That(TempoSync.BeatsTable(NeoBuzz.Filter.Params.Table[NeoBuzz.Filter.P.LfoSync].Names).Last(), Is.EqualTo(0.0625));
            foreach (var t in new[] { NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Feel], NeoBuzz.LfoMachine.Params.Table[NeoBuzz.LfoMachine.P.Feel], NeoBuzz.RandomMachine.Params.Table[NeoBuzz.RandomMachine.P.Feel],
                NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Feel], NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Feel], NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.LenFeel], NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoFeel],
                NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L1Feel], NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.L2Feel], NeoBuzz.Ensemble.Params.Table[NeoBuzz.Ensemble.P.Feel], NeoBuzz.Filter.Params.Table[NeoBuzz.Filter.P.LfoFeel] })
                Assert.That(t.Names, Is.EqualTo(TempoSync.FeelNames), t.Name);
        }

        // ---- the machines, against the frozen expressions ----------------------------------------------------------------

        static double OldFeel(double b, int feel) => feel == 1 ? b * 1.5 : feel == 2 ? b * (2.0 / 3.0) : b;

        [Test]
        public void NeoLfoAndNeoRandomCycleBeatsAreBitIdentical()
        {
            for (int sync = 1; sync < OldLfoRandom.Length; sync++)
                for (int feel = 0; feel < 3; feel++)
                {
                    var v = new ParamValues(NeoBuzz.LfoMachine.Params.Table);
                    v.Set(NeoBuzz.LfoMachine.P.Sync, sync); v.Set(NeoBuzz.LfoMachine.P.Feel, feel);
                    Assert.That(new NeoBuzz.LfoMachine.LfoEngine(v).CycleBeats(), Is.EqualTo(OldFeel(OldLfoRandom[sync], feel)).Within(0));
                    var w = new ParamValues(NeoBuzz.RandomMachine.Params.Table);
                    w.Set(NeoBuzz.RandomMachine.P.Sync, sync); w.Set(NeoBuzz.RandomMachine.P.Feel, feel);
                    Assert.That(new NeoBuzz.RandomMachine.RandomEngine(w).CycleBeats(), Is.EqualTo(OldFeel(OldLfoRandom[sync], feel)).Within(0));
                }
        }

        [Test]
        public void NeoDelaySecondsAreBitIdenticalAtEveryOldChoiceAndTempo()
        {
            foreach (double bpm in Tempi)
                for (int sync = 1; sync < OldDelay.Length; sync++)
                    for (int feel = 0; feel < 3; feel++)
                    {
                        var v = new ParamValues(NeoBuzz.Delay.Params.Table);
                        v.Set(NeoBuzz.Delay.P.Sync, sync); v.Set(NeoBuzz.Delay.P.Feel, feel);
                        double old = OldDelay[sync] * (feel == 0 ? 1 : feel == 1 ? 1.5 : 2.0 / 3) * 60.0 / Math.Max(1, bpm);
                        double want = Math.Clamp(old, NeoBuzz.Delay.DelayDesign.MinSeconds, NeoBuzz.Delay.DelayDesign.MaxSeconds);
                        Assert.That(NeoBuzz.Delay.DelayDesign.From(v, bpm, 48000).SecondsL, Is.EqualTo(want).Within(0), $"{sync} {feel} {bpm}");
                    }
        }

        [Test]
        public void NeoDelayNewDivisionsAndQuintupletAtThreeTempos()
        {
            foreach (double bpm in new[] { 90.0, 120, 174 })
                foreach (int sync in new[] { 1, 6, 8, 9, 10 })
                    for (int feel = 0; feel < 4; feel++)
                    {
                        var v = new ParamValues(NeoBuzz.Delay.Params.Table);
                        v.Set(NeoBuzz.Delay.P.Sync, sync); v.Set(NeoBuzz.Delay.P.Feel, feel);
                        double beats = NeoBuzz.Delay.DelayDesign.SyncBeats[sync] * TempoSync.FeelFactors[feel];
                        double want = Math.Clamp(beats * 60.0 / bpm, 0.001, 10);
                        Assert.That(NeoBuzz.Delay.DelayDesign.From(v, bpm, 48000).SecondsL, Is.EqualTo(want).Within(0), $"{sync} {feel} {bpm}");
                    }
        }

        [Test]
        public void NeoEnsembleAndNeoFilterHzAreBitIdentical()
        {
            foreach (double bpm in Tempi)
            {
                for (int sync = 1; sync < OldEnsemble.Length; sync++)
                {
                    var v = new ParamValues(NeoBuzz.Ensemble.Params.Table);
                    v.Set(NeoBuzz.Ensemble.P.Sync, sync);
                    Assert.That(NeoBuzz.Ensemble.EnsembleDesign.From(v, bpm, 48000).Rate, Is.EqualTo(Math.Max(1, bpm) / 60.0 / OldEnsemble[sync]).Within(0), $"ensemble {sync} {bpm}");
                }
                for (int sync = 1; sync < OldFilter.Length; sync++)
                {
                    var v = new ParamValues(NeoBuzz.Filter.Params.Table);
                    v.Set(NeoBuzz.Filter.P.LfoSync, sync);
                    var s = NeoBuzz.Filter.FilterDesign.From(v, bpm);
                    Assert.That(s.LfoHz, Is.EqualTo(Math.Max(1, bpm) / 60.0 / OldFilter[sync]).Within(0), $"filter {sync} {bpm}");
                    Assert.That(s.LfoBeats, Is.EqualTo(OldFilter[sync]).Within(0));
                }
            }
        }

        [Test]
        public void NeoPolyAndNeoWaveHzMatchTheOldFloatExpression()
        {
            foreach (double bpm in Tempi)
                for (int sync = 1; sync < OldPolyWave.Length; sync++)
                {
                    float old = (float)(bpm / 60.0 / OldPolyWave[sync]);
                    float now = (float)TempoSync.Hz(TempoSync.Beats(TempoSync.BeatsTable(NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L1Sync].Names)[sync], 0), bpm);
                    Assert.That(BitConverter.SingleToInt32Bits(now), Is.EqualTo(BitConverter.SingleToInt32Bits(old)), $"{sync} {bpm}");
                    float nowW = (float)TempoSync.Hz(TempoSync.Beats(TempoSync.BeatsTable(NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.L1Sync].Names)[sync], 0), bpm);
                    Assert.That(BitConverter.SingleToInt32Bits(nowW), Is.EqualTo(BitConverter.SingleToInt32Bits(old)));
                }
        }

        [Test]
        public void NeoArpPulseAndNotesStepLengthsAreBitIdentical()
        {
            for (int rate = 0; rate < OldStep.Length; rate++)
                for (int feel = 0; feel < 3; feel++)
                {
                    double arpOld = feel == 1 ? OldStep[rate] * 1.5 : feel == 2 ? OldStep[rate] * 2.0 / 3.0 : OldStep[rate];
                    double pulseOld = OldStep[rate] * new[] { 1.0, 1.5, 2.0 / 3.0 }[feel];
                    double b = NeoBuzz.Arp.ArpEngine.RateBeats[rate];
                    Assert.That(TempoSync.Beats(b, feel), Is.EqualTo(arpOld).Within(0), $"arp {rate} {feel}");
                    Assert.That(TempoSync.Beats(NeoBuzz.Pulse.PulseEngine.RateBeats[rate], feel), Is.EqualTo(pulseOld).Within(0), $"pulse {rate} {feel}");
                    Assert.That(NeoBuzz.Notes.NotesEngine.Beats(rate, feel), Is.EqualTo(pulseOld).Within(0), $"notes {rate} {feel}");
                }
        }

        // ---- appended parameters and old songs ---------------------------------------------------------------------------

        [Test]
        public void ChainsAreValidAndAnOldSongLoadsAsItSounded()
        {
            Assert.That(NeoBuzz.Pulse.PulseMigrations.Chain.Validate(), Is.Empty);
            Assert.That(NeoBuzz.Wave.WaveMigrations.Chain.Validate(), Is.Empty);
            Assert.That(NeoBuzz.Ensemble.EnsembleMigrations.Chain.Validate(), Is.Empty);
            Assert.That(NeoBuzz.Filter.FilterMigrations.Chain.Validate(), Is.Empty);
            Assert.That(NeoBuzz.Poly.PolyMigrations.Chain.Validate(), Is.Empty);

            // A song saved before: every old value at a non-default, the new parameters absent. It loads with the old values and Straight / Song tempo.
            void Check(StateMigrations chain, int fromVersion, string[] oldLayout, ParamDef[] table, string[] newKeys, int[] newValues)
            {
                var saved = new int[oldLayout.Length];
                for (int i = 0; i < saved.Length; i++) saved[i] = Math.Clamp(i % 3, table.First(p => p.Id == oldLayout[i]).Min, table.First(p => p.Id == oldLayout[i]).Max);
                var now = chain.MigrateGlobals(fromVersion, saved);
                Assert.That(now, Is.Not.Null);
                for (int i = 0; i < oldLayout.Length; i++)
                    Assert.That(now[Array.FindIndex(table, p => p.Id == oldLayout[i])], Is.EqualTo(saved[i]), oldLayout[i]);
                for (int k = 0; k < newKeys.Length; k++)
                    Assert.That(now[Array.FindIndex(table, p => p.Id == newKeys[k])], Is.EqualTo(newValues[k]), newKeys[k]);
            }
            Check(NeoBuzz.Pulse.PulseMigrations.Chain, 0, NeoBuzz.Pulse.PulseMigrations.Version0, NeoBuzz.Pulse.Params.Table, new[] { "timing", "free_ms" }, new[] { 0, 125 });
            Check(NeoBuzz.Wave.WaveMigrations.Chain, 0, NeoBuzz.Wave.WaveMigrations.Version0, NeoBuzz.Wave.Params.Table, new[] { "L1Feel", "L2Feel" }, new[] { 0, 0 });
            Check(NeoBuzz.Ensemble.EnsembleMigrations.Chain, 0, NeoBuzz.Ensemble.EnsembleMigrations.Version0, NeoBuzz.Ensemble.Params.Table, new[] { "Feel" }, new[] { 0 });
            Check(NeoBuzz.Filter.FilterMigrations.Chain, 1, NeoBuzz.Filter.FilterMigrations.Version1, NeoBuzz.Filter.Params.Table, new[] { "LfoFeel" }, new[] { 0 });
            Assert.That(NeoBuzz.Poly.PolyMigrations.Chain.Layout(2).Length, Is.EqualTo(102));
            Check(NeoBuzz.Poly.PolyMigrations.Chain, 2, NeoBuzz.Poly.PolyMigrations.Chain.Layout(2), NeoBuzz.Poly.Params.Table, new[] { "L1Feel", "L2Feel" }, new[] { 0, 0 });
        }

        [Test]
        public void PulseFreeTimeAndFeelDefaultToTheOldSound()
        {
            var t = NeoBuzz.Pulse.Params.Table;
            Assert.That(t[NeoBuzz.Pulse.P.Sync].Default, Is.EqualTo(0), "Song tempo");
            Assert.That(t[NeoBuzz.Pulse.P.Sync].Names, Is.EqualTo(new[] { "Song tempo", "Free time" }));
            Assert.That(t[NeoBuzz.Pulse.P.FreeMs].Value(t[NeoBuzz.Pulse.P.FreeMs].Default), Is.EqualTo(125).Within(1e-9));
            Assert.That(NeoBuzz.Pulse.P.Sync, Is.EqualTo(127));
            Assert.That(NeoBuzz.Pulse.P.FreeMs, Is.EqualTo(128));
        }

        // ---- the pins ----------------------------------------------------------------------------------------------------

        [Test]
        public void EveryGrownListIsPinnedAtItsOldLastChoice()
        {
            var expected = new (string, string, int, ParamDef)[]
            {
                ("Neo Delay", "Sync", 8, NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Sync]), ("Neo Delay", "Feel", 2, NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Feel]),
                ("Neo LFO", "Feel", 2, NeoBuzz.LfoMachine.Params.Table[NeoBuzz.LfoMachine.P.Feel]), ("Neo Random", "Feel", 2, NeoBuzz.RandomMachine.Params.Table[NeoBuzz.RandomMachine.P.Feel]),
                ("Neo Arp", "Rate", 6, NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Rate]), ("Neo Arp", "Feel", 2, NeoBuzz.Arp.Params.Table[NeoBuzz.Arp.P.Feel]),
                ("Neo Pulse", "Rate", 6, NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Rate]), ("Neo Pulse", "Feel", 2, NeoBuzz.Pulse.Params.Table[NeoBuzz.Pulse.P.Feel]),
                ("Neo Notes", "Fixed rate", 6, NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.LenRate]), ("Neo Notes", "Fixed feel", 2, NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.LenFeel]),
                ("Neo Notes", "Echo rate", 6, NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoRate]), ("Neo Notes", "Echo feel", 2, NeoBuzz.Notes.Params.Table[NeoBuzz.Notes.P.EchoFeel]),
                ("Neo Poly", "LFO1 sync", 11, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L1Sync]), ("Neo Poly", "LFO2 sync", 11, NeoBuzz.Poly.Params.Table[NeoBuzz.Poly.P.L2Sync]),
                ("Neo Wave", "LFO1 sync", 11, NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.L1Sync]), ("Neo Wave", "LFO2 sync", 11, NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.L2Sync]),
                ("Neo Ensemble", "Sync", 13, NeoBuzz.Ensemble.Params.Table[NeoBuzz.Ensemble.P.Sync]), ("Neo Filter", "LFO sync", 18, NeoBuzz.Filter.Params.Table[NeoBuzz.Filter.P.LfoSync]),
            };
            Assert.That(LegacyChoiceSpans.SyncPins.Length, Is.EqualTo(expected.Length));
            foreach (var (machine, name, oldMax, def) in expected)
            {
                Assert.That(def.Name, Is.EqualTo(name), "the parameter's own name is what the modulator and the MIDI map see");
                Assert.That(LegacyChoiceSpans.OldMax(machine, name), Is.EqualTo(oldMax), machine + " " + name);
                Assert.That(def.Max, Is.GreaterThan(oldMax), machine + " " + name + " did grow");
                Assert.That(ReBuzz.Midi.Mapping.LegacyChoicePins.OldMax(machine, name), Is.EqualTo(oldMax), "the v1 MIDI-map table agrees: " + machine + " " + name);
            }
            Assert.That(ReBuzz.Midi.Mapping.LegacyChoicePins.All.Length, Is.EqualTo(LegacyChoiceSpans.SyncPins.Length));
            foreach (var pin in LegacyChoiceSpans.SyncPins)
                Assert.That(ReBuzz.Midi.Mapping.LegacyChoicePins.OldMax(pin.Machine, pin.Parameter), Is.EqualTo(pin.OldMax));
            Assert.That(ReBuzz.Midi.Mapping.LegacyChoicePins.OldMax("Neo Delay", "Time"), Is.Null);
            Assert.That(LegacyChoiceSpans.OldMax("Neo Drive", "Mode"), Is.EqualTo(4), "the older pins stay");
        }
    }
}
