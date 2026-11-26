using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using BuzzGUI.Common;
using WDE.ModernPatternEditor.Actions;

namespace WDE.ModernPatternEditor.MPEStructures
{
    internal class ClipXMLImportExport
    {
        public static void Save(Stream output, object obj)
        {
            var ws = new XmlWriterSettings() { NamespaceHandling = System.Xml.NamespaceHandling.OmitDuplicates, NewLineOnAttributes = false, Indent = true };
            var w = XmlWriter.Create(output, ws);
            try
            {
                var s = new XmlSerializer(obj.GetType());
                s.Serialize(w, obj);
            }
            catch (Exception e)
            {
                Global.Buzz.DCWriteLine("[PE] Save error: " + e.Message);
            }

            w.Close();
            output.Close();
        }

        public static XMLClips LoadClips(Stream input)
        {
            var s = new XmlSerializer(typeof(XMLClips));

            var r = XmlReader.Create(input);
            object o = null;
            try
            {
                o = s.Deserialize(r);
            }
            catch (Exception e)
            {
                Global.Buzz.DCWriteLine("[PE] Clip load error: " + e.Message);
            }
            r.Close();
            input.Close();
            var t = o as XMLClips;
            return t;
        }

        
        public static XMLClip GetExportClip(MPEClip clip)
        {
            XMLClip xClip = new XMLClip();
            xClip.Machine = clip.Clip.Machine.Name;
            xClip.Clip = clip.ClipName;
            xClip.RowsPerBeat = clip.RowsPerBeat;
            xClip.LenghtInBeats = clip.Clip.Length / ClipControl.BUZZ_TICKS_PER_BEAT;

            List<XMLColumn> columns = new List<XMLColumn>();
            foreach(var mpeColumn in clip.MPEClipColumns)
            {
                var column = new XMLColumn();
                column.Track = mpeColumn.ParamTrack;
                column.Parameter = mpeColumn.Parameter.Name;
                column.Machine = mpeColumn.Machine.Name;
                column.Events = mpeColumn.GetEvents(0, int.MaxValue).ToArray();
                column.Beats = mpeColumn.BeatRowsList.ToArray();
                columns.Add(column);
            }

            xClip.Columns = columns.ToArray();
            return xClip;
        }
    }


    // The exported file keeps its old element and attribute names (XMLClips, Clips, Clip): the name of a file that people keep.
    [XmlRoot("XMLPatterns")]
    [XmlType("XMLPatterns")]
    public class XMLClips
    {
        [XmlAttribute]
        public string Machine;
        [XmlArray("Patterns")]
        public XMLClip[] Clips { get; set; }
    }

    [XmlType("XMLPattern")]
    public class XMLClip
    {
        [XmlAttribute]
        public string Machine;
        [XmlAttribute("Pattern")]
        public string Clip;
        [XmlAttribute]
        public int RowsPerBeat;
        [XmlAttribute]
        public int LenghtInBeats;
        public XMLColumn[] Columns { get; set; }
    }

    public class XMLColumn
    {
        [XmlAttribute]
        public string Machine;
        [XmlAttribute]
        public string Parameter;
        [XmlAttribute]
        public int Track;
        public ClipEvent[] Events;
        public int[] Beats;
    }
}
