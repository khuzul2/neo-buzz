#nullable disable
// Neo Amp (docs/specs/neo-amp.md): guitar and bass amps, cabinets and a tuner. DSP in Engine/, parameters generated from Params.def, the song's
// wave as the cabinet in AmpWaves.cs, editor in NeoAmpEditor.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Amp
{
    [MachineDecl(Name = "Neo Amp", ShortName = "Amp", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoAmpMachine : NeoMachineBase, IBuzzMachine, IDisposable
    {
        readonly float[] left = new float[AmpEngine.MaxBlock], right = new float[AmpEngine.MaxBlock];
        readonly Sample[] chunkIn = new Sample[AmpEngine.MaxBlock], chunkOut = new Sample[AmpEngine.MaxBlock];
        int rate;
        long silent;

        static NeoAmpMachine()
        {
            // The ends of the gate and the two cuts mean "off": the knob says so.
            var gate = Params.Table[P.GateThresh];
            gate.Describer = raw => raw <= gate.Min ? "Off" : gate.Value(raw).ToString("0") + " dB";
            var low = Params.Table[P.LowCut];
            low.Describer = raw => raw <= low.Min ? "Off" : NeoBuzz.Dsp.ParamDef.Freq(low.Value(raw));
            var high = Params.Table[P.HighCut];
            high.Describer = raw => raw >= high.Max ? "Off" : NeoBuzz.Dsp.ParamDef.Freq(high.Value(raw));
        }

        public NeoAmpMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Waves = new AmpWaves(() => Wavetable);
            Engine = new AmpEngine(Values, Waves);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal AmpEngine Engine { get; }

        internal AmpWaves Waves { get; }

        /// <summary>The song's wavetable (null before the machine is in a song).</summary>
        internal IWavetable Wavetable
        {
            get
            {
                try { return host?.Machine?.Graph?.Buzz?.Song?.Wavetable; }
                catch (Exception) { return null; }
            }
        }

        /// <summary>
        /// Agent server (mcp-server.md §3.5): true while the cabinet the parameters ask for is being made, or the wave is being read from the
        /// song. Renders ask first (on the song's thread, where reading the waves is safe), and asking reads the wave and starts the cabinet,
        /// so a render does not start without it.
        /// </summary>
        public bool Busy
        {
            get
            {
                try
                {
                    int cab = HostValue(P.Cabinet);
                    Waves.Want(cab == AmpCabs.WaveCab ? HostValue(P.CabWave) + 1 : 0);
                    Waves.Synchronise();
                    Engine.Poll(HostValue);
                    return Engine.Pending || Waves.Pending;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        IParameter[] hostParameters;

        /// <summary>A parameter as the host holds it now (a value just set reaches the engine only at the next tick); the engine's own value without a host.</summary>
        int HostValue(int index)
        {
            try
            {
                var ps = hostParameters ??= HostParameters();
                if (ps != null) return ps[index].GetValue(0);
            }
            catch (Exception) { }
            return Values[index];
        }

        IParameter[] HostParameters()
        {
            var globals = host?.Machine?.ParameterGroups?.FirstOrDefault(g => g.Type == ParameterGroupType.Global);
            if (globals == null || globals.Parameters.Count < Params.Table.Length) return null;
            return globals.Parameters.Take(Params.Table.Length).ToArray();
        }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            // Under the audio lock: the song's waves can't change while the wave is copied in.
            Waves.Pump(AmpWaves.FramesPerBlock);
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && HasSignal(input, n);
            bool tuning = Values.On(P.Tuner);
            if (!hasInput)
            {
                // Run until the tail has been heard, then rest (the states are cleared at the next input).
                silent += n;
                if (silent > Engine.TailSamples && !tuning)
                {
                    if (silent - n <= Engine.TailSamples) Engine.Skipped();
                    return false;
                }
            }
            else silent = 0;
            bool sound = false;
            for (int pos = 0; pos < n; pos += AmpEngine.MaxBlock)
            {
                int m = Math.Min(AmpEngine.MaxBlock, n - pos);
                if (hasInput)
                {
                    Array.Copy(input, pos, chunkIn, 0, m);
                    Split(chunkIn, left, right, m);
                }
                else
                {
                    Array.Clear(left, 0, m);
                    Array.Clear(right, 0, m);
                }
                Engine.Process(left, right, m);
                sound |= Join(left, right, chunkOut, m);
                Array.Copy(chunkOut, 0, output, pos, m);
            }
            return sound || hasInput;
        }

        /// <summary>Samples of delay ReBuzz compensates: the oversampler's, the same at every quality (the cabinet adds none).</summary>
        public int GetLatency() => AmpEngine.Latency;

        public void Dispose() => Waves.Dispose();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Amp...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Amp — a guitar and bass amp for a DI signal: a noise gate, eight voicings built from one to four gain stages, a passive " +
                        "bass-mid-treble tone stack worked out from its resistors and capacitors, a power stage with sag, presence and resonance, oversampling " +
                        "with anti-aliasing, six cabinets made by our own code or any wave of the song as the cabinet, a mic position and cuts, and a tuner." +
                        "\n\nPart of neo-buzz (docs/specs/neo-amp.md).", "Neo Amp"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoAmpEditor();
    }
#endif
}
