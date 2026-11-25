// Fork (docs/specs/ux-overhaul.md P2c): one selection for every view. Selecting a machine, a track, a clip or a pattern in
// the Machines, Sequence, Session or Pattern view selects that machine (and pattern) in the others: the track of a machine
// is the Sequence's row and the Session's column, so "select a track" and "select its machine" are one thing. A view
// publishes what the user selects and follows what the others publish; what it only follows it doesn't publish again
// (no echoes), and a selection that changes nothing raises nothing (no loops).

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views
{
    internal sealed class StudioSelection
    {
        static readonly ConditionalWeakTable<ReBuzzCore, StudioSelection> all = new();

        /// <summary>The selection of a song's window (one per engine).</summary>
        public static StudioSelection For(ReBuzzCore core) => all.GetValue(core, c => new StudioSelection(c));

        readonly ReBuzzCore core;
        IMachine machine;
        IClip clip;
        int? scene;

        StudioSelection(ReBuzzCore core) => this.core = core;

        /// <summary>The selected machine (its track), or null: nothing, the song. A machine the song lost is not selected.</summary>
        public IMachine Machine => machine != null && core.Song.Machines.Contains(machine) ? machine : null;

        /// <summary>The selected pattern (a clip, a block), always one of <see cref="Machine"/>'s, or null.</summary>
        public IClip Clip => Machine != null && clip != null && machine.Clips.Contains(clip) ? clip : null;

        /// <summary>The Session View's selected scene, where one is (the others keep it for the Session View).</summary>
        public int? Scene => scene;

        /// <summary>The selection changed; the argument is the view that changed it (it doesn't follow its own).</summary>
        public event Action<object> Changed;

        /// <summary>
        /// Selects a machine and, optionally, one of its patterns. Returns false, raising nothing, when this was already the
        /// selection. A scene given is kept with it; none given, the last stays.
        /// </summary>
        public bool Set(IMachine machine, IClip clip, object source, int? scene = null)
        {
            if (clip != null && machine == null) machine = clip.Machine;
            if (clip != null && clip.Machine != machine) clip = null;
            int? newScene = scene ?? this.scene;
            if (machine == this.Machine && clip == this.Clip && newScene == this.scene) return false;
            this.machine = machine;
            this.clip = clip;
            this.scene = machine == null ? null : newScene;
            Changed?.Invoke(source);
            return true;
        }

        /// <summary>Selects a machine; its pattern stays selected when it is the same machine.</summary>
        public bool SelectMachine(IMachine machine, object source) => Set(machine, machine != null && machine == Machine ? Clip : null, source);

        /// <summary>Nothing selected: the song.</summary>
        public bool Clear(object source) => Set(null, null, source);
    }
}
