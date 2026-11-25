using System;
using System.ComponentModel;
using System.Xml.Serialization;

namespace BuzzGUI.Interfaces
{
    // A saved template stores the member's old name (BuzzGUI.Common.Templates): the file format keeps PlayClip.
    public enum SequenceEventType { [XmlEnum("PlayPattern")] PlayClip, Break, Mute, Thru, Off };

    public class SequenceEvent
    {
        public SequenceEvent(SequenceEvent e) { Type = e.Type; Clip = e.Clip; Span = e.Span; Offset = e.Offset; End = e.End; Gain = e.Gain; PlacementId = e.PlacementId; }
        public SequenceEvent(SequenceEventType type) { Type = type; }
        public SequenceEvent(SequenceEventType type, IClip clip) { Type = type; Clip = clip; }
        public SequenceEvent(SequenceEventType type, IClip clip, int span) { Type = type; Clip = clip; Span = span; }

        public SequenceEventType Type { get; private set; }
        public IClip Clip { get; private set; }
        public int Span { get; set; }       // = PlayedLength unless clipped by another event

        // Fork (docs/specs/session-view.md 0.5.1): an event can play part of its clip, at a
        // volume, as the Session View played it (clip start and loop markers, legato, launch
        // velocity). Song files keep these in a fork subsection; upstream ReBuzz plays the whole
        // clip at full volume.

        /// <summary>Where the clip starts playing, in ticks into it (0: its first row).</summary>
        public int Offset { get; set; }

        /// <summary>Where it stops, in ticks into the clip (exclusive); 0 for the clip's end.</summary>
        public int End { get; set; }

        /// <summary>Volume of the clip's machine while the event plays, 0..1.</summary>
        public double Gain { get; set; } = 1.0;

        /// <summary>Whether the event plays its whole clip at full volume (no fork data).</summary>
        public bool IsPlain => Offset == 0 && End == 0 && Gain == 1.0;

        // Fork (docs/specs/daw-modes.md S2): the arrangement clip the event belongs to. A clip is
        // a run of PlayClip events of one clip on one sequence with the same id: the first
        // plays from the clip's start, the next ones its loop, the last is cut at its end. Song
        // files keep it in the fork subsection; upstream ReBuzz plays each event, cut at the next.

        /// <summary>The clip's id (unique in the song), or 0: the event is a clip of its own.</summary>
        public int PlacementId { get; set; }

        /// <summary>Where the clip starts playing, within the clip as it is now.</summary>
        public int PlayedOffset => Clip == null ? 0 : Math.Clamp(Offset, 0, Math.Max(0, PlayedEnd - 1));

        /// <summary>Ticks the event plays before another event cuts it: from its offset to its end (at least 1).</summary>
        public int PlayedLength => Clip == null ? 0 : Math.Max(1, PlayedEnd - PlayedOffset);

        int PlayedEnd => End > 0 ? Math.Min(End, Clip.Length) : Clip.Length;

        /// <summary>The clip's name, with the part played and the volume when not the whole at full ("Verse 4–12 50%").</summary>
        public string Describe()
        {
            if (Clip == null) return "";
            string text = Clip.Name;
            if (Offset > 0 || (End > 0 && End < Clip.Length))
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

        IClip PlayingClip { get; }
        int PlayingClipPosition { get; }

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
        // used for live clip triggering
        void TriggerEvent(int time, SequenceEvent e, bool loop);

        IntPtr CSequence { get; }
    }
}
