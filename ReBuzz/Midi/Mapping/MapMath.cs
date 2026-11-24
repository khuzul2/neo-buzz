#nullable disable
// Fork (docs/specs/midi-mapping.md, MM1): what a controller's message does to a parameter's value: range and invert, the three takeovers
// (pickup, jump, scale), relative encoders and how an encoder is told from a knob. Pure maths, no allocation: the router calls it on the
// MIDI thread for every message.

using System;
using System.Collections.Generic;

namespace ReBuzz.Midi.Mapping
{
    /// <summary>The state a mapped control keeps between messages (the takeover's).</summary>
    public struct TakeoverState
    {
        public bool Active;         // pickup: the control has met the parameter
        public bool HaveLast;
        public double Last;         // the control's last position
        public bool HaveBase;       // scale: where control and parameter were when the control started moving
        public double Base;         // the control's position then
        public double ParamBase;    // the parameter's (in the control's own terms)
        public double Written;      // what was last set, in the control's terms (a change from elsewhere starts the scale again)
        public double Acc;          // a relative control's steps not yet a whole value

        public void Reset() => this = default;
    }

    public static class MapMath
    {
        /// <summary>How close (as a fraction of the range) the control must come to the parameter to take it.</summary>
        public const double PickupWindow = 0.012;

        /// <summary>A relative control's steps from end to end of a wide parameter.</summary>
        public const double StepsAcross = 127;

        /// <summary>Whether a parameter of this span is a switch or a short list: one step is one value, takeover has no meaning.</summary>
        public static bool IsDiscrete(int min, int max) => max - min <= 1;

        /// <summary>The parameter value for a control position (0 to 1): the entry's range and invert applied.</summary>
        public static int ToValue(double position, int min, int max, double rangeMin, double rangeMax, bool invert)
        {
            double t = Math.Clamp(position, 0, 1);
            if (invert) t = 1 - t;
            double f = rangeMin + t * (rangeMax - rangeMin);
            return Math.Clamp(min + (int)Math.Round(f * (max - min), MidpointRounding.AwayFromZero), min, max);
        }

        /// <summary>The control position (0 to 1) at which the parameter has this value: the inverse of <see cref="ToValue"/>, clamped into the range.</summary>
        public static double ToPosition(int value, int min, int max, double rangeMin, double rangeMax, bool invert)
        {
            if (max <= min) return 0;
            double f = (value - min) / (double)(max - min);
            double span = rangeMax - rangeMin;
            double t = span <= 0 ? 0 : Math.Clamp((f - rangeMin) / span, 0, 1);
            return invert ? 1 - t : t;
        }

        /// <summary>
        /// An absolute control moved to <paramref name="position"/> (0 to 1) while the parameter is at <paramref name="current"/> (in the control's
        /// terms, <see cref="ToPosition"/>): the position the parameter should take, or false when it should stay (a pickup not yet met).
        /// </summary>
        public static bool Absolute(ref TakeoverState s, Takeover mode, double position, double current, bool discrete, out double target)
        {
            position = Math.Clamp(position, 0, 1);
            target = position;
            if (discrete || mode == Takeover.Jump)
            {
                s.Last = position;
                s.HaveLast = true;
                s.Active = true;
                return true;
            }
            if (mode == Takeover.Pickup)
            {
                bool met = s.Active;
                if (!met)
                {
                    met = Math.Abs(position - current) <= PickupWindow || (s.HaveLast && (s.Last - current) * (position - current) <= 0);
                    if (met) s.Active = true;
                }
                s.Last = position;
                s.HaveLast = true;
                return met;
            }
            // Scale: from where the control and the parameter were, the control's travel to its nearer end is spread over the parameter's.
            if (s.HaveBase && Math.Abs(current - s.Written) > 0.02) s.HaveBase = false;      // the parameter was moved from elsewhere
            if (!s.HaveBase)
            {
                s.HaveBase = true;
                s.Base = position;
                s.ParamBase = current;
                s.Written = current;
                target = current;
                s.Last = position;
                s.HaveLast = true;
                return false;
            }
            if (position >= s.Base)
                target = s.Base >= 1 ? s.ParamBase : s.ParamBase + (1 - s.ParamBase) * (position - s.Base) / (1 - s.Base);
            else
                target = s.Base <= 0 ? s.ParamBase : s.ParamBase * (position / s.Base);
            target = Math.Clamp(target, 0, 1);
            s.Written = target;
            s.Last = position;
            s.HaveLast = true;
            return true;
        }

        /// <summary>The signed step of a relative control's value: 64 plus n (offset) or n as two's complement (1 up, 127 down).</summary>
        public static int Step(KnobEncoding encoding, int value) => encoding switch
        {
            KnobEncoding.Offset64 => value - 64,
            KnobEncoding.TwosComplement => value < 64 ? value : value - 128,
            _ => 0,
        };

        /// <summary>
        /// A relative control's steps taken as whole parameter values: how many to add now. The rest stays in the state. A parameter with few
        /// values moves a whole value for a step; a wide one takes <see cref="StepsAcross"/> steps from end to end of the control's range.
        /// </summary>
        public static int Relative(ref TakeoverState s, KnobEncoding encoding, int value, int min, int max, double rangeMin, double rangeMax, bool invert)
        {
            int step = Step(encoding, value);
            if (step == 0) return 0;
            if (invert) step = -step;
            double span = (max - min) * Math.Max(0, rangeMax - rangeMin);
            double perStep = Math.Max(1.0, span / StepsAcross);
            s.Acc += step * perStep;
            int whole = (int)Math.Truncate(s.Acc);
            s.Acc -= whole;
            return whole;
        }

        /// <summary>The first guess at what a control sends from one value: 1 and 127 are steps (two's complement), values just off 64 are steps (offset), the rest a position.</summary>
        public static KnobEncoding Guess(int value)
        {
            if (value == 1 || value == 127) return KnobEncoding.TwosComplement;
            if (value >= 61 && value <= 67 && value != 64) return KnobEncoding.Offset64;
            return KnobEncoding.Absolute;
        }
    }

    /// <summary>
    /// Tells an absolute control from a relative one by what it sends while it is turned: a knob sweeps through values, an encoder only sends
    /// 1 and 127, or values within a few of 64. Feed it each value of one control.
    /// </summary>
    public sealed class EncoderDetector
    {
        readonly List<int> seen = new List<int>();

        public int Count => seen.Count;

        public void Observe(int value)
        {
            if (seen.Count < 64) seen.Add(value);
        }

        public KnobEncoding Encoding
        {
            get
            {
                if (seen.Count == 0) return KnobEncoding.Absolute;
                bool twos = true, offset = true;
                foreach (int v in seen)
                {
                    if (!(v >= 1 && v <= 8 || v >= 120 && v <= 127)) twos = false;
                    if (!(v >= 56 && v <= 72 && v != 64)) offset = false;
                }
                if (twos && (seen.Contains(1) || seen.Contains(127) || seen.Count >= 3)) return KnobEncoding.TwosComplement;
                if (offset) return KnobEncoding.Offset64;
                return KnobEncoding.Absolute;
            }
        }
    }
}
