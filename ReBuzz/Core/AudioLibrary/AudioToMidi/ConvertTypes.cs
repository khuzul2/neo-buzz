#nullable disable
// Fork (docs/specs/pitch.md section 9, AM2): offline "Convert to MIDI" for audio. The types shared by the three converters (melody,
// harmony, drums) and the front door that runs them. Pure: no UI, no session; ReBuzz.SessionTests and tools/ConvertEval link these files.

using System;
using System.Collections.Generic;

namespace ReBuzz.Core.AudioLibrary.AudioToMidi
{
    internal enum ConvertMode { Melody, Harmony, Drums }

    /// <summary>One note found in audio: seconds from the start of the audio, a MIDI pitch, a velocity, and (optionally) the pitch bend that follows it.</summary>
    internal sealed class ConvertedNote
    {
        public double Start, End;
        /// <summary>MIDI note number (60 = C4).</summary>
        public int Pitch;
        /// <summary>1 to 127.</summary>
        public int Velocity = 100;
        /// <summary>The pitch moves within the note: (seconds, cents away from <see cref="Pitch"/>), at least 8 ms apart. Null when it doesn't or isn't asked for.</summary>
        public List<(double Time, double Cents)> Bends;
        /// <summary>Drums: the class the hit was given (Kick, Snare, ...); empty for pitched notes.</summary>
        public string Label = "";
    }

    /// <summary>What the person can choose; the defaults are what the menu uses.</summary>
    internal sealed class ConvertOptions
    {
        public ConvertMode Mode = ConvertMode.Melody;

        // Melody (monophonic)
        /// <summary>What the tracker listens for: "voice", "guitar", "bass" or "full" (the default: any melody instrument).</summary>
        public string Range = "full";
        /// <summary>Notes shorter than this are dropped, milliseconds.</summary>
        public double MinNoteMs = 60;
        /// <summary>A slide or vibrato stays one note and its movement becomes pitch bends (up to two semitones from the note).</summary>
        public bool PitchBends;
        /// <summary>The median filter over the pitch curve, milliseconds (0 = none).</summary>
        public double SmoothMs = 30;
        /// <summary>The pitch must be this far (semitones) from a note before a new one starts: vibrato under it stays one note.</summary>
        public double Hysteresis = 0.7;

        // Harmony (polyphonic, Basic Pitch)
        public double OnsetThreshold = 0.5;
        public double FrameThreshold = 0.3;
        /// <summary>Basic Pitch's minimum note length, milliseconds.</summary>
        public double HarmonyMinNoteMs = 127.7;
        /// <summary>Lowest and highest pitch kept (Hz; 0 = no limit).</summary>
        public double MinHz, MaxHz;

        // Drums
        /// <summary>Drum onset threshold (the detector's: 4 is its default; lower finds more).</summary>
        public double DrumThreshold = 4;
        /// <summary>Hits closer than this are one hit, milliseconds.</summary>
        public double DrumMinIntervalMs = 40;
    }

    internal sealed class ConvertResult
    {
        public ConvertMode Mode;
        public List<ConvertedNote> Notes = new();
        /// <summary>Seconds of audio analysed and the seconds it took.</summary>
        public double AudioSeconds, WorkSeconds;
        /// <summary>One line for the status and the agent: what was found.</summary>
        public string Summary = "";
    }

    internal static class MidiNames
    {
        static readonly string[] Names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>"C4" for 60.</summary>
        public static string Of(int note) => Names[((note % 12) + 12) % 12] + (note / 12 - 1);
    }
}
