// Fork (docs/specs/native-format.md §5): a song's subsection that is an XML document (the Session View, the arrangement,
// markers, notes…) becomes JSON in the manifest: {"element":"Marker","attrs":{"name":"Intro"},"children":[…],"text":"…"}.
// The store that owns the document keeps reading and writing its XML (one schema, no second model); the file holds it as
// data you can read. A document is stored this way only if converting it back gives the same bytes; anything else
// (comments, CDATA, namespaces, mixed text, a BOM) goes in as a blob.

using System;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace ReBuzz.FileOps.Nbl
{
    internal static class NblXml
    {
        static readonly UTF8Encoding Utf8 = new(false);

        /// <summary>The document as JSON, or null when it can't be held exactly.</summary>
        public static JsonObject TryToJson(byte[] bytes)
        {
            try
            {
                if (bytes.Length == 0 || bytes[0] != (byte)'<') return null;
                var doc = XDocument.Parse(Utf8.GetString(bytes), LoadOptions.None);
                if (doc.Declaration != null || doc.Root == null || doc.Nodes().Any(n => n is XComment || n is XProcessingInstruction || n is XDocumentType)) return null;
                var json = ToJson(doc.Root);
                if (json == null) return null;
                var back = ToBytes(json);
                return back.AsSpan().SequenceEqual(bytes) ? json : null;
            }
            catch (Exception e) when (e is XmlException or InvalidOperationException or ArgumentException)
            {
                return null;
            }
        }

        public static byte[] ToBytes(JsonObject json) => Utf8.GetBytes(FromJson(json).ToString(SaveOptions.DisableFormatting));

        static JsonObject ToJson(XElement e)
        {
            if (e.Name.Namespace != XNamespace.None) return null;
            var o = new JsonObject { ["element"] = e.Name.LocalName };
            if (e.HasAttributes)
            {
                var attrs = new JsonObject();
                foreach (var a in e.Attributes())
                {
                    if (a.IsNamespaceDeclaration || a.Name.Namespace != XNamespace.None) return null;
                    attrs[a.Name.LocalName] = a.Value;
                }
                o["attrs"] = attrs;
            }
            bool hasElements = e.Elements().Any();
            if (e.Nodes().Any(n => n is XComment || n is XProcessingInstruction || n is XCData)) return null;
            if (hasElements)
            {
                if (e.Nodes().Any(n => n is XText)) return null;       // mixed content
                var kids = new JsonArray();
                foreach (var c in e.Elements())
                {
                    var j = ToJson(c);
                    if (j == null) return null;
                    kids.Add(j);
                }
                o["children"] = kids;
            }
            else if (e.Nodes().Any())
                o["text"] = e.Value;
            return o;
        }

        static XElement FromJson(JsonObject o)
        {
            var e = new XElement((string)o["element"]);
            if (o["attrs"] is JsonObject attrs)
                foreach (var a in attrs)
                    e.SetAttributeValue(a.Key, (string)a.Value);
            if (o["children"] is JsonArray kids)
                foreach (var k in kids)
                    e.Add(FromJson((JsonObject)k));
            else if (o["text"] != null)
                e.Add(new XText((string)o["text"]));
            return e;
        }
    }
}
