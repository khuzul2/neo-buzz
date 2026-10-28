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
    public enum LaunchQuantization { None, Beat, Bar, TwoBars, FourBars, EightBars }

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
