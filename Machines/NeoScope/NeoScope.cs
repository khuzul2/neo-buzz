#nullable disable
// Neo Scope (docs/specs/machines.md §5): the oscilloscope and vectorscope machine. Its output is its
// input, bit for bit, with no latency; the engine (Engine/) looks at what passes, with the song's
// position from the host so synced sweeps start on its beats and bars, and publishes snapshots for
// the editor (NeoScopeEditor.cs) and readings for the agent server (Readings, docs/specs/mcp-server.md
// §3.5). Parameters are generated from Params.def.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Scope
{
    [MachineDecl(Name = "Neo Scope", ShortName = "Scope", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoScopeMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[ScopeEngine.MaxBlock], right = new float[ScopeEngine.MaxBlock];
        int rate;

        public NeoScopeMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new ScopeEngine(Values);
        }

        internal ScopeEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            var transport = Transport();
            bool read = (mode & WorkModes.WM_READ) != 0 && input != null;
            bool sound = false;
            const float scale = 1f / DspMath.BuzzScale;
            for (int at = 0; at < n; at += ScopeEngine.MaxBlock)
            {
                int m = Math.Min(ScopeEngine.MaxBlock, n - at);
                if (read)
                {
                    for (int i = 0; i < m; i++)
                    {
                        var x = input[at + i];
                        if (output != null) output[at + i] = x;   // the input, bit for bit
                        left[i] = x.L * scale;
                        right[i] = x.R * scale;
                        if (!sound && (MathF.Abs(x.L) > 0.03f || MathF.Abs(x.R) > 0.03f)) sound = true;
                    }
                }
                else
                {
                    if (output != null) Array.Clear(output, at, m);
                    Array.Clear(left, 0, m);
                    Array.Clear(right, 0, m);
                }
                Engine.Process(left, right, m, transport);
                transport.Advance(m);
            }
            return sound;
        }

        /// <summary>Where the song is now (valid in Work): its tick, the samples into it, and the tempo.</summary>
        ScopeTransport Transport()
        {
            var t = new ScopeTransport { Bpm = BeatsPerMinute };
            try
            {
                var info = host?.MasterInfo;
                var buzz = host?.Machine?.Graph?.Buzz;
                if (info != null && buzz != null)
                {
                    t.Playing = buzz.Playing;
                    t.Tick = buzz.Song?.PlayPosition ?? 0;
                    t.PosInTick = info.PosInTick;
                    t.SamplesPerTick = info.SamplesPerTick;
                    t.TicksPerBeat = info.TicksPerBeat;
                }
            }
            catch
            {
                t.Playing = false;
            }
            return t;
        }

        /// <summary>
        /// What the scope measures, for agents (docs/specs/mcp-server.md §3.5): the keys of the
        /// manifest's readings (Params.def): peak in dBFS, RMS in dB, correlation, and 1 or 0 for the
        /// trigger's lock; −∞ when silent. Safe from any thread: a copy of the newest snapshot.
        /// </summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Scope...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Scope — an oscilloscope (left, right, mid, side, or both channels; synced to the song's beats " +
                        "and bars, or free with an edge trigger) and a vectorscope (goniometer or polar) with a correlation " +
                        "meter. Audio passes through untouched.\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Scope"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // fork: the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoScopeEditor();
    }
#endif
}
