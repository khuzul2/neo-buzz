using System;
using System.ComponentModel;

namespace BuzzGUI.Interfaces
{
    public enum SequenceEventType { PlayPattern, Break, Mute, Thru, Off };

    public class SequenceEvent
    {
        public SequenceEvent(SequenceEvent e) { Type = e.Type; Pattern = e.Pattern; Span = e.Span; Offset = e.Offset; End = e.End; Gain = e.Gain; }
        public SequenceEvent(SequenceEventType type) { Type = type; }
        public SequenceEvent(SequenceEventType type, IPattern pattern) { Type = type; Pattern = pattern; }
        public SequenceEvent(SequenceEventType type, IPattern pattern, int span) { Type = type; Pattern = pattern; Span = span; }

        public SequenceEventType Type { get; private set; }
        public IPattern Pattern { get; private set; }
        public int Span { get; set; }       // = PlayedLength unless clipped by another event

        // Fork (docs/specs/session-view.md 0.5.1): an event can play part of its pattern, at a
        // volume, as the Session View played it (clip start and loop markers, legato, launch
        // velocity). Song files keep these in a fork subsection; upstream ReBuzz plays the whole
        // pattern at full volume.

        /// <summary>Where the pattern starts playing, in ticks into it (0: its first row).</summary>
        public int Offset { get; set; }

        /// <summary>Where it stops, in ticks into the pattern (exclusive); 0 for the pattern's end.</summary>
        public int End { get; set; }

        /// <summary>Volume of the pattern's machine while the event plays, 0..1.</summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>Whether the event plays its whole pattern at full volume (no fork data).</summary>
        public bool IsPlain => Offset == 0 && End == 0 && Gain == 1.0;

        /// <summary>Where the pattern starts playing, within the pattern as it is now.</summary>
        public int PlayedOffset => Pattern == null ? 0 : Math.Clamp(Offset, 0, Math.Max(0, PlayedEnd - 1));

        /// <summary>Ticks the event plays before another event cuts it: from its offset to its end (at least 1).</summary>
        public int PlayedLength => Pattern == null ? 0 : Math.Max(1, PlayedEnd - PlayedOffset);

        int PlayedEnd => End > 0 ? Math.Min(End, Pattern.Length) : Pattern.Length;

        /// <summary>The pattern's name, with the part played and the volume when not the whole at full ("Verse 4–12 50%").</summary>
        public string Describe()
        {
            if (Pattern == null) return "";
            string text = Pattern.Name;
            if (Offset > 0 || (End > 0 && End < Pattern.Length))
                text += $" {PlayedOffset}–{PlayedOffset + PlayedLength}";
            if (Gain < 1.0)
                text += $" {Math.Round(Gain * 100)}%";
            return text;
        }
    }

    public interface ISequence : INotifyPropertyChanged
    {
        IMachine Machine { get; }
        ReadOnlyDictionary<int, SequenceEvent> Events { get; }

        bool IsDisabled { get; set;  }

        IPattern PlayingPattern { get; }
        int PlayingPatternPosition { get; }

        event Action<int> EventChanged;

        // fired after Insert/Delete/Clear operations instead of invalidating Events
        // needed for performance reasons
        event Action<int, int> SpanInserted;
        event Action<int, int> SpanDeleted;
        event Action<int, int> SpanCleared;

        void SetEvent(int time, SequenceEvent e);       // e == null deletes event
        void Insert(int time, int span);
        void Delete(int time, int span);
        void Clear(int time, int span);

        // trigger an event at time (ticks) overriding possible sequence event and previous call to TriggerEvent, e == null cancels (time is ignored)
        // used for live pattern triggering
        void TriggerEvent(int time, SequenceEvent e, bool loop);

        IntPtr CSequence { get; }
    }
}
