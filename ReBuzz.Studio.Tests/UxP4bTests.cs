// Fork (docs/specs/ux-overhaul.md P4b): the Light theme. Preferences ▸ Appearance: Dark, Light, System (and Classic Buzz); switching
// redraws every open window at once, with no brush of the old theme left on screen (measured from the pixels and from the brushes
// of every control); the setting is saved and restored; the contrast of the pairs the studio draws together holds in both themes.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NeoBuzz.Editor;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;
using static ReBuzz.Studio.Tests.UxP4aTests;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class UxP4bTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static Color Plain(NeoColor c) => Color.FromRgb(c.R, c.G, c.B);

        /// <summary>Puts the theme back as the tests found it: dark, the choice saved as the default.</summary>
        static void Reset(Func<bool> systemIsLight = null)
        {
            StudioTheme.SystemIsLight = systemIsLight ?? (() => false);
            StudioTheme.Settings.Set("Theme", "Dark");
            StudioTheme.Kind = AppearanceTheme.Dark;
            StudioTheme.Refresh();
            StudioTheme.Settings.Save();
            Settle();
        }

        static AppearanceTheme[] Themes => new[] { AppearanceTheme.Dark, AppearanceTheme.Light };

        // ------------------------------------------------------------------ the setting

        [AvaloniaTest]
        public void AppearanceOffersDarkLightSystemAndClassicAndAppliesAtOnce()
        {
            var prefs = PreferencesWindow.Open(Studio.Host, PreferencesWindow.AppearancePageName);
            using var restore = Studio.AfterTest(() => { prefs.Close(); Reset(); });
            var page = prefs.Pages.OfType<AppearancePage>().Single();
            var combo = (ComboBox)page.SettingControl("appearance.Theme");
            Settle();
            Assert.That(combo.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content!), Is.EqualTo(new[] { "Dark", "Light", "System", "Classic Buzz" }));
            Assert.That(combo.SelectedIndex, Is.EqualTo(0), "Dark is the default until the user decides otherwise");
            combo.SelectedIndex = 1;
            Settle();
            Assert.That(StudioTheme.Effective, Is.EqualTo(AppearanceTheme.Light), "Light applies at once");
            Assert.That(Tokens.IsLight, Is.True);
            Assert.That(StudioTokens.SurfaceView, Is.EqualTo(Plain(Tokens.Light.SurfaceView)));
            Assert.That(Application.Current!.ActualThemeVariant, Is.EqualTo(ThemeVariant.Light), "Avalonia's own controls follow");
            combo.SelectedIndex = 0;
            Settle();
            Assert.That(Tokens.IsLight, Is.False);
            Assert.That(Application.Current!.ActualThemeVariant, Is.EqualTo(ThemeVariant.Dark));
        }

        [AvaloniaTest]
        public void SystemFollowsTheOperatingSystemAndItsChanges()
        {
            using var restore = Studio.AfterTest(() => Reset());
            bool systemLight = true;
            StudioTheme.SystemIsLight = () => systemLight;
            StudioTheme.Kind = AppearanceTheme.System;
            Assert.That(StudioTheme.Effective, Is.EqualTo(AppearanceTheme.Light), "the system asks for light");
            Assert.That(Tokens.IsLight, Is.True);
            systemLight = false;
            StudioTheme.Refresh();       // the platform's colour values changed
            Settle();
            Assert.That(StudioTheme.Effective, Is.EqualTo(AppearanceTheme.Dark), "and follows it back");
            Assert.That(Tokens.IsLight, Is.False);
            Assert.That(StudioTheme.Kind, Is.EqualTo(AppearanceTheme.System), "the choice stays System");
            StudioTheme.Kind = AppearanceTheme.Dark;
            systemLight = true;
            StudioTheme.Refresh();
            Assert.That(Tokens.IsLight, Is.False, "a fixed choice ignores the system");
        }

        [AvaloniaTest]
        public void TheChoiceIsSavedAndRestored()
        {
            using var restore = Studio.AfterTest(() => Reset());
            // Preferences sets the setting by name (which marks it modified), and saving writes it to the settings store.
            StudioTheme.Settings.Set("Theme", "Light");
            StudioTheme.Settings.Save();
            Assert.That(Tokens.IsLight, Is.True);
            // A new start reads the saved value: the setting's object loads it as it is made.
            StudioTheme.Kind = AppearanceTheme.Dark;
            Assert.That(Tokens.IsLight, Is.False);
            var loaded = new AppearanceSettings();
            Assert.That(loaded.Theme, Is.EqualTo(AppearanceTheme.Light), "Light came back from the saved setting");
            Assert.That(StudioTheme.Effective, Is.EqualTo(AppearanceTheme.Light));
            Assert.That(Tokens.IsLight, Is.True, "and the tokens followed");
            StudioTheme.Settings.Set("Theme", "System");
            StudioTheme.Settings.Save();
            StudioTheme.Kind = AppearanceTheme.Dark;
            _ = new AppearanceSettings();
            Assert.That(StudioTheme.Kind, Is.EqualTo(AppearanceTheme.System), "System is saved as itself, not as what it was that day");
            StudioTheme.Settings.Set("Theme", "Classic");
            StudioTheme.Settings.Save();
            StudioTheme.Kind = AppearanceTheme.Dark;
            _ = new AppearanceSettings();
            Assert.That(StudioTheme.Kind, Is.EqualTo(AppearanceTheme.Classic), "an older saved choice still means what it did");
        }

        // ------------------------------------------------------------------ the contrast of the pairs drawn together

        [AvaloniaTest]
        [TestCase(AppearanceTheme.Dark)]
        [TestCase(AppearanceTheme.Light)]
        public void TheCanvasesAndThePaletteMeetTheContrastFloorInEachTheme(AppearanceTheme theme)
        {
            using var restore = Studio.AfterTest(() => Reset());
            StudioTheme.Kind = theme;
            Settle();
            static Color C(IBrush b) => ((ISolidColorBrush)b).Color;
            void AtLeast(Color fg, Color bg, double ratio, string what) =>
                Assert.That(StudioTokens.Contrast(fg, bg), Is.GreaterThanOrEqualTo(ratio), $"{theme}: {what}");

            var look = StudioTheme.Sequence;
            AtLeast(C(look.RulerText), C(look.Ruler), 4.5, "bar numbers on the ruler");
            AtLeast(C(look.HeaderText), C(look.Header), 4.5, "machine names on the headers");
            AtLeast(C(look.ListText), look.PatternList, 4.5, "the pattern list");
            AtLeast(C(look.ControlText), C(look.ControlBack), 4.5, "the corner's controls");
            AtLeast(Colors.White, C(look.ListSelected), 4.5, "the selected pattern");
            AtLeast(C(look.ButtonBorder.Brush!), C(look.Header), 3.0, "a button's edge is a UI part");
            AtLeast(StudioTokens.TextOn(C(look.MuteOn)), C(look.MuteOn), 4.5, "the lit mute button's glyph");
            AtLeast(StudioTokens.TextOn(C(look.SoloOn)), C(look.SoloOn), 4.5, "the lit solo button's glyph");
            AtLeast(C(look.MuteIconOn), C(look.MuteOn), 4.5, "the mute glyph as drawn");
            AtLeast(C(look.SoloTextOn), C(look.SoloOn), 4.5, "the solo text as drawn");
            AtLeast(C(look.DisableTextOn), C(look.DisableOn), 4.5, "the disable text as drawn");
            AtLeast(C(look.Line), C(look.Background), 3.0, "a track line is a UI part");
            AtLeast(C(look.Note), C(look.Background), 3.0, "an event's note mark");
            AtLeast(C(look.Play), C(look.Background), 3.0, "the play cursor");
            foreach (var box in look.BoxColours)
                AtLeast(StudioTokens.TextPrimary, box, 4.5, "a block's name on its colour");

            var machines = StudioTheme.Machines;
            var ground = machines.Colour("MV Background")!.Value;
            foreach (var kind in new[] { "MV Generator", "MV Effect", "MV Master", "MV Control", "MV Generator Mute", "MV Effect Mute" })
                AtLeast(machines.Colour("MV Machine Text")!.Value, machines.Colour(kind)!.Value, 4.5, kind + "'s name");
            AtLeast(machines.Colour("MV Line")!.Value, ground, 3.0, "a wire");
            AtLeast(machines.Colour("MV Machine Border")!.Value, ground, 3.0, "a box's edge");
            AtLeast(machines.Colour("MV Arrow")!.Value, ground, 4.5, "an arrow head");
            foreach (var kind in new[] { "MV Generator LED On", "MV Effect LED On", "MV Master LED On" })
            {
                string body = kind.Replace(" LED On", "");
                AtLeast(machines.Colour(kind)!.Value, machines.Colour(body)!.Value, 3.0, kind + " on its box");
            }
            AtLeast(C(machines.DeleteX), ground, 3.0, "the delete cross");
            AtLeast(StudioTokens.Warning, StudioTokens.SurfaceView, 4.5, "a machine's error on the view");

            // The session palette's pairs: text on every ground it is drawn on, the state chip and its marks.
            foreach (var (name, back) in new[] { ("Window", SessionPalette.Window), ("Bar", SessionPalette.Bar), ("Header", SessionPalette.Header), ("Empty", SessionPalette.Empty),
                ("Scene", SessionPalette.Scene), ("GroupHeader", SessionPalette.GroupHeader), ("ReturnHeader", SessionPalette.ReturnHeader), ("MixerBack", SessionPalette.MixerBack) })
            {
                AtLeast(C(SessionPalette.Text), C(back), 4.5, "text on the session's " + name);
                AtLeast(C(SessionPalette.LabelText), C(back), 4.5, "a label on the session's " + name);
            }
            AtLeast(C(SessionPalette.DimText), C(SessionPalette.Window), 4.5, "dim text on the session's window");
            AtLeast(C(SessionPalette.Playing), C(SessionPalette.StateChip), 3.0, "the play mark on its chip");
            AtLeast(C(SessionPalette.Record), C(SessionPalette.StateChip), 3.0, "the record mark on its chip");
            foreach (var (name, fill) in new[] { ("Mute", SessionPalette.Mute), ("Solo", SessionPalette.Solo), ("Learn", SessionPalette.Learn), ("Clip", SessionPalette.Clip) })
                AtLeast(StudioTokens.TextOn(C(fill)), C(fill), 4.5, "the text on " + name);
            foreach (var (name, line) in new[] { ("Fader", SessionPalette.Fader), ("Send", SessionPalette.Send), ("Pan", SessionPalette.Pan), ("Cue", SessionPalette.Cue),
                ("CrossA", SessionPalette.CrossA), ("CrossB", SessionPalette.CrossB), ("MeterLow", SessionPalette.MeterLow), ("MeterMid", SessionPalette.MeterMid), ("MeterHigh", SessionPalette.MeterHigh) })
                AtLeast(C(line), C(SessionPalette.MixerBack), 3.0, name + " on the mixer");

            // A machine's box: the identity tint under the name, muted or not.
            foreach (var c in Enum.GetValues<MachineCategory>())
                foreach (bool muted in new[] { false, true })
                {
                    var identity = StudioTokens.Identity(c);
                    AtLeast(StudioTokens.TextPrimary, MachineIdentity.BodyOf(identity, muted), 4.5, $"{c}'s box name (muted {muted})");
                    AtLeast(StudioTokens.TextPrimary, MachineIdentity.BandOf(identity, muted), 1.0, "band");
                }
        }

        // ------------------------------------------------------------------ a switch redraws everything, leaves nothing of the old theme

        static HashSet<uint> Rgbs(IEnumerable<NeoColor> colours) => colours.Select(c => (uint)(c.Argb & 0xFFFFFF)).ToHashSet();

        /// <summary>The surfaces of a set: what every view, panel and control face is made of (text colours are left out: Fluent's own light and dark greys can be the same value by chance).</summary>
        static HashSet<uint> Ramp(TokenSet s) => Rgbs(new[] { s.SurfaceBase, s.SurfaceView, s.SurfacePanel, s.SurfaceControl, s.SurfaceBorder });

        /// <summary>Anti-aliased text and shadows can land on a ramp colour by chance; a leaked surface or label is hundreds of pixels.</summary>
        const int Noise = 60;

        static int PixelsOf(UxP2bTests.Frame frame, HashSet<uint> colours)
        {
            int n = 0;
            foreach (var p in frame.Pixels) if (colours.Contains(p & 0xFFFFFF)) n++;
            return n;
        }

        /// <summary>Every solid colour a control of this surface holds (background, foreground, borders, fills, strokes) that is one of <paramref name="of"/>.</summary>
        static IEnumerable<string> BrushesOf(Control root, HashSet<uint> of)
        {
            foreach (var v in new Visual[] { root }.Concat(root.GetVisualDescendants()))
            {
                var held = new List<(string, IBrush)>();
                switch (v)
                {
                    case TemplatedControl t: held.Add(("Background", t.Background!)); held.Add(("Foreground", t.Foreground!)); held.Add(("BorderBrush", t.BorderBrush!)); break;
                    case Border b: held.Add(("Background", b.Background!)); held.Add(("BorderBrush", b.BorderBrush!)); break;
                    case Panel p: held.Add(("Background", p.Background!)); break;
                    case TextBlock tb: held.Add(("Foreground", tb.Foreground!)); held.Add(("Background", tb.Background!)); break;
                    case Shape s: held.Add(("Fill", s.Fill!)); held.Add(("Stroke", s.Stroke!)); break;
                }
                foreach (var (what, brush) in held)
                    if (brush is ISolidColorBrush solid && of.Contains((uint)(solid.Color.ToUInt32() & 0xFFFFFF)))
                        yield return $"{v.GetType().Name}.{what} = {solid.Color}";
            }
        }

        [AvaloniaTest]
        public void SwitchingTheThemeRedrawsEveryOpenWindowAndLeavesNoBrushOfTheOldOne()
        {
            var window = SongWith("Neo Poly", 8);
            var opened = new List<Window>();
            using var restore = Studio.AfterTest(() =>
            {
                foreach (var w in opened) { try { w.Close(); } catch (Exception) { } }
                Reset();
                Restore(window, null);
            });
            Call("machine_add", Args(("gear", "Neo Drums"), ("name", "Kit")));
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            Settle();
            var dark = Ramp(Tokens.Dark);
            var light = Ramp(Tokens.Light);
            var darkAll = Rgbs(Tokens.Dark.All());
            var lightAll = Rgbs(Tokens.Light.All());
            var problems = new List<string>();
            var names = new List<string>();
            foreach (var surface in Surfaces(window, opened))
            {
                names.Add(surface.Name);
                var inDark = UxP2bTests.Capture((Window)surface.Root);
                if (PixelsOf(inDark, light) > Noise) problems.Add($"{surface.Name}: light pixels in the dark theme before any switch");

                StudioTheme.Kind = AppearanceTheme.Light;
                Settle();
                var inLight = UxP2bTests.Capture((Window)surface.Root);
                if (Environment.GetEnvironmentVariable("THEME_SHOTS_DIR") is { } shots)
                    ((Window)surface.Root).CaptureRenderedFrame()?.Save(System.IO.Path.Combine(shots, "light-" + surface.Name.Replace('/', '-').Replace(' ', '_') + ".png"));
                int leaked = PixelsOf(inLight, dark);
                if (leaked > Noise) problems.Add($"{surface.Name}: {leaked} pixels of the dark ramp after the switch to Light");
                if (PixelsOf(inLight, light) < inLight.Width * inLight.Height / 40) problems.Add($"{surface.Name}: Light drew almost none of its own surfaces");
                // The brushes every window keeps: none holds a colour only the dark set has.
                foreach (var w in new[] { window }.Concat(opened).Where(w => w.IsVisible))
                    foreach (var held in BrushesOf(w, darkAll)) problems.Add($"{surface.Name}: {w.GetType().Name} {held} (a dark token) in the Light theme");

                StudioTheme.Kind = AppearanceTheme.Dark;
                Settle();
                var back = UxP2bTests.Capture((Window)surface.Root);
                if (PixelsOf(back, light) > Noise) problems.Add($"{surface.Name}: {PixelsOf(back, light)} pixels of the light ramp after the switch back to Dark");
                foreach (var w in new[] { window }.Concat(opened).Where(w => w.IsVisible))
                    foreach (var held in BrushesOf(w, lightAll)) problems.Add($"{surface.Name}: {w.GetType().Name} {held} (a light token) back in the Dark theme");
            }
            Assert.That(names.Count, Is.GreaterThanOrEqualTo(18), "the switch was tried on the main views, Preferences' pages, About, the export sheet, the dialogs and a parameter window: " + string.Join(", ", names));
            Assert.That(problems.Distinct().Take(60), Is.Empty, "old colours left on screen:\n  " + string.Join("\n  ", problems.Distinct().Take(60)));
        }

        [AvaloniaTest]
        public void AMachineInGearWithItsOwnCopyOfTheKitFollowsTheTheme()
        {
            // Each Neo machine compiles its own copy of the editor kit, so its Tokens is a different type from the studio's.
            using var restore = Studio.AfterTest(() => Reset());
            var dll = System.IO.Path.Combine(AppContext.BaseDirectory, "Gear", "Generators", "Neo Drums.NET.dll");
            if (!System.IO.File.Exists(dll)) Assert.Ignore("the Neo machines are not in this run's Gear folder");
            var asm = System.Reflection.Assembly.LoadFrom(dll);
            var tokens = asm.GetType("NeoBuzz.Editor.Tokens");
            Assert.That(tokens, Is.Not.Null.And.Not.EqualTo(typeof(Tokens)), "the machine's own copy");
            var isLight = tokens!.GetProperty("IsLight")!;
            StudioTheme.Kind = AppearanceTheme.Light;
            Assert.That(isLight.GetValue(null), Is.True, "told when the theme changed");
            StudioTheme.Kind = AppearanceTheme.Dark;
            Assert.That(isLight.GetValue(null), Is.False);
        }

        [AvaloniaTest]
        public void TheKitsBrushesAndTheNeoEditorsFollowTheTheme()
        {
            using var restore = Studio.AfterTest(() => Reset());
            var panel = NeoBuzz.Editor.Theme.Panel;
            var text = NeoBuzz.Editor.Theme.Text;
            var accent = NeoBuzz.Editor.Theme.Brush(Tokens.Identity(MachineCategory.Synth));
            StudioTheme.Kind = AppearanceTheme.Light;
            Assert.That(((ISolidColorBrush)panel).Color, Is.EqualTo(Plain(Tokens.Light.SurfaceView)), "a view's kept background");
            Assert.That(((ISolidColorBrush)text).Color, Is.EqualTo(Plain(Tokens.Light.TextPrimary)));
            Assert.That(((ISolidColorBrush)accent).Color, Is.EqualTo(Plain(Tokens.Light.Identity[(int)MachineCategory.Synth])), "an editor's accent, kept by the editor from before the switch");
            Assert.That(AvaloniaCanvas.ToAvalonia(Tokens.Dark.Accent), Is.EqualTo(Color.FromArgb(255, Tokens.Light.Accent.R, Tokens.Light.Accent.G, Tokens.Light.Accent.B)), "a colour a canvas kept draws as the light token");
            Assert.That(((ISolidColorBrush)AvaloniaCanvas.Brush(Tokens.Dark.SurfacePanel)!).Color, Is.EqualTo(Plain(Tokens.Light.SurfacePanel)), "the canvas' brush cache was dropped");
            StudioTheme.Kind = AppearanceTheme.Dark;
            Assert.That(((ISolidColorBrush)panel).Color, Is.EqualTo(Plain(Tokens.Dark.SurfaceView)));
            Assert.That(((ISolidColorBrush)accent).Color, Is.EqualTo(Plain(Tokens.Dark.Identity[(int)MachineCategory.Synth])));
        }

        [AvaloniaTest]
        public void ALitWireAndBoxStillShowTheLevelInLight()
        {
            // Decision 92's rule in the dark: a lit wire is 1.39 times as bright at -20 dB, 2.36 at -3 dB, a box 1.86 at -3 dB. In light the lift goes
            // towards black, so the same ratios are measured the other way round (silent over lit).
            using var restore = Studio.AfterTest(() => Reset());
            StudioTheme.Kind = AppearanceTheme.Light;
            var canvas = typeof(ReBuzz.Studio.Views.Machines.MachineCanvas);
            var litWire = canvas.GetMethod("LitWire", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var track = StudioTokens.ControlTrack;
            var arrow = StudioTokens.TextSecondary;
            double Lum(object lit) => StudioTokens.Luminance(((ISolidColorBrush)((IPen)lit.GetType().GetField("Item1")!.GetValue(lit)!).Brush!).Color);
            double silent = StudioTokens.Luminance(track);
            double quiet = Lum(litWire.Invoke(null, new object[] { 0.667, track, arrow })!);       // -20 dB on the -60..0 scale
            double loud = Lum(litWire.Invoke(null, new object[] { 0.95, track, arrow })!);
            Assert.That(silent / quiet, Is.GreaterThan(1.1), "a wire at -20 dB is darker (stronger) than a silent one");
            Assert.That(silent / loud, Is.GreaterThan(1.6), "a loud wire is much stronger than a silent one");
            Assert.That(loud, Is.LessThan(quiet));
        }
    }
}
