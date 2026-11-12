#nullable disable
// Neo machines shared DSP (docs/specs/machines.md §4): voice allocation for the synths. Notes are
// identified by a key (a track, or a MIDI note); each note plays on Unison voices. Poly mode
// steals released voices first, then the oldest; mono and legato keep a note stack.

using System;
using System.Runtime.CompilerServices;

namespace NeoBuzz.Dsp
{
    internal enum VoiceMode { Poly, Mono, Legato }

    /// <summary>What a synth engine does with notes and controllers (fed by the machine's note input).</summary>
    internal interface INoteSink
    {
        /// <summary><paramref name="key"/>: a track (0 … 15) or 1000 + a MIDI note. Velocity 0 … 1.</summary>
        void NoteOn(int key, int note, float velocity);
        void NoteOff(int key);
        /// <summary>Mod wheel, 0 … 1.</summary>
        void ModWheel(float value);
        /// <summary>Pitch bend, −1 … 1.</summary>
        void PitchBend(float value);
        void Sustain(bool down);
        /// <summary>Channel pressure, 0 … 1.</summary>
        void Aftertouch(float value);
        /// <summary>Release everything (transport stop); hard: silence at once.</summary>
        void AllNotesOff(bool hard);
    }

    /// <summary>
    /// A note sink that also wants each MIDI event's channel (live MPE): notes, pitch bend, channel pressure and CC74 come
    /// with it; the rest takes the plain <see cref="INoteSink"/> path. Pattern notes stay on the plain path.
    /// </summary>
    internal interface IChannelNoteSink : INoteSink
    {
        void NoteOn(int key, int note, float velocity, int channel);
        void NoteOff(int key, int note, int channel);
        /// <summary>A controller or bend (0xE0 / 0xFF) or pressure (0xD0 / 0xFC) on a channel; false: not taken, the plain path gets it.</summary>
        bool Control(int channel, int ctrl, int value);
    }

    internal interface IVoice
    {
        /// <summary>Making sound (or about to).</summary>
        bool IsActive { get; }
        /// <summary>Its amplitude envelope's current level (steal priority).</summary>
        float Loudness { get; }

        /// <summary>
        /// Starts a note. <paramref name="glideFrom"/> is the pitch to glide from (NaN: none).
        /// A voice that is sounding fades out quickly first (a steal).
        /// </summary>
        void NoteOn(float note, float velocity, int unisonIndex, int unisonCount, float glideFrom);

        /// <summary>Changes pitch without retriggering (legato), gliding when <paramref name="glide"/>.</summary>
        void Legato(float note, bool glide);

        void NoteOff();

        /// <summary>Silences within a few milliseconds (transport stop, all sound off).</summary>
        void Kill();
    }

    internal sealed class VoiceAllocator
    {
        readonly IVoice[] voices;
        readonly int[] voiceKey;          // key the voice plays; −1 none
        readonly long[] voiceStart, voiceRelease;
        readonly bool[] voiceHeld;
        long clock;

        // Mono note stack and the keys held, with their pitch and velocity.
        readonly int[] stackKey = new int[128];
        readonly float[] stackNote = new float[128], stackVel = new float[128];
        int stackCount;

        // Sustain pedal: keys released while it was down.
        readonly bool[] sustainedVoice;
        bool sustainDown;

        float lastNote = float.NaN;

        public VoiceAllocator(IVoice[] voices)
        {
            this.voices = voices;
            int n = voices.Length;
            voiceKey = new int[n];
            voiceStart = new long[n];
            voiceRelease = new long[n];
            voiceHeld = new bool[n];
            sustainedVoice = new bool[n];
            for (int i = 0; i < n; i++) voiceKey[i] = -1;
        }

        VoiceMode mode = VoiceMode.Poly;

        /// <summary>Poly, mono or legato. Changing it releases every held note (the two keep different books).</summary>
        public VoiceMode Mode
        {
            get => mode;
            set
            {
                if (value == mode) return;
                mode = value;
                AllNotesOff(false);
            }
        }
        /// <summary>Notes that can sound at once (poly).</summary>
        public int Polyphony = 8;
        /// <summary>Voices per note.</summary>
        public int Unison = 1;
        /// <summary>Glide between notes: off, always, or (poly/mono) only when notes overlap.</summary>
        public bool GlideAlways, GlideLegatoOnly;

        public int VoiceCount => voices.Length;

        /// <summary>Notes held (gate on), for tests and editors.</summary>
        public int HeldNotes
        {
            get
            {
                int count = 0;
                for (int i = 0; i < voices.Length; i++)
                    if (voiceHeld[i] && voiceKey[i] >= 0 && FirstVoiceOf(voiceKey[i]) == i) count++;
                return count;
            }
        }

