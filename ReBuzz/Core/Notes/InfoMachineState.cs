#nullable disable

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace ReBuzz.Core.Notes
{
    /// <summary>
    /// Fork (docs/specs/machine-safety.md §3.5): the saved state of WDE's Info machine
    /// ("Info View", <c>Gear/Generators/Info.NET.dll</c>, gear name "Info"), a WPF rich-text
    /// box that makes no sound and holds a song's notes. Songs keep it as the machine's data,
    /// which is what ReBuzz saves for any managed machine with a <c>MachineState</c> property
    /// (MachineManager.GetMachineData, ManagedMachineHost.MachineState):
    /// <code>
    /// byte    2                       the format version (MachineManager writes 2)
    /// int32   n                       the size of what follows, little-endian
    /// n bytes the State, as XmlSerializer writes WDE.Info.InfoMachine+State (UTF-8, with a BOM):
    ///         &lt;State&gt;
    ///           &lt;Text&gt;{\rtf1 …}&lt;/Text&gt;          the notes: RichTextBox's content saved as RTF
    ///           &lt;Background&gt;&lt;A/&gt;&lt;R/&gt;&lt;G/&gt;&lt;B/&gt;&lt;ScA/&gt;…&lt;/Background&gt;   System.Windows.Media.Color
    ///           &lt;Foreground&gt;…&lt;/Foreground&gt;
    ///         &lt;/State&gt;
    /// </code>
    /// Read here without the machine (off Windows it is a placeholder that keeps these bytes), so
    /// Song Notes can show the text.
    /// </summary>
    internal sealed class InfoMachineState
    {
        /// <summary>The machine's gear name (its DLL, Info.NET.dll, without ".NET.dll").</summary>
        public const string Gear = "Info";

        /// <summary>The RTF text.</summary>
        public string Rtf { get; private init; }

        /// <summary>0xAARRGGBB, when saved.</summary>
        public uint? Background { get; private init; }

        public uint? Foreground { get; private init; }

        /// <summary>The state in a machine's data, or null when it holds none (or isn't Info's).</summary>
        public static InfoMachineState Read(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            int offset = 0, length = data.Length;
            // The version byte and the size, unless the data is the XML alone.
            if (data[0] != (byte)'<' && data[0] != 0xEF && data.Length >= 5)
            {
                int size = BitConverter.ToInt32(data, 1);
                offset = 5;
                length = size >= 0 && size <= data.Length - 5 ? size : data.Length - 5;
            }
            if (length <= 0) return null;
            try
            {
                XDocument doc;
                using (var ms = new MemoryStream(data, offset, length))
                using (var reader = XmlReader.Create(ms, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                    doc = XDocument.Load(reader);
                var root = doc.Root;
                if (root == null || root.Name.LocalName != "State") return null;
                var text = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Text");
                return new InfoMachineState
                {
                    Rtf = (string)text ?? "",
                    Background = Color(root, "Background"),
                    Foreground = Color(root, "Foreground"),
                };
            }
            catch (XmlException)
            {
                return null;
            }
        }

        static uint? Color(XElement root, string name)
        {
            var e = root.Elements().FirstOrDefault(x => x.Name.LocalName == name);
            if (e == null) return null;
            byte Part(string p) =>
                byte.TryParse((string)e.Elements().FirstOrDefault(x => x.Name.LocalName == p), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte v) ? v : (byte)0;
            return (uint)Part("A") << 24 | (uint)Part("R") << 16 | (uint)Part("G") << 8 | Part("B");
        }

        /// <summary>The notes as Song Notes' Markdown (<see cref="NotesMarkdown"/>).</summary>
        public string ToMarkdown() => string.IsNullOrWhiteSpace(Rtf) ? "" :
            Rtf.TrimStart().StartsWith("{\\rtf", StringComparison.Ordinal) ? RtfToMarkdown.Convert(Rtf, Foreground) : NotesMarkdown.Escape(Rtf.Trim());

        /// <summary>
        /// The machine data an Info machine saves for <paramref name="rtf"/>: what ReBuzz on
        /// Windows writes for it (tests and fixtures).
        /// </summary>
        public static byte[] Write(string rtf, uint background = 0xFF1E1E1E, uint foreground = 0xFFE6E6E6)
        {
            XElement Color(string name, uint argb)
            {
                byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
                static string Sc(byte v)
                {
                    // ScRGB: linear light, as System.Windows.Media.Color keeps it.
                    double c = v / 255.0;
                    double lin = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
                    return ((float)lin).ToString("R", CultureInfo.InvariantCulture);
                }
                return new XElement(name,
                    new XElement("A", a), new XElement("R", r), new XElement("G", g), new XElement("B", b),
                    new XElement("ScA", ((float)(a / 255.0)).ToString("R", CultureInfo.InvariantCulture)),
                    new XElement("ScR", Sc(r)), new XElement("ScG", Sc(g)), new XElement("ScB", Sc(b)));
            }
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance", xsd = "http://www.w3.org/2001/XMLSchema";
            var root = new XElement("State",
                new XAttribute(XNamespace.Xmlns + "xsi", xsi), new XAttribute(XNamespace.Xmlns + "xsd", xsd),
                new XElement("Text", rtf), Color("Background", background), Color("Foreground", foreground));
            byte[] xml;
            using (var ms = new MemoryStream())
            {
                using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Indent = true, NamespaceHandling = NamespaceHandling.OmitDuplicates }))
                    new XDocument(root).Save(w);
                xml = ms.ToArray();
            }
            var data = new byte[5 + xml.Length];
            data[0] = 2;
            BitConverter.GetBytes(xml.Length).CopyTo(data, 1);
            xml.CopyTo(data, 5);
            return data;
        }
    }
}
