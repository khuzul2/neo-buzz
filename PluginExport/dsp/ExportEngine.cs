// What the plug-in runtime needs of a Neo engine, in one shape. The adapter for each machine is generated from its descriptor
// (tools/gen-plugin-export.py): it only forwards to the engine's own members, the parameter table is the machine's own.
using System;
using NeoBuzz.Dsp;

namespace NeoExport
{
    internal abstract class ExportEngine
    {
        public abstract ParamDef[] Table { get; }
        public abstract int MaxBlock { get; }
        public abstract void Create(ParamValues values);
        public abstract void SetSampleRate(float sr);
        public abstract int Latency { get; }
        public virtual bool Idle => false;
        public abstract void Reset();
        /// <summary>Tempo (beats per minute), whether the song plays, and its position in beats (quarter notes), before each block.</summary>
        public virtual void SetTransport(double tempo, bool playing, double beats, double rate) { }
        /// <summary>One block in place (<paramref name="kl"/>, <paramref name="kr"/>: the sidechain, or null). Instruments: false when silent.</summary>
        public abstract bool Process(float[] l, float[] r, float[] kl, float[] kr, int n);

        /// <summary>The version of the machine's saved state (its parameters: append-only, so older states load, newer ones load what is known).</summary>
        public virtual int StateVersion => 1;
        /// <summary>A state saved by an older version, before it is applied (values in table order).</summary>
        public virtual void MigrateState(int fromVersion, int[] values) { }

        public virtual void NoteOn(int key, int note, float velocity) { }
        public virtual void NoteOff(int key) { }
        public virtual void ModWheel(float v) { }
        public virtual void PitchBend(float v) { }
        public virtual void Sustain(bool down) { }
        public virtual void Aftertouch(float v) { }
        public virtual void AllNotesOff(bool hard) { }
    }

    internal sealed class PluginInfo
    {
        public readonly string Id, Name, Description;
        public readonly string[] Features;
        public readonly bool Instrument, Sidechain, GateIdle, GateQuiet;
        public readonly int TailSeconds;
        public readonly Func<ExportEngine> Make;

        public PluginInfo(string id, string name, string description, string[] features, bool instrument, bool sidechain, bool gateIdle, int tailSeconds, Func<ExportEngine> make, bool gateQuiet = false)
        {
            Id = id; Name = name; Description = description; Features = features; Instrument = instrument; Sidechain = sidechain; GateIdle = gateIdle; GateQuiet = gateQuiet; TailSeconds = tailSeconds; Make = make;
        }
    }
}
