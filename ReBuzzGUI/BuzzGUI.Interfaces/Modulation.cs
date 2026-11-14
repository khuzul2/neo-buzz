// Fork (docs/specs/neo-patch.md §3, phase NM): what the canvas modulators (Neo LFO, Neo Envelope, Neo Step,
// Neo Macro) share with the host across assemblies. Every machine DLL carries its own copy of the Neo kit, but all
// of them load this one, as with ExplainBoard. Plain data and one small registry:
//  - ModulationInfo: one target of a modulator (or one incoming modulation of a machine), as the studio, the agent
//    and the editors read it.
//  - IModulator: what a modulator machine offers (its targets, its live output, edits to its targets).
//  - ModulationBoard: the live modulators (each registers itself on creation and leaves on dispose), the safe-range
//    hook the host fills from the machines' manifests, and a change notice for the views.
//  - IBlockLevel: the peak the engine measured on a connection's last block (for the Neo Envelope's follower).
using System.Runtime.CompilerServices;

namespace BuzzGUI.Interfaces
{
    /// <summary>A canvas modulator machine.</summary>
    public interface IModulator
    {
        /// <summary>The machine in the song.</summary>
        IMachine Machine { get; }
        /// <summary>"LFO", "Envelope", "Step" or "Macro".</summary>
        string Kind { get; }
        /// <summary>The live output, 0 … 1.</summary>
        float Output { get; }
        /// <summary>The recent output for a scope: copies up to <paramref name="dest"/>.Length values, oldest first, 0 … 1; returns how many.</summary>
        int ReadScope(float[] dest);
        /// <summary>A snapshot of the targets in use (allocates: UI and agent, never the audio thread).</summary>
        System.Collections.Generic.IReadOnlyList<ModulationInfo> Targets { get; }
        /// <summary>The machine whose notes (or output) the modulator listens to, and its track (−1: all); empty: none.</summary>
        string ListenMachine { get; set; }
        int ListenTrack { get; set; }
        /// <summary>
        /// Adds (slot −1: the first free) or changes a target. Returns null when it worked, else why not (the 8 are in use,
        /// the machine or parameter isn't there, the parameter is never modulated). <paramref name="info"/> carries the settings.
        /// </summary>
        string SetTarget(int slot, IMachine machine, IParameter parameter, int track, ModulationInfo info, out int placed);
        /// <summary>Changes a target's depth, polarity, range and curve (from <paramref name="values"/>), keeping its parameter. Null: done.</summary>
        string EditTarget(int slot, ModulationInfo values);
        /// <summary>Removes a target and puts its parameter back at the base value.</summary>
        void RemoveTarget(int slot);
    }

    /// <summary>
    /// A control that a modulator can be dropped on (ux-overhaul.md P5b, drag-to-modulate): a knob, a slider, a row of the device view.
    /// The studio finds the one under the pointer when a drag from a modulator's output handle ends; coordinates are the control's own.
    /// </summary>
    public interface IModulationDropSurface
    {
        /// <summary>The parameter at (x, y), or false when nothing there takes a modulation.</summary>
        bool ModulationTargetAt(double x, double y, out IMachine machine, out IParameter parameter, out int track);
    }

    /// <summary>The live modulators, the safe-range hook and the change notice.</summary>
    public static class ModulationBoard
    {
        /// <summary>The modulator whose output handle is being dragged (the drop surfaces light up while it is set); null: no drag.</summary>
        public static IModulator Dragging { get; private set; }

        /// <summary>Raised when a drag starts or ends.</summary>
        public static event Action DragChanged;

        /// <summary>The host's: what happens when a drag from <c>source</c>'s handle ends at a screen point (pixels): it finds the surface there and adds the target. Returns the refusal, or null.</summary>
        public static Func<IModulator, double, double, string> DropHook { get; set; }

        /// <summary>The last drop's outcome: null when it added a target (or nothing was dropped), else why not.</summary>
        public static string LastDropMessage { get; private set; }

        /// <summary>The host's: opens the modulation matrix with this source chosen (the keyboard's way to add a target).</summary>
        public static Action<IModulator> MatrixHook { get; set; }