        int FirstVoiceOf(int key)
        {
            for (int i = 0; i < voices.Length; i++)
                if (voiceKey[i] == key && voiceHeld[i]) return i;
            return -1;
        }

        public void NoteOn(int key, float note, float velocity)
        {
            clock++;
            if (Mode == VoiceMode.Poly) PolyOn(key, note, velocity);
            else MonoOn(key, note, velocity);
        }

        public void NoteOff(int key)
        {
            clock++;
            if (Mode == VoiceMode.Poly) PolyOff(key);
            else MonoOff(key);
        }

        public void Sustain(bool down)
        {
            if (sustainDown == down) return;
            sustainDown = down;
            if (down) return;
            for (int i = 0; i < voices.Length; i++)
            {
                if (!sustainedVoice[i]) continue;
                sustainedVoice[i] = false;
                if (voiceHeld[i])
                {
                    voiceHeld[i] = false;
                    voiceRelease[i] = clock;
                    voices[i].NoteOff();
                }
            }
        }

        /// <summary>Releases every note (transport stop); <paramref name="hard"/> silences at once.</summary>
        public void AllNotesOff(bool hard)
        {
            stackCount = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                sustainedVoice[i] = false;
                if (hard) voices[i].Kill();
                else if (voiceHeld[i]) voices[i].NoteOff();
                voiceHeld[i] = false;
                voiceKey[i] = -1;
            }
        }

        // ── poly ────────────────────────────────────────────────────────────

        void PolyOn(int key, float note, float velocity)
        {
            // The same key again: its previous note is released (a track plays one note at a time).
            ReleaseKey(key);

            int unison = Math.Clamp(Unison, 1, voices.Length);
            int poly = Math.Max(1, Polyphony);
            // Too many notes: release the oldest held ones.
            while (CountHeldKeys() >= poly)
            {
                int oldest = OldestHeldKey();
                if (oldest < 0) break;
                ReleaseKey(oldest);
            }

            bool overlap = CountHeldKeys() > 0;
            float glideFrom = GlideAlways || (GlideLegatoOnly && overlap) ? lastNote : float.NaN;
            for (int u = 0; u < unison; u++)
            {
                int v = PickVoice();
                voiceKey[v] = key;
                voiceHeld[v] = true;
                voiceStart[v] = clock;
                sustainedVoice[v] = false;
                voices[v].NoteOn(note, velocity, u, unison, glideFrom);
            }
            lastNote = note;
        }

        void PolyOff(int key)
        {
            for (int i = 0; i < voices.Length; i++)
            {
                if (voiceKey[i] != key || !voiceHeld[i]) continue;
                if (sustainDown)
                {
                    sustainedVoice[i] = true;
                    continue;
                }
                voiceHeld[i] = false;
                voiceRelease[i] = clock;
                voices[i].NoteOff();
            }
        }

        void ReleaseKey(int key)
        {
            for (int i = 0; i < voices.Length; i++)
            {
                if (voiceKey[i] != key) continue;
                if (voiceHeld[i])
                {
                    voiceHeld[i] = false;
                    voiceRelease[i] = clock;
                    voices[i].NoteOff();
                }
                sustainedVoice[i] = false;
                voiceKey[i] = -1;
            }
        }

        int CountHeldKeys()
        {
            int count = 0;
            for (int i = 0; i < voices.Length; i++)
            {
                if (!voiceHeld[i] || voiceKey[i] < 0) continue;
                bool first = true;
                for (int j = 0; j < i; j++)
                    if (voiceHeld[j] && voiceKey[j] == voiceKey[i]) { first = false; break; }
                if (first) count++;
            }
            return count;
        }

        int OldestHeldKey()
        {
            long best = long.MaxValue;
            int key = -1;
            for (int i = 0; i < voices.Length; i++)
                if (voiceHeld[i] && voiceKey[i] >= 0 && voiceStart[i] < best)
                {
                    best = voiceStart[i];
                    key = voiceKey[i];
                }
            return key;
        }

        /// <summary>A free voice; else the quietest of the released ones (oldest on ties); else the oldest.</summary>
        int PickVoice()
        {
            for (int i = 0; i < voices.Length; i++)
                if (!voices[i].IsActive && !voiceHeld[i] && voiceKey[i] < 0) return i;
            for (int i = 0; i < voices.Length; i++)
                if (!voices[i].IsActive && !voiceHeld[i]) return i;
            int best = -1;
            float bestLevel = float.MaxValue;
            long bestTime = long.MaxValue;
            for (int i = 0; i < voices.Length; i++)
            {
                if (voiceHeld[i]) continue;
                float level = voices[i].Loudness;
                if (level < bestLevel - 1e-4f || (MathF.Abs(level - bestLevel) <= 1e-4f && voiceRelease[i] < bestTime))
                {
                    best = i;
                    bestLevel = level;
                    bestTime = voiceRelease[i];
                }
            }
            if (best >= 0) return best;
            long oldest = long.MaxValue;
            for (int i = 0; i < voices.Length; i++)
                if (voiceStart[i] < oldest)
                {
                    oldest = voiceStart[i];
                    best = i;
                }
            return Math.Max(0, best);
        }

