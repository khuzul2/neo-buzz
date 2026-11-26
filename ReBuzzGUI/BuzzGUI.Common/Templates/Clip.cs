using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace BuzzGUI.Common.Templates
{
    [XmlType("Pattern")]
    public class Clip
    {
        [XmlAttribute]
        public string Name;

        [XmlAttribute]
        public int Length;

        public List<ClipColumn> Columns;

        public Clip() { }
        public Clip(IClip p)
        {
            Name = p.Name;
            Length = p.Length;

            Columns = new List<ClipColumn>(p.Columns.Select(c => new ClipColumn(c)));

        }
    }
}
