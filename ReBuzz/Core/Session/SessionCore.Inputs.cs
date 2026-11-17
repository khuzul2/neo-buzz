using BuzzGUI.Interfaces;
using ReBuzz.Midi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Track inputs, engine side (docs/specs/neo-audio.md "Inputs and arm"): what each machine's tracks listen to, published whole by the
    /// layout (any change of the layout's <see cref="SessionDocument.Inputs"/>, undo and a loaded song included) for the audio thread (a Neo
    /// Audio's Audio From) and the MIDI threads (an armed track's MIDI From and channel).
    /// </summary>
    internal sealed partial class SessionCore
    {
        /// <summary>One machine's inputs, and for a track source the tap that keeps its output.</summary>
        internal sealed class InputRoute
        {
            public InputRoute(TrackInput input, TrackTap tap)
            {
                Input = input;
                Tap = tap;
            }

            public TrackInput Input { get; }
            public TrackTap Tap { get; }
        }

        sealed class RouteTable
        {
            public static readonly RouteTable Empty = new RouteTable(new Dictionary<MachineCore, InputRoute>());

            public RouteTable(Dictionary<MachineCore, InputRoute> map) => Map = map;

            public readonly Dictionary<MachineCore, InputRoute> Map;
        }

        volatile RouteTable routeTable = RouteTable.Empty;
        readonly Dictionary<MachineCore, TrackTap> taps = new Dictionary<MachineCore, TrackTap>();     // UI thread

        /// <summary>
        /// UI thread: the inputs of every machine that has an entry. A track source gets its tap (kept while some machine uses it);
        /// <paramref name="aux"/> are the names of the machines whose connections are sends or cue.
        /// </summary>
        internal void UseInputs(IReadOnlyDictionary<MachineCore, TrackInput> inputs, Func<string, MachineCore> machineNamed, HashSet<string> aux)
        {
            var map = new Dictionary<MachineCore, InputRoute>(inputs.Count);
            var wanted = new HashSet<MachineCore>();
            foreach (var (machine, input) in inputs)
            {
                TrackTap tap = null;
                if (input.Audio.Kind == AudioFromKind.Track && machineNamed(input.Audio.Machine) is MachineCore source && !ReferenceEquals(source, machine))
                {
                    wanted.Add(source);
                    if (!taps.TryGetValue(source, out tap))
                        taps[source] = tap = new TrackTap();
                    tap.SetAux(aux);
                    source.SessionTap = tap;
                }
                map[machine] = new InputRoute(input.Clone(), tap);
            }
            foreach (var gone in taps.Keys.Where(m => !wanted.Contains(m)).ToList())
            {
                gone.SessionTap = null;
                taps.Remove(gone);
            }
            routeTable = new RouteTable(map);
        }

        /// <summary>A machine's route (any thread; no allocation), or null: the defaults (audio from what is wired, MIDI from all inputs).</summary>
        internal InputRoute RouteOf(IMachine machine) =>
            machine is MachineCore m && routeTable.Map.TryGetValue(m, out var route) ? route : null;

        /// <summary>Whether a MIDI device is a connected control surface (its messages run the session; All inputs leave it out).</summary>
        bool IsClaimed(MidiDevice device) => device.IsDevice && buzz.SessionControllers?.ClaimsInput(device.Name) == true;

        /// <summary>The armed machines that take a message from <paramref name="device"/> on <paramref name="channel"/> (0 to 15), a new array or none.</summary>
        MachineCore[] Accepting(MachineCore[] armed, MidiDevice device, int channel)
        {
            if (armed.Length == 0) return armed;
            bool claimed = IsClaimed(device);
            var table = routeTable.Map;
            MachineCore[] taken = null;
            int count = 0;
            for (int i = 0; i < armed.Length; i++)
            {
                table.TryGetValue(armed[i], out var route);
                if (!MidiRouting.Accepts(route?.Input, device, channel, claimed)) continue;
                taken ??= new MachineCore[armed.Length];
                taken[count++] = armed[i];
            }
            if (count == 0) return Array.Empty<MachineCore>();
            if (count == armed.Length) return armed;
            Array.Resize(ref taken, count);
            return taken;
        }
    }
}
