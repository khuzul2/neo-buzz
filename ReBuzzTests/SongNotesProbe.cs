using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Xml;
using System.Xml.Serialization;

namespace ReBuzzTests
{
    // Temporary probe (machine-safety.md §3.5): what WPF's RTF and Info's saved state look like.
    public class SongNotesProbe
    {
        static string InfoDll()
        {
            for (var d = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "RunTime", "Gear", "Generators", "Info.NET.dll");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        [Test, Apartment(ApartmentState.STA)]
        public void Probe()
        {
            var sb = new StringBuilder();
            var doc = new FlowDocument();
            var p1 = new Paragraph();
            p1.Inlines.Add(new Run("Song title") { FontSize = 24, FontWeight = FontWeights.Bold });
            doc.Blocks.Add(p1);
            var p2 = new Paragraph();
            p2.Inlines.Add(new Run("Plain, "));
            p2.Inlines.Add(new Bold(new Run("bold")));
            p2.Inlines.Add(new Run(", "));
            p2.Inlines.Add(new Italic(new Run("italic")));
            p2.Inlines.Add(new Run(", "));
            p2.Inlines.Add(new Underline(new Run("underlined")));
            p2.Inlines.Add(new Run(", "));
            p2.Inlines.Add(new Run("red") { Foreground = Brushes.Red });
            p2.Inlines.Add(new Run(" and ü € “quotes” 音 {braces} back\\slash"));
            doc.Blocks.Add(p2);
            var list = new List { MarkerStyle = TextMarkerStyle.Disc };
            list.ListItems.Add(new ListItem(new Paragraph(new Run("first"))));
            list.ListItems.Add(new ListItem(new Paragraph(new Run("second"))));
            doc.Blocks.Add(list);
            var numbered = new List { MarkerStyle = TextMarkerStyle.Decimal };
            numbered.ListItems.Add(new ListItem(new Paragraph(new Run("one"))));
            numbered.ListItems.Add(new ListItem(new Paragraph(new Run("two"))));
            doc.Blocks.Add(numbered);
            var p3 = new Paragraph();
            p3.Inlines.Add(new Hyperlink(new Run("ReBuzz")) { NavigateUri = new Uri("https://github.com/wasteddesign/ReBuzz") });
            p3.Inlines.Add(new LineBreak());
            p3.Inlines.Add(new Run("after a line break\tand a tab"));
            doc.Blocks.Add(p3);
            doc.Blocks.Add(new Paragraph(new Run("Small grey") { FontSize = 9, Foreground = Brushes.Gray }));

            string rtf;
            using (var ms = new MemoryStream())
            {
                new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.Rtf);
                ms.Position = 0;
                rtf = new StreamReader(ms).ReadToEnd();
            }
            sb.AppendLine("=====RTF-FLOWDOC=====");
            sb.AppendLine(rtf);

            string dll = InfoDll();
            sb.AppendLine("=====INFO DLL===== " + dll);
            Assembly asm = null;
            try
            {
                asm = Assembly.LoadFrom(dll);
                var stateType = asm.GetType("WDE.Info.InfoMachine+State", true);
                var state = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(stateType);
                stateType.GetProperty("Text").SetValue(state, rtf);
                stateType.GetProperty("Background").SetValue(state, Color.FromRgb(0x20, 0x21, 0x22));
                stateType.GetProperty("Foreground").SetValue(state, Color.FromRgb(0xE0, 0xE1, 0xE2));
                using var ms2 = new MemoryStream();
                var w = XmlWriter.Create(ms2, new XmlWriterSettings { NamespaceHandling = NamespaceHandling.OmitDuplicates, NewLineOnAttributes = false, Indent = true });
                new XmlSerializer(stateType).Serialize(w, state);
                w.Close();
                var xml = ms2.ToArray();
                sb.AppendLine("=====STATE XML BASE64=====");
                sb.AppendLine(Convert.ToBase64String(xml));
                sb.AppendLine("=====STATE XML=====");
                sb.AppendLine(Encoding.UTF8.GetString(xml));
            }
            catch (Exception e)
            {
                sb.AppendLine("STATE FAILED: " + e);
            }

            try
            {
                var boxType = asm.GetType("WDE.Info.RTFBoxInfo", true);
                var box = Activator.CreateInstance(boxType);
                boxType.GetMethod("SetRTF").Invoke(box, new object[] { rtf });
                var back = (string)boxType.GetMethod("GetRTF").Invoke(box, null);
                sb.AppendLine("=====RTF-INFOBOX=====");
                sb.AppendLine(back);
            }
            catch (Exception e)
            {
                sb.AppendLine("RTFBOX FAILED: " + e);
            }
            Assert.Fail(sb.ToString());
        }
    }
}