        public static void BeginDrag(IModulator source)
        {
            Dragging = source;
            DragChanged?.Invoke();
        }

        /// <summary>Ends the drag; at a screen point (pixels) it is dropped there, with null it is cancelled.</summary>
        public static void EndDrag((double X, double Y)? at)
        {
            var source = Dragging;
            Dragging = null;
            LastDropMessage = null;
            try
            {
                if (at is { } p && source != null && DropHook != null) LastDropMessage = DropHook(source, p.X, p.Y);
            }
            finally { DragChanged?.Invoke(); }
        }

        /// <summary>The most targets a modulator has.</summary>
        public const int MaxTargets = 8;

        static readonly object gate = new object();
        static readonly System.Collections.Generic.List<WeakReference<IModulator>> live = new();

        /// <summary>
        /// The host's: the raw range (min, max) a parameter may be modulated within, or null for its own whole range. The
        /// manifest's safe range (machine-safety.md §3.4), and the master's: never past 0 dB, never a different tempo.
        /// </summary>
        public static Func<IMachine, IParameter, (int Min, int Max)?> SafeRange { get; set; }

        /// <summary>Raised when a modulator's targets change (the views redraw).</summary>
        public static event Action Changed;

        public static void Raise() => Changed?.Invoke();

        public static void Register(IModulator m)
        {
            lock (gate)
            {
                live.RemoveAll(w => !w.TryGetTarget(out var x) || ReferenceEquals(x, m));
                live.Add(new WeakReference<IModulator>(m));
            }
            Raise();
        }

        public static void Unregister(IModulator m)
        {
            lock (gate) live.RemoveAll(w => !w.TryGetTarget(out var x) || ReferenceEquals(x, m));
            Raise();
        }

        /// <summary>The modulators alive (a copy).</summary>
        public static System.Collections.Generic.List<IModulator> All()
        {
            var list = new System.Collections.Generic.List<IModulator>();
            lock (gate)
                foreach (var w in live)
                    if (w.TryGetTarget(out var m)) list.Add(m);
            return list;
        }

        /// <summary>The modulator machine of <paramref name="machine"/>, or null when it is not one.</summary>
        public static IModulator Of(IMachine machine)
        {
            if (machine == null) return null;
            if (machine.ManagedMachine is IModulator direct) return direct;
            foreach (var m in All()) if (ReferenceEquals(m.Machine, machine)) return m;
            return null;
        }

        /// <summary>The modulations that drive <paramref name="target"/>'s parameters, by identity (the machine's name may change).</summary>
        public static System.Collections.Generic.List<ModulationInfo> Incoming(IMachine target)
        {
            var list = new System.Collections.Generic.List<ModulationInfo>();
            if (target == null) return list;
            foreach (var m in All())
            {
                System.Collections.Generic.IReadOnlyList<ModulationInfo> t;
                try { t = m.Targets; } catch (Exception) { continue; }
                foreach (var info in t)
                    if (info.State == ModulationState.Active && ReferenceEquals(Resolve(m, info), target)) list.Add(info);
            }
            return list;
        }

        /// <summary>The machine a target drives (by the name it has now).</summary>
        public static IMachine Resolve(IModulator m, ModulationInfo info)
        {
            try
            {
                var machines = m.Machine?.Graph?.Machines;
                if (machines == null) return null;
                foreach (var x in machines) if (x.Name == info.Machine) return x;
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>Count of Neo Envelope followers running: the engine measures connection levels only while one is.</summary>
        public static int Followers => System.Threading.Volatile.Read(ref followers);
        static int followers;
        public static void FollowerStarted() => System.Threading.Interlocked.Increment(ref followers);
        public static void FollowerStopped()
        {
            if (System.Threading.Interlocked.Decrement(ref followers) < 0) System.Threading.Interlocked.Exchange(ref followers, 0);
        }
    }

    /// <summary>A connection that keeps the peak of its last block (full scale 1.0), while a follower asks for it.</summary>
    public interface IBlockLevel
    {
        float BlockPeak { get; }
    }
}
