// Fork (docs/specs/ux-overhaul.md P2a): the design tokens in the studio. The studio's tokens and the editor kit's palette
// are the kit's toolkit-free table (Machines/NeoBuzz.Editor/Core/Tokens.cs); the Neo editors and the manifest editors take
// their category's identity colour as their accent; Inter is the app's typeface, in the shell and the editors.

using System;
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Threading;
using NeoBuzz.Editor;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Tests
{
    public class UxFoundationTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        [Test]
        public void TheStudioAndTheKitReadOneTable()
        {
            Assert.That(StudioTokens.SurfaceBase, Is.EqualTo(StudioTokens.Of(Tokens.SurfaceBase)));
            Assert.That(StudioTokens.SurfaceView, Is.EqualTo(Color.Parse("#1B1B1E")), "the P1 values, now the kit's");
            Assert.That(StudioTokens.Accent, Is.EqualTo(Color.Parse("#4C8DF6")));
            Assert.That(StudioTokens.Play, Is.EqualTo(StudioTokens.Of(Tokens.Play)));
            Assert.That(StudioTokens.Record, Is.EqualTo(StudioTokens.Of(Tokens.Record)));
            Assert.That(StudioTokens.MinFontSize, Is.EqualTo(Tokens.FontCaption));
            // The studio builds the kit for Avalonia: its palette is the tokens (the classic WPF app keeps its own).
            Assert.That(Palette.Background, Is.EqualTo(Tokens.SurfaceBase));
            Assert.That(Palette.Panel, Is.EqualTo(Tokens.SurfaceView));
            Assert.That(Palette.Text, Is.EqualTo(Tokens.TextPrimary));
            Assert.That(Palette.DimText, Is.EqualTo(Tokens.TextSecondary));
            Assert.That(Palette.KnobTrack, Is.EqualTo(Tokens.ControlTrack));
            Assert.That(Palette.FontFamily, Is.EqualTo("Inter"));
            var classic = NeoColor.FromRgb(1, 2, 3);
            Assert.That(Tokens.Identity(MachineCategory.Drums, classic), Is.EqualTo(Tokens.Identity(MachineCategory.Drums)), "the identity, not the classic colour");
            Assert.That(StudioTokens.Identity(MachineCategory.Synth), Is.EqualTo(Color.Parse("#F34C82")));
        }

        [AvaloniaTest]
        public void InterIsTheAppsTypeface()
        {
            Assert.That(FontManager.Current.DefaultFontFamily.Name, Is.EqualTo("Inter"), "the default family: the shell's text");
            Assert.That(AvaloniaCanvas.Font.FamilyNames.First(), Is.EqualTo("Inter"), "the editors' text");
            var text = new TextBlock { Text = "Machines" };
            var window = new Window { Content = text, Width = 200, Height = 80 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var layout = text.TextLayout;
                var glyphs = layout.TextLines.SelectMany(l => l.TextRuns).OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().First().GlyphRun.GlyphTypeface;
                Assert.That(glyphs.FamilyName, Is.EqualTo("Inter"), "a text block draws in Inter");
            }
            finally
            {
                window.Close();
            }
        }

        /// <summary>An editor's accent (NeoEditor.Accent), as ARGB: each machine's assembly has its own copy of the kit.</summary>
        static uint AccentOf(Avalonia.Controls.Control editor)
        {
            for (var t = editor.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty("Accent", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (p == null) continue;
                var colour = p.GetValue(editor)!;
                return (uint)colour.GetType().GetProperty("Argb")!.GetValue(colour)!;
            }
            Assert.Fail($"{editor.GetType().Name} has no accent");
            return 0;
        }

        [AvaloniaTest]
        public void EditorsTakeTheirCategorysIdentity()
        {
            var cases = new (string Gear, MachineCategory Category)[]
            {
                ("Neo Poly", MachineCategory.Synth),
                ("Neo Delay", MachineCategory.DelayReverb),
                ("Neo Sampler", MachineCategory.Sampler),
                ("Neo Drive", MachineCategory.Distortion),
                ("Neo Filter", MachineCategory.EqFilter),
                ("Pedal SH101", MachineCategory.Synth),         // a manifest editor: "monosynth"
                ("Pedal EQ", MachineCategory.EqFilter),         // a manifest editor: "EQ"
            };
            var added = new System.Collections.Generic.List<MachineCore>();
            try
            {
                foreach (var (gear, category) in cases)
                {
                    string name = "Id " + gear;
                    Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.3f, 0.3f);
                    var m = Core.SongCore.MachinesList.First(x => x.Name == name);
                    added.Add(m);
                    Core.UiHost.OpenGui(m, null);
                    var window = StudioMachineUiHost.GuiWindowOf(m) ?? StudioMachineUiHost.EditorWindowOf(m);
                    Assert.That(window, Is.Not.Null, $"{gear}: an editor");
                    Dispatcher.UIThread.RunJobs();
                    var editor = StudioMachineUiHost.ContentOf(window!)!;
                    Assert.That(AccentOf(editor), Is.EqualTo(Tokens.Identity(category).Argb), $"{gear}: the {category} identity");
                }
            }
            finally
            {
                foreach (var m in added) Core.UiHost.ReleaseMachine(m);
                Core.SongCore.DeleteMachines(added.ToArray());
                Dispatcher.UIThread.RunJobs();
            }
        }
    }
}
