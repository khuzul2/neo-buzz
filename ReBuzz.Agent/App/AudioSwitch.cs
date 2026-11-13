// Fork (docs/specs/ux-overhaul.md P6): changing the audio output with a way back, as audio_set does it (agent-knowledge.md §5,
// K3), shared so Preferences ▸ Audio on Windows follows the same rule: the new settings are applied; if the device does not
// open, no audio flows within FlowTimeout, or the dropouts spike right after, the previous settings are put back and the result
// says why.

using System;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.App
{
    /// <summary>What <see cref="AudioSwitch.Apply"/> did.</summary>
    public sealed record AudioSwitchResult(bool Applied, bool Reverted, string? Reason, AudioConfig Settings, AudioState? After, string RevertNote);

    public static class AudioSwitch
    {
        /// <summary>No audio within this long after a change: the change is taken back.</summary>
        internal static TimeSpan FlowTimeout = TimeSpan.FromSeconds(10);
        /// <summary>Audio flowing, the output is watched this long for dropouts.</summary>
        internal static TimeSpan Settle = TimeSpan.FromSeconds(1.5);
        internal static TimeSpan Poll = TimeSpan.FromMilliseconds(100);
        /// <summary>More dropouts than this while settling: a spike, the change is taken back.</summary>
        internal static int SpikeDropouts = 3;

        /// <summary>
        /// Applies <paramref name="wanted"/> (the engine's calls through <paramref name="run"/>, which puts them on the engine's thread)
        /// and watches it; on a failure puts <paramref name="previous"/> back.
        /// </summary>
        public static async Task<AudioSwitchResult> Apply(IAudioControl a, Func<Func<object?>, CancellationToken, Task<object?>> run,
            AudioConfig previous, AudioConfig wanted, CancellationToken ct)
        {
            async Task<T> Engine<T>(Func<T> work, CancellationToken token) => (T)(await run(() => work(), token))!;

            long dropoutsBefore = 0;
            AudioState? after = null;
            string? reason = null;
            try
            {
                await Engine(() => { dropoutsBefore = a.Status().Dropouts; a.Apply(wanted); return true; }, ct);
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
                long played0 = (await Engine(a.Status, ct)).PlayedFrames;
                bool flowing = false;
                while (clock.Elapsed < FlowTimeout)
                {
                    var s = await Engine(a.Status, ct);
                    after = s;
                    if (s.Open && s.SampleRate > 0 && s.PlayedFrames - played0 >= s.SampleRate / 4) { flowing = true; break; }
                    await Task.Delay(Poll, ct);
                }
                if (!flowing)
                    reason = $"No audio flowed within {FlowTimeout.TotalSeconds:0.#} s of the change" + (after is { Open: false } ? " (the output did not open)" : "") + ".";
                else
                {
                    var settle = System.Diagnostics.Stopwatch.StartNew();
                    while (settle.Elapsed < Settle)
                    {
                        await Task.Delay(Poll, ct);
                        after = await Engine(a.Status, ct);
                        long dropped = after.Dropouts - dropoutsBefore;
                        if (dropped > SpikeDropouts)
                        {
                            reason = $"{dropped} dropouts in the first {settle.Elapsed.TotalSeconds:0.#} s at {wanted.BufferFrames} frames: the output can't keep up with these settings.";
                            break;
                        }
                    }
                }
            }

            if (reason == null) return new AudioSwitchResult(true, false, null, wanted, after, "");

            // Back to what worked, whatever the caller does now.
            string revertNote = "";
            try
            {
                await Engine(() => { a.Apply(previous); return true; }, CancellationToken.None);
                after = await Engine(a.Status, CancellationToken.None);
            }
            catch (Exception e)
            {
                revertNote = $" Putting the previous settings back failed too: {e.Message}. Choose a device in Preferences ▸ Audio.";
            }
            return new AudioSwitchResult(false, true, reason, previous, after, revertNote);
        }
    }
}
