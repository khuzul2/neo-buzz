// Fork (docs/specs/neo-audio.md, AW1): the test signals of auto-warp, synthesised, with the exact beats and downbeats of each: a drum
// clip, a bass and drums mix, and a pad (weak onsets), over a steady tempo, a slow drift, a step and a ritardando; with silence at the
// start and a pickup before bar 1 when asked. Shared by the unit tests and the evaluation tool (tools/AutoWarpEval).

namespace ReBuzz.SessionTests.AutoWarp;

public sealed class WarpLine
{
    public string Name = "";
    public int Rate;
    public float[] Audio = Array.Empty<float>();
    /// <summary>Seconds of every beat from the first sound (a pickup's beats included), and of the downbeats among them.</summary>
    public double[] Beats = Array.Empty<double>();
    public double[] Downbeats = Array.Empty<double>();
    /// <summary>Seconds of the first downbeat (bar 1).</summary>
    public double FirstDownbeat;
    public int BeatsPerBar = 4;
}

public static class WarpSignals
{
    public const int Rate = 44100;

    public enum Tempo { Steady, Drift, Step, Ritardando }
    public enum Style { Drums, Mix, Pad }

    /// <summary>The tempo (BPM) at a time of the line, for a kind of tempo.</summary>
    public static double BpmAt(Tempo kind, double t, double seconds, double scale = 1, double period = 45, double phase = 0) => scale * kind switch
    {
        Tempo.Steady => 120,
        Tempo.Drift => 120 * (1 + 0.06 * Math.Sin(2 * Math.PI * t / period + phase)),
        Tempo.Step => t < seconds * 0.5 ? 100 : 120,
        Tempo.Ritardando => 130 - 40 * Math.Clamp((t - seconds * 0.25) / (seconds * 0.7), 0, 1),
        _ => 120,
    };

    /// <summary>
    /// A line of <paramref name="seconds"/>: <paramref name="lead"/> seconds of silence, then <paramref name="pickup"/> beats before bar 1, then bars of 4/4.
    /// A step happens on a bar line. The sound of every beat is placed at its exact time.
    /// </summary>
    public static WarpLine Make(Tempo kind, Style style, double seconds = 64, int rate = Rate, double lead = 0, int pickup = 0, int seed = 1, int beatsPerBar = 4, double scale = 1, double period = 45, double phase = 0)
    {
        var rng = new Rng((uint)(seed * 2654435761u + 12345));
        // Beat times from the tempo curve. Beat n = 0 is the first downbeat; pickup beats are n < 0.
        var beats = new List<double>();
        var index = new List<int>();
        double t = lead;
        int n = -pickup;
        double zero = 0;
        bool stepped = false;
        while (t < seconds + 1)
        {
            if (n == 0) zero = t;
            beats.Add(t);
            index.Add(n);
            double clock = t - lead;
            double bpm = BpmAt(kind, clock, seconds, scale, period, phase);
            if (kind == Tempo.Step)
            {
                // The step falls on the first bar line at or after the half.
                if (!stepped && n >= 0 && n % beatsPerBar == 0 && clock >= seconds * 0.5) stepped = true;
                bpm = (stepped ? 120 : 100) * scale;
            }
            t += 60 / bpm;
            n++;
        }
        var audio = new float[(int)(seconds * rate)];
        var line = new WarpLine { Name = $"{kind}-{style}", Rate = rate, Audio = audio, BeatsPerBar = beatsPerBar, FirstDownbeat = zero };
        var chords = new[] { new[] { 40, 47, 52 }, new[] { 43, 50, 55 }, new[] { 45, 52, 57 }, new[] { 38, 45, 50 } };
        var roots = new[] { 28, 31, 33, 26 };       // E1 G1 A1 D1
        for (int i = 0; i < beats.Count; i++)
        {
            double at = beats[i];
            double next = i + 1 < beats.Count ? beats[i + 1] : at + 0.5;
            double span = next - at;
            int b = ((index[i] % beatsPerBar) + beatsPerBar) % beatsPerBar;     // the beat in its bar
            int bar = (int)Math.Floor(index[i] / (double)beatsPerBar);
            if (at >= seconds) break;
            if (style != Style.Pad)
            {
                bool down = b == 0;
                if (down || b == 2) Kick(audio, rate, at, down ? 1.0 : 0.8);
                if (b == 1 || b == 3) Snare(audio, rate, at, 0.7, rng);
                if (b == beatsPerBar - 1 && beatsPerBar == 4) Kick(audio, rate, at + span * 0.5, 0.55);
                Hat(audio, rate, at, down ? 0.35 : 0.25, rng);
                Hat(audio, rate, at + span * 0.5, 0.18, rng);
                if (style == Style.Mix)
                {
                    int root = roots[((bar % 4) + 4) % 4];
                    Bass(audio, rate, at, span * (b == 0 || b == 2 ? 0.9 : 0.45), root + (b == 3 ? 7 : 0), down ? 0.45 : 0.35);
                    if (b == 1) Bass(audio, rate, at + span * 0.5, span * 0.4, root + 12, 0.3);
                }
            }
            else
            {
                // A pad: a chord that comes in over 60 ms at each bar line and a soft swell on every other beat (weak onsets).
                var chord = chords[((bar % 4) + 4) % 4];
                if (b == 0) Pad(audio, rate, at, span * beatsPerBar, chord, 0.22, 0.06);
                else Pad(audio, rate, at, span, chord, 0.07, 0.04);
            }
        }
        var bt = new List<double>();
        var db = new List<double>();
        for (int i = 0; i < beats.Count; i++)
        {
            if (beats[i] >= seconds) break;
            bt.Add(beats[i]);
            if (index[i] >= 0 && index[i] % beatsPerBar == 0) db.Add(beats[i]);
        }
        line.Beats = bt.ToArray();
        line.Downbeats = db.ToArray();
        Normalise(audio);
        return line;
    }

