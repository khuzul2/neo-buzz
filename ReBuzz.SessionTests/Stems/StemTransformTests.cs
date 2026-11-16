// Fork (docs/specs/stem-separation.md): the transform and the chunker around the stem models. Their numbers are checked against torch's
// (a golden file made with torch 2.x and demucs 4.1: Data/stft-golden.json), and the chunker against an identity model.
using System.Text.Json;
using ReBuzz.Core.AudioLibrary.Stems;

namespace ReBuzz.SessionTests.Stems;

[TestFixture]
public class StemTransformTests
{
    static JsonElement Golden() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Stems", "Data", "stft-golden.json"))).RootElement;

    static float[] Floats(JsonElement e) => e.EnumerateArray().Select(x => x.GetSingle()).ToArray();

    static void Near(float[] want, float[] got, double tolerance, string what)
    {
        Assert.That(got.Length, Is.EqualTo(want.Length), what + " length");
        double worst = 0;
        for (int i = 0; i < want.Length; i++) worst = Math.Max(worst, Math.Abs(want[i] - got[i]));
        Assert.That(worst, Is.LessThan(tolerance), $"{what}: largest difference {worst}");
    }

    [Test]
    public void Stft_matches_torch_normalized_and_raw_and_inverts_like_torch()
    {
        var g = Golden();
        var x = Floats(g.GetProperty("x"));
        int frames = g.GetProperty("frames").GetInt32();
        foreach (var (name, normalized) in new[] { ("stft_norm", true), ("stft_raw", false) })
        {
            var stft = new Stft(32, 8, normalized);
            var padded = new float[x.Length + 32];
            Stft.PadReflect(x, padded, 16, 16);
            Assert.That(stft.Frames(padded.Length), Is.EqualTo(frames));
            var re = new float[17 * frames];
            var im = new float[17 * frames];
            stft.Analyze(padded, 0, frames, 17, re, im);
            Near(Floats(g.GetProperty(name + "_re")), re, 2e-5, name + " re");
            Near(Floats(g.GetProperty(name + "_im")), im, 2e-5, name + " im");
        }
        // torch.istft of the normalised spectrum, length n
        var norm = new Stft(32, 8, true);
        var wave = new float[32 + 8 * (frames - 1)];
        norm.Synthesize(Floats(g.GetProperty("stft_norm_re")), Floats(g.GetProperty("stft_norm_im")), frames, 17, frames, 0, wave);
        Near(Floats(g.GetProperty("istft_norm")), wave.AsSpan(16, x.Length).ToArray(), 2e-5, "istft");
    }

    [TestCase(64, 16, true)]
    [TestCase(2048, 512, false)]
    [TestCase(4096, 1024, true)]
    public void Stft_round_trips_a_signal(int size, int hop, bool normalized)
    {
        var rng = new Random(size);
        var x = new float[size * 3 + 123];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(rng.NextDouble() * 2 - 1);
        var stft = new Stft(size, hop, normalized);
        var padded = new float[x.Length + size];
        Stft.PadReflect(x, padded, size / 2, size / 2);
        int frames = stft.Frames(padded.Length);
        var re = new float[stft.Bins * frames];
        var im = new float[stft.Bins * frames];
        stft.Analyze(padded, 0, frames, stft.Bins, re, im);
        var wave = new float[size + hop * (frames - 1)];
        stft.Synthesize(re, im, frames, stft.Bins, frames, 0, wave);
        Near(x, wave.AsSpan(size / 2, x.Length).ToArray(), 2e-5, "round trip");
    }

    [Test]
    public void PadReflect_is_torchs_reflect_and_goes_on_mirroring()
    {
        var d = new float[9];
        Stft.PadReflect(new float[] { 1, 2, 3 }, d, 2, 4);
        Assert.That(d, Is.EqualTo(new float[] { 3, 2, 1, 2, 3, 2, 1, 2, 3 }));
    }

