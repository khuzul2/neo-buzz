// SYN1 (docs/sdk/tempo-sync.md): a timing in Sync allocates nothing in the audio thread, with the song's tempo changing under it.

using NeoBuzz.Dsp;
using NUnit.Framework;

namespace NeoBuzz.MachineTests
{
    public class SyncAllocationTests
    {
        const int Blocks = 2000;
        const int Block = 64;

        static int Find(ParamDef[] table, string name) => Array.FindIndex(table, p => p.Name == name);

        static void SyncOn(ParamValues v, ParamDef[] table, string division, string feel, int divisionRaw, int feelRaw)
        {
            v.Set(Find(table, division), divisionRaw);
            v.Set(Find(table, feel), feelRaw);
        }

        [Test]
        public void NeoDelayInSyncWithATempoChangeAllocatesNothing()
        {
            var table = NeoBuzz.Delay.Params.Table;
            var v = new ParamValues(table);
            SyncOn(v, table, "Sync", "Feel", 7, 1);                      // 1/4 dotted... any division but Free
            var e = new NeoBuzz.Delay.DelayEngine(v) { Tempo = 120 };
            e.SetSampleRate(48000);
            var l = Measure.PinkNoise(Block, 0.2, 1);
            var r = Measure.PinkNoise(Block, 0.2, 2);
            var bl = new float[NeoBuzz.Delay.DelayEngine.MaxBlock];
            var br = new float[NeoBuzz.Delay.DelayEngine.MaxBlock];
            var (bytes, why) = AllocationCheck.Run(Blocks, b =>
            {
                if (b == Blocks / 2 || b == 20) e.Tempo = b == 20 ? 120 : 137.5;
                Array.Copy(l, bl, Block); Array.Copy(r, br, Block);
                e.Process(bl, br, Block);
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }

        [Test]
        public void NeoLfoInSyncWithATempoChangeAllocatesNothing()
        {
            var table = NeoBuzz.LfoMachine.Params.Table;
            var v = new ParamValues(table);
            SyncOn(v, table, "Sync", "Feel", 7, 2);
            var e = new NeoBuzz.LfoMachine.LfoEngine(v);
            double beats = 0, bpm = 120;
            float sink = 0;
            var (bytes, why) = AllocationCheck.Run(Blocks, b =>
            {
                if (b >= Blocks / 2) bpm = 137.5;
                sink += e.Step(Block / 48000.0, beats, true, bpm);
                beats += Block / 48000.0 * bpm / 60;
            });
            Assert.That(bytes, Is.EqualTo(0), why);
            Assert.That(sink, Is.Not.NaN);
        }

        [Test]
        public void NeoArpInSyncWithATempoChangeAllocatesNothing()
        {
            var table = NeoBuzz.Arp.Params.Table;
            var v = new ParamValues(table);
            SyncOn(v, table, "Rate", "Feel", 4, 3);                      // 1/16 quintuplet, Song tempo
            var e = new NeoBuzz.Arp.ArpEngine(v);
            e.SetSampleRate(48000);
            var on = new[] { NeoBuzz.Arp.ArpEvent.On(0, 0, 60, 100), NeoBuzz.Arp.ArpEvent.On(0, 0, 64, 100) };
            var none = Array.Empty<NeoBuzz.Arp.ArpEvent>();
            double beat = 0, spb = 24000;
            var (bytes, why) = AllocationCheck.Run(Blocks, b =>
            {
                if (b == Blocks / 2) spb = 48000 * 60.0 / 137.5;
                var block = new NeoBuzz.Arp.ArpBlock { Samples = Block, Playing = true, Beat = beat, SamplesPerBeat = spb, BeatsPerBar = 4 };
                e.Process(in block, b == 0 ? on : none);
                beat += Block / spb;
            });
            Assert.That(bytes, Is.EqualTo(0), why);
        }
    }
}
