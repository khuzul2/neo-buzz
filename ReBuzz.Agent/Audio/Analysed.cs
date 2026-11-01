#nullable disable
// Agent server (docs/specs/mcp-server.md §5.7): one piece of audio and its analyses, each run
// once when first asked for, so a report that reads several metrics (or a comparison and a plot)
// shares the Welch spectrum, the loudness and the onsets.

using NeoBuzz.Analysis;

namespace ReBuzz.Agent.Audio
{
    internal sealed class Analysed
    {
        public Analysed(StereoAudio audio)
        {
            Audio = audio;
            Stereo = audio.Right != null && !ReferenceEquals(audio.Right, audio.Left);
        }

        public StereoAudio Audio { get; }
        public bool Stereo { get; }
        public int Rate => Audio.Rate;
        float[] Right => Stereo ? Audio.Right : null;

        float[] mono;
        /// <summary>(L + R) / 2.</summary>
        public float[] Mono => mono ??= Dynamics.Mono(Audio.Left, Right);

        LoudnessResult loudness;
        public LoudnessResult LoudnessResult => loudness ??= Loudness.Measure(Audio.Left, Right, Rate);

        Psd psd;
        public Psd Psd => psd ??= Welch.Measure(Audio.Left, Right, Rate);

        SpectrumResult spectrum;
        public SpectrumResult SpectrumResult => spectrum ??= Spectrum.FromPsd(Psd);

        TempoResult tempo;
        public TempoResult TempoResult => tempo ??= Tempo.Measure(Mono, Rate);

        DynamicsResult dynamics;
        public DynamicsResult DynamicsResult => dynamics ??= Dynamics.Measure(Audio.Left, Right, Rate, LoudnessResult, TempoResult.Onsets);

        DynamicsResult levels;
        /// <summary>Crest, RMS and the envelope without transient detection (for per-track summaries).</summary>
        public DynamicsResult Levels => dynamics ?? (levels ??= Dynamics.Measure(Audio.Left, Right, Rate, LoudnessResult, new System.Collections.Generic.List<double>()));

        StereoResult stereo;
        public StereoResult StereoResult => stereo ??= NeoBuzz.Analysis.Stereo.Measure(Audio.Left, Right, Rate, Psd);

        PitchTrack pitch;
        public PitchTrack PitchTrack => pitch ??= Yin.Track(Mono, Rate, onsets: TempoResult.Onsets);

        KeyResult key;
        public KeyResult KeyResult => key ??= KeyFinder.Find(KeyFinder.Chroma(Psd));

        public bool Silent => double.IsNegativeInfinity(LoudnessResult.SamplePeakDb) || LoudnessResult.SamplePeakDb < -90;
    }
}
