// Fork (docs/specs/pitch.md section 9): the test signals of Convert to MIDI, synthesised: melodies, chords and drum loops with exact note lists.

namespace ReBuzz.SessionTests.Convert;

internal static class ConvertSignals
{
    public const int Rate = 44100;

    public static double Hz(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);

    /// <summary>A sawtooth line: (start, length, MIDI pitch) in seconds, with a 10 ms attack and release, optional vibrato (semitones, 5.5 Hz) and per-note decay.</summary>
    public static float[] Line(IEnumerable<(double At, double Length, double Midi)> notes, double seconds, double vibrato = 0, double decay = 0, bool saw = true)
    {
        var x = new float[(int)(Rate * seconds)];
        foreach (var n in notes)
        {
            int a = (int)(n.At * Rate), b = (int)((n.At + n.Length) * Rate);
            double phase = 0;
            for (int i = a; i < b && i < x.Length; i++)
            {
                double t = (i - a) / (double)Rate;
                double m = n.Midi + (vibrato > 0 ? vibrato * Math.Sin(2 * Math.PI * 5.5 * t) : 0);
                phase += Hz(m) / Rate;
                phase -= Math.Floor(phase);
                double s = saw ? (2 * phase - 1) * 0.3 : 0.3 * Math.Sin(2 * Math.PI * phase);
                double env = Math.Min(1, t / 0.01) * Math.Min(1, (n.At + n.Length - i / (double)Rate) / 0.01);
                if (decay > 0) env *= Math.Exp(-t * decay);
                x[i] += (float)(s * env);
            }
        }
        return x;
    }

    /// <summary>A glide from one pitch to another over the middle of a note.</summary>
    public static float[] Glide(double at, double length, double from, double to, double seconds)
    {
        var x = new float[(int)(Rate * seconds)];
        double phase = 0;
        for (int i = (int)(at * Rate); i < (int)((at + length) * Rate) && i < x.Length; i++)
        {
            double t = (i / (double)Rate - at) / length;
            double m = from + (to - from) * Math.Clamp((t - 0.2) / 0.6, 0, 1);
            phase += Hz(m) / Rate;
            phase -= Math.Floor(phase);
            x[i] = (float)((2 * phase - 1) * 0.3 * Math.Min(1, t * 100) * Math.Min(1, (1 - t) * 100));
        }
        return x;
    }

    /// <summary>Chords of harmonic tones with a decay: (start, length, pitches).</summary>
    public static float[] Chords(IEnumerable<(double At, double Length, int[] Midi)> chords, double seconds, int rate = 22050)
    {
        var x = new float[(int)(rate * seconds)];
        foreach (var (at, length, pitches) in chords)
        {
            int a = (int)(at * rate), b = (int)((at + length) * rate);
            foreach (int p in pitches)
            {
                double f = Hz(p);
                for (int i = a; i < b && i < x.Length; i++)
                {
                    double t = (i - a) / (double)rate;
                    double env = Math.Min(1, t / 0.005) * Math.Exp(-t * 0.8) * Math.Min(1, (at + length - i / (double)rate) / 0.03);
                    double s = 0;
                    for (int h = 1; h <= 6 && f * h < rate / 2; h++) s += Math.Sin(2 * Math.PI * f * h * t) / (h * 1.3);
                    x[i] += (float)(0.12 * s * env);
                }
            }
        }
        return x;
    }

    /// <summary>One drum hit of a synthetic kit added into <paramref name="x"/> at <paramref name="at"/> seconds.</summary>
    public static void Hit(float[] x, string drum, double at, double level = 0.7, int rate = Rate)
    {
        var rnd = new Random((int)(at * 1000) + drum.Length);
        int a = (int)(at * rate);
        double len = drum switch { "kick" => 0.35, "snare" => 0.25, "hat" => 0.05, "open" => 0.8, _ => 0.2 };
        double phase = 0, hp = 0, last = 0, lp = 0;
        for (int i = 0; i < (int)(len * rate) && a + i < x.Length; i++)
        {
            double t = i / (double)rate;
            double v = 0;
            double noise = rnd.NextDouble() * 2 - 1;
            switch (drum)
            {
                case "kick":
                    phase += (45 + 90 * Math.Exp(-t * 28)) / rate;
                    v = Math.Sin(2 * Math.PI * phase) * Math.Exp(-t * 9) * 1.0;
                    break;
                case "snare":
                    lp += 0.3 * (noise - lp);                              // the snares' noise: a low-passed hiss, not the hat's
                    v = Math.Sin(2 * Math.PI * 190 * t) * Math.Exp(-t * 28) * 0.55 + Math.Sin(2 * Math.PI * 330 * t) * Math.Exp(-t * 30) * 0.3 + lp * Math.Exp(-t * 16) * 1.6;
                    break;
                case "hat":
                case "open":
                    hp = noise - last; last = noise;                     // a first difference: bright noise
                    v = hp * 0.5 * Math.Exp(-t * (drum == "hat" ? 90 : 2.0));
                    break;
            }
            x[a + i] += (float)(v * level);
        }
    }

    public static float[] Drums(IEnumerable<(string Drum, double At)> hits, double seconds, int rate = Rate)
    {
        var x = new float[(int)(rate * seconds)];
        foreach (var (d, at) in hits) Hit(x, d, at, 0.7, rate);
        return x;
    }
}
