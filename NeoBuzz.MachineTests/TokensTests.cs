using System.Linq;
using NeoBuzz.Editor;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The design tokens (Machines/NeoBuzz.Editor/Core/Tokens.cs, docs/specs/ux-overhaul.md P2a), toolkit-free: text readable
    /// on every surface it sits on, a control's track at 3:1, the identity colours apart from each other, from the accent and
    /// from the reserved state colours, graph series apart, the manifest types' categories, the grid. Built here without
    /// AVALONIA, as the classic WPF app builds the kit: there the palette and the editors' accents stay the classic ones.
    /// </summary>
    [TestFixture]
    public class TokensTests
    {
        static readonly NeoColor[] Surfaces = { Tokens.SurfaceBase, Tokens.SurfaceView, Tokens.SurfacePanel };

        [Test]
        public void TextIsReadableOnEverySurfaceItSitsOn()
        {
            foreach (var s in Surfaces)
            {
                Assert.That(Tokens.ContrastOf(Tokens.TextPrimary, s), Is.GreaterThanOrEqualTo(4.5), $"text on {s}");
                Assert.That(Tokens.ContrastOf(Tokens.TextSecondary, s), Is.GreaterThanOrEqualTo(4.5), $"secondary text on {s}");
                Assert.That(Tokens.ContrastOf(Tokens.TextPlaceholder, s), Is.GreaterThanOrEqualTo(4.5), $"a placeholder on {s}");
                Assert.That(Tokens.ContrastOf(Tokens.Accent, s), Is.GreaterThanOrEqualTo(3.0), $"the accent (focus) on {s}");
            }
            Assert.That(Tokens.ContrastOf(Tokens.ControlTrack, Tokens.SurfaceView), Is.GreaterThanOrEqualTo(3.0), "a knob's track: 3:1, a control's part");
            Assert.That(Tokens.IsReservedForState(Tokens.MeterNormal), Is.False, "a meter's green isn't play green");
            Assert.That(Tokens.IsReservedForState(Tokens.MeterHot), Is.False);
            Assert.That(Tokens.ContrastOf(NeoColor.FromRgb(255, 255, 255), Tokens.SelectionFill), Is.GreaterThanOrEqualTo(4.5), "white on the selection");
            foreach (var c in Tokens.StateColours.Concat(new[] { Tokens.Warning, Tokens.MeterNormal, Tokens.MeterHot }))
                Assert.That(Tokens.ContrastOf(Tokens.TextOn(c), c), Is.GreaterThanOrEqualTo(4.5), $"text on {c}");
        }

        [Test]
        public void IdentitiesAreApartFromEachOtherTheAccentAndTheStates()
        {
            var all = System.Enum.GetValues<MachineCategory>().Select(c => (c, Colour: Tokens.Identity(c))).ToArray();
            Assert.That(all.Length, Is.EqualTo(10), "the machine browser's ten categories");
            foreach (var (c, colour) in all)
            {
                Assert.That(Tokens.ContrastOf(colour, Tokens.SurfaceView), Is.GreaterThanOrEqualTo(5.0), $"{c}: readable as text on a view");
                Assert.That(Tokens.DeltaE(colour, Tokens.Accent), Is.GreaterThanOrEqualTo(24), $"{c}: not the accent");
                foreach (var s in Tokens.StateColours)
                    Assert.That(Tokens.DeltaE(colour, s), Is.GreaterThanOrEqualTo(24), $"{c}: not a state colour ({s})");
                Assert.That(Tokens.IsReservedForState(colour), Is.False);
                var partner = Tokens.Partner(colour);
                Assert.That(Tokens.DeltaE(colour, partner), Is.GreaterThanOrEqualTo(30), $"{c}: its second trace apart from it");
            }
            foreach (var (a, b) in all.SelectMany((x, i) => all.Skip(i + 1).Select(y => (x, y))))
                Assert.That(Tokens.DeltaE(a.Colour, b.Colour), Is.GreaterThanOrEqualTo(30), $"{a.c} and {b.c}");
            var series = Tokens.Series;
            for (int i = 0; i < series.Length; i++)
                for (int j = i + 1; j < series.Length; j++)
                    Assert.That(Tokens.DeltaE(series[i], series[j]), Is.GreaterThanOrEqualTo(30), $"series {i} and {j}");
        }

        [Test]
        public void ManifestTypesHaveTheBrowsersCategories()
        {
            Assert.That(Tokens.CategoryOf(true, "monosynth"), Is.EqualTo(MachineCategory.Synth));
            Assert.That(Tokens.CategoryOf(true, "polysynth"), Is.EqualTo(MachineCategory.Synth));
            Assert.That(Tokens.CategoryOf(true, "drums"), Is.EqualTo(MachineCategory.Drums));
            Assert.That(Tokens.CategoryOf(true, "triggered"), Is.EqualTo(MachineCategory.Drums));
            Assert.That(Tokens.CategoryOf(true, "sampler"), Is.EqualTo(MachineCategory.Sampler));
            Assert.That(Tokens.CategoryOf(true, "control"), Is.EqualTo(MachineCategory.Control));
            Assert.That(Tokens.CategoryOf(true, "generator (silent at its defaults)"), Is.EqualTo(MachineCategory.Synth));
            Assert.That(Tokens.CategoryOf(false, "EQ"), Is.EqualTo(MachineCategory.EqFilter));
            Assert.That(Tokens.CategoryOf(false, "filter or EQ"), Is.EqualTo(MachineCategory.EqFilter));
            Assert.That(Tokens.CategoryOf(false, "sequencer"), Is.Not.EqualTo(MachineCategory.EqFilter), "\"eq\" as a word only");
            Assert.That(Tokens.CategoryOf(false, "delay"), Is.EqualTo(MachineCategory.DelayReverb));
            Assert.That(Tokens.CategoryOf(false, "reverb"), Is.EqualTo(MachineCategory.DelayReverb));
            Assert.That(Tokens.CategoryOf(false, "dynamics"), Is.EqualTo(MachineCategory.Dynamics));
            Assert.That(Tokens.CategoryOf(false, "modulation"), Is.EqualTo(MachineCategory.Modulation));
            Assert.That(Tokens.CategoryOf(false, "distortion"), Is.EqualTo(MachineCategory.Distortion));
            Assert.That(Tokens.CategoryOf(false, "exciter"), Is.EqualTo(MachineCategory.Distortion));
            Assert.That(Tokens.CategoryOf(false, "utility"), Is.EqualTo(MachineCategory.Utility));
            Assert.That(Tokens.CategoryOf(false, null), Is.EqualTo(MachineCategory.Utility));
        }

        [Test]
        public void TheTypeScaleAndTheGrid()
        {
            Assert.That(Tokens.FontFamily, Is.EqualTo("Inter"));
            Assert.That(Tokens.FontCaption, Is.EqualTo(11), "the floor: nothing smaller");
            Assert.That(new[] { Tokens.FontCaption, Tokens.FontBody, Tokens.FontTitle, Tokens.FontHeading }, Is.Ordered.Ascending);
            Assert.That(Tokens.Space(1), Is.EqualTo(8));
            Assert.That(Tokens.Space(0.5), Is.EqualTo(Tokens.HalfGrid));
            Assert.That(Tokens.HitTarget % Tokens.Grid, Is.EqualTo(0), "a target spans whole grid steps");
        }

        [Test]
        public void TheClassicAppKeepsItsColours()
        {
            // Built without AVALONIA, as ReBuzz.exe builds the kit: WPF gets bug fixes only (ux-overhaul.md §1).
            var classic = NeoColor.FromRgb(0xFF, 0x6F, 0x52);
            Assert.That(Tokens.Identity(MachineCategory.DelayReverb, classic), Is.EqualTo(classic));
            Assert.That(Tokens.Partner(Tokens.Identity(MachineCategory.EqFilter), classic), Is.EqualTo(classic));
            var bands = new[] { classic, classic };
            Assert.That(Tokens.Bands(bands), Is.SameAs(bands));
            Assert.That(Palette.Background, Is.EqualTo(NeoColor.FromRgb(0x15, 0x17, 0x1C)), "the classic kit's background");
            Assert.That(Palette.FontFamily, Is.EqualTo("Segoe UI"));
        }
    }
}
