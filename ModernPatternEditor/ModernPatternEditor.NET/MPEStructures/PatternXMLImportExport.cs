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
    internal class PatternXMLImportExport
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

        public static XMLPatterns LoadPatterns(Stream input)
        {
            var s = new XmlSerializer(typeof(XMLPatterns));

            var r = XmlReader.Create(input);
            object o = null;
            try
            {
                o = s.Deserialize(r);
            }
            catch (Exception e)
            {
                Global.Buzz.DCWriteLine("[PE] Pattern load error: " + e.Message);
            }
            r.Close();
            input.Close();
            var t = o as XMLPatterns;
            return t;
        }

        
        public static XMLPattern GetExportPattern(MPEPattern pattern)
        {
            XMLPattern xPattern = new XMLPattern();
            xPattern.Machine = pattern.Pattern.Machine.Name;
            xPattern.Pattern = pattern.PatternName;
            xPattern.RowsPerBeat = pattern.RowsPerBeat;
            xPattern.LenghtInBeats = pattern.Pattern.Length / PatternControl.BUZZ_TICKS_PER_BEAT;

            List<XMLColumn> columns = new List<XMLColumn>();
            foreach(var mpeColumn in pattern.MPEPatternColumns)
            {
                var column = new XMLColumn();
                column.Track = mpeColumn.ParamTrack;
                column.Parameter = mpeColumn.Parameter.Name;
                column.Machine = mpeColumn.Machine.Name;
                column.Events = mpeColumn.GetEvents(0, int.MaxValue).ToArray();
                column.Beats = mpeColumn.BeatRowsList.ToArray();
                columns.Add(column);
            }

            xPattern.Columns = columns.ToArray();
            return xPattern;
        }
    }


    public class XMLPatterns
    {
        [XmlAttribute]
        public string Machine;
        public XMLPattern[] Patterns { get; set; }
    }

    public class XMLPattern
    {
        [XmlAttribute]
        public string Machine;
        [XmlAttribute]
        public string Pattern;
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
        public PatternEvent[] Events;
        public int[] Beats;
    }
}
