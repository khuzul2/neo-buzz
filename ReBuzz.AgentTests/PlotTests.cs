using System.IO.Compression;
using System.Text;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using static ReBuzz.AgentTests.Signals;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// Plots (docs/specs/mcp-server.md §5.7 plot) are real PNG files: the signature, chunks with
    /// valid CRCs, an RGB header, and image data that inflates to exactly one filtered row per line.
    /// Set PLOTS_DIR to keep the images for a look.
    /// </summary>
    [TestFixture]
    public class PlotTests
    {
        sealed record Decoded(int Width, int Height, byte[] Rgb, List<string> Chunks);

        /// <summary>Decodes an 8-bit RGB, non-interlaced PNG, checking everything on the way.</summary>
        static Decoded Decode(byte[] png)
        {
            Assert.That(png.Take(8), Is.EqualTo(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "PNG signature");
            int at = 8, width = 0, height = 0;
            var chunks = new List<string>();
            var idat = new MemoryStream();
            while (at < png.Length)
            {
                int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
                string type = Encoding.ASCII.GetString(png, at + 4, 4);
                chunks.Add(type);
                uint crc = (uint)((png[at + 8 + length] << 24) | (png[at + 9 + length] << 16) | (png[at + 10 + length] << 8) | png[at + 11 + length]);
                Assert.That(crc, Is.EqualTo(Crc32(png, at + 4, length + 4)), $"{type} CRC");
                if (type == "IHDR")
                {
                    Assert.That(length, Is.EqualTo(13));
                    width = (png[at + 8] << 24) | (png[at + 9] << 16) | (png[at + 10] << 8) | png[at + 11];
                    height = (png[at + 12] << 24) | (png[at + 13] << 16) | (png[at + 14] << 8) | png[at + 15];
                    Assert.That(png[at + 16], Is.EqualTo(8), "bit depth");
                    Assert.That(png[at + 17], Is.EqualTo(2), "colour type RGB");
                    Assert.That(png[at + 20], Is.EqualTo(0), "not interlaced");
                }
                else if (type == "IDAT") idat.Write(png, at + 8, length);
                at += 12 + length;
            }
            Assert.That(at, Is.EqualTo(png.Length), "chunks fill the file exactly");
            Assert.That(chunks.First(), Is.EqualTo("IHDR"));
            Assert.That(chunks.Last(), Is.EqualTo("IEND"));
            idat.Position = 0;
            using var z = new ZLibStream(idat, CompressionMode.Decompress);
            var raw = new MemoryStream();
            z.CopyTo(raw);
            int stride = 1 + 3 * width;
            Assert.That(raw.Length, Is.EqualTo((long)height * stride), "IDAT inflates to height rows of (1 + 3 × width) bytes");
            // Undo the row filters (None, Sub, Up, Average, Paeth).
            var data = raw.ToArray();
            var rgb = new byte[width * height * 3];
            for (int y = 0; y < height; y++)
            {
                byte filter = data[y * stride];
                Assert.That(filter, Is.InRange(0, 4), $"row {y} filter");
                for (int i = 0; i < 3 * width; i++)
                {
                    int a = i >= 3 ? rgb[y * 3 * width + i - 3] : 0, b = y > 0 ? rgb[(y - 1) * 3 * width + i] : 0, c = i >= 3 && y > 0 ? rgb[(y - 1) * 3 * width + i - 3] : 0;
                    int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    int predictor = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c };
                    rgb[y * 3 * width + i] = (byte)(data[y * stride + 1 + i] + predictor);
                }
            }
            return new Decoded(width, height, rgb, chunks);
        }

        static uint Crc32(byte[] data, int offset, int count)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = offset; i < offset + count; i++)
            {
                crc ^= data[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }

        static (byte R, byte G, byte B) Pixel(Decoded d, int x, int y) => (d.Rgb[(y * d.Width + x) * 3], d.Rgb[(y * d.Width + x) * 3 + 1], d.Rgb[(y * d.Width + x) * 3 + 2]);

        static void Keep(string name, byte[] png)
        {
            string? dir = Environment.GetEnvironmentVariable("PLOTS_DIR");
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), png);
        }

        [Test]
        public void EveryPlotIsAPngThatDecodes()
        {
            var (tracks, master) = AudioReportTests.PlantedMix(3);
            var reference = new StereoAudio { Left = PeriodicNoise(-1, 0.05, 4.5), Rate = Rate };
            foreach (var kind in AudioPlot.Kinds)
            {
                var sources = kind switch
                {
                    "spectrum" => new List<(string, StereoAudio)> { ("mix", master), ("pink reference", reference) },
                    "bands" => tracks,
                    _ => new List<(string, StereoAudio)> { ("mix", master) },
                };
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var png = AudioPlot.Draw(kind, sources);
                Keep(kind, png);
                var d = Decode(png);
                TestContext.Out.WriteLine($"{kind}: {png.Length / 1024} KB, {watch.ElapsedMilliseconds} ms");
                Assert.That((d.Width, d.Height), Is.EqualTo((900, 400)), kind);
                Assert.That(d.Chunks, Is.EqualTo(new[] { "IHDR", "IDAT", "IEND" }), kind);
                Assert.That(Pixel(d, 0, 0), Is.EqualTo(((byte)0xfc, (byte)0xfc, (byte)0xfb)), kind + ": the background");
                int inked = 0;
                for (int i = 0; i < d.Rgb.Length; i += 3)
                    if (d.Rgb[i] < 0x80 && d.Rgb[i + 1] < 0x80) inked++;
                Assert.That(inked, Is.GreaterThan(2000), kind + ": text, axes and data are drawn");
            }
        }

        [Test]
        public void PlotsTakeSizesAndRejectUnknownKinds()
        {
            var tone = new StereoAudio { Left = Sine(440, 0.5, 1), Rate = Rate };
            var d = Decode(AudioPlot.Draw("waveform", new[] { ("tone", tone) }, width: 640, height: 300));
            Assert.That((d.Width, d.Height), Is.EqualTo((640, 300)));
            var e = Assert.Throws<ToolError>(() => AudioPlot.Draw("histogram", new[] { ("tone", tone) }));
            Assert.That(e!.Message, Does.Contain("spectrogram"));
        }

        [Test]
        public void TheFontDrawsEveryPrintableCharacter()
        {
            var c = new Canvas(6 * 95 * 2, 20, new Rgb(0xffffff));
            string all = new(Enumerable.Range(32, 95).Select(i => (char)i).ToArray());
            c.Text(0, 2, all, new Rgb(0x000000), 2);
            var d = Decode(c.Png());
            Keep("font", c.Png());
            // Every glyph but the space sets pixels in its own cell.
            for (int ch = 33; ch <= 126; ch++)
            {
                int x0 = (ch - 32) * 12, set = 0;
                for (int y = 0; y < 20; y++)
                    for (int x = x0; x < x0 + 12; x++)
                        if (Pixel(d, x, y).R == 0) set++;
                Assert.That(set, Is.GreaterThan(0), $"'{(char)ch}'");
            }
            Assert.That(Canvas.TextWidth("−3 dB", 2), Is.EqualTo(Canvas.TextWidth("-3 dB", 2)), "a minus sign draws as a hyphen");
        }
    }
}
