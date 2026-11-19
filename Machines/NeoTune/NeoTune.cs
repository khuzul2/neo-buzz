#nullable disable
// Neo Tune (docs/specs/neo-tune.md): pitch correction for a single voice or a melodic line. DSP in Engine/ (the shared PT1 tracker,
// TD-PSOLA resynthesis, the control stage), parameters generated from Params.def, editor in NeoTuneEditor.cs. The latency is the
// range's lookahead (10, 28 or 36 ms), reported to the app; machine_readings gets the input pitch, the target note, the
// correction, the voiced flag and the latency.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Machine;

namespace NeoBuzz.Tune
{
    [MachineDecl(Name = "Neo Tune", ShortName = "Tune", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoTuneMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[TuneEngine.MaxBlock], right = new float[TuneEngine.MaxBlock];
        int rate;
        int quietBlocks;

        public NeoTuneMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new TuneEngine(Values);
            rate = SampleRate;
            Engine.SetSampleRate(rate);
        }

        internal TuneEngine Engine { get; }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (!hasInput)
            {
                // The delayed audio rings out for the latency after the input stops, then the machine rests.
                if (quietBlocks > 8 + Engine.Latency / Math.Max(1, n)) return false;
                quietBlocks++;
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else
            {
                quietBlocks = 0;
                Split(input, left, right, n);
            }
            if (n > TuneEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => Engine.Latency;

        /// <summary>MIDI notes for the MIDI target (the held notes are the notes the voice may land on).</summary>
        public void MidiNote(int channel, int value, int velocity) => Engine.MidiNote(value, velocity);

        public void MidiControlChange(int ctrl, int channel, int value)
        {
            if (ctrl == 123 || ctrl == 120) Engine.AllNotesOff();   // all notes off, all sound off
        }

        public void Stop() => Engine.AllNotesOff();

        /// <summary>For agents (machine_readings): input_hz, target_note, correction_cents, voiced, latency_samples.</summary>
        public IReadOnlyDictionary<string, double> Readings => Engine.Readings();

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Tune...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Tune — pitch correction for one voice or a melodic line. It follows the pitch, moves it to the nearest note of a " +
                        "key and scale (or to the notes a keyboard holds) at the retune speed you set, and re-synthesises the sound with " +
                        "period splicing or pitch-synchronous overlap-add so the voice keeps its colour. The Natural knob (0 % hard effect, 100 % transparent) sets the six fine controls. Formant preserve and shift, Transpose, Keep and Add " +
                        "vibrato, Humanize, Amount and Mix. Latency 10 / 28 / 36 ms by Range, reported to the app.\n\n" +
                        "Part of neo-buzz (docs/specs/neo-tune.md).", "Neo Tune"),
                },
            },
        };
    }

#if WINDOWS || AVALONIA   // the editor on WPF, or Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoTuneEditor();
    }
#endif
}
