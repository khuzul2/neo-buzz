#nullable disable
// Neo Vocoder's engine (docs/specs/neo-vocoder.md): the modulator (the main input, summed to mono) is measured in 8 to 40 bands and its band levels
// scale the same bands of the carrier: the machine's own synth (8 voices, played by notes), white noise, or the sidechain input. The filter
// bank is NeoBuzz.Dsp/VocoderBank.cs, the synth VocoderSynth.cs. Around them: the formant shift (the carrier's bands move against the modulator's),
// the modulator's highs passed through (Sibilance), Freeze, Spread (odd bands left, even bands right), Output, and Mix and Amount through FxStage.
// A change of the band count or of the carrier fades the sound out over 4 ms, makes the change and fades back in, so neither clicks. Zero latency.
// No allocation after construction; portable maths only.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.Vocoder
{
    internal enum VocoderCarrier { Synth, Sidechain, Noise }

    internal sealed class VocoderEngine : INoteSink
    {
        public const int MaxBlock = 256;
        const double SmoothSeconds = 0.02;
        const double DuckSeconds = 0.004;
        /// <summary>A uniform noise in ±this has RMS <see cref="VocoderBank.CarrierNominal"/>.</summary>
        const float NoiseAmplitude = VocoderBank.CarrierNominal * 1.7320508f;

        /// <summary>What Amount scales: the distance of the formant shift, the sibilance and the spread from 0 (200 % doubles them, held to their ranges).</summary>
        public static readonly AmountRule[] AmountRules =
        {
            AmountRule.Lin(P.Formant, 0),
            AmountRule.Lin(P.Sibilance, 0),
            AmountRule.Lin(P.Spread, 0),
        };

        readonly ParamValues values;
        readonly FxStage fx;
        readonly VocoderBank bank = new VocoderBank(48000);
        readonly VocoderSynth synth = new VocoderSynth();
        readonly float[] mod = new float[MaxBlock];
        readonly float[] oddL = new float[MaxBlock], evenL = new float[MaxBlock], oddR = new float[MaxBlock], evenR = new float[MaxBlock];
        readonly float[] synthL = new float[MaxBlock], synthR = new float[MaxBlock], noiseBufL = new float[MaxBlock], noiseBufR = new float[MaxBlock];
        readonly float[] silence = new float[MaxBlock];
        Biquad sibA, sibB;
        SmoothRamp sibilance, spread, output, wire;
        Rng noiseL = new Rng(0x1B873593u), noiseR = new Rng(0x85EBCA6Bu);

        // The carrier and the band count in use; a change fades out, is applied, and fades in.
        VocoderCarrier carrier = VocoderCarrier.Synth, carrierWanted = VocoderCarrier.Synth;
        int bandsApplied = 16, bandsWanted = 16;
        int duckPhase;                 // 0 none, 1 fading out, 2 fading in, 3 held silent to the end of the block
        float duck = 1, duckStep;
        bool started;

        public readonly LevelMeter InMeter = new LevelMeter(), OutMeter = new LevelMeter();

        public VocoderEngine(ParamValues raw)
        {
            fx = new FxStage(raw, P.Amount, P.Mix, AmountRules, maxLatency: 0, maxBlock: MaxBlock);
            values = fx.Effective;
            SetSampleRate(48000);
        }

        public int Latency => 0;

        internal VocoderBank Bank => bank;

        internal VocoderSynth Synth => synth;

        internal ParamValues Effective => values;

        /// <summary>The sidechain carrier is wired (set by the machine every block). With Carrier on Sidechain and nothing wired, the wet sound is silence.</summary>
        public bool SidechainWired = true;

        public const string WireMessage = "Wire a carrier into the sidechain";

        /// <summary>Carrier is Sidechain and nothing is wired to it: the sound is silent and the editor says <see cref="WireMessage"/>.</summary>
        public bool NeedsWire => carrierWanted == VocoderCarrier.Sidechain && !SidechainWired;

        /// <summary>The carrier in use now.</summary>
        public VocoderCarrier Carrier => carrier;

        /// <summary>Nothing is sounding or ringing: no synth voice, and the band levels have died away.</summary>
        public bool Silent => !synth.Active && bank.MaxLevel < 1e-6f && duckPhase == 0;

        public int ActiveVoices => synth.ActiveVoices;

        public int HeldNotes => synth.HeldNotes;

        public void SetSampleRate(float sr)
        {
            fx.SetSampleRate(sr);
            sibilance.Setup(SmoothSeconds, sr);
            spread.Setup(SmoothSeconds, sr);
            output.Setup(SmoothSeconds, sr);
            wire.Setup(SmoothSeconds, sr);
            synth.SetSampleRate(sr);
            bank.SetSampleRate(sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            duckStep = (float)(1.0 / (DuckSeconds * sr));
            sibA.Set(BiquadType.HighPass, 5000, sr, 0.5411961);
            sibB.Set(BiquadType.HighPass, 5000, sr, 1.3065630);
            started = false;
            Update(true);
        }

        /// <summary>Back to the start: filters, followers, voices and noise as a new machine has them.</summary>
        public void Reset()
        {
            fx.Reset();
            bank.Reset();
            synth.Reset();
            sibA.Reset(); sibB.Reset();
            noiseL = new Rng(0x1B873593u); noiseR = new Rng(0x85EBCA6Bu);
            duckPhase = 0; duck = 1;
            started = false;
            Update(true);
        }

        /// <summary>Reads the parameters that changed. Runs once per block, never per sample.</summary>
        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            carrierWanted = (VocoderCarrier)values.Choice(P.Carrier);
            bandsWanted = (int)Math.Round(values.Value(P.Bands));
            synth.Wave = values.F(P.Wave);
            synth.Noise = values.F(P.Noise);
            synth.DetuneCents = values.F(P.Detune);
            synth.GlideMs = values.F(P.Glide);
            synth.Attack = (float)(values.Value(P.SynAttack) * 0.001);
            synth.Decay = (float)(values.Value(P.SynDecay) * 0.001);
            synth.SustainLevel = values.F(P.SynSustain);
            synth.Release = (float)(values.Value(P.SynRelease) * 0.001);
            synth.Apply();
            bank.LowHz = values.Value(P.Low);
            bank.HighHz = values.Value(P.High);
            bank.WidthFraction = values.Value(P.Width) / 100.0;
            bank.ShiftSemitones = values.Value(P.Formant);
            bank.AttackMs = values.Value(P.Attack);
            bank.ReleaseMs = values.Value(P.Release);
            bank.Freeze = values.On(P.Freeze);
            float sib = values.F(P.Sibilance), sp = values.F(P.Spread);
            float og = (float)DspMath.DbToGain(values.Value(P.Output));
            if (force || !started)
            {
                sibilance.Reset(sib); spread.Reset(sp); output.Reset(og);
                carrier = carrierWanted;
                bandsApplied = bandsWanted;
                bank.Bands = bandsApplied;
                bank.Equalise = carrier == VocoderCarrier.Sidechain;
                bank.Settle();
                wire.Reset(1f);
                started = true;
            }
            else
            {
                sibilance.SetTarget(sib); spread.SetTarget(sp); output.SetTarget(og);
            }
            values.Clean();
        }

        public void NoteOn(int key, int note, float velocity) => synth.NoteOn(key, note, velocity);
        public void NoteOff(int key) => synth.NoteOff(key);
        public void ModWheel(float value) { }
        public void PitchBend(float value) => synth.PitchBend(value);
        public void Sustain(bool down) => ((INoteSink)synth).Sustain(down);
        public void Aftertouch(float value) { }
        public void AllNotesOff(bool hard) => synth.AllNotesOff(hard);

        /// <summary>
        /// Processes a stereo block in place. <paramref name="carrierL"/> and <paramref name="carrierR"/> are the sidechain input (null when nothing is
        /// wired; a null right channel uses the left).
        /// </summary>
        public void Process(float[] left, float[] right, float[] carrierL, float[] carrierR, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            fx.Begin(left, right, n);
            Update(false);
            if (duckPhase == 3) duckPhase = 2;
            if (duckPhase == 0 && (carrierWanted != carrier || bandsWanted != bandsApplied)) duckPhase = 1;
            InMeter.Feed(left, right, n);
            for (int i = 0; i < n; i++) mod[i] = 0.5f * (left[i] + right[i]);

            float[] cl, cr;
            if (carrier == VocoderCarrier.Synth || synth.Active)
            {
                synth.Render(synthL, synthR, n);
            }
            switch (carrier)
            {
                case VocoderCarrier.Synth:
                    cl = synthL; cr = synthR;
                    break;
                case VocoderCarrier.Noise:
                    for (int i = 0; i < n; i++)
                    {
                        noiseBufL[i] = noiseL.NextBipolar() * NoiseAmplitude;
                        noiseBufR[i] = noiseR.NextBipolar() * NoiseAmplitude;
                    }
                    cl = noiseBufL; cr = noiseBufR;
                    break;
                default:
                    if (carrierL != null) { cl = carrierL; cr = carrierR ?? carrierL; }
                    else { cl = silence; cr = silence; }
                    break;
            }
            bank.Equalise = carrier == VocoderCarrier.Sidechain;
            bank.Process(mod, cl, cr, oddL, evenL, oddR, evenR, n);

            float wireTarget = carrier == VocoderCarrier.Sidechain && !SidechainWired ? 0f : 1f;
            wire.SetTarget(wireTarget);
            for (int i = 0; i < n; i++)
            {
                float sib = sibilance.Next(), sp = spread.Next(), og = output.Next(), w = wire.Next();
                float far = 1f - sp;
                float comp = MathF.Sqrt(2f / (1f + far * far));
                float wl = (oddL[i] + far * evenL[i]) * comp;
                float wr = (far * oddR[i] + evenR[i]) * comp;
                float h = sibB.Process(sibA.Process(mod[i])) * sib;
                wl += h; wr += h;
                if (duckPhase == 1)
                {
                    duck -= duckStep;
                    if (duck <= 0f)
                    {
                        duck = 0f;
                        carrier = carrierWanted;
                        bandsApplied = bandsWanted;
                        bank.Bands = bandsApplied;
                        duckPhase = 3;
                    }
                }
                else if (duckPhase == 2)
                {
                    duck += duckStep;
                    if (duck >= 1f) { duck = 1f; duckPhase = 0; }
                }
                float g = og * w * duck;
                left[i] = DspMath.Flush(wl * g);   // products of small states are tiny but normal; this keeps them out of the denormal range
                right[i] = DspMath.Flush(wr * g);
            }
            sibA.Flush(); sibB.Flush();
            OutMeter.Feed(left, right, n);
            fx.End(left, right, n, 0);
        }
    }
}
