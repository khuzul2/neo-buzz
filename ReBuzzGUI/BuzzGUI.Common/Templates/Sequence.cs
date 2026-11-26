using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace BuzzGUI.Common.Templates
{
    public class Sequence
    {
        public class Event
        {
            [XmlAttribute]
            public int Time;

            [XmlAttribute]
            public SequenceEventType Type;

            [XmlAttribute("Pattern")]
            public string Clip;


            public Event() { }
            public Event(int t, SequenceEvent e)
            {
                Time = t;
                Type = e.Type;
                Clip = e.Clip != null ? e.Clip.Name : null;
            }
        }

        [XmlAttribute]
        public string Machine;

        public List<Event> Events;

        public Sequence() { }
        public Sequence(ISequence s)
        {
            Machine = s.Machine.Name;
            Events = new List<Event>(s.Events.Select(e => new Event(e.Key, e.Value)));
        }
    }
}
