using ReBuzz.Agent;
using ReBuzz.Core;
using ReBuzzTests.Automation;

namespace ReBuzzTests
{
    /// <summary>
    /// docs/specs/engine-split.md S1: the engine's messages and questions go through IUserMessages
    /// (no WPF message boxes in engine code). The WPF implementation captures them during an agent
    /// call, as it did for errors.
    /// </summary>
    public class UserMessagesTests
    {
        [Test]
        public void SaveChangesQuestionGoesThroughUserMessages()
        {
            using var driver = new Driver();
            driver.Start();
            driver.Core.Modified = true;

            driver.NewFile();   // the safe answer: Cancel

            Assert.That(driver.UserMessages.Questions, Has.Count.EqualTo(1));
            Assert.That(driver.UserMessages.Questions[0].Caption, Is.EqualTo("ReBuzz"));
            Assert.That(driver.UserMessages.Questions[0].Buttons, Is.EqualTo(UserButtons.YesNoCancel));
            Assert.That(driver.Core.Modified, Is.True, "Cancel keeps the song");

            driver.UserMessages.NextAnswer = UserAnswer.No;
            driver.NewFile();

            Assert.That(driver.UserMessages.Questions, Has.Count.EqualTo(2));
            Assert.That(driver.Core.Modified, Is.False, "No discards the changes");
            driver.AssertInitialStateAfterNewFile();
        }

        [Test]
        public void KeepWaitingDefaultsToAYesNoQuestion()
        {
            var messages = new FakeUserMessages();

            bool wait = ((IUserMessages)messages).KeepWaiting("Loading is slow", "Wait more?");

            Assert.That(wait, Is.False);
            Assert.That(messages.Questions, Has.Count.EqualTo(1));
            Assert.That(messages.Questions[0].Buttons, Is.EqualTo(UserButtons.YesNo));
            messages.NextAnswer = UserAnswer.Yes;
            Assert.That(((IUserMessages)messages).KeepWaiting("Loading is slow", "Wait more?"), Is.True);
        }

        [Test]
        public void StudioMessagesAreCapturedDuringAnAgentCall()
        {
            var messages = new UserMessagesViaMessageBox();
            using var scope = NonModal.Capture();

            messages.Error("broken", "Error", new System.Exception("x"));
            messages.Info("hello", "Info");
            var save = messages.Ask("Save changes to Untitled", "ReBuzz", UserButtons.YesNoCancel);
            var skip = messages.Ask("Skip initializing Polac?", "Safe load machine?", UserButtons.YesNo);
            var ok = messages.Ask("Done", "", UserButtons.Ok);
            bool wait = messages.KeepWaiting("Loading is slow", "Wait more?");

            Assert.That(save, Is.EqualTo(UserAnswer.Cancel));
            Assert.That(skip, Is.EqualTo(UserAnswer.No));
            Assert.That(ok, Is.EqualTo(UserAnswer.Ok));
            Assert.That(wait, Is.False);
            Assert.That(scope.Messages, Is.EqualTo(new[]
            {
                "Error: broken",
                "Info: hello",
                "ReBuzz: Save changes to Untitled",
                "Safe load machine?: Skip initializing Polac?",
                "Done",
                "Wait more?: Loading is slow",
            }));
        }

        [Test]
        public void PortableAnswersCastToWpfResults()
        {
            // UiUtils.MessageBox and the WPF implementation cast between the two.
            Assert.That((int)UserButtons.Ok, Is.EqualTo((int)System.Windows.MessageBoxButton.OK));
            Assert.That((int)UserButtons.OkCancel, Is.EqualTo((int)System.Windows.MessageBoxButton.OKCancel));
            Assert.That((int)UserButtons.YesNo, Is.EqualTo((int)System.Windows.MessageBoxButton.YesNo));
            Assert.That((int)UserButtons.YesNoCancel, Is.EqualTo((int)System.Windows.MessageBoxButton.YesNoCancel));
            Assert.That((int)UserAnswer.Ok, Is.EqualTo((int)System.Windows.MessageBoxResult.OK));
            Assert.That((int)UserAnswer.Cancel, Is.EqualTo((int)System.Windows.MessageBoxResult.Cancel));
            Assert.That((int)UserAnswer.Yes, Is.EqualTo((int)System.Windows.MessageBoxResult.Yes));
            Assert.That((int)UserAnswer.No, Is.EqualTo((int)System.Windows.MessageBoxResult.No));
        }
    }
}
