// Fork (docs/specs/stem-separation.md): builds the little ONNX models the stem tests run, byte by byte (the protobuf of an ONNX ModelProto),
// so the tests need no real weights and no Python. Each model unsqueezes its input(s) to add a stem axis and multiplies by one gain per stem.
using System.Text;

namespace ReBuzz.SessionTests.Stems;

internal static class TinyOnnx
{
    // ---- protobuf writing
    static void Varint(List<byte> b, ulong v)
    {
        while (v >= 0x80) { b.Add((byte)(v | 0x80)); v >>= 7; }
        b.Add((byte)v);
    }
    static void Tag(List<byte> b, int field, int wire) => Varint(b, (ulong)((field << 3) | wire));
    static void Int(List<byte> b, int field, long v) { Tag(b, field, 0); Varint(b, (ulong)v); }
    static void Bytes(List<byte> b, int field, IEnumerable<byte> data)
    {
        var d = data.ToList();
        Tag(b, field, 2);
        Varint(b, (ulong)d.Count);
        b.AddRange(d);
    }
    static void Str(List<byte> b, int field, string s) => Bytes(b, field, Encoding.UTF8.GetBytes(s));
    static List<byte> Msg(Action<List<byte>> write) { var b = new List<byte>(); write(b); return b; }

    static List<byte> Node(string op, string[] inputs, string output) => Msg(b =>
    {
        foreach (var i in inputs) Str(b, 1, i);
        Str(b, 2, output);
        Str(b, 3, output + "_node");
        Str(b, 4, op);
    });

    static List<byte> Floats(string name, float[] values, params long[] dims) => Msg(b =>
    {
        foreach (var d in dims) Int(b, 1, d);
        Int(b, 2, 1);                                                            // FLOAT
        Bytes(b, 9, values.SelectMany(v => BitConverter.GetBytes(v)));           // raw_data
        Str(b, 8, name);
    });

    static List<byte> Longs(string name, long[] values) => Msg(b =>
    {
        Int(b, 1, values.Length);
        Int(b, 2, 7);                                                            // INT64
        Bytes(b, 9, values.SelectMany(v => BitConverter.GetBytes(v)));           // raw_data
        Str(b, 8, name);
    });

    static List<byte> Info(string name, params long[] dims) => Msg(b =>
    {
        Str(b, 1, name);
        Bytes(b, 2, Msg(t => Bytes(t, 1, Msg(tensor =>
        {
            Int(tensor, 1, 1);
            Bytes(tensor, 2, Msg(shape => { foreach (var d in dims) Bytes(shape, 1, Msg(dim => Int(dim, 1, d))); }));
        }))));
    });

    static byte[] Model(List<byte> graph) => Msg(b =>
    {
        Int(b, 1, 8);                                                            // ir_version
        Str(b, 2, "neobuzz-tests");
        Bytes(b, 7, graph);
        Bytes(b, 8, Msg(o => Int(o, 2, 13)));                                    // opset 13
    }).ToArray();

    /// <summary>
    /// A "spec-mask" model: spec_real and spec_imag [1,2,bins,frames] to out_spec_real and out_spec_imag [1,S,2,bins,frames], stem k being
    /// the input times gains[k].
    /// </summary>
    public static byte[] SpecMask(int bins, int frames, float[] gains)
    {
        int s = gains.Length;
        var g = Msg(b =>
        {
            foreach (var (input, output) in new[] { ("spec_real", "out_spec_real"), ("spec_imag", "out_spec_imag") })
            {
                Bytes(b, 1, Node("Unsqueeze", new[] { input, "axes1" }, input + "_u"));
                Bytes(b, 1, Node("Mul", new[] { input + "_u", "gains" }, output));
            }
            Str(b, 2, "spec_mask");
            Bytes(b, 5, Longs("axes1", new long[] { 1 }));
            Bytes(b, 5, Floats("gains", gains, 1, s, 1, 1, 1));
            Bytes(b, 11, Info("spec_real", 1, 2, bins, frames));
            Bytes(b, 11, Info("spec_imag", 1, 2, bins, frames));
            Bytes(b, 12, Info("out_spec_real", 1, s, 2, bins, frames));
            Bytes(b, 12, Info("out_spec_imag", 1, s, 2, bins, frames));
        });
        return Model(g);
    }

    /// <summary>
    /// An "htdemucs" model: mix [1,2,L] and cac [1,4,bins,frames] to freq [1,S,4,bins,frames] (cac times freqGains[k]) and time [1,S,2,L]
    /// (mix times timeGains[k]); a stem is the inverse transform of the first plus the second.
    /// </summary>
    public static byte[] Demucs(int length, int bins, int frames, float[] freqGains, float[] timeGains)
    {
        int s = freqGains.Length;
        var g = Msg(b =>
        {
            Bytes(b, 1, Node("Unsqueeze", new[] { "cac", "axes1" }, "cac_u"));
            Bytes(b, 1, Node("Mul", new[] { "cac_u", "freq_gains" }, "freq"));
            Bytes(b, 1, Node("Unsqueeze", new[] { "mix", "axes1" }, "mix_u"));
            Bytes(b, 1, Node("Mul", new[] { "mix_u", "time_gains" }, "time"));
            Str(b, 2, "htdemucs");
            Bytes(b, 5, Longs("axes1", new long[] { 1 }));
            Bytes(b, 5, Floats("freq_gains", freqGains, 1, s, 1, 1, 1));
            Bytes(b, 5, Floats("time_gains", timeGains, 1, s, 1, 1));
            Bytes(b, 11, Info("mix", 1, 2, length));
            Bytes(b, 11, Info("cac", 1, 4, bins, frames));
            Bytes(b, 12, Info("freq", 1, s, 4, bins, frames));
            Bytes(b, 12, Info("time", 1, s, 2, length));
        });
        return Model(g);
    }
}