        // ── mono / legato ───────────────────────────────────────────────────

        void MonoOn(int key, float note, float velocity)
        {
            RemoveFromStack(key);
            bool wasHeld = stackCount > 0;
            if (stackCount < stackKey.Length)
            {
                stackKey[stackCount] = key;
                stackNote[stackCount] = note;
                stackVel[stackCount] = velocity;
                stackCount++;
            }
            int unison = Math.Clamp(Unison, 1, voices.Length);
            bool glide = GlideAlways || (GlideLegatoOnly && wasHeld);
            if (wasHeld && Mode == VoiceMode.Legato && AnyMonoVoiceHeld(unison))
            {
                for (int u = 0; u < unison; u++)
                {
                    voiceKey[u] = key;
                    voices[u].Legato(note, glide);
                }
            }
            else
            {
                float from = glide ? lastNote : float.NaN;
                for (int u = 0; u < unison; u++)
                {
                    voiceKey[u] = key;
                    voiceHeld[u] = true;
                    voiceStart[u] = clock;
                    sustainedVoice[u] = false;
                    voices[u].NoteOn(note, velocity, u, unison, from);
                }
                // Voices beyond the unison count (left from a larger setting) stop.
                for (int i = unison; i < voices.Length; i++)
                    if (voiceHeld[i]) { voiceHeld[i] = false; voices[i].NoteOff(); voiceKey[i] = -1; }
            }
            lastNote = note;
        }

        bool AnyMonoVoiceHeld(int unison)
        {
            for (int u = 0; u < unison; u++) if (voiceHeld[u] && voices[u].IsActive) return true;
            return false;
        }

        void MonoOff(int key)
        {
            bool wasTop = stackCount > 0 && stackKey[stackCount - 1] == key;
            RemoveFromStack(key);
            if (!wasTop) return;
            int unison = Math.Clamp(Unison, 1, voices.Length);
            if (stackCount > 0)
            {
                // Back to the previous held note.
                int top = stackCount - 1;
                float note = stackNote[top];
                bool glide = GlideAlways || GlideLegatoOnly;
                if (Mode == VoiceMode.Legato)
                {
                    for (int u = 0; u < unison; u++)
                    {
                        voiceKey[u] = stackKey[top];
                        voices[u].Legato(note, glide);
                    }
                }
                else
                {
                    for (int u = 0; u < unison; u++)
                    {
                        voiceKey[u] = stackKey[top];
                        voiceHeld[u] = true;
                        voices[u].NoteOn(note, stackVel[top], u, unison, glide ? lastNote : float.NaN);
                    }
                }
                lastNote = note;
                return;
            }
            // Every held voice, not just the current unison count (it may have been lowered meanwhile).
            for (int u = 0; u < voices.Length; u++)
            {
                if (!voiceHeld[u]) continue;
                if (sustainDown)
                {
                    sustainedVoice[u] = true;
                    continue;
                }
                voiceHeld[u] = false;
                voiceRelease[u] = clock;
                voices[u].NoteOff();
            }
        }

        void RemoveFromStack(int key)
        {
            for (int i = 0; i < stackCount; i++)
            {
                if (stackKey[i] != key) continue;
                for (int j = i; j < stackCount - 1; j++)
                {
                    stackKey[j] = stackKey[j + 1];
                    stackNote[j] = stackNote[j + 1];
                    stackVel[j] = stackVel[j + 1];
                }
                stackCount--;
                i--;
            }
        }
    }

    /// <summary>
    /// A voice's steal fade: a sounding voice asked to play a new note fades out over a few
    /// milliseconds first, then starts it.
    /// </summary>
    internal struct StealFade
    {
        float gain, step;
        bool fading;

        public bool Fading => fading;

        public void Start(float sampleRate, float seconds = 0.003f)
        {
            // A fade already under way goes on from where it is (no jump back to full level).
            if (!fading) gain = 1f;
            fading = true;
            step = 1f / Math.Max(1f, seconds * sampleRate);
        }

        /// <summary>The gain for this sample; false when the fade has just ended (start the pending note).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Next(out float g)
        {
            if (!fading)
            {
                g = 1f;
                return true;
            }
            gain -= step;
            if (gain <= 0f)
            {
                gain = 0;
                fading = false;
                g = 0f;
                return false;
            }
            g = gain;
            return true;
        }

        public void Cancel()
        {
            fading = false;
            gain = 1f;
        }
    }
}
