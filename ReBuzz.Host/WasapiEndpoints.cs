// Fork (docs/specs/ux-overhaul.md P6a-1..P6a-5): which Windows output a WASAPI mode plays through. The logic (the list, the names,
// the saved choice, the way back when an endpoint is gone, what to reopen when something changes) is plain code a Linux test
// reaches; MmWasapiEndpointSource is the thin NAudio part (MMDeviceEnumerator), Windows only.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;

namespace ReBuzz.Host
{
    /// <summary>An active render endpoint: its Windows ID (stable) and friendly name.</summary>
    internal sealed record WasapiEndpoint(string Id, string Name);

    /// <summary>The active render endpoints and the system default, and a signal when either changes.</summary>
    internal interface IWasapiEndpointSource
    {
        IReadOnlyList<WasapiEndpoint> Active();
        /// <summary>The ID of the system's default render endpoint ("" when there is none).</summary>
        string DefaultId();
        event Action Changed;
    }

    /// <summary>The device names of the WASAPI choices, and how a saved name resolves against the endpoints there are now.</summary>
    internal static class WasapiChoice
    {
        public const string Separator = " - ";
        public const string DefaultLabel = "System default (follows Windows)";

        /// <summary>What the host last did about an endpoint that went or came back, or the default changing (the Audio page shows it).</summary>
        public static string Notice { get; set; }

        /// <summary>The endpoints with the names the device list shows: the friendly name, with " (2)", " (3)" for twins (ordered by ID, so stable).</summary>
        public static IReadOnlyList<(WasapiEndpoint Endpoint, string Display)> Named(IEnumerable<WasapiEndpoint> endpoints)
        {
            var result = new List<(WasapiEndpoint, string)>();
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in endpoints.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                seen.TryGetValue(e.Name, out int n);
                seen[e.Name] = ++n;
                result.Add((e, n == 1 ? e.Name : $"{e.Name} ({n})"));
            }
            return result;
        }

        /// <summary>The device name of a mode on the system default (the bare mode name, as before endpoints) or on a named endpoint.</summary>
        public static string Name(string mode, string endpointDisplay) => endpointDisplay == null ? mode : mode + Separator + endpointDisplay;

        /// <summary>The mode a device name starts with, and the endpoint part (null: the system default).</summary>
        public static bool TryParse(string deviceName, out string mode, out string endpointDisplay)
        {
            mode = null; endpointDisplay = null;
            if (string.IsNullOrEmpty(deviceName)) return false;
            foreach (var m in new[] { WasapiModes.SharedLowLatency, WasapiModes.Shared, WasapiModes.Exclusive })
            {
                if (deviceName == m) { mode = m; return true; }
                if (deviceName.StartsWith(m + Separator, StringComparison.Ordinal))
                {
                    mode = m;
                    endpointDisplay = deviceName.Substring(m.Length + Separator.Length);
                    return true;
                }
            }
            return false;
        }

        /// <summary>What the pages show for a device name: the bare WASAPI modes say which output they follow.</summary>
        public static string Label(string deviceName) =>
            TryParse(deviceName, out var mode, out var endpoint) && endpoint == null ? mode + Separator + DefaultLabel : deviceName;

        /// <summary>What a saved WASAPI choice means now.</summary>
        /// <param name="Mode">The mode.</param>
        /// <param name="Endpoint">The endpoint to play through; null: the system default (also when the saved one is gone).</param>
        /// <param name="Display">The endpoint's name in the device list (null with the default).</param>
        /// <param name="Missing">The saved endpoint's name when it is not connected, else null.</param>
        public sealed record Resolved(string Mode, WasapiEndpoint Endpoint, string Display, string Missing);

        /// <summary>
        /// The saved choice: the device name, and the endpoint ID and name saved with it. The ID wins (the name may have changed in Windows)
        /// when the name saved with it is the one chosen; otherwise the friendly name finds the endpoint; neither: gone, the default plays.
        /// </summary>
        public static Resolved Resolve(string deviceName, string savedId, string savedName, IReadOnlyList<WasapiEndpoint> active)
        {
            if (!TryParse(deviceName, out var mode, out var wanted))
                return new Resolved(WasapiModes.Shared, null, null, null);
            if (wanted == null) return new Resolved(mode, null, null, null);
            var named = Named(active);
            if (!string.IsNullOrEmpty(savedId) && string.Equals(savedName, wanted, StringComparison.Ordinal))
            {
                var byId = named.FirstOrDefault(n => n.Endpoint.Id == savedId);
                if (byId.Endpoint != null) return new Resolved(mode, byId.Endpoint, byId.Display, null);
            }
            var byName = named.FirstOrDefault(n => string.Equals(n.Display, wanted, StringComparison.Ordinal));
            if (byName.Endpoint != null) return new Resolved(mode, byName.Endpoint, byName.Display, null);
            return new Resolved(mode, null, null, wanted);
        }

