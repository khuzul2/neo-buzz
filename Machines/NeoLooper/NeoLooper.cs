#nullable disable
// Neo Looper (docs/specs/neo-looper.md): record, overdub, multiply and undo, in time with the song, made for a MIDI controller.
// DSP in Engine/, parameters generated from Params.def, editor in NeoLooperEditor.cs. The loop's audio is the machine's own data
// (MachineState), so it is saved with the song and comes back bit for bit.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using NeoBuzz.Machine;

namespace NeoBuzz.Looper
{
    [MachineDecl(Name = "Neo Looper", ShortName = "Looper", Author = "neo-buzz", MaxTracks = 0, InputCount = 1, OutputCount = 1)]
    public partial class NeoLooperMachine : NeoMachineBase, IBuzzMachine
    {
        readonly float[] left = new float[LooperEngine.MaxBlock], right = new float[LooperEngine.MaxBlock];
        int rate;
        int barCheck, beatsPerBar = 4;
        LooperSaved saved = new LooperSaved();
        int savedRevision = -1;
        bool armed;

        public NeoLooperMachine(IBuzzMachineHost host) : base(host, Params.Table)
        {
            Engine = new LooperEngine(Values);
            Engine.SetSampleRate(SampleRate);
            rate = SampleRate;
            Engine.StartWorker();
            armed = false;
        }

        internal LooperEngine Engine { get; }

        /// <summary>The loop kept with the song: the engine's loop encoded (again only when it changed), and on load the loop put back.</summary>
        public LooperSaved MachineState
        {
            get
            {
                int rev = Engine.Revision;
                if (rev != savedRevision || saved == null)
                {
                    if (Engine.Snapshot(out var l, out var r, out int n, out double beats)) saved = LooperCodec.Encode(l, r, n, rate, beats);
                    else saved = new LooperSaved { Version = LooperCodec.Version, Rate = rate };
                    savedRevision = rev;
                }
                return saved;
            }
            set
            {
                saved = value ?? new LooperSaved();
                try
                {
                    if (LooperCodec.TryDecode(saved, out var l, out var r)) Engine.Load(l, r, saved.Frames, saved.LoopBeats);
                }
                catch (Exception)
                {
                    // A damaged loop loads as no loop rather than failing the song.
                }
                savedRevision = Engine.Revision;
            }
        }

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (SampleRate != rate)
            {
                rate = SampleRate;
                Engine.SetSampleRate(rate);
            }
            if (!armed) { armed = true; Engine.ArmTriggers(); }
            bool hasInput = (mode & WorkModes.WM_READ) != 0 && input != null && HasSignal(input, n);
            if (!hasInput)
            {
                // Nothing to loop and nothing playing: rest.
                if (Engine.State == LooperState.Empty || Engine.State == LooperState.Stopped)
                {
                    TransportStopped();
                    Transport();
                    Array.Clear(left, 0, n);
                    Array.Clear(right, 0, n);
                    Engine.Process(left, right, n);
                    return false;
                }
                Array.Clear(left, 0, n);
                Array.Clear(right, 0, n);
            }
            else Split(input, left, right, n);

            TransportStopped();
            Transport();
            if (n > LooperEngine.MaxBlock) throw new InvalidOperationException("block larger than 256");
            Engine.Process(left, right, n);
            return Join(left, right, output, n) || hasInput;
        }

        /// <summary>Where the song is now (valid in Work): whether it plays, its position in beats, the samples a beat takes and the beats in its bar.</summary>
        void Transport()
        {
            bool playing = false;
            double beat = 0, spb = SampleRate * 60.0 / BeatsPerMinute;
            try
            {
                var info = host?.MasterInfo;
                var buzz = host?.Machine?.Graph?.Buzz;
                if (info != null && buzz != null)
                {
                    int spt = info.SamplesPerTick, tpb = info.TicksPerBeat;
                    if (spt > 0 && tpb > 0)
                    {
                        spb = (double)spt * tpb;
                        playing = buzz.Playing;
                        int tick = buzz.Song?.PlayPosition ?? 0;
                        beat = (tick + (double)info.PosInTick / spt) / tpb;
                    }
                    // A bar is the song's bar (the session's beats per bar, from the song's meter); 4 when the host gives none.
                    if ((barCheck++ & 63) == 0)
                    {
                        int bars = (buzz as ISessionHost)?.Session?.BeatsPerBar ?? 4;
                        if (bars >= 1) beatsPerBar = bars;
                    }
                }
            }
            catch
            {
                playing = false;
            }
            Engine.SetTransport(playing, beat, spb, beatsPerBar);
        }

        public int GetLatency() => LooperEngine.Latency;

        public IEnumerable<IMenuItem> Commands => new IMenuItem[]
        {
            new MenuItemVM
            {
                Text = "About Neo Looper...",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => true,
                    ExecuteDelegate = _ => NeoBuzz.Machine.NeoAbout.Show(
                        "Neo Looper — record, overdub, multiply and undo, in time with the song's bars, from a pad or a footswitch: one Record trigger steps the cycle, " +
                        "Stop, Play, Undo, Redo, Clear and Multiply are their own; feedback, reverse, half speed, a crossfade at every edit; zero latency; " +
                        "the loop is saved with the song bit for bit.\n\nPart of neo-buzz (docs/specs/neo-looper.md).", "Neo Looper"),
                },
            },
        };
    }

#if AVALONIA   // the editor: Avalonia in the studio (studio-mvp.md S1); none headless
    [MachineGUIFactoryDecl(PreferWindowedGUI = true, IsGUIResizable = false, UseThemeStyles = false)]
    public class MachineGUIFactory : IMachineGUIFactory
    {
        public IMachineGUI CreateGUI(IMachineGUIHost host) => new NeoLooperEditor();
    }
#endif
}
