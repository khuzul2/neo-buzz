using NeoBuzz.Dsp;
using NeoBuzz.Multiband;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// Neo Multiband's renders before M6b (docs/specs/mastering-tools.md §5.14: dynamic phase, lookahead, upward compression, external
    /// sidechain, RMS), pinned: with the new parameters at their defaults every render is bit for bit what it was.
    /// </summary>
    [TestFixture]
    public class MultibandGoldenTests
    {
        static ulong Hash(float[] a, float[] b)
        {
            ulong h = 14695981039346656037UL;
            foreach (var x in a.Concat(b))
            {
                uint bits = (uint)BitConverter.SingleToInt32Bits(x);
                for (int k = 0; k < 4; k++)
                {
                    h ^= (bits >> (8 * k)) & 0xFF;
                    h *= 1099511628211UL;
                }
            }
            return h;
        }

        static void Set(ParamValues v, int p, double value) => v.Set(p, Params.Table[p].Raw(value));

        static IEnumerable<(string Name, Action<ParamValues> Setup, Action<ParamValues, int> Block)> Cases()
        {
            yield return ("defaults, linear", v => { }, null);
            yield return ("defaults, minimum", v => v.Set(P.Phase, 1), null);
            yield return ("three bands", v => v.Set(P.Bands, 0), null);
            yield return ("mid/side unlinked", v => { v.Set(P.Stereo, 1); Set(v, P.Link, 0); }, null);
            yield return ("squash, minimum, band 2 solo", v =>
            {
                v.Set(P.Phase, 1);
                for (int b = 0; b < 4; b++)
                {
                    v.Set(MultibandEngine.Param(b, MultibandEngine.BandParam.Ratio), Params.Table[MultibandEngine.Param(b, MultibandEngine.BandParam.Ratio)].Max);
                    Set(v, MultibandEngine.Param(b, MultibandEngine.BandParam.Threshold), -40);
                }
                v.Set(MultibandEngine.Param(1, MultibandEngine.BandParam.Solo), 1);
            }, null);
            yield return ("expansion, parallel", v =>
            {
                Set(v, MultibandEngine.Param(0, MultibandEngine.BandParam.Ratio), 0.5);
                Set(v, MultibandEngine.Param(2, MultibandEngine.BandParam.Ratio), 0.6);
                Set(v, P.Mix, 50);
                Set(v, P.Output, 1.5);
            }, null);
            yield return ("moves", v => { }, (v, pos) =>
            {
                if (pos == 29952) { Set(v, P.Xo1, 300); Set(v, P.Xo2, 3000); }
                if (pos == 44928) v.Set(P.Bands, 0);
                if (pos == 59904) v.Set(P.Phase, 1);
                if (pos == 74880) { v.Set(P.Bands, 1); v.Set(P.Phase, 0); }
            });
        }

        [Test]
        public void TheRendersAreBitForBitWhatTheyWere()
        {
            string outFile = Environment.GetEnvironmentVariable("MB_GOLDEN_OUT");   // set: write every render's hash (to pin them)
            var lines = new List<string>();
            var changed = new List<string>();
            int count = 0;
            foreach (int rate in new[] { 44100, 48000 })
            {
                var x = Measure.PinkNoise(rate * 3 / 2, DspMath.DbToGain(-18), 31);
                var y = Measure.PinkNoise(rate * 3 / 2, DspMath.DbToGain(-20), 32);
                var all = Cases().Select(c => (c.Name, c.Setup, c.Block)).ToList();
                foreach (var (_, machine, name, values) in PresetTests.AllPresets())
                    if (machine == "Neo Multiband")
                        all.Add(("preset " + name, v => { foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value); }, null));
                foreach (var (name, setup, block) in all)
                {
                    string id = $"{rate} {name}";
                    if (name.StartsWith("preset ") && string.IsNullOrEmpty(outFile) && !Pinned.ContainsKey(id)) continue;   // a preset added since
                    var v = new ParamValues(Params.Table);
                    setup(v);
                    var e = new MultibandEngine(v);
                    e.SetSampleRate(rate);
                    var ol = new float[x.Length];
                    var or = new float[x.Length];
                    var bl = new float[MultibandEngine.MaxBlock];
                    var br = new float[MultibandEngine.MaxBlock];
                    for (int pos = 0; pos < x.Length; pos += MultibandEngine.MaxBlock)
                    {
                        int n = Math.Min(MultibandEngine.MaxBlock, x.Length - pos);
                        block?.Invoke(v, pos);
                        Array.Copy(x, pos, bl, 0, n);
                        Array.Copy(y, pos, br, 0, n);
                        e.Process(bl, br, n);
                        Array.Copy(bl, 0, ol, pos, n);
                        Array.Copy(br, 0, or, pos, n);
                    }
                    ulong h = Hash(ol, or);
                    lines.Add($"            [\"{id}\"] = 0x{h:X16}UL,");
                    if (!Pinned.TryGetValue(id, out ulong before) || before != h) changed.Add(id);
                    count++;
                }
            }
            if (!string.IsNullOrEmpty(outFile)) File.WriteAllLines(outFile, lines);
            TestContext.Out.WriteLine(string.Join("\n", lines));
            Measure.Report($"Neo Multiband before M6b: {count - changed.Count} of {count} renders bit-identical (defaults in both phases, three bands, mid/side unlinked, squash with a solo, expansion in parallel, moves and switches, the factory presets; 44.1 and 48 kHz)");
            Assert.That(count, Is.EqualTo(Pinned.Count));
            Assert.That(changed, Is.Empty);
        }

        static readonly Dictionary<string, ulong> Pinned = new()
        {
            ["44100 defaults, linear"] = 0xF34FF52ED517084BUL,
            ["44100 defaults, minimum"] = 0xC3ADF001E118F5B5UL,
            ["44100 three bands"] = 0x6820C416C1FFD51AUL,
            ["44100 mid/side unlinked"] = 0x9B6709B934CACD93UL,
            ["44100 squash, minimum, band 2 solo"] = 0x264D282E849B3BAFUL,
            ["44100 expansion, parallel"] = 0x444B48C5B1CE78BCUL,
            ["44100 moves"] = 0x5EA7705A17740DEFUL,
            ["44100 preset Mastering — Gentle Glue (4 Bands)"] = 0x26D25AF4C4DEC168UL,
            ["44100 preset Mastering — Tight Low End"] = 0xBC5CDA5877793869UL,
            ["44100 preset Mastering — De-Harsh 2–6 kHz"] = 0x986B349F55C899C7UL,
            ["44100 preset Mastering — Air Lift (Upward Expansion)"] = 0x4765AD1454405C05UL,
            ["44100 preset Mastering — Transient Restore (Upward Expansion)"] = 0x4FC6538D1D2449E0UL,
            ["44100 preset Mastering — Loud and Dense"] = 0x7B4CC3C73D340586UL,
            ["44100 preset Mastering — Mid/Side Control (Unlinked)"] = 0xE7889C0E1BE8A51FUL,
            ["44100 preset Mastering — Zero-Latency Glue (Minimum Phase)"] = 0x045C2E69E36FD011UL,
            ["44100 preset Bus — Drum Bus Punch (3 Bands)"] = 0x77DB1E3CFEB5A2CEUL,
            ["44100 preset Bus — Mix Bus Glue (3 Bands)"] = 0x9AD10455F6EAA340UL,
            ["44100 preset Bus — Bass Leveler"] = 0xE1F34C22DAAEBC9CUL,
            ["44100 preset Vocal — De-Esser Above 5 kHz"] = 0xEF25B4C8F4CECCC6UL,
            ["44100 preset Vocal — Smooth Mids 300 Hz–3 kHz"] = 0x76AC82206B96D17EUL,
            ["44100 preset Fix — De-Mud 200–500 Hz"] = 0x428568226F02C109UL,
            ["44100 preset Fix — Boom Control Below 90 Hz"] = 0x0D28B2B439269E33UL,
            ["44100 preset Creative — Heavy Squash (Parallel 50 %)"] = 0x57A6CCF186878492UL,
            ["44100 preset Utility — Linear-Phase Split (Transparent)"] = 0x88755498A5B0D7DBUL,
            ["48000 defaults, linear"] = 0xAE59DB3B7E414FC0UL,
            ["48000 defaults, minimum"] = 0xB6BC7F665B4D73FAUL,
            ["48000 three bands"] = 0x782A09FF1F774845UL,
            ["48000 mid/side unlinked"] = 0x4E58316ED63655AEUL,
            ["48000 squash, minimum, band 2 solo"] = 0xCC6200EAA3255086UL,
            ["48000 expansion, parallel"] = 0x4D028E6AB59D889DUL,
            ["48000 moves"] = 0xFD21FCD9F155E459UL,
            ["48000 preset Mastering — Gentle Glue (4 Bands)"] = 0xC9A622B5073C68C0UL,
            ["48000 preset Mastering — Tight Low End"] = 0x3C6D8B3155BB58C7UL,
            ["48000 preset Mastering — De-Harsh 2–6 kHz"] = 0x974DE14608F48432UL,
            ["48000 preset Mastering — Air Lift (Upward Expansion)"] = 0x8E3B34E31366B370UL,
            ["48000 preset Mastering — Transient Restore (Upward Expansion)"] = 0xD2DAA30A4012219EUL,
            ["48000 preset Mastering — Loud and Dense"] = 0xBBD4145D5C64A3EEUL,
            ["48000 preset Mastering — Mid/Side Control (Unlinked)"] = 0xEC36520D6B6062A9UL,
            ["48000 preset Mastering — Zero-Latency Glue (Minimum Phase)"] = 0x332A1289ED24CC96UL,
            ["48000 preset Bus — Drum Bus Punch (3 Bands)"] = 0xB231B07F03FCF8CAUL,
            ["48000 preset Bus — Mix Bus Glue (3 Bands)"] = 0x04698F8F773757D6UL,
            ["48000 preset Bus — Bass Leveler"] = 0x34D5CBD9A5C47215UL,
            ["48000 preset Vocal — De-Esser Above 5 kHz"] = 0xD05D0733F60A9237UL,
            ["48000 preset Vocal — Smooth Mids 300 Hz–3 kHz"] = 0xA39030AF2BE3715EUL,
            ["48000 preset Fix — De-Mud 200–500 Hz"] = 0x675FD76882CAF687UL,
            ["48000 preset Fix — Boom Control Below 90 Hz"] = 0xC33ECE8C57149783UL,
            ["48000 preset Creative — Heavy Squash (Parallel 50 %)"] = 0x8EAD25DC4C37A23EUL,
            ["48000 preset Utility — Linear-Phase Split (Transparent)"] = 0xCBC4ED5B0EBECD1FUL,
        };
    }
}
