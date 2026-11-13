#nullable disable
// Neo machines' Buzz glue (docs/specs/machines.md §3), linked as source into each machine.
// Deliberately does not implement IBuzzMachine: ReBuzz takes the first type in the assembly that
// does as the machine, so only the machine class itself may.

using System;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Machine
{
    public abstract class NeoMachineBase
    {
        protected readonly IBuzzMachineHost host;
        bool wasPlaying;

        internal NeoMachineBase(IBuzzMachineHost host, ParamDef[] table)
        {
            this.host = host;
            Values = new ParamValues(table);
        }

        /// <summary>Raw parameter values as ReBuzz sets them (generated properties write here).</summary>
        internal ParamValues Values { get; }

        /// <summary>The machine's parameter table.</summary>
        internal ParamDef[] Table => Values.Table;

        /// <summary>Sample rate from the host (44100 until the host says otherwise).</summary>
        protected int SampleRate
        {
            get
            {
                int sr = host?.MasterInfo?.SamplesPerSec ?? 0;
                return sr > 0 ? sr : 44100;
            }
        }

        /// <summary>Tempo from the host, for tempo-synced rates.</summary>
        protected double BeatsPerMinute
        {
            get
            {
                int bpm = host?.MasterInfo?.BeatsPerMin ?? 0;
                return bpm > 0 ? bpm : 120;
            }
        }

        /// <summary>ReBuzz asks for the text of a value: ours for global parameters, its own otherwise.</summary>
        public string DescribeValue(IParameter parameter, int value)
        {
            try
            {
                if (parameter?.Group == null || parameter.Group.Type != ParameterGroupType.Global) return null;
                int i = parameter.IndexInGroup;
                return i >= 0 && i < Table.Length ? Table[i].Describe(value) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Whether the transport just stopped (polled once per block): ReBuzz pauses patterns
        /// without note-offs, so synths release their notes on this edge.
        /// </summary>
        protected bool TransportStopped()
        {
            bool playing;
            try
            {
                playing = host?.Machine?.Graph?.Buzz?.Playing ?? false;
            }
            catch
            {
                playing = wasPlaying;
            }
            bool stopped = wasPlaying && !playing;
            wasPlaying = playing;
            if (stopped) stoppedAt = Environment.TickCount64;
            return stopped;
        }

        long stoppedAt = long.MinValue / 2;

        /// <summary>The transport as a new machine has it (see NeoSynthBase.ResetForRender).</summary>
        protected void ResetTransport()
        {
            wasPlaying = false;
            stoppedAt = long.MinValue / 2;
        }

        /// <summary>The transport has been stopped for a while (as of the last <see cref="TransportStopped"/> poll).</summary>
        protected bool StoppedForAWhile => !wasPlaying && Environment.TickCount64 - stoppedAt > 400;

        /// <summary>Buzz samples (±32768) to two float channels (±1).</summary>
        protected static void Split(Sample[] input, float[] left, float[] right, int n)
        {
            const float s = 1f / DspMath.BuzzScale;
            for (int i = 0; i < n; i++)
            {
                left[i] = input[i].L * s;
                right[i] = input[i].R * s;
            }
        }

        /// <summary>Two float channels to Buzz samples; whether anything is louder than −120 dB.</summary>
        protected static bool Join(float[] left, float[] right, Sample[] output, int n)
        {
            const float s = DspMath.BuzzScale;
            bool sound = false;
            for (int i = 0; i < n; i++)
            {
                float l = left[i], r = right[i];
                if (!float.IsFinite(l)) l = 0;
                if (!float.IsFinite(r)) r = 0;
                output[i].L = l * s;
                output[i].R = r * s;
                if (!sound && (MathF.Abs(l) > 1e-6f || MathF.Abs(r) > 1e-6f)) sound = true;
            }
            return sound;
        }

        /// <summary>Whether a Buzz buffer holds anything louder than −120 dB.</summary>
        protected static bool HasSignal(Sample[] x, int n)
        {
            for (int i = 0; i < n; i++)
                if (MathF.Abs(x[i].L) > 0.03f || MathF.Abs(x[i].R) > 0.03f) return true;
            return false;
        }
    }
}
