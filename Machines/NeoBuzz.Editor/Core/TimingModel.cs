#nullable disable
// Neo editor kit, toolkit-free core (docs/sdk/tempo-sync.md, SYN1): what the shared tempo-time control shows and writes. A timing is Free
// (the machine's own rate knob, in ms or Hz) or Sync: a division (8 bars to 1/64) and a Feel (Straight, Dotted, Triplet, Quintuplet). A
// machine stores it in one of two shapes, and this model takes both:
//   A. the division list holds "Free" as an entry (Neo LFO, Neo Delay, Neo Poly ...): Free is entry 0 of the Sync parameter;
//   B. a separate Timing switch (Neo Arp, Neo Pulse, Neo Notes): raw 0 Song tempo, raw 1 Free time, and the list holds divisions only.
// The picker shows the divisions longest first, whatever order the list is stored in (stored order never changes), leaves the old inline
// entries ("1/16T", "1/8D") out and shows them as their division plus a Feel, and greys out a division that cannot fit a machine's longest
// time at the song's tempo. The controls in the toolkit (Controls: ParamTempoTime) only draw and route; the decisions are here, so they
// are tested without a window.

using System;
using System.Collections.Generic;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>The values a pick writes: each is a raw parameter value, or null when that parameter stays as it is.</summary>
    internal readonly struct TimingWrite
    {
        public TimingWrite(int? division, int? feel, int? mode)
        {
            Division = division; Feel = feel; Mode = mode;
        }

        public int? Division { get; }
        public int? Feel { get; }
        public int? Mode { get; }
    }

    internal sealed class TimingModel
    {
        readonly ParamDef division, feel, mode;
        readonly SyncEntry[] entries;
        readonly List<SyncEntry> picker;
        readonly int freeEntry = -1;

        /// <param name="division">The Sync (shape A) or Rate (shape B) list.</param>
        /// <param name="feel">The Feel parameter; null when the timing has none (shows Straight).</param>
        /// <param name="mode">The Song tempo / Free time switch of shape B; null for shape A.</param>
        /// <param name="outputHz">The free value is in Hz (an LFO) and the synced one is shown in Hz; otherwise in time.</param>
        /// <param name="maxSeconds">The longest time the machine runs (0: none): longer divisions are greyed out and clamp.</param>
        public TimingModel(ParamDef division, ParamDef feel, ParamDef mode, bool outputHz, double maxSeconds = 0)
        {
            this.division = division; this.feel = feel; this.mode = mode;
            OutputHz = outputHz;
            MaxSeconds = maxSeconds;
            entries = TempoSync.Entries(division.Names);
            picker = new List<SyncEntry>();
            foreach (var e in TempoSync.DisplayOrder(division.Names))
            {
                if (e.Free) freeEntry = e.Index;
                else picker.Add(e);
            }
        }

        public bool OutputHz { get; }
        public double MaxSeconds { get; }
        public bool HasMode => mode != null;
        public bool HasFeel => feel != null;

        /// <summary>The divisions the picker offers, longest first (no Free, no inline entries).</summary>
        public IReadOnlyList<SyncEntry> Divisions => picker;

        /// <summary>The Feel names, in stored order.</summary>
        public string[] FeelNames => feel?.Names ?? TempoSync.FeelNames;

        int Index(int raw) => Math.Clamp(raw - division.Min, 0, entries.Length - 1);

        /// <summary>Whether the timing runs on its own time (the rate knob) rather than the song's tempo.</summary>
        public bool IsFree(int divisionRaw, int modeRaw)
        {
            if (mode != null) return modeRaw - mode.Min == 1;
            return freeEntry >= 0 && Index(divisionRaw) == freeEntry;
        }

        /// <summary>The division the picker shows as chosen, and the Feel it shows (an inline entry reads as its division and Feel).</summary>
        public SyncEntry Shown(int divisionRaw, int feelRaw, out int shownFeel)
        {
            int f = feel != null ? feelRaw - feel.Min : 0;
            int plain = TempoSync.PlainIndex(division.Names, Index(divisionRaw), f, out shownFeel);
            return entries[Math.Clamp(plain, 0, entries.Length - 1)];
        }

        /// <summary>"Free", "1/8 dotted", "2 bars": what the division picker says.</summary>
        public string DivisionText(int divisionRaw, int feelRaw, int modeRaw)
        {
            if (IsFree(divisionRaw, modeRaw) && mode == null) return "Free";
            var e = Shown(divisionRaw, feelRaw, out int shownFeel);
            if (e.Free) return "Free";
            return TempoSync.Label(e.DivisionBeats, 0);
        }

        /// <summary>The Feel's name as shown (the inline feel of an old entry when the Feel is Straight).</summary>
        public int ShownFeel(int divisionRaw, int feelRaw)
        {
            Shown(divisionRaw, feelRaw, out int shownFeel);
            return shownFeel;
        }

        /// <summary>The value of a Free/Sync switch as the control shows it: 0 Free, 1 Sync.</summary>
        public int ModeIndex(int divisionRaw, int modeRaw) => IsFree(divisionRaw, modeRaw) ? 0 : 1;

        // ---- picks -------------------------------------------------------------------------------------------------------

        /// <summary>The user chose Free.</summary>
        public TimingWrite PickFree() => mode != null ? new TimingWrite(null, null, mode.Min + 1) : new TimingWrite(freeEntry >= 0 ? division.Min + freeEntry : (int?)null, null, null);

        /// <summary>The user chose Sync: back to the last division the timing had (<paramref name="lastDivisionRaw"/>), or a quarter note.</summary>
        public TimingWrite PickSync(int divisionRaw, int lastDivisionRaw)
        {
            if (mode != null) return new TimingWrite(null, null, mode.Min);
            if (!IsFree(divisionRaw, 0)) return new TimingWrite(null, null, null);
            if (lastDivisionRaw >= division.Min && lastDivisionRaw <= division.Max && !IsFree(lastDivisionRaw, 0)) return new TimingWrite(lastDivisionRaw, null, null);
            foreach (var e in picker) if (e.Beats == 1) return new TimingWrite(division.Min + e.Index, null, null);
            return new TimingWrite(picker.Count > 0 ? division.Min + picker[0].Index : (int?)null, null, null);
        }

        /// <summary>The user chose a division in the picker. An old inline entry's Feel is kept (1/16T then 1/8 is 1/8 triplet).</summary>
        public TimingWrite PickDivision(int divisionRaw, int feelRaw, SyncEntry picked)
        {
            int? f = null;
            if (feel != null && !IsFree(divisionRaw, 0))
            {
                Shown(divisionRaw, feelRaw, out int shown);
                int now = feelRaw - feel.Min;
                if (shown != now) f = feel.Min + shown;
            }
            return new TimingWrite(division.Min + picked.Index, f, mode != null ? mode.Min : (int?)null);
        }

        /// <summary>The user chose a Feel. On an old inline entry the division is first made plain, so the two never multiply.</summary>
        public TimingWrite PickFeel(int divisionRaw, int feelRaw, int newFeelRaw)
        {
            int idx = Index(divisionRaw);
            int plain = TempoSync.PlainIndex(division.Names, idx, feelRaw - (feel?.Min ?? 0), out _);
            return new TimingWrite(plain != idx ? division.Min + plain : (int?)null, newFeelRaw, null);
        }

        // ---- time --------------------------------------------------------------------------------------------------------

        /// <summary>The length in beats of the stored pair (inline feel included, then the Feel parameter); 0 when Free.</summary>
        public double Beats(int divisionRaw, int feelRaw, int modeRaw)
        {
            if (IsFree(divisionRaw, modeRaw)) return 0;
            var e = entries[Index(divisionRaw)];
            return TempoSync.Beats(e.Beats, feel != null ? feelRaw - feel.Min : 0);
        }

        /// <summary>Whether a division at the current Feel fits the machine's longest time at this tempo (always true with no longest time).</summary>
        public bool Fits(SyncEntry e, int feelRaw, double bpm)
        {
            if (MaxSeconds <= 0) return true;
            return TempoSync.Seconds(TempoSync.Beats(e.Beats, feel != null ? feelRaw - feel.Min : 0), bpm) <= MaxSeconds;
        }

        /// <summary>Whether the stored sync time is longer than the machine runs (it is clamped).</summary>
        public bool Clamped(int divisionRaw, int feelRaw, int modeRaw, double bpm)
        {
            double b = Beats(divisionRaw, feelRaw, modeRaw);
            return MaxSeconds > 0 && b > 0 && TempoSync.Seconds(b, bpm) > MaxSeconds;
        }

        /// <summary>What the readout says: "1/8 dotted = 375 ms at 120 BPM", "1/4 = 2.00 Hz at 120 BPM", "... (clamped to 10 s)". Free: the free value.</summary>
        public string Readout(int divisionRaw, int feelRaw, int modeRaw, double bpm, string freeValueText)
        {
            if (IsFree(divisionRaw, modeRaw)) return freeValueText ?? "";
            double beats = Beats(divisionRaw, feelRaw, modeRaw);
            if (beats <= 0) return freeValueText ?? "";
            var e = Shown(divisionRaw, feelRaw, out int shownFeel);
            string name = TempoSync.Label(e.DivisionBeats, feel != null ? (feelRaw - feel.Min != 0 ? feelRaw - feel.Min : shownFeel) : 0);
            string bpmText = Math.Round(bpm).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " BPM";
            if (OutputHz) return name + " = " + ParamDef.Freq(TempoSync.Hz(beats, bpm)) + " at " + bpmText;
            double seconds = TempoSync.Seconds(beats, bpm);
            if (MaxSeconds > 0 && seconds > MaxSeconds) return name + " = " + ParamDef.Time(seconds) + ", clamped to " + ParamDef.Time(MaxSeconds) + " at " + bpmText;
            return name + " = " + ParamDef.Time(seconds) + " at " + bpmText;
        }
    }
}
