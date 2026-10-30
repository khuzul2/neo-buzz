using BuzzGUI.Interfaces;
using System;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// What every slot and scene of the layout launches, resolved on the UI thread for the
    /// threads that can't read the layout (0.6, from 0.4.1's MIDI targets): the MIDI thread
    /// (pads) and the audio thread (follow actions). Immutable; <see cref="SessionLayout"/>
    /// replaces it whenever the layout, a clip's settings, the beats per bar or the ticks per
    /// beat change.
    /// </summary>
    internal sealed class LaunchMap
    {
        public static readonly LaunchMap Empty = new LaunchMap(
            Array.Empty<ISequence>(), Array.Empty<SlotEntry[]>(), Array.Empty<SceneEntry>());

        /// <summary>A filled slot: its pattern, settings, and launch options at full velocity.</summary>
        internal sealed class SlotEntry
        {
            public SlotEntry(IPattern pattern, SessionDocument.ClipSettings settings, LaunchOptions options)
            {
                Pattern = pattern;
                Settings = settings;
                Options = options;
            }

            public IPattern Pattern { get; }
            /// <summary>A copy; never modified.</summary>
            public SessionDocument.ClipSettings Settings { get; }
            /// <summary>Never modified: copy it to change it (<see cref="OptionsAt"/>).</summary>
            public LaunchOptions Options { get; }

            /// <summary>The launch options at a launch velocity (a copy when the velocity matters).</summary>
            public LaunchOptions OptionsAt(int velocity)
            {
                double gain = SessionLayout.VelocityGain(Settings.Velocity, velocity);
                if (gain == Options.Gain) return Options;
                var o = Copy(Options);
                o.Gain = gain;
                return o;
            }
        }

        /// <summary>A scene row: its filled slots and its settings (tempo, beats, follow actions, tracks it stops).</summary>
        internal sealed class SceneEntry
        {
            public SceneEntry((ISequence Track, SlotEntry Slot)[] clips, SceneOptions options, bool isEmpty)
            {
                Clips = clips;
                Options = options;
                IsEmpty = isEmpty;
            }

            public (ISequence Track, SlotEntry Slot)[] Clips { get; }
            /// <summary>Never modified.</summary>
            public SceneOptions Options { get; }
            /// <summary>No clips and no tempo or beats: scene follow actions skip over it, as clip ones skip empty slots.</summary>
            public bool IsEmpty { get; }
        }

        public LaunchMap(ISequence[] tracks, SlotEntry[][] slots, SceneEntry[] scenes)
        {
            Tracks = tracks;
            Slots = slots;
            Scenes = scenes;
        }

        public ISequence[] Tracks { get; }
        /// <summary>[track][scene]; null for an empty slot.</summary>
        public SlotEntry[][] Slots { get; }
        public SceneEntry[] Scenes { get; }

        public ISequence Track(int track) => track >= 0 && track < Tracks.Length ? Tracks[track] : null;

        public SlotEntry Slot(int track, int scene) =>
            track >= 0 && track < Slots.Length && scene >= 0 && scene < Slots[track].Length ? Slots[track][scene] : null;

        public SceneEntry Scene(int scene) => scene >= 0 && scene < Scenes.Length ? Scenes[scene] : null;

        static LaunchOptions Copy(LaunchOptions o) => new LaunchOptions
        {
            Quantization = o.Quantization,
            Loop = o.Loop,
            StartOffset = o.StartOffset,
            Mode = o.Mode,
            Legato = o.Legato,
            Start = o.Start,
            LoopStart = o.LoopStart,
            LoopEnd = o.LoopEnd,
            Gain = o.Gain,
            Follow = o.Follow,
            Slot = o.Slot
        };
    }
}
