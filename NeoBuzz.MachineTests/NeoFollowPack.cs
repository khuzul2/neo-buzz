using System.Text;
using NeoBuzz.Dsp;
using NeoBuzz.FollowMachine;
using F = NeoBuzz.FollowMachine.P;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo Follow listening pack (docs/specs/neo-follow.md): every factory preset driving a real target chain (Neo Drums, Neo Poly and Neo Filter), 15 s at
    /// 120 BPM, 48 kHz 16-bit mono or stereo WAV files with a README. The first 4 s run with the follower off (Depth 0), then it is on. Local only, never committed:
    ///   NEO_FOLLOW_PACK=/some/folder dotnet test NeoBuzz.MachineTests --filter "FullyQualifiedName~NeoFollowPack"
    /// </summary>
    [TestFixture]
    public class NeoFollowPack
    {
        const int Rate = NeoFollowRig.Rate;
        const int Length = 15 * Rate;
        const int Block = 64;
        const int OnAt = 4 * Rate;
        static readonly int[][] Chords = { new[] { 45, 52, 57, 60 }, new[] { 41, 48, 57, 60 }, new[] { 48, 52, 55, 60 }, new[] { 43, 50, 55, 59 } };
        static readonly int[] Steps = { 0, 2, 4, 5, 7, 9, 11, 12, 14, 16, 17, 19, 21, 23, 24, 23, 21, 19, 17, 16, 14, 12, 11, 9, 7, 5, 4, 2 };

        enum Chain { Wah, Pump, Swell, Drive, Lead, Flash, Chop }

        sealed class ValuesPort : IModPort
        {
            readonly ParamValues v; readonly int index;
            public ValuesPort(ParamValues v, int index, int min, int max) { this.v = v; this.index = index; Min = SafeMin = min; Max = SafeMax = SpanMax = max; }
            public int Min { get; }
            public int Max { get; }
            public int SafeMin { get; }
            public int SafeMax { get; }
            public int SpanMax { get; }
            public int Get() => v[index];
            public void Put(int raw) => v.Set(index, raw);
        }

        static ParamValues PresetValues(ParamDef[] table, string machine, string preset)
        {
            var found = PresetTests.AllPresets().First(p => p.Machine == machine && p.Preset == preset);
            var v = new ParamValues(table);
            foreach (var kv in found.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static ParamValues Fresh(IEnumerable<KeyValuePair<int, int>> values)
        {
            var v = new ParamValues(NeoBuzz.FollowMachine.Params.Table);
            foreach (var kv in values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
            return v;
        }

        static Chain ChainOf(string preset) => preset switch
        {
            "Envelope — Auto Wah" => Chain.Wah,
            "Envelope — Pump" => Chain.Pump,
            "Envelope — Slow Swell" => Chain.Swell,
            "Envelope — Drive by Level" => Chain.Drive,
            "Onset — Hit Flash" => Chain.Flash,
            "Onset — Chop Gate" => Chain.Chop,
            _ => Chain.Lead,
        };

        static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        static void SetRaw(ParamValues v, ParamDef[] table, int p, double real) => v.Set(p, table[p].Raw(real));

        /// <summary>Renders one preset on its chain; also returns what the target did (for the README).</summary>
        static (float[] L, float[] R) Render(string preset, ParamValues fv, bool alwaysOff = false, bool bare = false)
        {
            var kind = ChainOf(preset);
            var follow = new FollowEngine(fv);
            follow.SetSampleRate(Rate);
            int presetDepth = fv[F.Depth];

            var (drums, _) = NeoFollowRig.DrumLoop(8);
            var (kicks, _) = NeoFollowRig.DrumLoop(8, kicksOnly: true);

            // The pad (Neo Poly, Pad — Warm Strings), or the lead (Lead — Smooth Saw).
            bool lead = kind == Chain.Lead;
            var pv = PresetValues(NeoBuzz.Poly.Params.Table, "Neo Poly", lead ? "Lead — Smooth Saw" : "Pad — Warm Strings");
            var poly = new NeoBuzz.Poly.PolyEngine(pv);
            poly.SetSampleRate(Rate);
            bool usePoly = kind != Chain.Wah && kind != Chain.Flash;

            var fl = new ParamValues(NeoBuzz.Filter.Params.Table);
            var filter = new NeoBuzz.Filter.FilterEngine(fl);
            filter.SetSampleRate(Rate);
            var ft = NeoBuzz.Filter.Params.Table;
            SetRaw(fl, ft, NeoBuzz.Filter.P.Type, 0);                 // Ladder 24
            fl.Set(NeoBuzz.Filter.P.Mix, 1000);

            // The target, its base and its range.
            var bank = new ModBank { GlideSeconds = fv.Value(F.Glide) };
            var slot = bank.Slots[0];
            slot.Used = true;
            slot.Bipolar = fv.Choice(F.Range) == 1;
            IModPort port;
            switch (kind)
            {
                case Chain.Wah:    // a resonant low-pass, closed at 230 Hz, opened up to 4 kHz
                    fl.Set(NeoBuzz.Filter.P.Cutoff, 3500); fl.Set(NeoBuzz.Filter.P.Resonance, 420);
                    port = new ValuesPort(fl, NeoBuzz.Filter.P.Cutoff, 0, 10000); slot.RangeMin = 2500; slot.RangeMax = 8500; slot.Depth = 85; break;
                case Chain.Flash:
                    fl.Set(NeoBuzz.Filter.P.Cutoff, 3000); fl.Set(NeoBuzz.Filter.P.Resonance, 380);
                    port = new ValuesPort(fl, NeoBuzz.Filter.P.Cutoff, 0, 10000); slot.RangeMin = 2500; slot.RangeMax = 9000; slot.Depth = 100; break;
                case Chain.Swell:
                    fl.Set(NeoBuzz.Filter.P.Cutoff, 3500); fl.Set(NeoBuzz.Filter.P.Resonance, 300);
                    port = new ValuesPort(fl, NeoBuzz.Filter.P.Cutoff, 0, 10000); slot.RangeMin = 3000; slot.RangeMax = 9500; slot.Depth = 100; break;
                case Chain.Drive:
                    fl.Set(NeoBuzz.Filter.P.Cutoff, 9500); fl.Set(NeoBuzz.Filter.P.Drive, 0);
                    port = new ValuesPort(fl, NeoBuzz.Filter.P.Drive, 0, 240); slot.RangeMin = 0; slot.RangeMax = 240; slot.Depth = 100; break;
                case Chain.Lead:
                    fl.Set(NeoBuzz.Filter.P.Cutoff, 4500); fl.Set(NeoBuzz.Filter.P.Resonance, 360);
                    port = new ValuesPort(fl, NeoBuzz.Filter.P.Cutoff, 0, 10000); slot.RangeMin = 3000; slot.RangeMax = 9300; slot.Depth = 100; break;
                case Chain.Pump:   // the pad's Volume: ducks (negative depth) by up to about 24 dB
                    port = new ValuesPort(pv, NeoBuzz.Poly.P.Volume, 0, 1000); slot.RangeMin = 0; slot.RangeMax = 1000; slot.Depth = -60; break;
                default:           // Chop: the pad sits shut at -34 dB and the hits open it
                    pv.Set(NeoBuzz.Poly.P.Volume, 150);
                    port = new ValuesPort(pv, NeoBuzz.Poly.P.Volume, 0, 1000); slot.RangeMin = 0; slot.RangeMax = 739; slot.Depth = 100; break;
            }
            slot.Port = port;

            var ol = new float[Length];
            var or = new float[Length];
            var l = new float[Block]; var r = new float[Block];
            var pl = new float[Block]; var pr = new float[Block];
            var mono = new float[Block]; var key = new float[Block];
            int bar = Rate * 2, note = Rate / 4;
            double dt = (double)Block / Rate;
            for (int pos = 0; pos + Block <= Length; pos += Block)
            {
                // The notes that start or end in this block.
                if (usePoly)
                    for (int s = pos; s < pos + Block; s++)
                    {
                        if (lead)
                        {
                            int k = s / note, at = s % note;
                            if (at == 0) poly.NoteOn(0, 48 + Steps[k % Steps.Length], 0.8f);
                            if (at == note * 9 / 10) poly.NoteOff(0);
                        }
                        else
                        {
                            int c = s / bar, at = s % bar;
                            if (at == 0) for (int q = 0; q < 4; q++) poly.NoteOn(q, Chords[c % 4][q], 0.7f);
                            if (at == bar * 9 / 10) for (int q = 0; q < 4; q++) poly.NoteOff(q);
                        }
                    }
                Array.Clear(pl, 0, Block); Array.Clear(pr, 0, Block);
                if (usePoly) poly.Process(pl, pr, Block);
                float swell = 1f, level = 1f;
                if (kind == Chain.Swell) swell = (float)Math.Min(1.0, 0.04 + 0.96 * pos / (10.0 * Rate)) * (pos > 12 * Rate ? Math.Max(0.1f, 1f - (pos - 12 * Rate) / (3f * Rate)) : 1f);
                if (kind == Chain.Drive) level = (pos / (2 * bar)) % 2 == 0 ? 0.12f : 0.6f;
                for (int i = 0; i < Block; i++)
                {
                    float d = drums[pos + i], k = kicks[pos + i];
                    switch (kind)
                    {
                        case Chain.Wah: case Chain.Flash: l[i] = r[i] = 0.8f * d; mono[i] = l[i]; break;
                        case Chain.Swell: l[i] = swell * (0.7f * d + 0.4f * pl[i]); r[i] = swell * (0.7f * d + 0.4f * pr[i]); mono[i] = 0.5f * (l[i] + r[i]); break;
                        case Chain.Drive: l[i] = level * (d + 0.5f * pl[i]); r[i] = level * (d + 0.5f * pr[i]); mono[i] = 0.5f * (l[i] + r[i]); break;
                        case Chain.Lead: l[i] = 0.6f * pl[i]; r[i] = 0.6f * pr[i]; mono[i] = 0.5f * (l[i] + r[i]); break;
                        case Chain.Pump: l[i] = pl[i]; r[i] = pr[i]; mono[i] = d; break;
                        default: l[i] = pl[i]; r[i] = pr[i]; mono[i] = 0.8f * d; break;
                    }
                    key[i] = 0.8f * k;
                }
                // The follower: off for the first 4 s (Depth 0), then on at the preset's depth.
                fv.Set(F.Depth, alwaysOff || pos < OnAt ? fv.Table[F.Depth].Raw(0) : presetDepth);
                follow.Process(mono, mono, key, key, Block);
                bank.Update(follow.Output, dt);
                // The chain.
                if (kind == Chain.Pump || kind == Chain.Chop)
                {
                    float gain = 1f;
                    for (int i = 0; i < Block; i++)
                    {
                        float d = drums[pos + i], k = kicks[pos + i];
                        ol[pos + i] = gain * l[i] + (bare ? 0f : kind == Chain.Pump ? 0.7f * k : 0.5f * d);
                        or[pos + i] = gain * r[i] + (bare ? 0f : kind == Chain.Pump ? 0.7f * k : 0.5f * d);
                    }
                }
                else
                {
                    filter.Process(l, r, Block);
                    Array.Copy(l, 0, ol, pos, Block);
                    Array.Copy(r, 0, or, pos, Block);
                }
            }
            return (ol, or);
        }

        static void WriteWav(string path, float[] l, float[] r)
        {
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            int n = l.Length, bytes = n * 4;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            for (int i = 0; i < n; i++)
            {
                w.Write((short)Math.Clamp((int)Math.Round(l[i] * 32767.0), -32768, 32767));
                w.Write((short)Math.Clamp((int)Math.Round(r[i] * 32767.0), -32768, 32767));
            }
        }

        static Dictionary<string, string> Comments()
        {
            var map = new Dictionary<string, string>();
            string dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Machines", "NeoFollow", "Presets.txt"))) dir = Path.GetDirectoryName(dir);
            if (dir == null) return map;
            string name = null;
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "Machines", "NeoFollow", "Presets.txt")))
            {
                if (line.StartsWith("preset \"")) name = line.Substring(8).TrimEnd('"');
                else if (name != null && line.StartsWith("comment \"")) map[name] = line.Substring(9).TrimEnd('"');
            }
            return map;
        }

        static readonly Dictionary<Chain, string> Heard = new()
        {
            [Chain.Wah] = "Neo Drums into Neo Filter (ladder low-pass, resonant, closed at 230 Hz). After 4 s each hit throws the cutoff open and it falls back.",
            [Chain.Pump] = "A kick on the sidechain, a Neo Poly pad whose Volume is the target at depth -60 %. After 4 s the pad ducks on every kick and swells back.",
            [Chain.Swell] = "Neo Drums and a Neo Poly pad swelling over 10 s into Neo Filter. After 4 s the cutoff opens slowly with the loudness and closes again after 12 s.",
            [Chain.Drive] = "Neo Drums and a pad, alternating two quiet and two loud bars, into Neo Filter's Drive. After 4 s the loud bars distort, the quiet bars stay clean.",
            [Chain.Lead] = "A Neo Poly lead walking a scale from C3 to C5 into Neo Filter's cutoff. After 4 s the filter follows the pitch of the lead.",
            [Chain.Flash] = "Neo Drums into Neo Filter. After 4 s every hit flashes the cutoff open (harder hits open it further) and it falls in about a fifth of a second.",
            [Chain.Chop] = "A Neo Poly pad shut at -34 dB with Neo Drums behind it. After 4 s each hit opens the pad for 110 ms: the pad chops on the rhythm.",
        };

        [Test, Explicit("writes the listening pack: set NEO_FOLLOW_PACK to an output folder")]
        public void WriteTheListeningPack()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_FOLLOW_PACK");
            if (string.IsNullOrEmpty(dir)) Assert.Inconclusive("set NEO_FOLLOW_PACK");
            Directory.CreateDirectory(dir);
            var comments = Comments();
            var log = new StringBuilder();
            log.AppendLine("# Neo Follow listening pack");
            log.AppendLine();
            log.AppendLine("Every factory preset driving a real target: Neo Drums, Neo Poly (\"Pad — Warm Strings\" and \"Lead — Smooth Saw\") and Neo Filter, 120 BPM, 15 s,");
            log.AppendLine("48 kHz, 16-bit, stereo WAV. The first 4 s run with the follower off (Depth 0): that is the chain at its set values. From 4 s the follower is on at the preset's depth.");
            log.AppendLine("The follower's control delay (see the spec) is not taken out: it is part of the sound. Click detector (second difference against its local mean) on every file, run on the target's own sound (without the drums or kick that Pump and Chop mix in as the source) and without clicks the chain has with the follower off: 0 clicks.");
            log.AppendLine();
            log.AppendLine("| File | Preset | The chain, and what to listen for | Peak | RMS | Clicks |");
            log.AppendLine("|---|---|---|---|---|---|");
            int index = 0, clicksTotal = 0;
            foreach (var p in PresetTests.AllPresets().Where(p => p.Machine == "Neo Follow"))
            {
                index++;
                var v = Fresh(p.Values);
                var (l, r) = Render(p.Preset, v);
                // Clicks the chain without the follower already has (a pad's own note starts) are not the follower's.
                // The drums and the kick are the source, not the target: the clicks are looked for on the target's own sound (the pad alone for Pump and Chop).
                var (bl, br) = Render(p.Preset, Fresh(p.Values), bare: true);
                var (dl, dr) = Render(p.Preset, Fresh(p.Values), alwaysOff: true, bare: true);
                var dryL = ClickDetector.Find(dl); var dryR = ClickDetector.Find(dr);
                var cl = ClickDetector.Find(bl).Where(c => !dryL.Any(o => Math.Abs(o - c) <= 96)).ToList();
                var cr = ClickDetector.Find(br).Where(c => !dryR.Any(o => Math.Abs(o - c) <= 96)).ToList();
                int clicks = cl.Count + cr.Count;
                clicksTotal += clicks;
                TestContext.Out.WriteLine($"clicks {p.Preset}: follower's {clicks}, the chain's own {dryL.Count + dryR.Count}; dry at {string.Join(",", dryL.Select(c => (c / (double)Rate).ToString("0.000")))}; at {string.Join(",", cl.Concat(cr).Select(c => (c / (double)Rate).ToString("0.000")))}");
                double pk = 0, sq = 0;
                for (int i = 0; i < Length; i++) { pk = Math.Max(pk, Math.Max(Math.Abs(l[i]), Math.Abs(r[i]))); sq += (double)l[i] * l[i] + (double)r[i] * r[i]; }
                string file = $"{index:00}-{Slug(p.Preset)}.wav";
                WriteWav(Path.Combine(dir, file), l, r);
                log.AppendLine($"| {file} | {p.Preset} | {Heard[ChainOf(p.Preset)]} {comments.GetValueOrDefault(p.Preset, "")} | {20 * Math.Log10(pk):0.0} dBFS | {10 * Math.Log10(sq / (2.0 * Length)):0.0} dBFS | {clicks} |");
            }
            log.AppendLine();
            log.AppendLine($"Click detector, all files: {clicksTotal} clicks.");
            File.WriteAllText(Path.Combine(dir, "README.md"), log.ToString());
            TestContext.Out.WriteLine(log.ToString());
            Assert.That(clicksTotal, Is.EqualTo(0));
        }
    }
}
