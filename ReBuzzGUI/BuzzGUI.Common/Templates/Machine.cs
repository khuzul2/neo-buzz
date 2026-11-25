using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using SevenZip.Compression.LZMA;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;

namespace BuzzGUI.Common.Templates
{
    public class Machine
    {
        [XmlAttribute]
        public string Name;

        [XmlAttribute]
        public float PositionX;

        [XmlAttribute]
        public float PositionY;

        [XmlAttribute]
        public int TrackCount;

        [XmlAttribute("PatternEditor")]
        public string ClipEditor;

        [XmlAttribute]
        public int OversampleFactor;

        [XmlAttribute]
        public int MIDIInputChannel;

        [XmlAttribute]
        public bool IsWireless;

        public Preset Preset;

        public byte[] CompressedData;

        // The names a saved template stores (the file format keeps Pattern: docs/specs/clips.md section 9).
        [XmlArray("Patterns")]
        public List<Clip> Clips;
        [XmlElement("CompressedPatternEditorData")]
        public byte[] CompressedClipEditorData;


        [XmlIgnore]
        public byte[] Data
        {
            get { return CompressedData != null ? SevenZipHelper.Decompress(CompressedData) : null; }
            set { CompressedData = value != null ? SevenZipHelper.Compress(value) : null; }
        }

        [XmlIgnore]
        public byte[] ClipEditorData
        {
            get { return CompressedClipEditorData != null ? SevenZipHelper.Decompress(CompressedClipEditorData) : null; }
            set { CompressedClipEditorData = value != null ? SevenZipHelper.Compress(value) : null; }
        }

        public Machine() { }
        public Machine(IMachine m, bool includeclips)
        {
            Name = m.Name;
            PositionX = m.Position.Item1;
            PositionY = m.Position.Item2;

            var tpg = m.ParameterGroups.Where(pg => pg.Type == ParameterGroupType.Track).FirstOrDefault();
            if (tpg != null) TrackCount = tpg.TrackCount;

            ClipEditor = m.ClipEditorDLL != null ? m.ClipEditorDLL.Name : null;
            OversampleFactor = m.OversampleFactor;
            MIDIInputChannel = m.MIDIInputChannel;
            Preset = new Preset(m, false, false);
            Data = m.Data;
            IsWireless = m.IsWireless;

            if (includeclips)
            {
                Clips = new List<Clip>(m.Clips.Select(p => new Clip(p)));
                if (m.ClipEditorDLL != null) ClipEditorData = m.ClipEditorData;
            }

        }

    }
}
