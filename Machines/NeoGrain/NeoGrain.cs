#nullable disable
// Neo Grain (docs/specs/neo-grain.md): a granular synth. A generator that plays by notes (16 tracks of Note and Velocity, MIDI, note wires): each note
// is a cloud of grains cut from a built-in source or a wave of the song. DSP in Engine/, the song's wave in through GrainWaveSource.cs, parameters
// generated from Params.def, editor in NeoGrainEditor.cs. Live input and the audio of an audio clip as a source come later (the host cannot feed audio
// into a generator yet, and has no way for a machine to read a clip's audio).

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Grain
{
    [MachineDecl(Name = "Neo Grain", ShortName = "Grain", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoGrainMachine : NeoSynthBase, IBuzzMachine, IDisposable
    {
        readonly float[] left = new float[GrainEngine.MaxBlock], right = new float[GrainEngine.MaxBlock];
        int rate;

        public NeoGrainMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new GrainEngine(Values);
            Waves = new GrainWaveSource(() => Wavetable, WantedSlot, w => Engine.SetWave(w));
        }

        internal GrainEngine Engine { get; private set; }
        internal GrainWaveSource Waves { get; }

        /// <summary>The wave slot the cloud reads (1 to 200) when Source is Song wave, else 0.</summary>
        int WantedSlot() => Raw(P.Source) == 3 ? Raw(P.Wave) + 1 : 0;

        int Raw(int index) => Values[index];

        internal override void Restart()
        {
            var wave = Engine?.CurrentSource;
            Engine = new GrainEngine(Values);
            if (Raw(P.Source) == 3 && wave != null) Engine.SetWave(wave);
            rate = 0;
        }

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
        /// Agent server (mcp-server.md §3.5): true while the song's wave is being copied in. Renders ask first (on the song's thread, where reading the wave
        /// is safe), and asking copies it at once, so a render starts with the wave.
        /// </summary>
        public bool Busy => Waves.Synchronise();

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            // Under the audio lock: the song's wave cannot change while it is copied in.
            Waves.Pump(GrainWaveSource.FramesPerBlock);
            Engine.Playing = IsPlaying();
            BeginBlock(Engine);
            n = Math.Min(n, GrainEngine.MaxBlock);
            Array.Clear(left, 0, n);
            Array.Clear(right, 0, n);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        /// <summary>The transport runs (the Drone plays only then); read every block, as the stop edge is.</summary>
        bool IsPlaying()
        {
            try { return host?.Machine?.Graph?.Buzz?.Playing ?? false; }
            catch (Exception) { return false; }
        }

        public void Dispose() => Waves.Dispose();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Grain...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Grain — a granular synth: each note is a cloud of up to 128 grains in all, 5 to 500 ms long, started 1 to 200 a second from a place in a " +
                        "built-in chord, bells or noise, or a wave of your song, with a random spread of position, pitch and pan, reversed grains, three windows " +
                        "and phase alignment that keeps a tonal source in tune; an ADSR, a Drone switch, 16 voices.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-grain.md).", "Neo Grain"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoGrainEditor();
    }
#endif
}
