// Fork (docs/specs/ux-overhaul.md P2b): every view on the tokens. The Sequence view and the Machine view are dark by
// default (measured from the pixels of the window), Classic Buzz (beige machine canvas, brown arrangement) is a theme in
// Preferences ▸ Appearance, no view hard-codes a colour (the sources are read), and the type scale holds.

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Sequence;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP2bTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        sealed class Frame
        {
            public uint[] Pixels;
            public int Width, Height;
            public Color At(int x, int y) { uint p = Pixels[y * Width + x]; return Color.FromRgb((byte)(p >> 16), (byte)(p >> 8), (byte)p); }

            /// <summary>The share of pixels in a rectangle brighter than a relative luminance.</summary>
            public double ShareBrighter(Rect r, double luminance)
            {
                int n = 0, bright = 0;
                for (int y = (int)r.Top; y < (int)r.Bottom && y < Height; y++)
                    for (int x = (int)r.Left; x < (int)r.Right && x < Width; x++)
                    {
                        n++;
                        if (StudioTokens.Luminance(At(x, y)) > luminance) bright++;
                    }
                return n == 0 ? 0 : bright / (double)n;
            }
        }

        static Frame Capture(Window window)
        {
            Settle();
            var bitmap = window.CaptureRenderedFrame();
            Assert.That(bitmap, Is.Not.Null);
            using var fb = bitmap!.Lock();
            int w = fb.Size.Width, h = fb.Size.Height;
            bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            var pixels = new uint[w * h];
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, w);
                for (int x = 0; x < w; x++)
                {
                    uint p = (uint)row[x];
                    pixels[y * w + x] = rgba ? ((p & 0xFF) << 16) | (p & 0xFF00) | ((p >> 16) & 0xFF) : p & 0xFFFFFF;
                }
            }
            return new Frame { Pixels = pixels, Width = w, Height = h };
        }

        /// <summary>A song of one Neo Poly with a track, a window on it and a view's tab selected.</summary>
        static MainWindow Open(string tab)
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Settle();
            var song = Core.SongCore;
            song.CreateMachine("Neo Poly", null, "Lead", null, null, null, -1, -0.3f, 0.3f);
            var lead = song.MachinesList.Last(m => m.Name == "Lead");
            song.ConnectMachines(lead, song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.AddSequence(lead, song.Sequences.Count);
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == tab).IsSelected = true;
            Settle();
            return window;
        }

        static Rect ContentArea(Window w, Avalonia.Controls.Control view)
        {
            var o = view.TranslatePoint(new Point(0, 0), w) ?? new Point();
            return new Rect(o, view.Bounds.Size);
        }

        // ------------------------------------------------------------------ dark by default

        [AvaloniaTest]
        public void TheSequenceViewIsDarkByDefaultAndHasNoWhitePanel()
        {
            Assert.That(StudioTheme.Kind, Is.EqualTo(AppearanceTheme.Dark), "Dark is the default");
            var window = Open("Arrangement");
            try
            {
                var view = (SequenceView)window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement").Content!;
                var frame = Capture(window);
                var area = ContentArea(window, view);
                // Dark: the track area is the view surface, the ruler and the pattern list the panel's (measured, not seen).
                var tracks = view.Canvas.TranslatePoint(new Point(SequenceGeometry.HeaderWidth + 400, SequenceGeometry.RulerHeight + 200), window)!.Value;
                Assert.That(frame.At((int)tracks.X, (int)tracks.Y), Is.EqualTo(StudioTokens.SurfaceView), "the track area");
                var ruler = view.Canvas.TranslatePoint(new Point(SequenceGeometry.HeaderWidth + 600, 2), window)!.Value;
                Assert.That(frame.At((int)ruler.X, (int)ruler.Y), Is.EqualTo(StudioTokens.SurfacePanel), "the ruler");
                var list = view.PatternList.TranslatePoint(new Point(40, 200), window)!.Value;
                Assert.That(frame.At((int)list.X, (int)list.Y), Is.EqualTo(StudioTokens.SurfacePanel), "the pattern list");
                // No white panel: next to the text and the buttons nothing bright covers the view (the old list was #F7F7F3 over 120 × 800 pt).
                double bright = frame.ShareBrighter(area, 0.30);
                Assert.That(bright, Is.LessThan(0.02), $"{bright:P1} of the view is brighter than a mid grey");
                Studio.Screenshot(window, "p2b-sequence-dark");
            }
            finally
            {
                window.Close();
            }
        }

        [AvaloniaTest]
        public void TheMachineViewIsDarkByDefault()
        {
            var window = Open("Machines");
            try
            {
                var view = (MachineView)window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Machines").Content!;
                var frame = Capture(window);
                var area = ContentArea(window, view);
                Assert.That(frame.At((int)area.Right - 40, (int)area.Bottom - 40), Is.EqualTo(StudioTokens.SurfaceView), "the canvas");
                Assert.That(frame.ShareBrighter(area, 0.30), Is.LessThan(0.02), "no beige");
                Studio.Screenshot(window, "p2b-machines-dark");
            }
            finally
            {
                window.Close();
            }
        }

        // ------------------------------------------------------------------ Classic Buzz

        [AvaloniaTest]
        public void ClassicBuzzIsTheBeigeMachineCanvasAndTheBrownArrangement()
        {
            var window = Open("Machines");
            try
            {
                StudioTheme.Kind = AppearanceTheme.Classic;
                var machines = (MachineView)window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Machines").Content!;
                var frame = Capture(window);
                var area = ContentArea(window, machines);
                Assert.That(frame.At((int)area.Right - 40, (int)area.Bottom - 40), Is.EqualTo(Color.Parse("#DAD6C9")), "Buzz's machine canvas");

                window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement").IsSelected = true;
                var sequence = (SequenceView)window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement").Content!;
                frame = Capture(window);
                var ruler = sequence.Canvas.TranslatePoint(new Point(SequenceGeometry.HeaderWidth + 600, 2), window)!.Value;
                Assert.That(frame.At((int)ruler.X, (int)ruler.Y), Is.EqualTo(Color.Parse("#BBAA99")), "Buzz's ruler");
                var list = sequence.PatternList.TranslatePoint(new Point(40, 200), window)!.Value;
                Assert.That(frame.At((int)list.X, (int)list.Y), Is.EqualTo(Color.Parse("#F7F7F3")), "Buzz's pattern list");
                Studio.Screenshot(window, "p2b-sequence-classic");

                // And back: the same window draws dark again, the list and the buttons included.
                StudioTheme.Kind = AppearanceTheme.Dark;
                frame = Capture(window);
                list = sequence.PatternList.TranslatePoint(new Point(40, 200), window)!.Value;
                Assert.That(frame.At((int)list.X, (int)list.Y), Is.EqualTo(StudioTokens.SurfacePanel));
                Assert.That(((ISolidColorBrush)sequence.AddTrackButton.Background!).Color, Is.EqualTo(StudioTokens.SurfaceControl), "the corner's buttons");
            }
            finally
            {
                StudioTheme.Kind = AppearanceTheme.Dark;
                window.Close();
            }
        }

        [AvaloniaTest]
        public void PreferencesAppearanceChoosesTheTheme()
        {
            var prefs = PreferencesWindow.Open(Studio.Host, PreferencesWindow.AppearancePageName);
            try
            {
                var page = prefs.Pages.OfType<AppearancePage>().Single();
                var combo = (ComboBox)page.SettingControl("appearance.Theme");
                Settle();
                var labels = combo.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content!).ToArray();
                Assert.That(labels, Is.EqualTo(new[] { "Dark", "Classic Buzz" }), "Dark and Classic Buzz, the other no third-party name");
                Assert.That(combo.SelectedIndex, Is.EqualTo(0), "Dark is the default");

                combo.SelectedIndex = 1;
                Assert.That(StudioTheme.Kind, Is.EqualTo(AppearanceTheme.Classic), "applies at once");
                Assert.That(StudioTheme.Settings.Theme, Is.EqualTo(AppearanceTheme.Classic));
                combo.SelectedIndex = 0;
                Assert.That(StudioTheme.Kind, Is.EqualTo(AppearanceTheme.Dark));
            }
            finally
            {
                StudioTheme.Kind = AppearanceTheme.Dark;
                StudioTheme.Settings.Save();
                prefs.Close();
            }
        }

        // ------------------------------------------------------------------ contrast of the dark canvases' text and buttons

        [Test]
        public void TheDarkSequenceMeetsTheContrastFloor()
        {
            var look = SequenceLook.Dark;
            static Color C(IBrush b) => ((ISolidColorBrush)b).Color;
            Assert.That(StudioTokens.Contrast(C(look.RulerText), C(look.Ruler)), Is.GreaterThanOrEqualTo(4.5), "bar numbers on the ruler");
            Assert.That(StudioTokens.Contrast(C(look.HeaderText), C(look.Header)), Is.GreaterThanOrEqualTo(4.5), "machine names on the headers");
            Assert.That(StudioTokens.Contrast(C(look.ListText), look.PatternList), Is.GreaterThanOrEqualTo(4.5), "the pattern list");
            Assert.That(StudioTokens.Contrast(C(look.ControlText), C(look.ControlBack)), Is.GreaterThanOrEqualTo(4.5), "the corner's controls");
            Assert.That(StudioTokens.Contrast(Colors.White, C(look.ListSelected)), Is.GreaterThanOrEqualTo(4.5), "the selected pattern");
            Assert.That(StudioTokens.Contrast(C(look.ButtonBorder.Brush!), C(look.Header)), Is.GreaterThanOrEqualTo(3.0), "a button's edge is a UI part");
            foreach (var on in new[] { look.MuteOn, look.SoloOn, look.DisableOn })
                Assert.That(StudioTokens.Contrast(Colors.Black, C(on)), Is.GreaterThanOrEqualTo(4.5).Or.GreaterThanOrEqualTo(3.0), "a lit button's glyph");
            foreach (var box in look.BoxColours)
                Assert.That(StudioTokens.Contrast(StudioTokens.TextPrimary, box), Is.GreaterThanOrEqualTo(4.5), "a block's name on its colour");
            var machines = MachineLook.Dark;
            Assert.That(StudioTokens.Contrast(machines.Colour("MV Machine Text")!.Value, machines.Colour("MV Generator")!.Value), Is.GreaterThanOrEqualTo(4.5), "a generator's name");
            Assert.That(StudioTokens.Contrast(machines.Colour("MV Machine Text")!.Value, machines.Colour("MV Effect")!.Value), Is.GreaterThanOrEqualTo(4.5), "an effect's name");
            Assert.That(StudioTokens.Contrast(machines.Colour("MV Machine Text")!.Value, machines.Colour("MV Master")!.Value), Is.GreaterThanOrEqualTo(4.5), "the master's name");
            Assert.That(StudioTokens.Contrast(machines.Colour("MV Line")!.Value, machines.Colour("MV Background")!.Value), Is.GreaterThanOrEqualTo(3.0), "a wire");
        }

        // ------------------------------------------------------------------ no literal colour, the type scale

        static string StudioSources()
        {
            for (var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "ReBuzz.Studio", "Shell"))) return Path.Combine(dir.FullName, "ReBuzz.Studio");
            Assert.Inconclusive("the studio's sources aren't beside the tests");
            return null;
        }

        static readonly string[] ThemeHooks =
        {
            "StudioTokens.cs",        // the table
            "StudioTheme.cs",         // the two themes' colours
            "SessionPalette.cs",      // the Session View's colours (a clip's palette, saved in songs) and its design seed
            "PianoRoll.cs",           // code-drawn canvases with their own dark tables:
            "AmpControl.cs",          // the wire's amp and pan popup (P3 gives it the tokens with the wire's redesign)
        };

        [Test]
        public void NoViewHardCodesAColour()
        {
            var literal = new Regex(@"Color\.Parse\(|Color\.FromRgb\(\s*0x|Color\.FromArgb\(\s*0x[0-9A-Fa-f]+\s*,\s*0x|""#[0-9A-Fa-f]{3,8}""|\bBrushes\.(?!Transparent\b|Black\b|White\b)\w|\bColors\.(?!Transparent\b|Black\b|White\b)\w");
            var found = Directory.EnumerateFiles(StudioSources(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => !ThemeHooks.Contains(Path.GetFileName(f)))
                .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)).Where(t => !t.line.TrimStart().StartsWith("//") && literal.IsMatch(t.line)))
                .Select(t => $"{Path.GetFileName(t.f)}:{t.i + 1}: {t.line.Trim()}")
                .ToList();
            Assert.That(found, Is.Empty, "colours come from the tokens or the theme:\n" + string.Join("\n", found));
        }

        [Test]
        public void EveryFontSizeIsOnTheScale()
        {
            // 11 caption, 12 dense lists, 13 body, 15 title, 20 heading (§3.2): no other literal size.
            var sizes = new Regex(@"FontSize\s*=\s*(\d+(\.\d+)?)\b");
            var off = Directory.EnumerateFiles(StudioSources(), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
                .SelectMany(f => File.ReadLines(f).Select((line, i) => (f, i, line)))
                .SelectMany(t => sizes.Matches(t.line).Select(m => (t, size: double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))))
                .Where(x => !new[] { 11.0, 12, 13, 15, 20 }.Contains(x.size))
                .Select(x => $"{Path.GetFileName(x.t.f)}:{x.t.i + 1}: {x.size}")
                .ToList();
            Assert.That(off, Is.Empty, string.Join("\n", off));
        }
    }
}
