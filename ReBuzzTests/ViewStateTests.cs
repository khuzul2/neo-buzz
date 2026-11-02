using System.Linq;
using ReBuzz.AppViews;
using ReBuzz.AppViews.Session;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md S6 and S7: UI state the engine used to read from WPF statics
    /// (machine view settings, the sequence editor's pattern colours, the Session View palette) or
    /// set itself (WPF's render mode) goes through seams the app answers.
    /// </summary>
    public class ViewStateTests
    {
        [Test]
        public void TheAppAnswersTheViewState()
        {
            WpfMachineApiHooks.Install();

            Assert.That(ReBuzzCore.SoftwareRendering, Is.Not.Null);
            Assert.That(MachineViewState.ShowsEngineThreads, Is.EqualTo(BuzzGUI.MachineView.MachineView.StaticSettings.ShowEngineThreads));
            Assert.That(MachineViewState.SkinsEnabled, Is.EqualTo(BuzzGUI.MachineView.MachineView.StaticSettings.EnableSkins));
        }

        [Test]
        public void PatternColoursFollowTheSequenceEditor()
        {
            WpfMachineApiHooks.Install();

            var map = BuzzGUI.SequenceEditor.SequenceEditor.ViewSettings?.PatternAssociations;
            var colours = PatternColours.Current;

            Assert.That(colours == null, Is.EqualTo(map == null), "null while no sequence editor is up");
            if (map != null)
                Assert.That(colours!.Patterns, Is.EquivalentTo(map.Keys));
        }

        [Test]
        public void SessionPaletteFeedsTheEngineTable()
        {
            var original = SessionPalette.ColorChoices;
            try
            {
                Assert.That(original.Select(c => (c.Name, SessionPalette.Argb(c.Color))), Is.EqualTo(SessionColourChoices.Defaults),
                    "the engine's defaults are the Default theme's list");

                SessionPalette.ColorChoices = new[] { ("Teal", System.Windows.Media.Color.FromRgb(0x12, 0x34, 0x56)) };

                Assert.That(SessionColourChoices.Choices, Is.EqualTo(new[] { ("Teal", 0xFF123456u) }));
            }
            finally
            {
                SessionPalette.ColorChoices = original;
            }
            Assert.That(SessionColourChoices.Choices, Is.EqualTo(SessionColourChoices.Defaults));
        }
    }
}