    static void Normalise(float[] x)
    {
        float peak = 0;
        foreach (var v in x) peak = Math.Max(peak, Math.Abs(v));
        if (peak > 0) for (int i = 0; i < x.Length; i++) x[i] *= 0.8f / peak;
    }

    struct Rng
    {
        uint s;
        public Rng(uint seed) { s = seed == 0 ? 1 : seed; }
        public double Next() { s ^= s << 13; s ^= s >> 17; s ^= s << 5; return s / 4294967296.0; }
        public double Noise() => Next() * 2 - 1;
    }

    static void Kick(float[] x, int rate, double at, double gain)
    {
        int a = (int)(at * rate);
        double phase = 0;
        for (int i = 0; i < (int)(0.28 * rate) && a + i < x.Length; i++)
        {
            if (a + i < 0) continue;
            double t = i / (double)rate;
            double f = 48 + 110 * Math.Exp(-t * 28);
            phase += f / rate;
            x[a + i] += (float)(gain * 0.9 * Math.Sin(2 * Math.PI * phase) * Math.Exp(-t * 11) * Math.Min(1, t * 2000));
        }
    }

    static void Snare(float[] x, int rate, double at, double gain, Rng rng)
    {
        int a = (int)(at * rate);
        for (int i = 0; i < (int)(0.22 * rate) && a + i < x.Length; i++)
        {
            if (a + i < 0) continue;
            double t = i / (double)rate;
            double tone = Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t * 28);
            double noise = rng.Noise() * Math.Exp(-t * 20);
            x[a + i] += (float)(gain * 0.5 * (0.6 * tone + noise));
        }
    }

    static void Hat(float[] x, int rate, double at, double gain, Rng rng)
    {
        int a = (int)(at * rate);
        double prev = 0;
        for (int i = 0; i < (int)(0.05 * rate) && a + i < x.Length; i++)
        {
            if (a + i < 0) continue;
            double t = i / (double)rate;
            double w = rng.Noise();
            double hp = w - prev;                    // a crude high-pass
            prev = w;
            x[a + i] += (float)(gain * 0.5 * hp * Math.Exp(-t * 90));
        }
    }

    static void Bass(float[] x, int rate, double at, double length, int midi, double gain)
    {
        double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
        int a = (int)(at * rate), len = (int)(length * rate);
        double phase = 0, low = 0;
        for (int i = 0; i < len && a + i < x.Length; i++)
        {
            if (a + i < 0) continue;
            double t = i / (double)rate;
            phase += f / rate;
            phase -= Math.Floor(phase);
            double saw = 2 * phase - 1;
            low += 0.08 * (saw - low);               // a one-pole low-pass
            double env = Math.Min(1, t * 400) * Math.Min(1, (length - t) * 60);
            x[a + i] += (float)(gain * env * (0.7 * low * 3 + 0.5 * Math.Sin(2 * Math.PI * phase)));
        }
    }

    static void Pad(float[] x, int rate, double at, double length, int[] chord, double gain, double attack)
    {
        int a = (int)(at * rate), len = (int)(length * rate);
        foreach (int midi in chord)
        {
            double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
            for (int h = 1; h <= 3; h++)
            {
                double w = 2 * Math.PI * f * h / rate;
                for (int i = 0; i < len && a + i < x.Length; i++)
                {
                    if (a + i < 0) continue;
                    double t = i / (double)rate;
                    double env = Math.Min(1, t / attack) * Math.Min(1, (length - t) / 0.06);
                    x[a + i] += (float)(gain / h / chord.Length * env * Math.Sin(w * (a + i)));
                }
            }
        }
    }
}
