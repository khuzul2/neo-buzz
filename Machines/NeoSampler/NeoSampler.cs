#nullable disable
// Neo Sampler (docs/specs/machines.md §5): plays the song's waves (wavetable slots) and built-in
// samples in 16 zones; Kit mode makes it a drum machine. 16 tracks of Note and Velocity, plus MIDI.
// The successor of Jeskola Tracker, Matilde Tracker2 and Pedal M1 (docs/specs/mcp-server.md §12.5).
// DSP in Engine/, the wavetable's waves in through WaveSource.cs, parameters generated from
// Params.def, editor in NeoSamplerEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Sampler
{
    [MachineDecl(Name = "Neo Sampler", ShortName = "Sampler", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoSamplerMachine : NeoSynthBase, IBuzzMachine, IDisposable
    {
        readonly float[] left = new float[SamplerEngine.MaxBlock], right = new float[SamplerEngine.MaxBlock];
        int rate;

        public NeoSamplerMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            SamplerText.Install(Params.Table);
            Bank = new SampleBank();
            Engine = new SamplerEngine(Values, Bank);
            Source = new WaveSource(() => Wavetable, Bank, ZoneWave);
            SamplerText.SongWaveName = Source.NameOf;
            // The built-in samples are made once per process, in the background, not when a note first asks.
            BuiltIns.StartBuilding();
        }

        internal SamplerEngine Engine { get; }
        internal SampleBank Bank { get; }
        internal WaveSource Source { get; }

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
        /// Agent server (mcp-server.md §3.5): true while the built-in samples are being made or the
        /// song's waves copied in. Renders ask first (on the song's thread, where reading the waves is
        /// safe), and asking copies them all at once, so a render starts with every wave.
        /// </summary>
        public bool Busy => !BuiltIns.Ready || Source.Synchronise();

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            // Under the audio lock: the song's waves can't change while they are copied in.
            Source.Pump(WaveSource.FramesPerBlock);
            BeginBlock(Engine);
            n = Math.Min(n, SamplerEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public void Dispose() => Source.Dispose();

        IParameter[] zoneWaves;

        /// <summary>
        /// Zone z's wave as the host holds it: a value set (param_set, machine_replace, automation) reaches the engine at the next
        /// tick, the host at once, so a render waiting on Busy reads in a wave a zone has just been given. Off the audio thread
        /// (WaveSource's scans); the engine's value where there's no host.
        /// </summary>
        int ZoneWave(int z)
        {
            try
            {
                var ps = zoneWaves ??= HostZoneWaves();
                if (ps != null) return ps[z].GetValue(0);
            }
            catch (Exception) { }
            return Values[SamplerEngine.ZoneParam(z, SamplerEngine.FWave)];
        }

        IParameter[] HostZoneWaves()
        {
            var groups = host?.Machine?.ParameterGroups;
            if (groups == null || groups.Count < 2) return null;
            var globals = groups[1].Parameters;
            var found = new IParameter[SamplerEngine.ZoneCount];
            for (int z = 0; z < found.Length; z++)
            {
                int i = SamplerEngine.ZoneParam(z, SamplerEngine.FWave);
                if (i >= globals.Count || globals[i].Name != $"Z{z + 1} wave") return null;
                found[z] = globals[i];
            }
            return found;
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Sampler...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Sampler — plays the song's waves (its wavetable, saved with the song) and built-in " +
                        "samples in 16 zones by key and velocity, each with its root, tune, level, pan, start and end, " +
                        "a loop with crossfade, gate or one-shot, reverse and a choke group; a slot's layers split the " +
                        "keys by root or alternate round robin. Kit mode puts zone n on key Kit base + n − 1 as a " +
                        "one-shot drum. Amp and filter envelopes, velocity to level and filter, pitch bend, 32 voices. " +
                        "16 tracks (Note, Velocity) and MIDI.\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Sampler"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoSamplerEditor();
    }
#endif
}
