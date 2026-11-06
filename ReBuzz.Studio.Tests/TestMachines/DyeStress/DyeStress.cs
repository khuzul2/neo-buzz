#nullable disable
// Fork (docs/specs/dye-essence.md §5.2): Dye Essence's G0 stress machine. A parameter table shaped as
// Dye Essence's will be (704 parameters, Params.def by make-params.py, code by gen-machine-params.py),
// 16 tracks of Note and Velocity and MIDI through the Neo glue, and a machine state of megabytes: the
// tables as base64 floats (as Neo Wave keeps its user table) and the modulation routes as text. It
// plays a sine per note at Volume, the first slot's octave and Amp level, so a song, a pattern or a
// preset that reaches the machine is heard. Built into the studio tests' Gear (DyeStress.csproj); not
// shipped.

using System;
using Buzz.MachineInterface;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace ReBuzz.Studio.Tests.DyeStress
{
    /// <summary>Saved with the song: tables (32-bit floats, base64) and routes (one per line).</summary>
    public class DyeStressState
    {
        public int Frames { get; set; }
        public string Tables { get; set; } = "";
        public string Routes { get; set; } = "";
    }

    [MachineDecl(Name = "Dye Stress", ShortName = "Stress", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class DyeStressMachine : NeoSynthBase, IBuzzMachine
    {
        const int MaxVoices = 16;
        readonly Sink sink;
        DyeStressState state = new DyeStressState();

        public DyeStressMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            sink = new Sink(this);
        }

        public DyeStressState MachineState
        {
            get => state;
            set
            {
                state = value ?? new DyeStressState();
                StatesSet++;
            }
        }

        // The tests read these (reflection on the machine instance).
        /// <summary>How many times ReBuzz set the state (a song or preset loading).</summary>
        public int StatesSet;
        /// <summary>Blocks rendered.</summary>
        public int Blocks;
        /// <summary>Notes started.</summary>
        public int NotesStarted;
        /// <summary>The raw value of every global parameter, as the machine has it.</summary>
        public int[] Raw()
        {
            var r = new int[P.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Values[i];
            return r;
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            Blocks++;
            BeginBlock(sink);
            double rate = SampleRate;
            double vol = Values.Value(P.Volume);
            float gain = double.IsNegativeInfinity(vol) ? 0f : (float)(DspMath.DbToGain(vol) * 0.25);
            double oct = Values.Value(P.S1Oct);
            bool any = false;
            for (int i = 0; i < n; i++) output[i] = new Sample(0, 0);
            for (int v = 0; v < MaxVoices; v++)
            {
                if (!sink.On[v]) continue;
                any = true;
                double inc = DspMath.NoteToHz(sink.Note[v] + 12 * oct) / rate;
                float g = gain * sink.Velocity[v] * DspMath.BuzzScale;
                double ph = sink.Phase[v];
                for (int i = 0; i < n; i++)
                {
                    float s = g * PortableMathF.Sin((float)(2 * Math.PI * ph));
                    output[i].L += s;
                    output[i].R += s;
                    ph += inc;
                    if (ph >= 1) ph -= 1;
                }
                sink.Phase[v] = ph;
            }
            return any;
        }

        sealed class Sink : INoteSink
        {
            readonly DyeStressMachine m;
            public readonly bool[] On = new bool[MaxVoices];
            public readonly int[] Key = new int[MaxVoices], Note = new int[MaxVoices];
            public readonly float[] Velocity = new float[MaxVoices];
            public readonly double[] Phase = new double[MaxVoices];

            public Sink(DyeStressMachine m) { this.m = m; }

            public void NoteOn(int key, int note, float velocity)
            {
                int v = Array.IndexOf(Key, key);
                if (v < 0 || !On[v]) v = Array.IndexOf(On, false);
                if (v < 0) v = 0;
                On[v] = true;
                Key[v] = key;
                Note[v] = note;
                Velocity[v] = velocity;
                Phase[v] = 0;
                m.NotesStarted++;
            }

            public void NoteOff(int key)
            {
                for (int v = 0; v < MaxVoices; v++) if (On[v] && Key[v] == key) On[v] = false;
            }

            public void ModWheel(float value) { }
            public void PitchBend(float value) { }
            public void Sustain(bool down) { }
            public void Aftertouch(float value) { }
            public void AllNotesOff(bool hard) { Array.Clear(On); }
        }
    }
}
