#nullable disable
// Neo Chord's listening pack (CHD1; AGENTS.md §5): each preset plays a four-bar progression through Neo Poly on one fixed plain patch (its default parameters),
// 48 kHz, 16-bit WAV, with a README. Local only (never committed): NEO_CHORD_PACK=<folder> makes it; without it the test does nothing but still checks that the renders are
// finite and click-free (the click detector on every render: 0 clicks).
//
// The input is the same for every item: four single notes, one a bar (C3, A2, F3, G2), 4/4 at 64 beats a minute (3.75 s a bar, 15 s with a short tail). A Fixed preset turns them into
// the chord it is set to; a Diatonic preset in C hears them as the degrees I vi IV V.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NeoBuzz.Chord;
using NeoBuzz.Dsp;
using NUnit.Framework;

namespace NeoBuzz.MachineTests
{
    public class ChordPack
    {
        const int Rate = 48000;
        const double Bar = 3.75;

        static readonly int[] Roots = { 48, 45, 53, 43 };

        static void WriteWav(string path, float[] mono, float gain)
        {
            using var f = File.Create(path);
            using var w = new BinaryWriter(f);
            int n = mono.Length, bytes = n * 4;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            for (int i = 0; i < n; i++)
            {
                short s = (short)Math.Clamp((int)Math.Round(mono[i] * gain * 32767.0), -32768, 32767);
                w.Write(s); w.Write(s);
            }
        }

        static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Replace("--", "-").Trim('-');

        static string Describe(ParamValues v)
        {
            var sb = new StringBuilder();
            foreach (var p in Params.Table)
            {
                if (v[p.Index] == p.Default) continue;
                sb.Append(p.Name).Append(' ').Append(p.Describe(v[p.Index])).Append("; ");
            }
            return sb.Length == 0 ? "defaults" : sb.ToString().TrimEnd(' ', ';');
        }

        [Test]
        public void EveryPresetRendersThroughNeoPolyWithoutClicks()
        {
            string dir = Environment.GetEnvironmentVariable("NEO_CHORD_PACK");
            var presets = PresetTests.AllPresets().Where(p => p.Machine == "Neo Chord").ToList();
            Assert.That(presets.Count, Is.InRange(6, 8));
            long length = (long)(4 * Bar * Rate) + Rate;
            var renders = new List<(string Name, ParamValues V, float[] Y, int Clicks)>();
            foreach (var p in presets)
            {
                var v = new ParamValues(Params.Table);
                foreach (var kv in p.Values) if (kv.Key >= 0) v.Set(kv.Key, kv.Value);
                var notes = new List<(long, bool, int, int)>();
                for (int bar = 0; bar < 4; bar++)
                {
                    notes.Add(((long)(bar * Bar * Rate), true, Roots[bar], 100));
                    notes.Add(((long)((bar * Bar + 3.4) * Rate), false, Roots[bar], 0));
                }
                var y = NeoChordTests.RenderThroughPoly(v, notes, length);
                int clicks = ClickDetector.Find(y).Count;
                Assert.That(y.All(float.IsFinite), Is.True, p.Preset);
                Assert.That(y.Max(Math.Abs), Is.GreaterThan(0.01f), p.Preset + " is audible");
                Assert.That(clicks, Is.EqualTo(0), p.Preset + ": clicks");
                renders.Add((p.Preset, v, y, clicks));
                TestContext.Out.WriteLine($"{p.Preset}: peak {y.Max(Math.Abs):F3}, {clicks} clicks");
            }
            if (string.IsNullOrEmpty(dir)) return;

            Directory.CreateDirectory(dir);
            float loudest = renders.Max(r => r.Y.Max(Math.Abs));
            float gain = 0.708f / loudest;      // one gain for all items: the loudest peaks at -3 dBFS, the levels between items stay as they are
            var readme = new StringBuilder();
            readme.AppendLine("Neo Chord listening pack (CHD1)").AppendLine();
            readme.AppendLine("Each item: one preset of Neo Chord in front of Neo Poly (its default patch, nothing changed), playing the same four single notes, one a bar:");
            readme.AppendLine("C3, A2, F3, G2 at 64 beats a minute, 4/4 (3.75 s a bar), 15 s and a 1 s tail. Fixed presets make the chord they are set to on each note;");
            readme.AppendLine("the Diatonic preset (key C) hears them as the degrees I, vi, IV, V. 48 kHz, 16-bit, stereo (both sides equal). One gain for every item:");
            readme.AppendLine($"{20 * Math.Log10(gain):F1} dB, so the loudest peaks at -3 dBFS. Click detector (second difference against its local mean): 0 clicks in every item.").AppendLine();
            int index = 1;
            foreach (var (name, v, y, clicks) in renders)
            {
                string file = $"{index:00}-{Slug(name)}.wav";
                WriteWav(Path.Combine(dir, file), y, gain);
                readme.AppendLine($"{file}\n    {name}\n    settings (not default): {Describe(v)}\n    peak {20 * Math.Log10(y.Max(Math.Abs) * gain):F1} dBFS, clicks {clicks}\n");
                index++;
            }
            File.WriteAllText(Path.Combine(dir, "README.txt"), readme.ToString());
        }
    }
}
