// Agent server (docs/specs/agent-knowledge.md §5, K3): the audio device. audio_status reports the output (device,
// buffer, rate, latency, dropouts), audio_devices lists the devices, and audio_set changes device, buffer and
// rate with an automatic way back: if no audio flows within 10 seconds, or the dropouts spike right after, the
// previous settings are restored and the result says why. Only the device's own driver decides what it takes.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    internal static class AudioTools
    {
        /// <summary>No audio within this long after a change: the change is taken back.</summary>
        internal static TimeSpan FlowTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Audio flowing, the output is watched this long for dropouts.</summary>
        internal static TimeSpan Settle = TimeSpan.FromSeconds(1.5);
        internal static TimeSpan Poll = TimeSpan.FromMilliseconds(100);
        /// <summary>More dropouts than this while settling: a spike, the change is taken back.</summary>
        internal static int SpikeDropouts = 3;

        static IAudioControl Audio(IAppServices app, string mode) =>
            app.Audio ?? throw new ToolError($"There is no audio output to control in {mode} mode (it renders only). Use rebuzz-headless --audio, or the studio.");

        public static void Register(ToolRegistry tools, IAppServices app)
        {
            tools.Add(new ToolDef
            {
                Name = "audio_status",
                Title = "Audio status",
                Description =
                    "The audio output: whether a device is open, its name, the buffer in frames, the sample rate, the latency from a rendered sample to the speaker, the input, " +
                    "the dropouts (buffers that missed their deadline) since the app started, and the frames the device has taken (its clock). Example: audio_status().",
                Input = Object(null),
                Output = StateSchema(),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var a = Audio(app, app.Facts().Mode);
                    var s = await app.Engine("audio_status", false, a.Status, c.Cancellation);
                    return ToolResult.Ok(Line(s), State(s));
                },
            });

            tools.Add(new ToolDef
            {
                Name = "audio_devices",
                Title = "Audio devices",
                Description =
                    "The audio output devices the app can open, the one in use, and the buffer sizes and sample rates audio_set takes (rate 0 is the device's own). Example: audio_devices().",
                Input = Object(null),
                Output = Object(null,
                    Req("devices", Array(Object(null, Req("name", String("Pass to audio_set(device=…)")), Req("kind", String("The driver family")), Req("current", Boolean("In use"))), "Output devices")),
                    Req("buffer_frames", Array(Integer("Frames"), "Buffer sizes")),
                    Req("sample_rates", Array(Integer("Hz; 0 is the device's own"), "Rates")),
                    Req("buffer_and_rate_settable", Boolean("This driver takes a buffer and rate from audio_set"))),
                Annotations = ToolAnnotations.Read,
                Handler = async c =>
                {
                    var a = Audio(app, app.Facts().Mode);
                    var (devices, settable) = await app.Engine("audio_devices", false, () => (a.Devices(), a.Status().BufferAndRateSettable), c.Cancellation);
                    var o = new JsonObject
                    {
                        ["devices"] = new JsonArray(devices.Select(d => (JsonNode)new JsonObject { ["name"] = d.Name, ["kind"] = d.Kind, ["current"] = d.Current }).ToArray()),
                        ["buffer_frames"] = new JsonArray(a.Buffers.Select(b => (JsonNode)b).ToArray()),
                        ["sample_rates"] = new JsonArray(a.Rates.Select(r => (JsonNode)r).ToArray()),
                        ["buffer_and_rate_settable"] = settable,
                    };
                    var current = devices.FirstOrDefault(d => d.Current);
                    return ToolResult.Ok($"{devices.Count} output device{(devices.Count == 1 ? "" : "s")}" + (current != null ? $", in use: {current.Name}" : ", none open") + ": " +
                                         string.Join(", ", devices.Take(8).Select(d => d.Name)) + (devices.Count > 8 ? ", …" : "") + ".", o);
                },
            });

            tools.Add(new ToolDef
            {
                Name = "audio_set",
                Title = "Change the audio output",
                Description =
                    "Opens the audio output with another device, buffer size (frames) and/or sample rate (Hz; 0 is the device's own); audio_devices lists the choices. It plays on at once, so what the person hears changes. " +
                    "Auto-revert: if no audio flows within 10 seconds, or the dropouts spike in the moments after, the previous settings come back and the result says why (applied=false, reverted=true). " +
                    "Smaller buffers lower the latency and raise the risk of dropouts. Buffer and rate are for drivers that take them (audio_devices says). Annotated destructive: it can silence the output. " +
                    "Example: audio_set(buffer_frames=128).",
                Input = Object(null,
                    Opt("device", String("An output device name from audio_devices")),
                    Opt("buffer_frames", Integer("Frames per buffer (32 to 2048)", 16, 4096)),
                    Opt("sample_rate", Integer("Hz: 44100, 48000, 88200, 96000, or 0 for the device's own", 0, 384000))),
                Output = Object(null,
                    Req("applied", Boolean("The new settings are in use")),
                    Req("reverted", Boolean("The previous settings were put back")),
                    Opt("reason", String("Why it was taken back")),
                    Req("settings", Object(null, Req("device", String("Device")), Req("buffer_frames", Integer("Frames")), Req("sample_rate", Integer("Hz; 0 the device's own")))),
                    Opt("status", StateSchema())),
                Annotations = new ToolAnnotations(Destructive: true, Idempotent: true),
                Handler = async c => await Set(app, c),
            });
        }

        static async Task<ToolResult> Set(IAppServices app, ToolContext c)
        {
            var a = Audio(app, app.Facts().Mode);
            string? device = c.String("device");
            int? buffer = c.Int("buffer_frames"), rate = c.Int("sample_rate");
            if (device == null && buffer == null && rate == null) throw new ToolError("Give device, buffer_frames and/or sample_rate.");
            if (rate is int r && r != 0 && !a.Rates.Contains(r)) throw new ToolError($"sample_rate: {string.Join(", ", a.Rates)} (0 is the device's own).");
            if (buffer is int b && b < 32) throw new ToolError("buffer_frames: 32 or more.");

            var (previous, devices, settable) = await app.Engine("audio_set", false, () => (a.Current(), a.Devices(), a.Status().BufferAndRateSettable), c.Cancellation);
            if (device != null)
            {
                var hit = devices.FirstOrDefault(d => d.Name.Equals(device, StringComparison.OrdinalIgnoreCase));
                if (hit == null)
                {
                    var near = Text.Closest(device, devices.Select(d => d.Name));
                    throw new ToolError($"No output device \"{device}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") + " audio_devices lists them.");
                }
                device = hit.Name;
            }
            if ((buffer != null || rate != null) && !settable && (device == null || device.Equals(previous.Device, StringComparison.OrdinalIgnoreCase)))
                throw new ToolError("This audio driver takes its buffer and rate from its own control panel, not from audio_set. Change the device, or set them there.");
            var wanted = new AudioConfig(device ?? previous.Device, buffer ?? previous.BufferFrames, rate ?? previous.SampleRate);
            if (wanted == previous)
            {
                var same = await app.Engine("audio_set", false, a.Status, c.Cancellation);
                return Outcome(true, false, null, wanted, same, "The audio output already has these settings.");
            }

            long dropoutsBefore = 0;
            AudioState? after = null;
            string? reason = null;
            try
            {
                await app.Engine("audio_set", false, () => { dropoutsBefore = a.Status().Dropouts; a.Apply(wanted); return true; }, c.Cancellation);
            }
            catch (ToolError e)
            {
                reason = e.Message;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                reason = $"The driver refused the new settings: {e.Message}";
            }

            if (reason == null)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                long played0 = (await app.Engine("audio_set", false, a.Status, c.Cancellation)).PlayedFrames;
                bool flowing = false;
                while (clock.Elapsed < FlowTimeout)
                {
                    var s = await app.Engine("audio_set", false, a.Status, c.Cancellation);
                    after = s;
                    if (s.Open && s.SampleRate > 0 && s.PlayedFrames - played0 >= s.SampleRate / 4) { flowing = true; break; }
                    await Task.Delay(Poll, c.Cancellation);
                }
                if (!flowing)
                    reason = $"No audio flowed within {FlowTimeout.TotalSeconds:0.#} s of the change" + (after is { Open: false } ? " (the output did not open)" : "") + ".";
                else
                {
                    var settle = System.Diagnostics.Stopwatch.StartNew();
                    while (settle.Elapsed < Settle)
                    {
                        await Task.Delay(Poll, c.Cancellation);
                        after = await app.Engine("audio_set", false, a.Status, c.Cancellation);
                        long dropped = after.Dropouts - dropoutsBefore;
                        if (dropped > SpikeDropouts)
                        {
                            reason = $"{dropped} dropouts in the first {settle.Elapsed.TotalSeconds:0.#} s at {wanted.BufferFrames} frames: the output can't keep up with these settings.";
                            break;
                        }
                    }
                }
            }

            if (reason == null)
            {
                string text = $"Audio now plays through {after!.Device} at {after.SampleRate} Hz, {after.BufferFrames} frames ({after.LatencyMs:0.#} ms).";
                return Outcome(true, false, null, wanted, after, text);
            }

            // Back to what worked, whatever the caller does now.
            string revertNote = "";
            try
            {
                await app.Engine("audio_set", false, () => { a.Apply(previous); return true; }, CancellationToken.None);
                after = await app.Engine("audio_set", false, a.Status, CancellationToken.None);
            }
            catch (Exception e)
            {
                revertNote = $" Putting the previous settings back failed too: {e.Message}. Choose a device in Preferences ▸ Audio.";
            }
            return Outcome(false, true, reason, previous, after, $"Not applied: {reason} The previous settings ({previous.Device}, {previous.BufferFrames} frames) are back." + revertNote);
        }

        static ToolResult Outcome(bool applied, bool reverted, string? reason, AudioConfig settings, AudioState? status, string text)
        {
            var o = new JsonObject
            {
                ["applied"] = applied,
                ["reverted"] = reverted,
                ["settings"] = new JsonObject { ["device"] = settings.Device, ["buffer_frames"] = settings.BufferFrames, ["sample_rate"] = settings.SampleRate },
            };
            if (reason != null) o["reason"] = reason;
            if (status != null) o["status"] = State(status);
            return applied ? ToolResult.Ok(text, o) : new ToolResult { Summary = text, Structured = o, IsError = true };
        }

        static JsonObject StateSchema() => Object("The audio output",
            Req("open", Boolean("A device is open")),
            Opt("device", String("The output device")),
            Opt("input", String("The input device")),
            Req("buffer_frames", Integer("Frames per buffer")),
            Req("sample_rate", Integer("Hz")),
            Req("latency_ms", Number("From a rendered sample to the speaker")),
            Req("dropouts", Integer("Buffers that missed their deadline since the app started")),
            Req("worst_overrun_ms", Number("How far past its deadline the slowest ran")),
            Req("played_seconds", Number("Audio the device has taken: its clock")),
            Req("driver", String("The driver family")),
            Req("buffer_and_rate_settable", Boolean("audio_set can change the buffer and rate")));

        static JsonObject State(AudioState s)
        {
            var o = new JsonObject
            {
                ["open"] = s.Open,
                ["buffer_frames"] = s.BufferFrames,
                ["sample_rate"] = s.SampleRate,
                ["latency_ms"] = Math.Round(s.LatencyMs, 2),
                ["dropouts"] = s.Dropouts,
                ["worst_overrun_ms"] = Math.Round(s.WorstOverrunMs, 2),
                ["played_seconds"] = s.SampleRate > 0 ? Math.Round(s.PlayedFrames / (double)s.SampleRate, 3) : 0,
                ["driver"] = s.Driver,
                ["buffer_and_rate_settable"] = s.BufferAndRateSettable,
            };
            if (s.Device != null) o["device"] = s.Device;
            if (s.Input != null) o["input"] = s.Input;
            return o;
        }

        static string Line(AudioState s) =>
            !s.Open ? "No audio output is open." :
            $"Playing through {s.Device} at {s.SampleRate} Hz, {s.BufferFrames} frames ({s.LatencyMs:0.#} ms to the speakers); {s.Dropouts} dropout{(s.Dropouts == 1 ? "" : "s")} since start" +
            (s.Input != null ? $"; input {s.Input}" : "") + ".";
    }
}