    [Test]
    public void Demucs_transform_matches_HTDemucs_spec_and_ispec()
    {
        var d = Golden().GetProperty("demucs");
        int L = d.GetProperty("L").GetInt32(), T = d.GetProperty("T").GetInt32(), S = d.GetProperty("S").GetInt32();
        var mix = Floats(d.GetProperty("mix"));              // [2][L]
        var cac = Floats(d.GetProperty("cac"));              // [4][bins][T]
        var freq = Floats(d.GetProperty("freq"));            // [S][4][bins][T]
        var want = Floats(d.GetProperty("out"));             // [S][2][L]
        var tr = new DemucsTransform(L, 32, 8);
        Assert.That(tr.Frames, Is.EqualTo(T));
        Assert.That(tr.Bins, Is.EqualTo(16));
        int plane = tr.Bins * T;
        var gotCac = new float[4 * plane];
        for (int ch = 0; ch < 2; ch++)
            tr.Forward(mix.AsSpan(ch * L, L), gotCac.AsSpan(ch * 2 * plane, plane), gotCac.AsSpan((ch * 2 + 1) * plane, plane));
        Near(cac, gotCac, 2e-5, "cac");
        for (int s = 0; s < S; s++)
            for (int ch = 0; ch < 2; ch++)
            {
                var got = new float[L];
                int b = (s * 4 + ch * 2) * plane;
                tr.AddInverse(freq.AsSpan(b, plane), freq.AsSpan(b + plane, plane), got);
                Near(want.AsSpan((s * 2 + ch) * L, L).ToArray(), got, 2e-5, $"ispec stem {s} channel {ch}");
            }
    }

    // ---------------------------------------------------------------- the chunker

    sealed class Identity : IChunkModel
    {
        public int Segment { get; init; }
        public int Stems { get; init; } = 1;
        public int Calls;
        public void Process(ReadOnlySpan<float> left, ReadOnlySpan<float> right, Span<float> output, CancellationToken cancel)
        {
            Calls++;
            for (int s = 0; s < Stems; s++)
            {
                float gain = 1f / (s + 1);
                for (int i = 0; i < Segment; i++)
                {
                    output[(s * 2) * Segment + i] = left[i] * gain;
                    output[(s * 2 + 1) * Segment + i] = right[i] * gain;
                }
            }
        }
    }

    [TestCase(1000, 4096, 0.25)]       // shorter than a segment
    [TestCase(4096, 4096, 0.25)]       // exactly one
    [TestCase(10000, 4096, 0.25)]
    [TestCase(10000, 4096, 0.5)]
    [TestCase(343980 + 777, 343980, 0.25)]
    public void OverlapAdd_of_an_identity_model_gives_back_the_input(int length, int segment, double overlap)
    {
        var rng = new Random(length);
        var l = new float[length];
        var r = new float[length];
        for (int i = 0; i < length; i++) { l[i] = (float)(rng.NextDouble() * 2 - 1); r[i] = (float)(rng.NextDouble() * 2 - 1); }
        var model = new Identity { Segment = segment, Stems = 2 };
        var results = new[] { new float[length], new float[length], new float[length], new float[length] };
        double last = 0;
        OverlapAdd.Run(model, l, r, length, overlap, results, p => last = p, CancellationToken.None);
        Assert.That(last, Is.EqualTo(1.0).Within(1e-9));
        Assert.That(model.Calls, Is.EqualTo(OverlapAdd.Plan(length, segment, overlap).Count));
        double worst = 0;
        for (int i = 0; i < length; i++)
        {
            worst = Math.Max(worst, Math.Abs(results[0][i] - l[i]));
            worst = Math.Max(worst, Math.Abs(results[1][i] - r[i]));
            worst = Math.Max(worst, Math.Abs(results[2][i] - l[i] * 0.5f));
            worst = Math.Max(worst, Math.Abs(results[3][i] - r[i] * 0.5f));
        }
        Assert.That(worst, Is.LessThan(1e-5), $"largest difference {worst}");
    }

    [Test]
    public void OverlapAdd_plans_like_demucs_and_weights_are_a_triangle()
    {
        // stride int((1 - 0.25) * 343980) = 257985; offsets 0, 257985, 515970 for 600000 samples
        Assert.That(OverlapAdd.Plan(600000, 343980, 0.25), Is.EqualTo((3, 257985)));
        Assert.That(OverlapAdd.Weight(0, 10), Is.EqualTo(0.2f).Within(1e-6));
        Assert.That(OverlapAdd.Weight(4, 10), Is.EqualTo(1f).Within(1e-6));
        Assert.That(OverlapAdd.Weight(9, 10), Is.EqualTo(0.2f).Within(1e-6));
        Assert.That(OverlapAdd.Weight(5, 10), Is.EqualTo(1f).Within(1e-6));
    }

    [Test]
    public void OverlapAdd_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var model = new Identity { Segment = 1000 };
        var x = new float[10000];
        var results = new[] { new float[10000], new float[10000] };
        Assert.Throws<OperationCanceledException>(() =>
            OverlapAdd.Run(model, x, x, x.Length, 0.25, results, p => cts.Cancel(), cts.Token));
        Assert.That(model.Calls, Is.EqualTo(1));
    }
}
