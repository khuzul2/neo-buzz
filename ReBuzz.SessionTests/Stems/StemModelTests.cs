// Fork (docs/specs/stem-separation.md): the model files, ONNX Runtime and the whole separation, with models built in the test (TinyOnnx).
using System.IO.Compression;
using System.Text.Json;
using ReBuzz.Core.AudioLibrary.Stems;

namespace ReBuzz.SessionTests.Stems;

[TestFixture]
public class StemModelTests
{
    string dir = "";
    string oldData = "", oldGear = "";

    [SetUp]
    public void SetUp()
    {
        dir = Path.Combine(Path.GetTempPath(), "stem-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        oldData = StemModels.DataFolder;
        oldGear = StemModels.GearFolder;
        StemModels.DataFolder = Path.Combine(dir, "data");
        StemModels.GearFolder = Path.Combine(dir, "gear");
    }

    [TearDown]
    public void TearDown()
    {
        StemModels.DataFolder = oldData;
        StemModels.GearFolder = oldGear;
        try { Directory.Delete(dir, true); } catch (IOException) { }
    }

    static readonly float[] Gains = { 0.4f, 0.3f, 0.2f, 0.1f };             // vocals, drums, bass, other: they sum to 1

    /// <summary>Writes a model folder: the ONNX bytes and a model.json that maps the four stems to the four outputs.</summary>
    string MakeModel(string root, string id, string quality, string family, byte[] onnx, int segment, int nfft, int hop, string revision = "1")
    {
        string folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "m.onnx"), onnx);
        var outputs = new Dictionary<string, int[]> { ["vocals"] = new[] { 0 }, ["drums"] = new[] { 1 }, ["bass"] = new[] { 2 }, ["other"] = new[] { 3 } };
        File.WriteAllText(Path.Combine(folder, "model.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["id"] = id, ["quality"] = quality, ["family"] = family, ["sampleRate"] = 44100, ["segment"] = segment, ["overlap"] = 0.25,
            ["nfft"] = nfft, ["hop"] = hop, ["outputStems"] = 4, ["revision"] = revision,
            ["stems"] = new[] { "vocals", "drums", "bass", "other" },
            ["passes"] = new[] { new Dictionary<string, object> { ["file"] = "m.onnx", ["outputs"] = outputs } },
        }));
        return folder;
    }

    StemModelManifest SpecMaskModel(string quality = "Best")
    {
        const int nfft = 64, hop = 16, segment = 2048;
        int frames = 1 + segment / hop;
        string root = Path.Combine(dir, "models");
        MakeModel(root, "tiny-spec", quality, "spec-mask", TinyOnnx.SpecMask(nfft / 2 + 1, frames, Gains), segment, nfft, hop);
        return StemModels.For(Enum.Parse<StemQuality>(quality), new[] { root })!;
    }

    static (float[] L, float[] R) Signal(int length, int rate = 44100, double noise = 0.1)
    {
        var l = new float[length];
        var r = new float[length];
        var rng = new Random(5);
        for (int i = 0; i < length; i++)
        {
            double t = (double)i / rate;
            l[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 220 * t) + noise * (rng.NextDouble() - 0.5));
            r[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 330 * t) + noise * (rng.NextDouble() - 0.5));
        }
        return (l, r);
    }

    static double Worst(float[] a, float[] b, float gain)
    {
        double w = 0;
        for (int i = 0; i < a.Length; i++) w = Math.Max(w, Math.Abs(a[i] - b[i] * gain));
        return w;
    }

    [Test]
    public void A_spec_mask_model_runs_through_ONNX_Runtime_and_splits_by_its_gains()
    {
        var m = SpecMaskModel();
        var (l, r) = Signal(10000);
        double last = 0;
        var res = StemSeparator.Separate(m, l, r, 44100, null, null, p => last = p, CancellationToken.None);
        Assert.That(last, Is.EqualTo(1).Within(1e-9));
        Assert.That(res.Stems.Keys, Is.EquivalentTo(StemSeparator.AllStems));
        for (int k = 0; k < 4; k++)
        {
            string stem = new[] { "vocals", "drums", "bass", "other" }[k];
            Assert.That(res.Stems[stem].Left.Length, Is.EqualTo(10000));
            Assert.That(Worst(res.Stems[stem].Left, l, Gains[k]), Is.LessThan(2e-4), stem + " left");
            Assert.That(Worst(res.Stems[stem].Right, r, Gains[k]), Is.LessThan(2e-4), stem + " right");
        }
    }

    [Test]
    public void An_htdemucs_model_runs_both_branches_and_the_inverse_transform()
    {
        const int nfft = 64, hop = 16, segment = 1500;
        int frames = (segment + hop - 1) / hop;
        string root = Path.Combine(dir, "models");
        float[] freq = { 0.3f, 0.2f, 0.1f, 0.05f }, time = { 0.1f, 0.1f, 0.1f, 0.05f };
        MakeModel(root, "tiny-demucs", "Fast", "htdemucs", TinyOnnx.Demucs(segment, nfft / 2, frames, freq, time), segment, nfft, hop);
        var m = StemModels.For(StemQuality.Fast, new[] { root })!;
        // no noise: Demucs's transform drops the Nyquist bin, so only a signal without content there comes back exactly
        var (l, r) = Signal(5000, noise: 0);
        var res = StemSeparator.Separate(m, l, r, 44100, new[] { "bass" }, null, null, CancellationToken.None);
        Assert.That(res.Stems.Keys, Is.EquivalentTo(new[] { "bass" }));
        // As in Demucs, the first and last frames of a segment's inverse lack their neighbours (the padded frames are zero), so the ends of a
        // segment differ a little (weighted down where segments meet); the rest follows the gains.
        const int edge = 200;
        Assert.That(Worst(res.Stems["bass"].Left[edge..^edge], l[edge..^edge], freq[2] + time[2]), Is.LessThan(3e-3));
        Assert.That(Worst(res.Stems["bass"].Right[edge..^edge], r[edge..^edge], freq[2] + time[2]), Is.LessThan(3e-3));
        double sum = 0;
        for (int i = edge; i < l.Length - edge; i++) sum += Math.Abs(res.Stems["bass"].Left[i] - l[i] * (freq[2] + time[2]));
        Assert.That(sum / (l.Length - 2 * edge), Is.LessThan(2e-5));
    }

    [Test]
    public void A_recording_at_another_rate_comes_back_at_its_own_rate_and_length()
    {
        var m = SpecMaskModel();
        var (l, r) = Signal(48000 / 2, 48000);
        var res = StemSeparator.Separate(m, l, r, 48000, new[] { "drums", "other" }, null, null, CancellationToken.None);
        Assert.That(res.Stems.Keys, Is.EquivalentTo(new[] { "drums", "other" }));
        Assert.That(res.Stems["drums"].Left.Length, Is.EqualTo(l.Length));
        // The resampling to 44.1 kHz and back is not bit exact: the stems follow the gains to a loose tolerance.
        Assert.That(Worst(res.Stems["drums"].Left.AsSpan(2000, 20000).ToArray(), l.AsSpan(2000, 20000).ToArray(), Gains[1]), Is.LessThan(0.01));
    }

    [Test]
    public void A_mono_recording_stays_one_signal()
    {
        var m = SpecMaskModel();
        var (l, _) = Signal(6000);
        var res = StemSeparator.Separate(m, l, l, 44100, new[] { "vocals" }, null, null, CancellationToken.None);
        Assert.That(ReferenceEquals(res.Stems["vocals"].Left, res.Stems["vocals"].Right));
    }

    [Test]
    public void The_second_run_with_the_same_audio_and_model_comes_from_the_cache()
    {
        var m = SpecMaskModel();
        var (l, r) = Signal(9000);
        var first = StemSeparator.Separate(m, l, r, 44100, null, "a0123456789abcdef", null, CancellationToken.None);
        Assert.That(first.FromCache, Is.False);
        var second = StemSeparator.Separate(m, l, r, 44100, null, "a0123456789abcdef", null, CancellationToken.None);
        Assert.That(second.FromCache, Is.True);
        foreach (var stem in StemSeparator.AllStems)
        {
            Assert.That(second.Stems[stem].Left, Is.EqualTo(first.Stems[stem].Left));
            Assert.That(second.Stems[stem].Right, Is.EqualTo(first.Stems[stem].Right));
        }
        // another audio id, or a model with another revision, misses
        Assert.That(StemSeparator.Separate(m, l, r, 44100, new[] { "bass" }, "a1111111111111111", null, CancellationToken.None).FromCache, Is.False);
        m.Revision = "2";
        Assert.That(StemSeparator.Separate(m, l, r, 44100, new[] { "bass" }, "a0123456789abcdef", null, CancellationToken.None).FromCache, Is.False);
    }

    [Test]
    public void Cancelling_stops_a_run_and_leaves_no_cache()
    {
        var m = SpecMaskModel();
        var (l, r) = Signal(60000);
        using var cts = new CancellationTokenSource();
        Assert.Throws<OperationCanceledException>(() =>
            StemSeparator.Separate(m, l, r, 44100, null, "acancelled0000000", p => cts.Cancel(), cts.Token));
        Assert.That(Directory.Exists(Path.Combine(StemCache.Root)) && Directory.GetDirectories(StemCache.Root).Length > 0, Is.False);
    }

    [Test]
    public void Stem_names_are_checked_and_all_means_four()
    {
        Assert.That(StemSeparator.Normalise(null), Is.EqualTo(StemSeparator.AllStems));
        Assert.That(StemSeparator.Normalise(new[] { "ALL" }), Is.EqualTo(StemSeparator.AllStems));
        Assert.That(StemSeparator.Normalise(new[] { "bass", "vocals" }), Is.EqualTo(new[] { "vocals", "bass" }));
        Assert.Throws<ArgumentException>(() => StemSeparator.Normalise(new[] { "guitar" }));
    }

    // ---------------------------------------------------------------- finding and installing

    [Test]
    public void Models_are_found_by_quality_and_a_missing_one_says_how_to_install()
    {
        string root = Path.Combine(dir, "models");
        MakeModel(root, "fast-one", "Fast", "htdemucs", new byte[] { 1 }, 1500, 64, 16);
        Assert.That(StemModels.For(StemQuality.Fast, new[] { root })?.Id, Is.EqualTo("fast-one"));
        Assert.That(StemModels.For(StemQuality.Best, new[] { root }), Is.Null);
        Assert.That(StemModels.Missing(StemQuality.Best, new[] { root }), Does.Contain("Install models"));
        Assert.That(StemModels.Missing(StemQuality.Fast, new[] { root }), Is.Null);
        // a model whose ONNX file is gone is not complete
        File.Delete(Path.Combine(root, "fast-one", "m.onnx"));
        Assert.That(StemModels.For(StemQuality.Fast, new[] { root }), Is.Null);
    }

    [Test]
    public void The_models_folder_of_the_preferences_is_searched_first_and_kept()
    {
        string mine = Path.Combine(dir, "mine");
        StemModels.UserFolder = mine;
        Assert.That(StemModels.UserFolder, Is.EqualTo(mine));
        Assert.That(StemModels.SearchFolders()[0], Is.EqualTo(mine));
        Assert.That(StemModels.InstallFolder(), Is.EqualTo(mine));
        StemModels.UserFolder = "";
        Assert.That(StemModels.SearchFolders()[0], Is.EqualTo(StemModels.GearFolder));
        Assert.That(StemModels.InstallFolder(), Is.EqualTo(StemModels.GearFolder));
    }

    [Test]
    public void Install_takes_the_zip_and_refuses_one_with_no_model_or_a_bad_path()
    {
        string src = Path.Combine(dir, "src");
        MakeModel(src, "htdemucs", "Fast", "htdemucs", new byte[] { 1, 2, 3 }, 1500, 64, 16);
        MakeModel(src, "bs-roformer", "Best", "spec-mask", new byte[] { 4, 5 }, 2048, 64, 16);
        string zip = Path.Combine(dir, "models.zip");
        ZipFile.CreateFromDirectory(src, zip, CompressionLevel.Fastest, true, System.Text.Encoding.UTF8);  // the models' folders at the top, as the workflow zips them
        string dest = Path.Combine(dir, "installed");
        var ids = StemModels.Install(zip, dest);
        Assert.That(ids, Is.EquivalentTo(new[] { "htdemucs", "bs-roformer" }));
        Assert.That(File.ReadAllBytes(Path.Combine(dest, "htdemucs", "m.onnx")), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(StemModels.For(StemQuality.Best, new[] { dest })?.Id, Is.EqualTo("bs-roformer"));
        // again: replaced, not doubled
        Assert.That(StemModels.Install(zip, dest).Count, Is.EqualTo(2));
        Assert.That(Directory.GetDirectories(dest).Length, Is.EqualTo(2));

        string empty = Path.Combine(dir, "empty.zip");
        using (var z = ZipFile.Open(empty, ZipArchiveMode.Create)) z.CreateEntry("readme.txt");
        Assert.That(() => StemModels.Install(empty, dest), Throws.TypeOf<InvalidDataException>().With.Message.Contains("no stem model"));

        string evil = Path.Combine(dir, "evil.zip");
        using (var z = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("../x/model.json").Open());
            w.Write("{}");
        }
        Assert.That(() => StemModels.Install(evil, dest), Throws.TypeOf<InvalidDataException>());
        Assert.That(Directory.Exists(Path.Combine(dir, "x")), Is.False);
    }

    [Test]
    public void A_model_json_that_is_wrong_is_refused_with_the_reason()
    {
        string f = Path.Combine(dir, "model.json");
        File.WriteAllText(f, "{\"id\":\"x\",\"quality\":\"Loud\",\"family\":\"htdemucs\"}");
        Assert.That(StemModelManifest.Load(f, out var why), Is.Null);
        Assert.That(why, Does.Contain("quality"));
        File.WriteAllText(f, "{\"id\":\"x\",\"quality\":\"Fast\",\"family\":\"htdemucs\",\"segment\":8,\"nfft\":64,\"hop\":16,\"outputStems\":4," +
                             "\"passes\":[{\"file\":\"../evil.onnx\",\"outputs\":{\"bass\":[0]}}]}");
        Assert.That(StemModelManifest.Load(f, out why), Is.Null);
        Assert.That(why, Does.Contain("outside the folder"));
    }
}
