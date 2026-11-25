using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace BuzzGUI.Common.Templates
{
    [XmlType("PatternColumn")]
    public class ClipColumn
    {
        [XmlAttribute]
        public string Machine;

        [XmlAttribute]
        public string Parameter;

        [XmlAttribute]
        public long[] Events;

        [XmlIgnore]
        public IEnumerable<ClipEvent> EnumerableEvents
        {
            get
            {
                if (Events == null)
                    return Enumerable.Empty<ClipEvent>();
                else
                    return Events.SelectFromTwo((t, v) => new ClipEvent((int)t, (int)(v & 0xffffffff), (int)(v >> 32)));
            }
        }

        public ClipColumn() { }
        public ClipColumn(IClipColumn c)
        {
            if (c.Machine != null) Machine = c.Machine.Name;
            if (c.Parameter != null)
            {
                Parameter = c.Parameter.Name;
            }

            var ev = c.GetEvents(int.MinValue, int.MaxValue).SelectTwo(e => e.Time, e => e.Value + ((long)e.Duration << 32));
            if (ev.Any()) Events = ev.ToArray();

        }



    }
}