        /// <summary>The Audio page's sentence when the saved endpoint is not connected (null when it is, or the choice is not an endpoint).</summary>
        public static string MissingNote(string deviceName, IEnumerable<string> listedNames)
        {
            if (!TryParse(deviceName, out _, out var endpoint) || endpoint == null) return null;
            if (listedNames.Contains(deviceName)) return null;
            return $"{endpoint} is not connected: playing through the system default. It plays there again when it is plugged in.";
        }
    }

    /// <summary>
    /// Keeps the open device in step with the endpoints: an endpoint that went away (the system default plays instead), one that came back,
    /// and, on the system default, a change of the default. <see cref="Evaluate"/> says what to open; the host opens it (with the way back
    /// AudioSwitch gives) and calls <see cref="Opened"/>.
    /// </summary>
    internal sealed class WasapiFollower : IDisposable
    {
        readonly IWasapiEndpointSource source;
        readonly WindowsHostAudioDriverFactory factory;
        readonly Func<string> saved, playing;
        readonly Action<string, string> retarget;
        readonly TimeSpan debounce;
        Timer timer;
        string openedDefaultId;

        /// <param name="saved">The device name the settings hold.</param>
        /// <param name="playing">The device name that plays now (null: none).</param>
        /// <param name="retarget">Called (device, reason) when another device should open; may run on any thread.</param>
        public WasapiFollower(IWasapiEndpointSource source, WindowsHostAudioDriverFactory factory, Func<string> saved, Func<string> playing, Action<string, string> retarget, TimeSpan? debounce = null)
        {
            this.source = source;
            this.factory = factory;
            this.saved = saved;
            this.playing = playing;
            this.retarget = retarget;
            this.debounce = debounce ?? TimeSpan.FromMilliseconds(600);
            openedDefaultId = Safe(source.DefaultId);
            source.Changed += OnChanged;
        }

        static string Safe(Func<string> f) { try { return f() ?? ""; } catch (Exception) { return ""; } }

        void OnChanged()
        {
            // A plug event comes in bursts (state, default, properties): one check once it settles.
            var t = timer ??= new Timer(_ => Check());
            t.Change(debounce, Timeout.InfiniteTimeSpan);
        }

        /// <summary>The device to open now, with the reason, or (null, null) when what plays is right.</summary>
        public (string Device, string Reason) Evaluate()
        {
            string want = saved();
            if (!WasapiChoice.TryParse(want, out var mode, out var endpoint)) return (null, null);
            string target = want, reason = null;
            if (endpoint != null && !factory.OutputDevices().Any(d => d.Name == want))
            {
                target = factory.FallbackDevice(want) ?? WasapiChoice.Name(mode, null);
                reason = $"{endpoint} is not connected: playing through the system default.";
            }
            string now = playing();
            if (now == null) return (null, null);          // nothing open: not this class's to start
            if (now != target) return (target, reason ?? $"{want} is connected again.");
            if (WasapiChoice.TryParse(target, out _, out var ep) && ep == null && Safe(source.DefaultId) != openedDefaultId)
                return (target, "The Windows default output changed: following it.");
            return (null, null);
        }

        void Check()
        {
            try
            {
                var (device, reason) = Evaluate();
                if (device != null) retarget(device, reason);
            }
            catch (Exception) { }
        }

        /// <summary>The host opened a device (or tried to): the default it is following now.</summary>
        public void Opened() => openedDefaultId = Safe(source.DefaultId);

        public void Dispose()
        {
            source.Changed -= OnChanged;
            timer?.Dispose();
        }
    }

    /// <summary>MMDeviceEnumerator (Windows): the active render endpoints, the default, and the notifications.</summary>
    internal sealed class MmWasapiEndpointSource : IWasapiEndpointSource, IDisposable
    {
        MMDeviceEnumerator watcher;
        MMDeviceNotificationClient client;
        Action changed;

        public IReadOnlyList<WasapiEndpoint> Active()
        {
            using var enumerator = new MMDeviceEnumerator();
            var list = new List<WasapiEndpoint>();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                try { list.Add(new WasapiEndpoint(d.ID, d.FriendlyName)); }
                catch (Exception) { }
            }
            return list;
        }

        public string DefaultId()
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                return enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var d) ? d.ID : "";
            }
            catch (Exception) { return ""; }      // no active output
        }

        public event Action Changed
        {
            add
            {
                lock (this)
                {
                    changed += value;
                    if (client == null)
                    {
                        watcher = new MMDeviceEnumerator();
                        client = watcher.CreateNotificationClient(false);
                        client.DeviceStateChanged += (s, e) => Raise();
                        client.DeviceAdded += (s, e) => Raise();
                        client.DeviceRemoved += (s, e) => Raise();
                        client.DefaultDeviceChanged += (s, e) => { if (e.Flow == DataFlow.Render) Raise(); };
                    }
                }
            }
            remove { lock (this) changed -= value; }
        }

        void Raise() => changed?.Invoke();

        public void Dispose()
        {
            lock (this)
            {
                client?.Dispose();
                client = null;
                watcher?.Dispose();
                watcher = null;
            }
        }
    }
}
