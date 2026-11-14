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
        /// <summary>Runs a check in the dark set and in the light one (P4b), and puts the dark set back.</summary>
        static void BothThemes(System.Action<string> check)
        {
            try
            {
                foreach (bool light in new[] { false, true })
                {
                    Tokens.SetLight(light);
                    check(light ? "light" : "dark");
                }
            }
            finally
            {
                Tokens.SetLight(false);
            }
        }

        static NeoColor[] Surfaces => new[] { Tokens.SurfaceBase, Tokens.SurfaceView, Tokens.SurfacePanel };

        [Test]
        public void TextIsReadableOnEverySurfaceItSitsOn() => BothThemes(theme =>
        {
            foreach (var s in Surfaces)
            {
                Assert.That(Tokens.ContrastOf(Tokens.TextPrimary, s), Is.GreaterThanOrEqualTo(4.5), $"{theme}: text on {s}");
                Assert.That(Tokens.ContrastOf(Tokens.TextSecondary, s), Is.GreaterThanOrEqualTo(4.5), $"{theme}: secondary text on {s}");
                Assert.That(Tokens.ContrastOf(Tokens.TextPlaceholder, s), Is.GreaterThanOrEqualTo(4.5), $"{theme}: a placeholder on {s}");
                Assert.That(Tokens.ContrastOf(Tokens.Accent, s), Is.GreaterThanOrEqualTo(3.0), $"{theme}: the accent (focus) on {s}");
            }
            Assert.That(Tokens.ContrastOf(Tokens.TextPrimary, Tokens.SurfaceControl), Is.GreaterThanOrEqualTo(4.5), $"{theme}: text on a control");
            Assert.That(Tokens.ContrastOf(Tokens.TextSecondary, Tokens.SurfaceControl), Is.GreaterThanOrEqualTo(4.5), $"{theme}: secondary text on a control");
            Assert.That(Tokens.ContrastOf(Tokens.Accent, Tokens.SurfaceControl), Is.GreaterThanOrEqualTo(3.0), $"{theme}: the accent on a control");
            Assert.That(Tokens.ContrastOf(Tokens.ControlTrack, Tokens.SurfaceView), Is.GreaterThanOrEqualTo(3.0), $"{theme}: a knob's track: 3:1, a control's part");
            Assert.That(Tokens.IsReservedForState(Tokens.MeterNormal), Is.False, $"{theme}: a meter's green isn't play green");
            Assert.That(Tokens.IsReservedForState(Tokens.MeterHot), Is.False);
            Assert.That(Tokens.ContrastOf(NeoColor.FromRgb(255, 255, 255), Tokens.SelectionFill), Is.GreaterThanOrEqualTo(4.5), $"{theme}: white on the selection");
            foreach (var c in Tokens.StateColours.Concat(new[] { Tokens.Warning, Tokens.MeterNormal, Tokens.MeterHot }))
                Assert.That(Tokens.ContrastOf(Tokens.TextOn(c), c), Is.GreaterThanOrEqualTo(4.5), $"{theme}: text on {c}");
        });

        [Test]
        public void StateMeterAndMessageColoursReadOnTheirGrounds() => BothThemes(theme =>
        {
            // The pairs the studio draws together: a state colour as an icon or a line on a view and a panel (3:1, a UI part) and as
            // text (4.5:1: Record, Override and Warning are words too), the meters on their well (the view), the problem bar's and the
            // notes' banner's text on their grounds, a link in the notes, white on the guard's red box.
            var set = Tokens.Set;
            foreach (var ground in new[] { set.SurfaceView, set.SurfacePanel })
            {
                Assert.That(Tokens.ContrastOf(set.Play, ground), Is.GreaterThanOrEqualTo(3.0), $"{theme}: play on {ground}");
                foreach (var c in new[] { set.Record, set.Override, set.Warning })
                    Assert.That(Tokens.ContrastOf(c, ground), Is.GreaterThanOrEqualTo(4.5), $"{theme}: {c} on {ground}");
                Assert.That(Tokens.ContrastOf(set.MeterNormal, ground), Is.GreaterThanOrEqualTo(3.0), $"{theme}: the meter on {ground}");
                Assert.That(Tokens.ContrastOf(set.MeterHot, ground), Is.GreaterThanOrEqualTo(3.0), $"{theme}: the hot meter on {ground}");
                Assert.That(Tokens.ContrastOf(set.Link, ground), Is.GreaterThanOrEqualTo(4.5), $"{theme}: a link on {ground}");
                Assert.That(Tokens.ContrastOf(set.ErrorText, ground), Is.GreaterThanOrEqualTo(4.5), $"{theme}: an error's text on {ground}");
            }
            Assert.That(Tokens.ContrastOf(set.ErrorText, set.SevereBack), Is.GreaterThanOrEqualTo(4.5), $"{theme}: the problem bar's text");
            Assert.That(Tokens.ContrastOf(set.TextPrimary, set.SevereBack), Is.GreaterThanOrEqualTo(4.5), $"{theme}: primary text on the severe ground");
            Assert.That(Tokens.ContrastOf(set.TextPrimary, set.NoticeBack), Is.GreaterThanOrEqualTo(4.5), $"{theme}: text on the notice ground");
            Assert.That(Tokens.ContrastOf(set.TextPrimary, set.BannerBack), Is.GreaterThanOrEqualTo(4.5), $"{theme}: text on the banner");
            Assert.That(Tokens.ContrastOf(set.Link, set.BannerBack), Is.GreaterThanOrEqualTo(4.5), $"{theme}: a link in the banner");
            Assert.That(Tokens.ContrastOf(set.Warning, set.NoticeBack), Is.GreaterThanOrEqualTo(3.0), $"{theme}: the notice's edge");
            Assert.That(Tokens.ContrastOf(set.Record, set.SevereBack), Is.GreaterThanOrEqualTo(3.0), $"{theme}: the severe edge");
            Assert.That(Tokens.ContrastOf(NeoColor.FromRgb(255, 255, 255), set.Diverged), Is.GreaterThanOrEqualTo(4.5), $"{theme}: white on the guard's red");
            Assert.That(Tokens.ContrastOf(set.Record, set.SurfaceBase), Is.GreaterThanOrEqualTo(3.0), $"{theme}: the clip light on the chip");
            Assert.That(Tokens.DeltaE(set.Record, set.Override), Is.GreaterThanOrEqualTo(24), $"{theme}: clip red and override orange apart");
            Assert.That(Tokens.DeltaE(set.Play, set.MeterNormal), Is.GreaterThanOrEqualTo(15), $"{theme}: the meter's green and play's");
        });

        [Test]
        public void IdentitiesAreApartFromEachOtherTheAccentAndTheStates() => BothThemes(theme =>
        {
            bool light = theme == "light";
            var all = System.Enum.GetValues<MachineCategory>().Select(c => (c, Colour: Tokens.Identity(c))).ToArray();
            Assert.That(all.Length, Is.EqualTo(10), "the machine browser's ten categories");
            foreach (var (c, colour) in all)
            {
                foreach (var s in Surfaces)
                    Assert.That(Tokens.ContrastOf(colour, s), Is.GreaterThanOrEqualTo(4.5), $"{theme} {c}: readable as text on {s}");
                Assert.That(Tokens.ContrastOf(colour, Tokens.SurfaceView), Is.GreaterThanOrEqualTo(light ? 4.5 : 5.0), $"{theme} {c}: readable as text on a view");
                Assert.That(Tokens.DeltaE(colour, Tokens.Accent), Is.GreaterThanOrEqualTo(24), $"{theme} {c}: not the accent");
                foreach (var s in Tokens.StateColours)
                    Assert.That(Tokens.DeltaE(colour, s), Is.GreaterThanOrEqualTo(24), $"{theme} {c}: not a state colour ({s})");
                Assert.That(Tokens.IsReservedForState(colour), Is.False);
                var partner = Tokens.Partner(colour);
                Assert.That(Tokens.DeltaE(colour, partner), Is.GreaterThanOrEqualTo(30), $"{theme} {c}: its second trace apart from it");
                Assert.That(Tokens.ContrastOf(partner, Tokens.SurfaceView), Is.GreaterThanOrEqualTo(3.0), $"{theme} {c}: the second trace on a view");
            }
            foreach (var (a, b) in all.SelectMany((x, i) => all.Skip(i + 1).Select(y => (x, y))))
                Assert.That(Tokens.DeltaE(a.Colour, b.Colour), Is.GreaterThanOrEqualTo(light ? 30 : 37), $"{theme} {a.c} and {b.c}");
            var series = Tokens.Series;
            for (int i = 0; i < series.Length; i++)
            {
                Assert.That(Tokens.ContrastOf(series[i], Tokens.SurfaceView), Is.GreaterThanOrEqualTo(3.0), $"{theme}: series {i} on a view (a line is a UI part)");
                for (int j = i + 1; j < series.Length; j++)
                    Assert.That(Tokens.DeltaE(series[i], series[j]), Is.GreaterThanOrEqualTo(30), $"{theme}: series {i} and {j}");
            }
        });

        [Test]
        public void EveryTokenIsDefinedInBothSets()
        {
            var names = TokenSet.AllNames();
            var dark = Tokens.Dark.All();
            var light = Tokens.Light.All();
            Assert.That(dark.Length, Is.EqualTo(names.Length));
            Assert.That(light.Length, Is.EqualTo(names.Length), "the light set has every token the dark one has");
            for (int i = 0; i < names.Length; i++)
            {
                Assert.That(dark[i].A, Is.EqualTo(0xFF), "dark " + names[i] + " is defined and opaque");
                Assert.That(light[i].A, Is.EqualTo(0xFF), "light " + names[i] + " is defined and opaque");
                Assert.That(light[i].Argb & 0xFFFFFF, Is.Not.EqualTo(0u).And.Not.EqualTo(0xFFFFFFu), "light " + names[i] + ": pure white and black are for text on a colour, in both themes");
                Assert.That(dark[i].Argb & 0xFFFFFF, Is.Not.EqualTo(0u).And.Not.EqualTo(0xFFFFFFu), "dark " + names[i]);
                Assert.That(light[i], Is.Not.EqualTo(dark[i]), names[i] + " has a value of its own in each theme");
            }
            Assert.That(Tokens.Light.Identity.Length, Is.EqualTo(System.Enum.GetValues<MachineCategory>().Length));
            Assert.That(Tokens.Light.Series.Length, Is.EqualTo(Tokens.Dark.Series.Length));
            // Resolve pairs a colour with its token by value: no value is in both sets, and two tokens that share a value in one set share it in the other.
            var darkValues = dark.Select(c => c.Argb).ToHashSet();
            foreach (var c in light) Assert.That(darkValues.Contains(c.Argb), Is.False, "a value only one set has: " + c);
            for (int i = 0; i < names.Length; i++)
                for (int j = i + 1; j < names.Length; j++)
                {
                    Assert.That(dark[i] == dark[j], Is.EqualTo(light[i] == light[j]), $"{names[i]} and {names[j]} are the same colour in both themes or in neither");
                }
        }

        [Test]
        public void AColourKeptFromOneThemeDrawsAsTheTokenOfTheOtherAfterASwitch()
        {
            try
            {
                var darkAccent = Tokens.Identity(MachineCategory.Synth);
                Tokens.SetLight(true);
                Assert.That(Tokens.Resolve(darkAccent), Is.EqualTo(Tokens.Identity(MachineCategory.Synth)), "an editor's cached dark accent draws as the light one");
                Assert.That(Tokens.Resolve(Tokens.SurfacePanel.WithAlpha(0x80)), Is.EqualTo(Tokens.SurfacePanel.WithAlpha(0x80)), "the alpha stays");
                Assert.That(Tokens.Resolve(NeoColor.FromRgb(255, 255, 255)), Is.EqualTo(NeoColor.FromRgb(255, 255, 255)), "white stays");
                Assert.That(Tokens.Resolve(Tokens.Dark.SurfaceView), Is.EqualTo(Tokens.Light.SurfaceView));
                // A colour an editor chose for itself: a dark panel turns pale, a bright accent darkens to 3:1 on the view, a neutral keeps its place.
                var panel = Tokens.Resolve(NeoColor.FromRgb(0x1E, 0x21, 0x27));
                Assert.That(Tokens.Luminance(panel), Is.GreaterThan(0.6), "an editor's own dark panel becomes a pale one");
                var yellow = Tokens.Resolve(NeoColor.FromRgb(0xF2, 0xB1, 0x3C));
                Assert.That(Tokens.ContrastOf(yellow, Tokens.SurfaceView), Is.GreaterThanOrEqualTo(3.0), "an editor's own bright accent reads on a light view");
                Tokens.SetLight(false);
                Assert.That(Tokens.Resolve(Tokens.Light.Accent), Is.EqualTo(Tokens.Dark.Accent), "and back");
                Assert.That(Tokens.Resolve(NeoColor.FromRgb(0x1E, 0x21, 0x27)), Is.EqualTo(NeoColor.FromRgb(0x1E, 0x21, 0x27)), "an editor's own colour is itself in dark");
            }
            finally
            {
                Tokens.SetLight(false);
            }
        }

        [Test]
        public void ChangedIsRaisedWhenTheSetChangesAndOnlyThen()
        {
            int raised = 0;
            void Count() => raised++;
            Tokens.Changed += Count;
            try
            {
                Tokens.SetLight(false);
                Assert.That(raised, Is.EqualTo(0), "no change, no event");
                Tokens.SetLight(true);
                Assert.That(raised, Is.EqualTo(1));
                Assert.That(Tokens.SurfaceView, Is.EqualTo(Tokens.Light.SurfaceView));
                Tokens.SetLight(false);
                Assert.That(raised, Is.EqualTo(2));
                Assert.That(Tokens.SurfaceView, Is.EqualTo(Tokens.Dark.SurfaceView));
            }
            finally
            {
                Tokens.Changed -= Count;
                Tokens.SetLight(false);
            }
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
