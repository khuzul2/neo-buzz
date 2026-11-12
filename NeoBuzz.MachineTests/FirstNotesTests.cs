using NeoBuzz.Dsp;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Fork (audio hygiene, docs/specs/machines.md §3): a Neo synth's first notes allocate nothing on the audio thread.
    /// What its voices need (Neo Wave's tables, Neo FM6's DX7 tables and carrier gains) is made with the engine, not by
    /// the first blocks that play: the transport's first play allocated about 340 KB on the audio thread, 221 KB of it
    /// Neo Wave's (a closure every voice block). Measured from the engine's very first block, notes starting and ending.
    /// The studio's AudioHygieneTests.TheFirstPlayOfEveryNeoMachineAllocatesNothing measures the machines in ReBuzz.
    /// </summary>
    [TestFixture]
    public class FirstNotesTests
    {
        static readonly string[] Synths = { "Neo Poly", "Neo Wave", "Neo FM6", "Neo Drums", "Neo Sampler" };

        /// <summary>The engine of <paramref name="gear"/> at 48 kHz: note on, note off, a block.</summary>
        static (Action<int, int> On, Action<int> Off, Func<float[], float[], int, bool> Process) Engine(string gear)
        {
            switch (gear)
            {
                case "Neo Poly":
                    {
                        var e = new NeoBuzz.Poly.PolyEngine(new ParamValues(NeoBuzz.Poly.Params.Table));
                        e.SetSampleRate(48000);
                        return ((k, n) => e.NoteOn(k, n, 0.8f), e.NoteOff, e.Process);
                    }
                case "Neo Wave":
                    {
                        // The machine asks for every table when it's made (NeoWave.cs); they build off the audio thread.
                        for (int i = 0; i < NeoBuzz.Wave.Tables.Names.Length; i++) NeoBuzz.Wave.Tables.GetNow(i);
                        var e = new NeoBuzz.Wave.WaveEngine(new ParamValues(NeoBuzz.Wave.Params.Table));
                        e.SetSampleRate(48000);
                        return ((k, n) => e.NoteOn(k, n, 0.8f), e.NoteOff, e.Process);
                    }
                case "Neo FM6":
                    {
                        var e = new NeoBuzz.Fm6.FmEngine(new ParamValues(NeoBuzz.Fm6.Params.Table));
                        e.SetSampleRate(48000);
                        return ((k, n) => e.NoteOn(k, n, 0.8f), e.NoteOff, e.Process);
                    }
                case "Neo Drums":
                    {
                        var e = new NeoBuzz.Drums.DrumsEngine(new ParamValues(NeoBuzz.Drums.Params.Table));
                        e.SetSampleRate(48000);
                        return ((k, n) => e.NoteOn(k, 36 + n % 12, 0.8f), e.NoteOff, e.Process);
                    }
                default:
                    {
                        var e = new NeoBuzz.Sampler.SamplerEngine(new ParamValues(NeoBuzz.Sampler.Params.Table));
                        e.SetSampleRate(48000);
                        return ((k, n) => e.NoteOn(k, n, 0.8f), e.NoteOff, e.Process);
                    }
            }
        }

        [TestCaseSource(nameof(Synths))]
        public void TheFirstNotesAllocateNothing(string gear)
        {
            var (on, off, process) = Engine(gear);
            var l = new float[256];
            var r = new float[256];
            var (bytes, why) = AllocationCheck.Run(400, b =>
            {
                if (b % 20 == 0) on(b / 20 % 9, 48 + b / 20 % 24);
                if (b % 20 == 10) off((b / 20 + 5) % 9);
                process(l, r, 256);
            });
            Measure.Report($"{gear}: {bytes} bytes allocated in its first 400 blocks with notes");
            Assert.That(bytes, Is.EqualTo(0), why);
        }
    }
}
