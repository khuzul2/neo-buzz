using System;
using System.Linq;
using System.Threading;
using ReBuzz.Core;
using ReBuzz.MachineManagement;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md S2 (UI timers), S5 (keyboard), S9 (file dialogs) and S10 (build
    /// info): the engine reaches each through a seam, and the Windows implementations behave as the
    /// WPF code they replace.
    /// </summary>
    public class EngineSeamTests
    {
        [Test]
        public void MidiActivityTimerGoesThroughTheDispatcher()
        {
            using var driver = new Driver();
            driver.Start();

            driver.Core.MIDIActivity = true;
            var timer = driver.Dispatcher.Timers.Single(t => t.Interval == TimeSpan.FromSeconds(0.5));
            Assert.That(timer.IsEnabled, Is.True);

            timer.Fire();

            Assert.That(driver.Core.MIDIActivity, Is.False);
            Assert.That(timer.IsEnabled, Is.False);
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void WpfTimerKeepsDispatcherTimerSettings()
        {
            var timer = new WindowsGuiDispatcher().CreateTimer(TimeSpan.FromMilliseconds(40), () => { });

            Assert.That(timer.Interval, Is.EqualTo(TimeSpan.FromMilliseconds(40)));
            Assert.That(timer.IsEnabled, Is.False);
            timer.Start();
            Assert.That(timer.IsEnabled, Is.True);
            timer.Interval = TimeSpan.FromMilliseconds(80);
            Assert.That(timer.Interval, Is.EqualTo(TimeSpan.FromMilliseconds(80)));
            timer.Stop();
            Assert.That(timer.IsEnabled, Is.False);
        }

        [Test]
        public void DefaultTimerTicksThroughBeginInvoke()
        {
            var dispatcher = new MinimalDispatcher();
            int ticks = 0;
            var timer = ((IUiDispatcher)dispatcher).CreateTimer(TimeSpan.FromMilliseconds(10), () => Interlocked.Increment(ref ticks));
            Thread.Sleep(50);
            Assert.That(timer.IsEnabled, Is.False);
            Assert.That(Volatile.Read(ref ticks), Is.Zero, "a new timer is stopped");

            timer.Start();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref ticks) < 3 && DateTime.UtcNow < deadline)
                Thread.Sleep(10);
            timer.Stop();
            Thread.Sleep(50);
            int afterStop = Volatile.Read(ref ticks);
            Thread.Sleep(100);

            Assert.That(afterStop, Is.GreaterThanOrEqualTo(3));
            Assert.That(Volatile.Read(ref ticks), Is.EqualTo(afterStop), "no tick after Stop");
            Assert.That(dispatcher.BeginInvokes, Is.GreaterThanOrEqualTo(3));
        }

        [Test]
        public void KeyboardModifiersDefaultAsksEachKey()
        {
            IKeyboard keyboard = new HeldKeys(KeyModifiers.Control | KeyModifiers.Alt);
            Assert.That(keyboard.Modifiers, Is.EqualTo(KeyModifiers.Control | KeyModifiers.Alt));
            Assert.That(((IKeyboard)new FakeKeyboard()).Modifiers, Is.EqualTo(KeyModifiers.None));
        }

        [Test]
        public void KeyModifiersHaveWpfValues()
        {
            Assert.That((int)KeyModifiers.Alt, Is.EqualTo((int)System.Windows.Input.ModifierKeys.Alt));
            Assert.That((int)KeyModifiers.Control, Is.EqualTo((int)System.Windows.Input.ModifierKeys.Control));
            Assert.That((int)KeyModifiers.Shift, Is.EqualTo((int)System.Windows.Input.ModifierKeys.Shift));
            Assert.That((int)KeyModifiers.Windows, Is.EqualTo((int)System.Windows.Input.ModifierKeys.Windows));
        }

        [Test]
        public void ImportSongAsksTheLoadFileChoice()
        {
            using var driver = new Driver();
            driver.Start();
            int asked = 0;

            driver.DawCommands.ImportSong(() =>
            {
                asked++;
                return ChosenValue<string>.Nothing;
            });

            Assert.That(asked, Is.EqualTo(1));
        }

        [Test]
        public void BuildInfoMatchesTheResources()
        {
            Assert.That(BuildInfo.Number, Is.EqualTo(ReBuzz.Properties.Resources.BuildNumber));
            Assert.That(BuildInfo.Date, Is.EqualTo(ReBuzz.Properties.Resources.BuildDate));
            Assert.That(ReBuzzCore.buildNumber, Is.EqualTo(int.Parse(ReBuzz.Properties.Resources.BuildNumber)));
        }

        sealed class MinimalDispatcher : IUiDispatcher
        {
            int beginInvokes;
            public int BeginInvokes => beginInvokes;
            public void Invoke(Action action) => action();
            public void BeginInvoke(Action action)
            {
                Interlocked.Increment(ref beginInvokes);
                action();
            }
            public void BeginInvoke(Action action, UiPriority priority) => BeginInvoke(action);
        }

        sealed class HeldKeys(KeyModifiers held) : IKeyboard
        {
            public bool HasModifierKeyPressed(Enum modifierKey) => modifierKey is KeyModifiers m && held.HasFlag(m);
        }
    }
}
