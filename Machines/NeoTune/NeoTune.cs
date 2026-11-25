#nullable disable
// Neo Tune (docs/specs/neo-tune.md): pitch correction for a single voice or a melodic line. DSP in Engine/ (the shared PT1 tracker,
// TD-PSOLA resynthesis, the control stage), parameters generated from Params.def, editor in NeoTuneEditor.cs. The latency is the
// range's lookahead (10, 28 or 36 ms), reported to the app; machine_readings gets the input pitch, the target note, the
// correction, the voiced flag and the latency.

using System;
using System.Collections.Generic;
using System.Linq;
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
            if (Engine.TakeNaturalWrite(out double nat)) ShowNatural(nat);
            return Join(left, right, output, n) || hasInput;
        }

        public int GetLatency() => Engine.Latency;

        /// <summary>Natural was moved alone (automation, a pattern, a knob card, a binding): the six fine controls follow it in the engine; here the host's
        /// displayed values are made to match (not recorded into patterns), so the knobs show what plays. Where the host refuses, the knobs keep their last values.</summary>
        void ShowNatural(double n)
        {
            try
            {
                var machine = host?.Machine;
                var list = machine?.ParameterGroups?.FirstOrDefault(g => g.Type == ParameterGroupType.Global)?.Parameters;
                if (list == null) return;
                for (int i = 0; i < TuneNatural.Controls.Length; i++)
                {
                    int idx = TuneNatural.Controls[i];
                    if (idx < list.Count) list[idx].SetValue(1 << 16, Params.Table[idx].Raw(TuneNatural.Value(i, n)));
                }
                machine.SendControlChanges();
            }
            catch (Exception)
            {
            }
        }

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

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoTuneEditor();
    }
#endif
}
