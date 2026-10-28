using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Mixer side of the Session View (fork addition, docs/specs/session-view.md 0.4). No DSP of
    /// its own: a strip edits the machine graph, so the Machine View and the session always show
    /// the same thing.
    ///
    /// <list type="bullet">
    /// <item>Volume and pan are the amp and pan of the machine's <i>main outputs</i>: its output
    /// connections other than sends and cue. With several main outputs, volume moves them all by
    /// the same number of dB (keeping their balance) and pan sets them all.</item>
    /// <item>A send is the connection from the machine to a return machine
    /// (<see cref="SessionDocument.Returns"/>); raising a send from nothing creates it
    /// (undoable in the Machine View).</item>
    /// <item>Cue connects the machine to <see cref="SessionDocument.CueMachine"/> at unity, a
    /// machine that plays to a second output (ReBuzzAudioOut, Polac Out); off disconnects it.</item>
    /// <item>Meters show the main outputs' post-fader peak (<see cref="MachineConnectionCore.TakePeak"/>).</item>
    /// </list>
    /// Fader positions use the Machine View's amp knob scale (and ConnectionMixer's): linear in
    /// dB from -66 dB (0, silence) to +12 dB, unity at about 0.85.
    /// </summary>
    internal static class SessionMixer
    {
        public const int UnityAmp = 0x4000;
        public const int MaxAmp = 0xFFFE;
        public const int CenterPan = 0x4000;
        public const int MaxPan = 0x8000;

        const double MinDb = 66;
        static readonly double MaxDb = Decibel.FromAmplitude((double)MaxAmp / UnityAmp);

        /// <summary>Meter scale: -60 dB at the bottom, +6 dB at the top.</summary>
        public const double MeterFloorDb = -60;
        public const double MeterTopDb = 6;
        const double FullScale = 32768.0;

        // ------------------------------------------------------------------ scales

        /// <summary>Fader position (0..1) of a connection amp.</summary>
        public static double FaderOf(int amp)
        {
            if (amp <= 0) return 0;
            if (amp >= MaxAmp) return 1;
            return Math.Clamp((Decibel.FromAmplitude((double)amp / UnityAmp) + MinDb) / (MinDb + MaxDb), 0, 1);
        }

        /// <summary>Connection amp of a fader position (0..1).</summary>
        public static int AmpOf(double fader)
        {
            if (fader <= 0) return 0;
            return Math.Clamp((int)Math.Round(Decibel.ToAmplitude(Math.Min(fader, 1) * (MinDb + MaxDb) - MinDb) * UnityAmp), 0, MaxAmp);
        }

        public static double UnityFader => FaderOf(UnityAmp);

        public static string FormatAmp(int amp) =>
            amp <= 0 ? "-inf" : FormatDb(Decibel.FromAmplitude((double)amp / UnityAmp));

        public static string FormatDb(double db) => Math.Abs(db) < 0.05 ? "0.0" : db.ToString("+0.0;-0.0");

        /// <summary>Pan as -1 (left) .. 1 (right).</summary>
        public static double PanOf(int pan) => Math.Clamp((pan - CenterPan) / (double)CenterPan, -1, 1);

        public static int PanValue(double pan) => Math.Clamp((int)Math.Round(CenterPan + Math.Clamp(pan, -1, 1) * CenterPan), 0, MaxPan);

        public static string FormatPan(int pan)
        {
            int percent = (int)Math.Round(PanOf(pan) * 100);
            return percent == 0 ? "C" : percent < 0 ? $"{-percent}L" : $"{percent}R";
        }

        /// <summary>Master volume (linear gain 0..1, <see cref="IBuzz.MasterVolume"/>) as a fader position on the same dB scale, topping out at 0 dB.</summary>
        public static double MasterFaderOf(double gain) =>
            gain <= 0 ? 0 : Math.Clamp((Decibel.FromAmplitude(gain) + MinDb) / MinDb, 0, 1);

        public static double MasterGainOf(double fader) =>
            fader <= 0 ? 0 : Math.Clamp(Decibel.ToAmplitude(Math.Min(fader, 1) * MinDb - MinDb), 0, 1);

        /// <summary>Meter level (0..1 over <see cref="MeterFloorDb"/>..<see cref="MeterTopDb"/>) of a peak in sample units.</summary>
        public static double MeterLevel(float peak)
        {
            if (!(peak > 0)) return 0;
            return LevelOfDb(Decibel.FromAmplitude(peak / FullScale));
        }

        public static double LevelOfDb(double db) =>
            Math.Clamp((db - MeterFloorDb) / (MeterTopDb - MeterFloorDb), 0, 1);

        // ------------------------------------------------------------------ connections

        static bool IsAux(SessionDocument doc, IMachine destination) =>
            destination != null && doc != null &&
            (doc.Returns.Contains(destination.Name) || (doc.CueMachine.Length > 0 && destination.Name == doc.CueMachine));

        /// <summary>Output connections that carry the machine's own mix (not sends or cue).</summary>
        public static IReadOnlyList<IMachineConnection> MainOutputs(SessionDocument doc, IMachine machine)
        {
            if (machine == null) return Array.Empty<IMachineConnection>();
            var outs = machine.Outputs.Where(o => o.Destination != null && !IsAux(doc, o.Destination)).ToList();
            return outs;
        }

        /// <summary>Volume (connection amp) of the machine, or -1 when it has no main output.</summary>
        public static int GetAmp(SessionDocument doc, IMachine machine) =>
            MainOutputs(doc, machine).FirstOrDefault()?.Amp ?? -1;

        /// <summary>Sets the volume: the first main output to <paramref name="amp"/>, the others by the same ratio.</summary>
        public static void SetAmp(SessionDocument doc, IMachine machine, int amp)
        {
            var outs = MainOutputs(doc, machine);
            if (outs.Count == 0) return;
            amp = Math.Clamp(amp, 0, MaxAmp);
            int reference = outs[0].Amp;
            for (int i = 0; i < outs.Count; i++)
            {
                int value = i == 0 || reference <= 0 ? amp : (int)Math.Round((double)outs[i].Amp * amp / reference);
                outs[i].Amp = Math.Clamp(value, 0, MaxAmp);
            }
        }

        /// <summary>Pan of the first main output, or -1 when it has none (or no stereo input to pan into).</summary>
        public static int GetPan(SessionDocument doc, IMachine machine) =>
            MainOutputs(doc, machine).FirstOrDefault(o => o.HasPan)?.Pan ?? -1;

        public static void SetPan(SessionDocument doc, IMachine machine, int pan)
        {
            pan = Math.Clamp(pan, 0, MaxPan);
            foreach (var o in MainOutputs(doc, machine).Where(o => o.HasPan))
                o.Pan = pan;
        }

        public static IMachineConnection ConnectionTo(IMachine source, IMachine destination) =>
            source == null || destination == null ? null : source.Outputs.FirstOrDefault(o => o.Destination == destination);

        /// <summary>Whether the machine has a send to the return or could get one (no feedback loop, not itself).</summary>
        public static bool CanSend(IMachine machine, IMachine target) =>
            machine != null && target != null && machine != target &&
            (ConnectionTo(machine, target) != null || machine.Graph.CanConnectMachines(machine, target));

        /// <summary>Send amp to a return machine; 0 without a connection.</summary>
        public static int GetSend(IMachine machine, IMachine target) => ConnectionTo(machine, target)?.Amp ?? 0;

        /// <summary>Sets a send, connecting the machine to the return when it isn't yet (and the amp isn't 0).</summary>
        public static void SetSend(IMachine machine, IMachine target, int amp)
        {
            amp = Math.Clamp(amp, 0, MaxAmp);
            var connection = ConnectionTo(machine, target);
            if (connection != null)
            {
                connection.Amp = amp;
                return;
            }
            if (amp > 0 && CanSend(machine, target))
                machine.Graph.ConnectMachines(machine, target, new[] { 0 }, new[] { 0 }, amp, CenterPan);
        }

        public static bool IsCued(IMachine machine, IMachine cue) => ConnectionTo(machine, cue) != null;

        /// <summary>Connects the machine to the cue machine at unity, or disconnects it.</summary>
        public static bool SetCue(IMachine machine, IMachine cue, bool on)
        {
            var connection = ConnectionTo(machine, cue);
            if (!on)
            {
                if (connection != null)
                    machine.Graph.DisconnectMachines(connection);
                return true;
            }
            if (connection != null) return true;
            if (!CanSend(machine, cue)) return false;
            machine.Graph.ConnectMachines(machine, cue, new[] { 0 }, new[] { 0 }, UnityAmp, CenterPan);
            return true;
        }

        /// <summary>Machines that can be returns or the cue output: effects (not the master).</summary>
        public static IEnumerable<IMachine> EffectMachines(ISong song) =>
            song.Machines.Where(m => m.DLL?.Info?.Type == MachineType.Effect);

        // ------------------------------------------------------------------ meters

        // A peak is taken (and reset) at most once per frame, so two views showing the same
        // connection (docked and windowed) don't steal each other's peaks.
        static readonly Dictionary<IMachineConnection, (long Time, float Left, float Right)> peaks =
            new Dictionary<IMachineConnection, (long, float, float)>();

        /// <summary>Peak of a connection since the last frame (sample units), per channel. UI thread.</summary>
        public static (float Left, float Right) PeakOf(IMachineConnection connection)
        {
            if (connection is not MachineConnectionCore core) return (0, 0);
            long now = Environment.TickCount64;
            if (peaks.TryGetValue(connection, out var cached) && now - cached.Time < 15)
                return (cached.Left, cached.Right);
            if (peaks.Count > 1024)
                peaks.Clear();
            var p = core.TakePeak();
            peaks[connection] = (now, p.Left, p.Right);
            return p;
        }

        /// <summary>Peak of the machine's main outputs (the loudest), per channel.</summary>
        public static (float Left, float Right) MainPeak(SessionDocument doc, IMachine machine)
        {
            float l = 0, r = 0;
            foreach (var o in MainOutputs(doc, machine))
            {
                var p = PeakOf(o);
                l = Math.Max(l, p.Left);
                r = Math.Max(r, p.Right);
            }
            return (l, r);
        }

        /// <summary>Turns the engine's peak metering on or off (counted).</summary>
        public static void UseMeters(bool use) => MachineConnectionCore.UsePeakMeters(use);

        // ------------------------------------------------------------------ groups

        /// <summary>The machine group of a machine, if any.</summary>
        public static IMachineGroup GroupOf(ISong song, IMachine machine) =>
            machine != null && song?.MachineToGroupDict != null && song.MachineToGroupDict.TryGetValue(machine, out var g) ? g : null;
    }
}
