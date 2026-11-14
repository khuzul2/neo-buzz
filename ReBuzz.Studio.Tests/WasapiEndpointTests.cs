// Fork (docs/specs/ux-overhaul.md P6a-1..P6a-5): the WASAPI endpoint choice, without a sound card: the list every mode offers, the saved
// choice (ID first, name as fallback), an unplugged endpoint, following the Windows default.

using ReBuzz.Audio;
using ReBuzz.Host;
using ReBuzz.Platform;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public class WasapiEndpointTests
    {
        sealed class FakeEndpoints : IWasapiEndpointSource
        {
            public List<WasapiEndpoint> Endpoints = new();
            public string Default = "";
            public event Action Changed;
            public IReadOnlyList<WasapiEndpoint> Active() => Endpoints.ToList();
            public string DefaultId() => Default;
            public void Raise() => Changed?.Invoke();
        }

        static readonly WasapiEndpoint Speakers = new("{0.0.0}.{aaa}", "Speakers (Realtek)"), Dac = new("{0.0.0}.{bbb}", "USB DAC (Topping)");

        static JsonRegistry Settings() => new JsonRegistry(TestTemp.Path("neobuzz-wasapiep-" + Guid.NewGuid().ToString("N") + ".json"), @"Software\ReBuzz");

        static (WindowsHostAudioDriverFactory Factory, FakeEndpoints Source, JsonRegistry Registry) Make(params WasapiEndpoint[] endpoints)
        {
            var source = new FakeEndpoints { Endpoints = endpoints.ToList(), Default = endpoints.Length > 0 ? endpoints[0].Id : "" };
            var registry = Settings();
            return (new WindowsHostAudioDriverFactory(registry, () => Array.Empty<string>(), source), source, registry);
        }

        static List<string> Names(WindowsHostAudioDriverFactory f) => f.OutputDevices().Select(d => d.Name).ToList();

        [Test]
        public void EveryModeListsTheSystemDefaultFirstThenEveryActiveEndpoint()
        {
            var (factory, _, registry) = Make(Speakers, Dac);
            Assert.That(Names(factory), Is.EqualTo(new[]
            {
                WasapiModes.SharedLowLatency, WasapiModes.SharedLowLatency + " - Speakers (Realtek)", WasapiModes.SharedLowLatency + " - USB DAC (Topping)",
                WasapiModes.Shared, WasapiModes.Shared + " - Speakers (Realtek)", WasapiModes.Shared + " - USB DAC (Topping)",
            }), "the bare mode name is the system default, first in its mode");
            Assert.That(new AudioDeviceService(new ConsoleUserMessages(), factory).ResolveDevice("").Name, Is.EqualTo(WasapiModes.SharedLowLatency), "the default of the default rule: the system default");
            registry.Write(WindowsHostAudioDriverFactory.ExclusiveKey, 1, "Settings");
            Assert.That(Names(factory), Is.EqualTo(new[] { WasapiModes.Exclusive, WasapiModes.Exclusive + " - Speakers (Realtek)", WasapiModes.Exclusive + " - USB DAC (Topping)" }));
            Assert.That(WasapiChoice.Label(WasapiModes.Exclusive), Does.EndWith("System default (follows Windows)"));
        }

        [Test]
        public void TwinNamesGetANumberAndNoEndpointsMeansTheSystemDefaultOnly()
        {
            var twin = new WasapiEndpoint("{0.0.0}.{ccc}", "Speakers (Realtek)");
            var (factory, _, _) = Make(Speakers, twin);
            var names = Names(factory);
            Assert.That(names, Does.Contain(WasapiModes.Shared + " - Speakers (Realtek)"));
            Assert.That(names, Does.Contain(WasapiModes.Shared + " - Speakers (Realtek) (2)"));
            Assert.That(names.Distinct().Count(), Is.EqualTo(names.Count));
            var (none, _, _) = Make();
            Assert.That(Names(none), Is.EqualTo(new[] { WasapiModes.SharedLowLatency, WasapiModes.Shared }));
        }

        [Test]
        public void TheChoiceIsSavedByIdWithTheNameAsFallback()
        {
            var (_, _, registry) = Make();
            string chosen = WasapiModes.Shared + " - USB DAC (Topping)";
            // The ID wins when Windows renamed the endpoint since the choice was saved.
            var renamed = new[] { new WasapiEndpoint(Dac.Id, "Topping DAC") };
            var byId = WasapiChoice.Resolve(chosen, Dac.Id, "USB DAC (Topping)", renamed);
            Assert.That(byId.Endpoint?.Id, Is.EqualTo(Dac.Id));
            Assert.That(byId.Display, Is.EqualTo("Topping DAC"));
            // No ID saved (or a stale one): the friendly name finds it.
            Assert.That(WasapiChoice.Resolve(chosen, "", "", new[] { Dac }).Endpoint?.Id, Is.EqualTo(Dac.Id));
            Assert.That(WasapiChoice.Resolve(chosen, Speakers.Id, "Speakers (Realtek)", new[] { Speakers, Dac }).Endpoint?.Id, Is.EqualTo(Dac.Id), "an ID saved for another choice isn't this one");
            // Gone: the default, with the name that is missing.
            var gone = WasapiChoice.Resolve(chosen, Dac.Id, "USB DAC (Topping)", new[] { Speakers });
            Assert.That(gone.Endpoint, Is.Null);
            Assert.That(gone.Missing, Is.EqualTo("USB DAC (Topping)"));
            Assert.That(gone.Mode, Is.EqualTo(WasapiModes.Shared));
            Assert.That(WasapiChoice.Resolve(WasapiModes.Shared, "x", "y", new[] { Dac }).Endpoint, Is.Null, "the bare mode follows the system default");
        }

        [Test]
        public void AnUnpluggedEndpointFallsBackToTheDefaultOfItsModeAndTheNoteSaysSo()
        {
            var (factory, source, registry) = Make(Speakers, Dac);
            string chosen = WasapiModes.Shared + " - USB DAC (Topping)";
            Assert.That(new AudioDeviceService(new ConsoleUserMessages(), factory).ResolveDevice(chosen).Name, Is.EqualTo(chosen));
            registry.Write("DeviceID", Dac.Id, "WASAPI");
            registry.Write("DeviceName", "USB DAC (Topping)", "WASAPI");
            source.Endpoints.Remove(Dac);
            var service = new AudioDeviceService(new ConsoleUserMessages(), factory);
            Assert.That(service.ResolveDevice(chosen).Name, Is.EqualTo(WasapiModes.Shared), "same mode, system default, not the first device of the list");
            Assert.That(WasapiChoice.MissingNote(chosen, Names(factory)), Does.Contain("USB DAC (Topping) is not connected").And.Contain("system default"));
            Assert.That(WasapiChoice.MissingNote(WasapiModes.Shared, Names(factory)), Is.Null);
            source.Endpoints.Add(Dac);
            Assert.That(WasapiChoice.MissingNote(chosen, Names(factory)), Is.Null, "plugged in again");
            Assert.That(service.ResolveDevice(chosen).Name, Is.EqualTo(chosen));
        }

        [Test]
        public void AnEndpointThatWasRenamedStillPlaysThroughItsId()
        {
            var (factory, source, registry) = Make(Speakers, Dac);
            string chosen = WasapiModes.Shared + " - USB DAC (Topping)";
            registry.Write("DeviceID", Dac.Id, "WASAPI");
            registry.Write("DeviceName", "USB DAC (Topping)", "WASAPI");
            source.Endpoints[1] = new WasapiEndpoint(Dac.Id, "Topping DAC");
            string playing = new AudioDeviceService(new ConsoleUserMessages(), factory).ResolveDevice(chosen).Name;
            Assert.That(playing, Is.EqualTo(WasapiModes.Shared + " - Topping DAC"));
        }

        [Test]
        public void TheFollowerReopensWhenAnEndpointGoesComesBackOrTheDefaultMoves()
        {
            var (factory, source, registry) = Make(Speakers, Dac);
            string chosen = WasapiModes.Shared + " - USB DAC (Topping)";
            registry.Write("DeviceID", Dac.Id, "WASAPI");
            registry.Write("DeviceName", "USB DAC (Topping)", "WASAPI");
            string saved = chosen, playing = chosen;
            using var follower = new WasapiFollower(source, factory, () => saved, () => playing, (d, r) => { }, TimeSpan.FromMilliseconds(1));
            Assert.That(follower.Evaluate().Device, Is.Null, "what plays is right");

            source.Endpoints.Remove(Dac);
            var gone = follower.Evaluate();
            Assert.That(gone.Device, Is.EqualTo(WasapiModes.Shared));
            Assert.That(gone.Reason, Does.Contain("not connected"));
            playing = WasapiModes.Shared;      // the host opened it; the saved choice stays
            Assert.That(follower.Evaluate().Device, Is.Null, "no reopening again and again");

            source.Endpoints.Add(Dac);
            Assert.That(follower.Evaluate().Device, Is.EqualTo(chosen), "plugged in again: back on it");
            playing = chosen;

            // On the system default, a change of the Windows default is followed; on a named endpoint it isn't.
            saved = playing = WasapiModes.Shared;
            follower.Opened();
            Assert.That(follower.Evaluate().Device, Is.Null);
            source.Default = Dac.Id;
            var moved = follower.Evaluate();
            Assert.That(moved.Device, Is.EqualTo(WasapiModes.Shared));
            Assert.That(moved.Reason, Does.Contain("default output changed"));
            follower.Opened();
            Assert.That(follower.Evaluate().Device, Is.Null);
            saved = playing = chosen;
            source.Default = Speakers.Id;
            Assert.That(follower.Evaluate().Device, Is.Null, "a named endpoint doesn't follow the default");

            // Not WASAPI, or nothing open: not its business.
            saved = "Focusrite USB ASIO"; playing = saved;
            Assert.That(follower.Evaluate().Device, Is.Null);
            saved = chosen; playing = null;
            Assert.That(follower.Evaluate().Device, Is.Null);
        }

        [Test]
        public void TheFollowerChecksOnceAfterABurstOfEndpointChanges()
        {
            var (factory, source, _) = Make(Speakers, Dac);
            int calls = 0;
            using var done = new ManualResetEventSlim();
            string playing = WasapiModes.Shared;
            using var follower = new WasapiFollower(source, factory, () => WasapiModes.Shared, () => playing, (d, r) => { calls++; done.Set(); }, TimeSpan.FromMilliseconds(50));
            source.Default = Dac.Id;
            for (int i = 0; i < 5; i++) source.Raise();
            Assert.That(done.Wait(5000), Is.True);
            Thread.Sleep(200);
            Assert.That(calls, Is.EqualTo(1));
        }

        [Test]
        public void TheDriverIsTheSameListAsTheAgentToolsAndPreferences()
        {
            // audio_devices, audio_set (AppBackend.AudioBackend.Devices/Apply) and Preferences' Outputs() all read engine.AudioDevices();
            // settings_get's audio.Device choices come from the same call. What is listed is what the factory lists.
            var (factory, _, _) = Make(Speakers);
            var service = new AudioDeviceService(new ConsoleUserMessages(), factory);
            Assert.That(service.GetOutputDevices().Select(d => d.Name), Is.EqualTo(Names(factory)));
        }
    }
}
