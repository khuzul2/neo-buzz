// Pedal Gain — Simple stereo gain effect for ReBuzz
//
// Parameters:
//   Gain     : Output level in percent (100 = unity / 0 dB, 200 = +6 dB)
//   Mute     : Silence the output (off / on)
//   Inertia  : Fade time in ms when Mute is toggled (0 = instant snap)
//
// Build:
//   dotnet build PedalGain.csproj -c Release /p:BuzzDir="C:\Program Files\ReBuzz"
//
// The output DLL  "Pedal Gain.NET.dll"  is written straight to:
//   <BuzzDir>\Gear\Effects\

using System;
using Buzz.MachineInterface;

namespace WDE.PedalGain
{
    [MachineDecl(
        Name        = "Pedal Gain",
        ShortName   = "PGain",
        Author      = "WDE",
        MaxTracks   = 0,
        InputCount  = 1,
        OutputCount = 1)]
    public class PedalGainMachine : IBuzzMachine
    {
        readonly IBuzzMachineHost host;

        // Running gain value — smoothed toward target each sample.
        float _currentGain = 1f;

        public PedalGainMachine(IBuzzMachineHost host)
        {
            this.host = host;
        }

        // ── Parameters ────────────────────────────────────────────────────────

        /// <summary>
        /// Output gain as a percentage.
        ///   0   = silence
        ///  50   = −6 dB
        /// 100   = unity (0 dB)  ← default
        /// 200   = +6 dB
        /// </summary>
        [ParameterDecl(
            Name        = "Gain",
            Description = "Output level in percent (100 = unity / 0 dB, 200 = +6 dB)",
            MinValue    = 0,
            MaxValue    = 200,
            DefValue    = 100)]
        public int Gain { get; set; } = 100;

        /// <summary>Toggle mute.  When on, the output is silenced (with inertia fade).</summary>
        [ParameterDecl(
            Name              = "Mute",
            Description       = "Silence the output",
            ValueDescriptions = new[] { "off", "on" },
            MinValue          = 0,
            MaxValue          = 1,
            DefValue          = 0)]
        public int Mute { get; set; }

        /// <summary>
        /// Fade duration in milliseconds applied when Mute is engaged or released.
        ///   0 ms = instant (hard cut / hard unmute)
        ///  20 ms = short pop-free fade (default)
        /// 500 ms = long cinematic fade
        /// </summary>
        [ParameterDecl(
            Name        = "Inertia",
            Description = "Fade time in ms when Mute is toggled (0 = instant)",
            MinValue    = 0,
            MaxValue    = 500,
            DefValue    = 20)]
        public int Inertia { get; set; } = 20;

        // ── Audio processing ──────────────────────────────────────────────────

        public bool Work(Sample[] output, Sample[] input, int n, WorkModes mode)
        {
            if (mode == WorkModes.WM_NOIO) return false;

            // Target gain: zero when muted, otherwise Gain% converted to linear.
            float targetGain = Mute != 0 ? 0f : Gain * 0.01f;

            if (Inertia <= 0)
            {
                // Instant snap — no smoothing at all.
                _currentGain = targetGain;

                for (int i = 0; i < n; i++)
                {
                    output[i].L = input[i].L * targetGain;
                    output[i].R = input[i].R * targetGain;
                }
                // Always return true so ReBuzz uses our output buffer.
                // Returning false would cause ReBuzz to pass the dry input through,
                // defeating the mute entirely.
                return true;
            }

            // Smoothed fade — step size = full 0→1 range over Inertia ms.
            float sr   = host.MasterInfo.SamplesPerSec;
            float step = 1f / (Inertia * sr / 1000f);

            for (int i = 0; i < n; i++)
            {
                // Nudge current gain toward target one step per sample.
                if (_currentGain < targetGain)
                    _currentGain = MathF.Min(_currentGain + step, targetGain);
                else if (_currentGain > targetGain)
                    _currentGain = MathF.Max(_currentGain - step, targetGain);

                output[i].L = input[i].L * _currentGain;
                output[i].R = input[i].R * _currentGain;
            }

            // Always return true — we've written to the output buffer (even if it's
            // zeros). Returning false would let ReBuzz substitute the dry input signal.
            return true;
        }
    }
}
