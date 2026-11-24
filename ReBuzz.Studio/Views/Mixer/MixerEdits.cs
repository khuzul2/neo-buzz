// Fork (docs/specs/ux-overhaul.md §11, MX1): the mixer's edits as undo steps, one per gesture. A drag changes the connection's amp live
// (as the Session mixer always did, so the sound follows the hand) and the gesture's end makes the whole move one step on the song's
// undo history; a wheel notch or a double click is a gesture of its own. Mute and solo are the machine's flags, a track colour is the
// colour store's (and the Session View's own track colour, when its track has one).

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Views.Mixer
{
    internal static class MixerEdits
    {
        /// <summary>A machine's wires as they are now: what a gesture compares at its end.</summary>
        public sealed class WireSnapshot
        {
            readonly List<(string From, string To, int Amp, int Pan)> wires;

            WireSnapshot(List<(string, string, int, int)> wires) => this.wires = wires;

            public static WireSnapshot Of(IEnumerable<IMachine> machines) =>
                new(machines.Where(m => m != null).SelectMany(m => m.Outputs.Where(o => o.Destination != null)
                    .Select(o => (m.Name, o.Destination.Name, o.Amp, o.Pan))).ToList());

            /// <summary>Makes what changed since this snapshot one undo step (nothing when nothing did).</summary>
            public bool Commit(ReBuzzCore core, string name, IEnumerable<IMachine> machines)
            {
                var now = Of(machines).wires;
                var changes = new List<(string From, string To, int Amp0, int Pan0, int Amp1, int Pan1)>();
                foreach (var (from, to, amp, pan) in now)
                {
                    var was = wires.FirstOrDefault(w => w.From == from && w.To == to);
                    if (was.From == null) continue;             // a wire made during the gesture is a step of its own (grouped by the caller)
                    if (was.Amp != amp || was.Pan != pan) changes.Add((from, to, was.Amp, was.Pan, amp, pan));
                }
                if (changes.Count == 0) return false;
                void Apply(bool after)
                {
                    foreach (var c in changes)
                    {
                        var conn = core.SongCore.MachinesList.FirstOrDefault(m => m.Name == c.From)?.Outputs.FirstOrDefault(o => o.Destination?.Name == c.To);
                        if (conn == null) continue;
                        core.SongCore.SetConnectionParameter(conn, 0, conn.Amp, after ? c.Amp1 : c.Amp0);
                        if (conn.HasPan) core.SongCore.SetConnectionParameter(conn, 1, conn.Pan, after ? c.Pan1 : c.Pan0);
                    }
                }
                AgentActions.Do(core, new DelegateAction(() => Apply(true), () => Apply(false), name));
                return true;
            }
        }

        /// <summary>The Master's Volume parameter (the song's gain is 1 - value / 0x4000).</summary>
        public static IParameter MasterVolume(ReBuzzCore core) =>
            core.SongCore.MachinesList.FirstOrDefault(m => m.DLL?.Info?.Type == MachineType.Master)?.ParameterGroups[1].Parameters[0];

        /// <summary>Makes a Master volume gesture one undo step (the fader set the value live).</summary>
        public static void CommitMasterVolume(ReBuzzCore core, int before)
        {
            var p = MasterVolume(core);
            if (p == null) return;
            int after = p.GetValue(0);
            if (after == before) return;
            void Set(int v) => MasterVolume(core)?.SetValue(0, v);
            AgentActions.Do(core, new DelegateAction(() => Set(after), () => Set(before), "Master volume"));
        }

        public static void SetMute(ReBuzzCore core, IMachine machine, bool on)
        {
            if (machine is MachineCore m && m.IsMuted != on) AgentActions.SetFlag(core, m, AgentActions.Flag.Mute, on);
        }

        public static void SetSolo(ReBuzzCore core, IMachine machine, bool on)
        {
            if (machine is MachineCore m && m.IsSoloed != on) AgentActions.SetFlag(core, m, AgentActions.Flag.Solo, on);
        }

        /// <summary>The track colour of a machine, by the user: one undo step (its Session track's colour and the store's together).</summary>
        public static void SetTrackColour(ReBuzzCore core, IMachine machine, int index)
        {
            if (machine == null) return;
            core.TrackColours?.SetByUser(machine, index);
        }

        /// <summary>Whether this machine's track colour shows through the Session View's: its track has none of its own set.</summary>
        public static bool IsStoreColour(ReBuzzCore core, IMachine machine)
        {
            var layout = core.SessionLayout;
            var doc = layout?.Document;
            if (doc == null) return true;
            for (int t = 0; t < doc.Tracks.Count; t++)
                if (layout.SequenceOf(t)?.Machine == machine && doc.Tracks[t].ColorIndex >= 0) return false;
            return true;
        }
    }
}
