using System;
using BuzzGUI.Interfaces;
using ReBuzz.AppViews;
using ReBuzz.Core;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md §4.3: the Windows build of BuzzGUI.Interfaces gives the WPF-typed
    /// members default bodies that ask WpfMachineApi. The app sets every hook, and the engine's
    /// classes still implement the members themselves, so machines see exactly what they did before.
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
        }

        [TestCase(typeof(MachineCore), typeof(IMachine), "get_ParameterWindow")]
        [TestCase(typeof(ReBuzzCore), typeof(IBuzz), "get_ThemeColors")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_Skin")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_SkinLED")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_SkinLEDSize")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_SkinLEDPosition")]
        [TestCase(typeof(MachineDLL), typeof(IMachineDLL), "get_TextColor")]
        public void TheEngineImplementsTheWpfMemberItself(Type engineType, Type api, string getter)
        {
            var map = engineType.GetInterfaceMap(api);
            int i = Array.FindIndex(map.InterfaceMethods, m => m.Name == getter);

            Assert.That(i, Is.GreaterThanOrEqualTo(0), getter);
            Assert.That(map.TargetMethods[i].DeclaringType, Is.EqualTo(engineType), getter);
        }
    }
}
