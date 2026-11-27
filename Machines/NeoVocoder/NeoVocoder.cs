#nullable disable
// Neo Vocoder (docs/specs/neo-vocoder.md): a channel vocoder. Two inputs: 1 is the modulator (a voice, drums, anything), 2 the sidechain carrier
// (connect a machine to the second input); the machine's own synth is the carrier when Carrier is Synth, and it takes notes (a note wire, live
// MIDI, its own note tracks) like an instrument. DSP in Engine/ and NeoBuzz.Dsp/VocoderBank.cs, parameters generated from Params.def, editor in
// NeoVocoderEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Vocoder
{
    [MachineDecl(Name = "Neo Vocoder", ShortName = "Vocoder", Author = "neo-buzz", MaxTracks = 8, InputCount = 2, OutputCount = 1)]
    public partial class NeoVocoderMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[VocoderEngine.MaxBlock], right = new float[VocoderEngine.MaxBlock];
        readonly float[] keyLeft = new float[VocoderEngine.MaxBlock], keyRight = new float[VocoderEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoVocoderMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new VocoderEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal VocoderEngine Engine { get; }

        /// <summary>An offline render starts from a machine as made (NeoSynthBase.ResetForRender): the filters, followers, voices and noise cleared.</summary>
        internal override void Restart()
        {
            Engine.Reset();
            rate = 0;
        }

        /// <summary>The sidechain input has a machine wired to it (the editor says "Wire a carrier into the sidechain" when Carrier is Sidechain and it has not).</summary>
        public bool SidechainWired => Engine.SidechainWired;

        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            if (n > VocoderEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            var main = input != null && input.Count > 0 ? input[0] : null;
            var key = input != null && input.Count > 1 ? input[1] : null;
            var outBuf = output != null && output.Count > 0 ? output[0] : null;

            BeginBlock(Engine);
            bool hasInput = main != null && HasSignal(main, n);
            bool hasKey = key != null && HasSignal(key, n);
            Engine.SidechainWired = key != null;
            if (!hasInput && !hasKey && Engine.Silent)
            {
                // The bands and the voices have died away: rest after a few quiet blocks.
                if (quietBlocks > 8) return false;
                quietBlocks++;
            }
            else quietBlocks = 0;

            if (main != null) Split(main, left, right, n);
            else
            {
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            if (key != null) Split(key, keyLeft, keyRight, n);
            Engine.Process(left, right, key != null ? keyLeft : null, key != null ? keyRight : null, n);
            if (outBuf == null) return false;
            return Join(left, right, outBuf, n) || hasInput;
        }

        /// <summary>No delay: the filters are causal and nothing looks ahead.</summary>
        public int GetLatency() => 0;

        public string GetChannelName(bool input, int index) => input ? (index == 0 ? "Modulator" : "Carrier") : "Output";

        /// <summary>For agents (machine_readings): bands, voices sounding and the loudest band's level.</summary>
        public IReadOnlyDictionary<string, double> Readings => new Dictionary<string, double>
        {
            ["bands"] = Engine.Bank.BuiltBands,
            ["voices"] = Engine.ActiveVoices,
            ["loudest_band_db"] = DbOf(Engine.Bank.MaxLevel),
        };

        static double DbOf(float x) => x > 1e-9f ? 20.0 * Math.Log10(x) : -180.0;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Vocoder...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Vocoder — a channel vocoder with 8 to 40 bands. The main input is the modulator; the carrier is the built-in synth " +
                        "(8 voices, played by notes and chords), noise, or any machine wired into the sidechain (input 2). Formant shift, clear " +
                        "consonants, freeze and stereo spread, Mix and Amount; no latency.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-vocoder.md).", "Neo Vocoder"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoVocoderEditor();
    }
#endif
}
