using System.ComponentModel;

namespace BuzzGUI.Interfaces
{
    // Session View engine (fork addition). See docs/specs/session-view.md.
    //
    // A track is a sequence (ISequence), a clip is one of that sequence's machine's
    // patterns (IPattern). Each track plays at most one clip. Launches and stops are
    // quantized to a session clock that starts at 0 when the transport starts and,
    // unlike the song position, never wraps at the loop end.

    /// <summary>Grid that launches and stops snap to.</summary>
    public enum LaunchQuantization { None, Beat, Bar, TwoBars, FourBars, EightBars, HalfNote, Eighth, Sixteenth }

    /// <summary>
    /// What pressing and releasing a clip's launch control does (0.5, as in Live).
    /// Trigger: press launches, release is ignored. Gate: press launches, release stops (quantized).
    /// Toggle: press launches, or stops the clip if it plays. Repeat: while held, the clip
    /// restarts at its quantization rate; after release it plays on.
    /// </summary>
    public enum ClipLaunchMode { Trigger, Gate, Toggle, Repeat }

    /// <summary>
    /// What happens when a clip's or scene's follow time comes (0.6, Live's follow actions).
    /// Previous, Next, First, Last, Any and Other work within the clip's group (the run of
    /// filled slots around it; scenes: the run of non-empty scenes); Next wraps to the first.
    /// Other never picks the current one (unless it's alone). Jump goes to a slot (scene row).
    /// No action ends the chain until the clip is launched again.
    /// </summary>
    public enum ClipFollowAction { None, Stop, PlayAgain, Previous, Next, First, Last, Any, Other, Jump }

    /// <summary>Follow actions of a clip or scene (0.6).</summary>
    public class FollowOptions
    {
        public ClipFollowAction A { get; set; } = ClipFollowAction.Next;
        public ClipFollowAction B { get; set; } = ClipFollowAction.None;
        /// <summary>Chance of A in percent (B gets the rest).</summary>
        public int ChanceA { get; set; } = 100;
        /// <summary>At the end of the clip (after <see cref="Multiplier"/> passes of its loop) rather than after <see cref="TimeTicks"/>. Clips only.</summary>
        public bool Linked { get; set; } = true;
        public int Multiplier { get; set; } = 1;
        /// <summary>Follow time in ticks from where the clip (scene) started playing.</summary>
        public int TimeTicks { get; set; } = 16;
        /// <summary>Slot (scene row) for Jump, for A and for B.</summary>
        public int JumpA { get; set; } = -1;
        public int JumpB { get; set; } = -1;
    }

    /// <summary>State of a clip on its track.</summary>
    public enum ClipState
    {
        /// <summary>Not playing and not queued.</summary>
        Stopped,
        /// <summary>Waiting for its launch time.</summary>
        Queued,
        /// <summary>Playing.</summary>
        Playing,
        /// <summary>Playing, with a quantized stop pending.</summary>
        Stopping
    }

    public class LaunchOptions
    {
        /// <summary>Quantization for this launch; null uses <see cref="ISession.LaunchQuantization"/>.</summary>
        public LaunchQuantization? Quantization { get; set; }

        /// <summary>Loop the clip (default). When false the clip plays once and its track stops.</summary>
        public bool Loop { get; set; } = true;
        /// <summary>Start this many ticks into the clip (0.4.1; legato and clip start markers build on it).</summary>
        public int StartOffset { get; set; }
        /// <summary>Press and release behaviour (0.5). <see cref="ISession.Launch"/> is a press.</summary>
        public ClipLaunchMode Mode { get; set; } = ClipLaunchMode.Trigger;
        /// <summary>Take over the play position of the clip playing on the track (0.5).</summary>
        public bool Legato { get; set; }
        /// <summary>Start marker in ticks: where the clip starts playing (0.5).</summary>
        public int Start { get; set; }
        /// <summary>Loop region start in ticks (0.5).</summary>
        public int LoopStart { get; set; }
        /// <summary>Loop region end in ticks, exclusive; 0 for the clip's end (0.5). With Loop off, where the clip ends.</summary>
        public int LoopEnd { get; set; }
        /// <summary>Volume of the clip's machine while it plays, 0..1 (0.5: launch velocity).</summary>
        public double Gain { get; set; } = 1.0;
        /// <summary>Follow actions (0.6); null for none.</summary>
        public FollowOptions? Follow { get; set; }
        /// <summary>The slot (scene row) the clip is launched from, -1 if unknown (0.6: its follow action group).</summary>
        public int Slot { get; set; } = -1;
    }

