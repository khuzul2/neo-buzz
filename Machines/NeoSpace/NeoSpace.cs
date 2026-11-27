#nullable disable
// Neo Space (docs/specs/neo-space.md): the convolution reverb machine. DSP in Engine/, parameters generated from Params.def, the song's
// wave as the room in SpaceWaves.cs, editor in NeoSpaceEditor.cs.

using System;
using System.Collections.Generic;
using System.Linq;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Space
{
    [MachineDecl(Name = "Neo Space", ShortName = "Space", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoSpaceMachine : NeoMachineBase, IBuzzMachine, IDisposable
    {
        readonly float[] left = new float[SpaceEngine.MaxBlock], right = new float[SpaceEngine.MaxBlock];
        readonly Sample[] chunkIn = new Sample[SpaceEngine.MaxBlock], chunkOut = new Sample[SpaceEngine.MaxBlock];
        int rate;
        long silent;

        static NeoSpaceMachine()
        {
            // The ends of the two cuts mean "off": the knob says so.
            var low = Params.Table[P.LowCut];
            low.Describer = raw => raw <= low.Min ? "Off" : NeoBuzz.Dsp.ParamDef.Freq(low.Value(raw));
            var high = Params.Table[P.HighCut];
            high.Describer = raw => raw >= high.Max ? "Off" : NeoBuzz.Dsp.ParamDef.Freq(high.Value(raw));
        }

        public NeoSpaceMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Waves = new SpaceWaves(() => Wavetable);
            Engine = new SpaceEngine(Values, Waves);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal SpaceEngine Engine { get; }

        internal SpaceWaves Waves { get; }

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
        /// Agent server (mcp-server.md §3.5): true while the response the parameters ask for is being made, or the wave is being read
        /// from the song. Renders ask first (on the song's thread, where reading the waves is safe), and asking reads the wave and
        /// starts the response, so a render does not start without its room.
        /// </summary>
        public bool Busy
        {
            get
            {
                try
                {
                    int room = HostValue(P.Room);
                    Waves.Want(room == SpaceEngine.WaveRoom ? HostValue(P.Wave) + 1 : 0);
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
            Waves.Pump(SpaceWaves.FramesPerBlock);
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && HasSignal(input, n);
            if (!hasInput)
            {
                // Run until the tail of the response has been heard, then rest (the convolver's histories are then cleared at the next input).
                silent += n;
                if (silent > Engine.TailSamples)
                {
                    if (silent - n <= Engine.TailSamples) Engine.Skipped();
                    return false;
                }
            }
            else silent = 0;
            bool sound = false;
            for (int pos = 0; pos < n; pos += SpaceEngine.MaxBlock)
            {
                int m = Math.Min(SpaceEngine.MaxBlock, n - pos);
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

        /// <summary>Samples of delay ReBuzz compensates: none, at every setting (the convolver's head runs in the time domain).</summary>
        public int GetLatency() => SpaceEngine.Latency;

        public void Dispose() => Waves.Dispose();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Space...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Space — a convolution reverb with zero latency: nine rooms made by our own code (image-source early reflections and a " +
                        "filtered-noise tail) or any wave of the song as the room, with pre-delay, length, low and high cut, width and reverse; " +
                        "a new room is crossfaded in over 50 ms.\n\nPart of neo-buzz (docs/specs/neo-space.md).", "Neo Space"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoSpaceEditor();
    }
#endif
}
