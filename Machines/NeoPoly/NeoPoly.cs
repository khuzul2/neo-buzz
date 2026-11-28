#nullable disable
// Neo Poly (docs/specs/machines.md §5): the polyphonic virtual-analog machine. 16 tracks of Note
// and Velocity, plus MIDI. DSP in Engine/, parameters generated from Params.def, editor in
// NeoPolyEditor.cs.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Poly
{
    [MachineDecl(Name = "Neo Poly", ShortName = "Poly", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
    public partial class NeoPolyMachine : NeoSynthBase, IBuzzMachine
    {
        readonly float[] left = new float[PolyEngine.MaxBlock], right = new float[PolyEngine.MaxBlock];
        int rate;

        public NeoPolyMachine(IBuzzMachineHost host) : base(host, Params.Table, Params.Tracks)
        {
            Engine = new PolyEngine(Values);
        }

        internal PolyEngine Engine { get; private set; }

        // State version 1 (machines.md §3.2): F1 type gained Diode 18 (a wider range; every value a song saved means what it did) and the acid
        // parameters were appended. Nothing a song saved moves: the new ones take their defaults, which change nothing.
        static readonly StateMigrations PolyChain = new StateMigrations(Params.Table)
            .Step(new[]
            {
                "VoiceMode", "Polyphony", "Unison", "UniDetune", "UniSpread", "GlideMode", "Glide", "BendRange",
                "Drift", "Volume", "VelAmp", "O1Shape", "O1PW", "O1Oct", "O1Semi", "O1Fine",
                "O1Level", "O2Shape", "O2PW", "O2Oct", "O2Semi", "O2Fine", "O2Level", "O2Sync",
                "O2Fm", "O3Shape", "O3Oct", "O3Semi", "O3Fine", "O3Level", "RingLevel", "NoiseLevel",
                "NoiseColor", "F1Type", "F1Cutoff", "F1Reso", "F1Drive", "F1Env", "F1Key", "F1Vel",
                "F2Type", "F2Cutoff", "F2Reso", "F2Env", "Routing", "AmpA", "AmpD", "AmpS",
                "AmpR", "FltA", "FltD", "FltS", "FltR", "ModA", "ModD", "ModS",
                "ModR", "L1Shape", "L1Rate", "L1Sync", "L1Retrig", "L1Fade", "L2Shape", "L2Rate",
                "L2Sync", "L2Retrig", "L2Fade", "M1Src", "M1Dst", "M1Amt", "M2Src", "M2Dst",
                "M2Amt", "M3Src", "M3Dst", "M3Amt", "M4Src", "M4Dst", "M4Amt", "M5Src",
                "M5Dst", "M5Amt", "M6Src", "M6Dst", "M6Amt", "M7Src", "M7Dst", "M7Amt",
                "M8Src", "M8Dst", "M8Amt",
            }, s => { })
            // State version 2 (AC3): the acid bounds were appended (Filter range, Sweep limit, Filter quality, Soft start). A song saved before
            // them takes their defaults.
            .Step(new[]
            {
                "VoiceMode", "Polyphony", "Unison", "UniDetune", "UniSpread", "GlideMode", "Glide", "BendRange",
                "Drift", "Volume", "VelAmp", "O1Shape", "O1PW", "O1Oct", "O1Semi", "O1Fine",
                "O1Level", "O2Shape", "O2PW", "O2Oct", "O2Semi", "O2Fine", "O2Level", "O2Sync",
                "O2Fm", "O3Shape", "O3Oct", "O3Semi", "O3Fine", "O3Level", "RingLevel", "NoiseLevel",
                "NoiseColor", "F1Type", "F1Cutoff", "F1Reso", "F1Drive", "F1Env", "F1Key", "F1Vel",
                "F2Type", "F2Cutoff", "F2Reso", "F2Env", "Routing", "AmpA", "AmpD", "AmpS",
                "AmpR", "FltA", "FltD", "FltS", "FltR", "ModA", "ModD", "ModS",
                "ModR", "L1Shape", "L1Rate", "L1Sync", "L1Retrig", "L1Fade", "L2Shape", "L2Rate",
                "L2Sync", "L2Retrig", "L2Fade", "M1Src", "M1Dst", "M1Amt", "M2Src", "M2Dst",
                "M2Amt", "M3Src", "M3Dst", "M3Amt", "M4Src", "M4Dst", "M4Amt", "M5Src",
                "M5Dst", "M5Amt", "M6Src", "M6Dst", "M6Amt", "M7Src", "M7Dst", "M7Amt",
                "M8Src", "M8Dst", "M8Amt",
                "AccentThr", "Accent", "Slide", "SlideTime", "AccentDecay", "AccentSweep", "AccentLevel",
            }, s => s.Add("AcidRange", 797).Add("AcidSweepMax", 500).Add("AcidQuality", 1).Add("AcidSoft", 650))
            // State version 2 (SYN1, tempo sync): LFO feel was appended and LFO sync gained 1/64 and 8 bars; an old song loads Straight, as before.
            .Step(new[]
            {
                "VoiceMode", "Polyphony", "Unison", "UniDetune", "UniSpread", "GlideMode", "Glide", "BendRange", "Drift",
                "Volume", "VelAmp", "O1Shape", "O1PW", "O1Oct", "O1Semi", "O1Fine", "O1Level", "O2Shape", "O2PW", "O2Oct",
                "O2Semi", "O2Fine", "O2Level", "O2Sync", "O2Fm", "O3Shape", "O3Oct", "O3Semi", "O3Fine", "O3Level",
                "RingLevel", "NoiseLevel", "NoiseColor", "F1Type", "F1Cutoff", "F1Reso", "F1Drive", "F1Env", "F1Key",
                "F1Vel", "F2Type", "F2Cutoff", "F2Reso", "F2Env", "Routing", "AmpA", "AmpD", "AmpS", "AmpR", "FltA", "FltD",
                "FltS", "FltR", "ModA", "ModD", "ModS", "ModR", "L1Shape", "L1Rate", "L1Sync", "L1Retrig", "L1Fade",
                "L2Shape", "L2Rate", "L2Sync", "L2Retrig", "L2Fade", "M1Src", "M1Dst", "M1Amt", "M2Src", "M2Dst", "M2Amt",
                "M3Src", "M3Dst", "M3Amt", "M4Src", "M4Dst", "M4Amt", "M5Src", "M5Dst", "M5Amt", "M6Src", "M6Dst", "M6Amt",
                "M7Src", "M7Dst", "M7Amt", "M8Src", "M8Dst", "M8Amt", "AccentThr", "Accent", "Slide", "SlideTime",
                "AccentDecay", "AccentSweep", "AccentLevel", "AcidRange", "AcidSweepMax", "AcidQuality", "AcidSoft",
            }, s => s.Add("L1Feel", 0).Add("L2Feel", 0));

        internal override StateMigrations Migrations => PolyChain;

        internal override void Restart()
        {
            Engine = new PolyEngine(Values);
            rate = 0;
        }

        public bool Work(Sample[] output, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            Engine.Tempo = BeatsPerMinute;
            BeginBlock(Engine);
            n = Math.Min(n, PolyEngine.MaxBlock);
            if (!Engine.Process(left, right, n)) return false;
            return Join(left, right, output, n);
        }

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Poly...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Poly — polyphonic virtual analog: three band-limited oscillators with sync, FM and " +
                        "ring, ladder, diode and state-variable filters, three envelopes, two LFOs and an 8-slot " +
                        "modulation matrix. 16 tracks (Note, Velocity) and MIDI (mod wheel, pitch bend, " +
                        "sustain, aftertouch).\n\nAcid: the Diode 18 filter, Accent (hard notes open the filter further, " +
                        "close faster and play louder; a run of accents swells) and Slide (an overlapping note glides " +
                        "without retriggering, in Mono or Legato mode).\n\nPart of neo-buzz (docs/specs/machines.md).", "Neo Poly"),
                },
            },
        };
    }

#if AVALONIA   // fork: the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoPolyEditor();
    }
#endif
}
