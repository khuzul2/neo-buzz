using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BuzzGUI.Interfaces;
using ReBuzz.AppViews;
using ReBuzz.Core;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md §4.3: the Windows build of BuzzGUI.Interfaces gives the WPF-typed
    /// members default bodies that ask WpfMachineApi, and the app sets every hook. MachineCore still
    /// implements ParameterWindow (S3). Since S4 the engine keeps the theme colours and skins as
    /// portable data and the hooks answer with the WPF values the engine made before.
    /// </summary>
    public class WpfMachineApiTests
    {
        [Test]
        public void InstallSetsEveryHook()
        {
            WpfMachineApiHooks.Install();

            Assert.That(WpfMachineApi.ParameterWindow, Is.Not.Null);
            Assert.That(WpfMachineApi.ThemeColors, Is.Not.Null);
            Assert.That(WpfMachineApi.Skin, Is.Not.Null);
            Assert.That(WpfMachineApi.SkinLED, Is.Not.Null);
            Assert.That(WpfMachineApi.SkinLEDSize, Is.Not.Null);
            Assert.That(WpfMachineApi.SkinLEDPosition, Is.Not.Null);
            Assert.That(WpfMachineApi.TextColor, Is.Not.Null);
            Assert.That(RawImage.Viewer, Is.Not.Null);
            Assert.That(ReBuzzCore.ThemeColorsView, Is.Not.Null);
        }

        [TestCase(typeof(MachineCore), typeof(IMachine), "get_ParameterWindow")]
        public void TheEngineImplementsTheWpfMemberItself(Type engineType, Type api, string getter)
        {
            Assert.That(TargetOf(engineType, api, getter).DeclaringType, Is.EqualTo(engineType), getter);
        }

        [TestCase(typeof(ReBuzzCore), typeof(IBuzz), "get_ThemeColors")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_Skin")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_SkinLED")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_SkinLEDSize")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_SkinLEDPosition")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_TextColor")]
        public void TheDefaultBodyAnswersForTheEngine(Type engineType, Type api, string getter)
        {
            Assert.That(TargetOf(engineType, api, getter).DeclaringType, Is.EqualTo(api), getter);
        }

        static System.Reflection.MethodInfo TargetOf(Type engineType, Type api, string getter)
        {
            var map = engineType.GetInterfaceMap(api);
            int i = Array.FindIndex(map.InterfaceMethods, m => m.Name == getter);
            Assert.That(i, Is.GreaterThanOrEqualTo(0), getter);
            return map.TargetMethods[i];
        }

        [Test]
        public void SkinMembersComeFromThePortableData()
        {
            WpfMachineApiHooks.Install();
            byte[] skin = [10, 20, 30, 0, 40, 50, 60, 99];
            var dll = new MachineDLL
            {
                SkinImage = new RawImage(2, 1, skin),
                SkinLedImage = new RawImage(3, 2, new byte[3 * 2 * 4]),
                SkinLedX = 5,
                SkinLedY = 7,
            };
            IMachineDLL api = dll;

            var image = api.Skin as BitmapSource;
            Assert.That(image, Is.Not.Null);
            Assert.That(image!.PixelWidth, Is.EqualTo(2));
            Assert.That(image.PixelHeight, Is.EqualTo(1));
            Assert.That(image.DpiX, Is.EqualTo(96));
            Assert.That(image.Format, Is.EqualTo(PixelFormats.Bgr32));
            Assert.That(image.IsFrozen, Is.True);
            var pixels = new byte[8];
            image.CopyPixels(pixels, 8, 0);
            Assert.That(new[] { pixels[0], pixels[1], pixels[2], pixels[4], pixels[5], pixels[6] }, Is.EqualTo(new byte[] { 10, 20, 30, 40, 50, 60 }));
            Assert.That(api.Skin, Is.SameAs(image), "made once");

            Assert.That((api.SkinLED as BitmapSource)?.PixelWidth, Is.EqualTo(3));
            Assert.That(api.SkinLEDSize, Is.EqualTo(new Size(3, 2)));
            Assert.That(api.SkinLEDPosition, Is.EqualTo(new Point(5, 7)));
            Assert.That(new MachineDLL() is IMachineDLL none && none.Skin == null && none.SkinLEDSize == default(Size), Is.True);
        }

        [TestCase("SkinLED")]
        [TestCase("SkinLEDSize", "Width", "Height")]
        [TestCase("SkinLEDPosition", "X", "Y")]
        public void ThemeXamlStillFindsTheSkinProperties(string property, params string[] parts)
        {
            // RunTime/Themes/*/MachineView/MachineControl.xaml binds Machine.DLL.SkinLED,
            // SkinLEDSize.Width/Height and SkinLEDPosition.X/Y on the MachineDLL object.
            var p = typeof(MachineDLL).GetProperty(property);
            Assert.That(p, Is.Not.Null, property);
            foreach (var part in parts)
                Assert.That(p!.PropertyType.GetProperty(part)?.PropertyType, Is.EqualTo(typeof(double)), property + "." + part);
        }

        [Test]
        public void ThemeColorsComeFromTheEngineTable()
        {
            using var driver = new Driver();
            driver.Start();

            var colors = ((IBuzz)driver.Core).ThemeColors;

            Assert.That(colors, Is.InstanceOf<ReBuzz.Common.UiUtils.ColIndex>());
            Assert.That(colors["MV Background"], Is.EqualTo(Color.FromRgb(0xDA, 0xD6, 0xC9)));
            Assert.That(colors["no such colour"], Is.EqualTo(Colors.Black));
            Assert.That(((IBuzz)driver.Core).ThemeColors, Is.SameAs(colors), "the same index until the theme changes");
            // Theme XAML binds Buzz.ThemeColors[...] on the ReBuzzCore object.
            Assert.That(driver.Core.ThemeColors, Is.SameAs(colors));
            Assert.That(driver.Core.GetThemeColour("MV Background"), Is.EqualTo(System.Drawing.Color.FromArgb(0xFF, 0xDA, 0xD6, 0xC9)));
        }

        [TestCase("#ff123456")]
        [TestCase("#ffABCDEF")]
        [TestCase("#ffa")]
        [TestCase("#ffab")]
        [TestCase("#ff1234")]
        [TestCase("#ff12345678")]
        [TestCase("#ffxyz123")]
        [TestCase("#ff")]
        public void HexColoursParseAsWpfColorConverter(string s)
        {
            Color? wpf;
            try { wpf = (Color)ColorConverter.ConvertFromString(s); }
            catch (Exception) { wpf = null; }   // the loader skipped any line that threw

            uint? ours;
            try { ours = ThemeColours.ParseHexColour(s); }
            catch (FormatException) { ours = null; }

            Assert.That(ours, Is.EqualTo(wpf.HasValue ? (uint)(wpf.Value.A << 24 | wpf.Value.R << 16 | wpf.Value.G << 8 | wpf.Value.B) : (uint?)null), s);
        }

        [Test]
        public void ThemeColFileOverridesTheDefaults()
        {
            string root = Path.Combine(Path.GetTempPath(), "rebuzz-theme-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Themes"));
            try
            {
                File.WriteAllLines(Path.Combine(root, "Themes", "Test.col"),
                [
                    "# a comment",
                    "MV Background\t\t102030",
                    "SE Text  405060",
                    "New Colour\tabcdef",
                    "Broken\tnot-a-colour",
                    "",
                ]);

                var table = ThemeColours.Load(root, "Test");

                Assert.That(table["MV Background"], Is.EqualTo(0xFF102030u));
                Assert.That(table["SE Text"], Is.EqualTo(0xFF405060u));
                Assert.That(table["New Colour"], Is.EqualTo(0xFFABCDEFu));
                Assert.That(table.ContainsKey("Broken"), Is.False);
                Assert.That(table["DC Text"], Is.EqualTo(0xFFC0C0C0u), "defaults stay");
                Assert.That(ThemeColours.Load(root, "<default>").Count, Is.EqualTo(ThemeColours.Defaults.Select(d => d.Name).Distinct().Count()));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }
    }
}