    /// <summary>Scene-wide settings applied when a scene launches (0.5).</summary>
    public class SceneOptions
    {
        /// <summary>Quantization of the scene launch; null uses <see cref="ISession.LaunchQuantization"/>.</summary>
        public LaunchQuantization? Quantization { get; set; }
        /// <summary>Tempo (BPM) to set on the launch tick; 0 leaves it.</summary>
        public int Tempo { get; set; }
        /// <summary>Beats per bar to set on the launch tick; 0 leaves it.</summary>
        public int BeatsPerBar { get; set; }
        /// <summary>The scene's row (0.6: for its follow actions); -1 if none.</summary>
        public int Scene { get; set; } = -1;
        /// <summary>Scene follow actions (0.6): A or B launches another scene (or stops all clips) at the follow time. Not linked.</summary>
        public FollowOptions? Follow { get; set; }
        /// <summary>Tracks the scene stops on its launch tick (0.6: Live's stop buttons in a scene's empty slots).</summary>
        public System.Collections.Generic.IReadOnlyList<ISequence>? StopTracks { get; set; }
    }

    public interface ISession : INotifyPropertyChanged
    {
        /// <summary>Session ticks since the transport started; -1 while stopped. Never wraps.</summary>
        long Clock { get; }

        /// <summary>Beats per bar for bar-based quantization (Buzz has no time signature). Default 4.</summary>
        int BeatsPerBar { get; set; }

        /// <summary>Default launch quantization. Default: one bar.</summary>
        LaunchQuantization LaunchQuantization { get; set; }

        /// <summary>Length of a quantization step in ticks at the current ticks-per-beat.</summary>
        int GetQuantizationTicks(LaunchQuantization quantization);

        /// <summary>
        /// Launches a clip on its track at the next quantization point (relaunching restarts it).
        /// Starts the transport if it is stopped; the clip then starts on the first tick.
        /// The track's arrangement is ignored from then on (see <see cref="IsArrangementOverridden"/>).
        /// </summary>
        void Launch(ISequence track, IPattern clip, LaunchOptions? options = null);

        /// <summary>Launches several clips so they all start on the same tick.</summary>
        void LaunchScene(IEnumerable<(ISequence Track, IPattern Clip)> slots, LaunchOptions? options = null);
        /// <summary>
        /// Launches clips together (0.5), each with its own options (shape, legato, gain; their
        /// mode and quantization don't apply), all on the same tick. With <paramref name="scene"/>
        /// it is a scene launch: the scene's tempo and beats per bar change on that tick (a scene
        /// with no clips still changes them), and it replaces a scene still waiting to start.
        /// Null: clips launched together without scene settings (a group slot).
        /// </summary>
        void LaunchScene(IEnumerable<(ISequence Track, IPattern Clip, LaunchOptions? Options)> slots, SceneOptions? scene);
        /// <summary>
        /// Release of a clip's launch control (0.5): Gate stops the clip on the next quantization
        /// point (released before it started: one quantization step after it starts), Repeat stops
        /// repeating and lets the clip play on; other modes ignore it.
        /// </summary>
        void Release(ISequence track, IPattern clip, LaunchOptions? options = null);

        /// <summary>Stops a track at the next quantization point, or on the next tick if immediate.
        /// Cancels a queued launch if nothing plays.</summary>
        void Stop(ISequence track, bool immediate = false);

        /// <summary>Stops every track.</summary>
        void StopAll(bool immediate = false);

        /// <summary>Clip playing on the track, or null.</summary>
        IPattern? GetPlaying(ISequence track);

        /// <summary>Clip waiting to launch on the track, or null.</summary>
        IPattern? GetQueued(ISequence track);

        ClipState GetState(ISequence track, IPattern clip);

        /// <summary>Clock value at which the playing clip started, or -1.</summary>
        long GetClipStartClock(ISequence track);

        /// <summary>Position of the playing clip in ticks, or -1.</summary>
        int GetClipPosition(ISequence track);

        /// <summary>True when a session launch has taken the track away from the arrangement.
        /// Cleared by <see cref="BackToArrangement"/> and when the transport stops.</summary>
        bool IsArrangementOverridden(ISequence track);

        bool IsAnyArrangementOverridden { get; }

        /// <summary>Stops all session clips on the next tick and gives every track back to the arrangement.</summary>
        void BackToArrangement();

        /// <summary>Raised on the UI thread when a track's playing or queued clip changes.</summary>
        event Action<ISequence>? TrackStateChanged;
    }

    /// <summary>Implemented by the host (ReBuzzCore) when it supports the session engine.</summary>
    public interface ISessionHost
    {
        ISession Session { get; }
    }
}
