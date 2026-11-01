// Agent server (docs/specs/mcp-server.md §3.3): pitches. Agents use MIDI numbers or scientific
// names (60 = "C4", 69 = "A4" = 440 Hz). Buzz patterns store notes as (octave << 4) | (semitone + 1)
// and show them an octave higher ("C-5" for MIDI 60); the server converts and never mixes them.

using System;
using System.Globalization;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Model
{
    public static class Pitch
    {
        static readonly string[] Sharps = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>Buzz's "note off" value.</summary>
        public const int BuzzNoteOff = 255;

        /// <summary>MIDI 60 → "C4".</summary>
        public static string Name(int midi) => Sharps[((midi % 12) + 12) % 12] + (Math.Floor(midi / 12.0) - 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>A pitch from a MIDI number or a name ("C4", "F#3", "Bb2", "E♭5"). Throws a ToolError that explains the format.</summary>
        public static int Parse(string text)
        {
            if (TryParse(text, out int midi, out string error)) return midi;
            throw new ToolError(error);
        }

        public static bool TryParse(string text, out int midi, out string error)
        {
            midi = 0;
            error = null;
            string t = (text ?? "").Trim();
            if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                if (n < 0 || n > 127) { error = $"Pitch {n} is outside MIDI's 0–127."; return false; }
                midi = n;
                return true;
            }
            // Buzz writes "C-5" (an octave above scientific C4); scientific names only go down to octave -1.
            if (t.Length == 3 && t[1] == '-' && char.IsLetter(t[0]) && char.IsDigit(t[2]) && t[2] != '1')
            {
                error = $"\"{text}\" looks like Buzz pattern notation; use scientific names, an octave lower (Buzz C-5 = C4 = 60), or MIDI numbers.";
                return false;
            }
            if (t.Length < 2) { error = $"\"{text}\" isn't a pitch: use a MIDI number (60) or a name like C4, F#3, Bb2."; return false; }
            int letter = "C D EF G A B".IndexOf(char.ToUpperInvariant(t[0]));
            if (letter < 0 || t[0] == ' ') { error = $"\"{text}\" isn't a pitch: use a MIDI number (60) or a name like C4, F#3, Bb2."; return false; }
            int i = 1, accidental = 0;
            while (i < t.Length && (t[i] == '#' || t[i] == '♯' || t[i] == 'b' || t[i] == '♭'))
            {
                accidental += t[i] == '#' || t[i] == '♯' ? 1 : -1;
                i++;
            }
            if (!int.TryParse(t.Substring(i), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int octave))
            {
                error = $"\"{text}\" needs an octave number: C4 is middle C (MIDI 60).";
                return false;
            }
            int m = (octave + 1) * 12 + letter + accidental;
            if (m < 0 || m > 127) { error = $"{text} is outside MIDI's range (C-1 … G9)."; return false; }
            midi = m;
            return true;
        }

        /// <summary>A Buzz note value (octave &lt;&lt; 4 | semitone + 1) → MIDI; −1 for note-off or none.</summary>
        public static int FromBuzz(int buzzNote)
        {
            if (buzzNote <= 0 || buzzNote == BuzzNoteOff) return -1;
            int octave = buzzNote >> 4, semitone = (buzzNote & 15) - 1;
            return semitone < 0 || semitone > 11 ? -1 : octave * 12 + semitone;
        }

        /// <summary>MIDI → a Buzz note value. MIDI 0–11 have no Buzz note (octave 0 is used for C-0 = MIDI 0 here).</summary>
        public static int ToBuzz(int midi)
        {
            midi = Math.Clamp(midi, 0, 9 * 12 + 11);
            return ((midi / 12) << 4) | (midi % 12 + 1);
        }

        public static double Hz(double midi) => 440.0 * Math.Pow(2, (midi - 69) / 12.0);
    }

    /// <summary>Song positions: agents speak beats (or "bar.beat" text); the song stores ticks.</summary>
    public static class SongTime
    {
        /// <summary>
        /// A position in beats from a number (beats from the song start) or "bar.beat" text:
        /// "17.1" is bar 17's first beat, "17" its start, "17.2.5" half a beat after its second beat.
        /// </summary>
        public static double ParseBeats(System.Text.Json.Nodes.JsonNode value, int beatsPerBar, string what)
        {
            if (Schema.TryNumber(value, out double beats))
            {
                if (beats < 0) throw new ToolError($"{what}: {beats} is before the song start (beats count from 0).");
                return beats;
            }
            string text = value is System.Text.Json.Nodes.JsonValue v && v.TryGetValue(out string s) ? s.Trim() : null;
            if (text == null) throw new ToolError($"{what}: give beats (a number) or \"bar.beat\" text like \"17.1\".");
            var parts = text.Split('.', 2);
            if (parts.Length > 2 || !int.TryParse(parts[0], out int bar) || bar < 1)
                throw new ToolError($"{what}: \"{text}\" isn't \"bar.beat\" (bars and beats count from 1: \"1.1\" is the song start).");
            double beat = 1;
            if (parts.Length == 2 && (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out beat) || beat < 1 || beat >= beatsPerBar + 1))
                throw new ToolError($"{what}: beat \"{parts[1]}\" must be from 1 to {beatsPerBar} (there are {beatsPerBar} beats per bar).");
            return (bar - 1) * beatsPerBar + (beat - 1);
        }

        public static int Ticks(double beats, int ticksPerBeat) => (int)Math.Round(beats * ticksPerBeat);

        public static double Beats(int ticks, int ticksPerBeat) => Math.Round(ticks / (double)Math.Max(1, ticksPerBeat), 6);

        /// <summary>"17.1" for a beat position.</summary>
        public static string BarBeat(double beats, int beatsPerBar)
        {
            int bar = (int)Math.Floor(beats / beatsPerBar) + 1;
            double beat = beats - (bar - 1) * beatsPerBar + 1;
            return bar.ToString(CultureInfo.InvariantCulture) + "." + Math.Round(beat, 3).ToString(CultureInfo.InvariantCulture);
        }
    }
}
