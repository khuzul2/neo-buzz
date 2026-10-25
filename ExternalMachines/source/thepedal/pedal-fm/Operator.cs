using System;

namespace PedalFM
{
    /// <summary>
    /// Single FM operator. Holds the phase accumulator, instantaneous output
    /// level, envelope, and the static per-op frequency config (ratio + detune
    /// + fixed-Hz flag). Inc is now written by <see cref="Voice"/> at control
    /// rate so that LFO pitch and glide can move it without going through
    /// here every block.
    /// </summary>
    internal sealed class Operator
    {
        public uint  Phase;
        public uint  Inc;                   // written by Voice each control block
        public float Level;
        public readonly Envelope Env = new Envelope();

        // Per-op frequency config. RatioWithDetune folds the detune Pow into a
        // single multiplier so the control-rate Inc update is one mul + cast.
        public float Ratio;
        public float DetuneCents;
        public bool  Fixed;
        public float RatioWithDetune;       // Ratio * 2^(DetuneCents/1200), or Ratio when Fixed/zero detune

        // Dirty-cache so we only do the Pow when something actually changed.
        float _cRatio = -2f;
        float _cDet   = -999f;
        bool  _cFixed;

        public void Reset()
        {
            Phase = 0;
            Inc   = 0;
            Env.Reset();
        }

        /// <summary>
        /// Set ratio / detune / fixed flag and refresh RatioWithDetune. Cheap
        /// when nothing changed; the Pow only runs on actual edits.
        /// </summary>
        public void UpdateConfig(float ratio, float detuneCents, bool fixedHz)
        {
            if (ratio == _cRatio && detuneCents == _cDet && fixedHz == _cFixed) return;
            _cRatio = ratio;
            _cDet   = detuneCents;
            _cFixed = fixedHz;

            Ratio       = ratio;
            DetuneCents = detuneCents;
            Fixed       = fixedHz;

            if (fixedHz || detuneCents == 0f)
                RatioWithDetune = ratio;
            else
                RatioWithDetune = ratio * MathF.Pow(2f, detuneCents / 1200f);
        }
    }
}
