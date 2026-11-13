#nullable disable
// Neo Patch NP1: the machine's engine, the glue between the parameters (Volume, Voices, Glide, the audio-rate switch, the 16 macros), the
// patch (a PatchGraph, kept as text in the machine state) and the runtime. The machine and the tests drive it the same way: SetPatch, notes,
// Process. The patch text is loaded and compiled off the audio thread (SetPatch); the audio thread takes the new patch at a block boundary.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Patch
{
    internal sealed class PatchEngine : IChannelNoteSink
    {
        public const int MaxBlock = 256;
        readonly ParamValues values;
        readonly PatchRuntime rt;
        PatchGraph graph = new PatchGraph();
        string error;
        float meanSquare, peakReading;
        int lastNotes;

        public PatchEngine(ParamValues values, float sampleRate = 48000f, LaneKind lanes = LaneKind.Auto)
        {
            this.values = values;
            rt = new PatchRuntime(sampleRate, lanes, 16);
            rt.SetGraph(graph);
            ApplyParameters();
        }

        public PatchRuntime Runtime => rt;
        public PatchGraph Graph => graph;
        /// <summary>The last compile's error (null: it compiled).</summary>
        public string Error => error;

        /// <summary>Loads a patch from its text (empty: an empty patch, which is silent); an unreadable text keeps the patch. Returns the compile error, or null.</summary>
        public string SetPatch(string text)
        {
            var g = PatchText.Load(text);
            if (g == null) return error = "not a Neo Patch patch";
            return SetGraph(g);
        }

        public string SetGraph(PatchGraph g)
        {
            graph = g;
            return error = rt.SetGraph(g);
        }

        public string Text() => PatchText.Save(graph);

        public void SetSampleRate(float sr)
        {
            rt.SetSampleRate(sr);
        }

        public bool Process(float[] left, float[] right, int n)
        {
            ApplyParameters();
            rt.Process(left, right, n);
            float peak = 0f;
            double ms = 0;
            for (int i = 0; i < n; i++)
            {
                float a = Math.Abs(left[i]), b = Math.Abs(right[i]);
                float m = a > b ? a : b;
                if (m > peak) peak = m;
                ms += 0.5 * ((double)left[i] * left[i] + (double)right[i] * right[i]);
            }
            float k = (float)Math.Min(1.0, n / (0.5 * rt.SampleRateNow));
            meanSquare += ((float)(ms / Math.Max(1, n)) - meanSquare) * k;
            peakReading = Math.Max(peak, peakReading * (float)PortableMath.Pow(0.1, n / (double)rt.SampleRateNow));
            lastNotes = rt.ActiveVoices;
            return true;
        }

        void ApplyParameters()
        {
            double vol = values.Value(P.Volume);
            rt.Volume = double.IsNegativeInfinity(vol) ? 0f : (float)DspMath.DbToGain(vol);
            rt.Voices = (int)values.Value(P.Voices);
            rt.Glide = (float)values.Value(P.Glide);
            rt.AudioRateModulation = values.On(P.AudioMod);
            for (int m = 0; m < 16; m++) rt.SetMacro(m, values.F(P.Macro1 + m));
        }

        /// <summary>Whether a table or sample the patch plays is still being built in the background (offline renders wait for it).</summary>
        public bool Busy
        {
            get
            {
                foreach (var n in graph.Nodes)
                {
                    if (n.Kind == NodeKind.WavetableOsc && TableBank.Get((int)(n.Controls[NodeInfo.WtTable] + 0.5f)) == null) return true;
                    if (n.Kind == NodeKind.Sample)
                    {
                        int s = (int)(n.Controls[NodeInfo.SampleSource] + 0.5f);
                        if (s >= 0 && s < SampleSources.Count && SampleSources.Get(s) == null) return true;
                    }
                }
                return false;
            }
        }

        public System.Collections.Generic.IReadOnlyDictionary<string, double> Readings()
        {
            double rms = meanSquare > 0 ? 10 * PortableMath.Log10(meanSquare) : double.NegativeInfinity;
            double peak = peakReading > 1e-9f ? 20 * PortableMath.Log10(peakReading) : double.NegativeInfinity;
            return new System.Collections.Generic.Dictionary<string, double>
            {
                ["rms_db"] = rms, ["peak_db"] = peak, ["active_voices"] = lastNotes, ["nodes"] = rt.Current?.Nodes ?? 0,
            };
        }

        // The note sink, forwarded.
        public void NoteOn(int key, int note, float velocity) => rt.NoteOn(key, note, velocity);
        public void NoteOff(int key) => rt.NoteOff(key);
        public void ModWheel(float value) => rt.ModWheel(value);
        public void PitchBend(float value) => rt.PitchBend(value);
        public void Sustain(bool down) => rt.Sustain(down);
        public void Aftertouch(float value) => rt.Aftertouch(value);
        public void AllNotesOff(bool hard) => rt.AllNotesOff(hard);
        public void NoteOn(int key, int note, float velocity, int channel) => rt.NoteOn(key, note, velocity, channel);
        public void NoteOff(int key, int note, int channel) => rt.NoteOff(key, note, channel);
        public bool Control(int channel, int ctrl, int value) => rt.Control(channel, ctrl, value);
    }
}
