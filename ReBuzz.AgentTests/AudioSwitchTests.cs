using ReBuzz.Agent;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.AgentTests
{
    /// <summary>
    /// The way back audio_set has, shared with Preferences ▸ Audio on Windows (docs/specs/ux-overhaul.md P6, decision 3): a device that
    /// doesn't open or produces no audio is replaced by the one that worked, and the reason is told.
    /// </summary>
    [TestFixture]
    public class AudioSwitchTests
    {
        sealed class Rig : IAudioControl
        {
            public string Open = "ASIO Good";
            public HashSet<string> Silent = new(), Refuses = new();
            public List<string> Applied = new();
            public long Played;
            public AudioConfig Config = new("ASIO Good", 256, 0);
            public IReadOnlyList<int> Buffers { get; } = new[] { 256 };
            public IReadOnlyList<int> Rates { get; } = new[] { 0 };
            public AudioState Status()
            {
                if (!Silent.Contains(Open)) Played += 12000;
                return new AudioState(true, Open, null, 256, 48000, 5, 0, 0, Played, "test", false);
            }
            public IReadOnlyList<AudioDeviceInfo> Devices() => new List<AudioDeviceInfo>();
            public AudioConfig Current() => Config;
            public void Apply(AudioConfig config)
            {
                Applied.Add(config.Device);
                if (Refuses.Contains(config.Device)) throw new ToolError($"{config.Device} did not open.");
                Open = config.Device;
                Config = config;
            }
        }

        static Task<object?> Inline(Func<object?> work, CancellationToken ct) => Task.FromResult(work());

        [SetUp]
        public void Quick()
        {
            AudioSwitch.FlowTimeout = TimeSpan.FromMilliseconds(300);
            AudioSwitch.Settle = TimeSpan.FromMilliseconds(100);
            AudioSwitch.Poll = TimeSpan.FromMilliseconds(10);
        }

        [TearDown]
        public void Slow()
        {
            AudioSwitch.FlowTimeout = TimeSpan.FromSeconds(10);
            AudioSwitch.Settle = TimeSpan.FromSeconds(1.5);
            AudioSwitch.Poll = TimeSpan.FromMilliseconds(100);
        }

        [Test]
        public async Task ADeviceThatPlaysStays()
        {
            var rig = new Rig();
            var done = await AudioSwitch.Apply(rig, Inline, rig.Config, new AudioConfig("WASAPI: Shared", 256, 0), default);
            Assert.That(done.Applied, Is.True);
            Assert.That(done.Reverted, Is.False);
            Assert.That(rig.Open, Is.EqualTo("WASAPI: Shared"));
        }

        [Test]
        public async Task ADeviceThatProducesNoAudioIsReplacedByThePreviousOneAndTheReasonIsTold()
        {
            var rig = new Rig { Silent = { "WASAPI: Exclusive (other apps go silent)" } };
            var previous = rig.Config;
            var done = await AudioSwitch.Apply(rig, Inline, previous, new AudioConfig("WASAPI: Exclusive (other apps go silent)", 256, 0), default);
            Assert.That(done.Applied, Is.False);
            Assert.That(done.Reverted, Is.True);
            Assert.That(done.Reason, Does.Contain("No audio flowed"));
            Assert.That(rig.Open, Is.EqualTo("ASIO Good"), "the one that worked is back");
            Assert.That(rig.Applied, Is.EqualTo(new[] { "WASAPI: Exclusive (other apps go silent)", "ASIO Good" }));
        }

        [Test]
        public async Task ADeviceThatDoesNotOpenIsReplacedByThePreviousOne()
        {
            var rig = new Rig { Refuses = { "ASIO Broken" } };
            var done = await AudioSwitch.Apply(rig, Inline, rig.Config, new AudioConfig("ASIO Broken", 256, 0), default);
            Assert.That(done.Reverted, Is.True);
            Assert.That(done.Reason, Does.Contain("ASIO Broken did not open"));
            Assert.That(rig.Open, Is.EqualTo("ASIO Good"));
        }
    }
}
