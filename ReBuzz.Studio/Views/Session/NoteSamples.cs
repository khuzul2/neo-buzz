// Fork (TM1, docs/specs/machines.md Neo Sampler Tracker mode): what the piano roll knows of a machine that plays a sample per note: the samples it
// can name (number, name, length, an outline of the wave), the colour each number has, and the chip a note wears. The roll, the picker and the
// Sample lane all draw from here, so a number looks the same everywhere.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Buzz.MachineInterface;

namespace ReBuzz.Studio.Views.Session
{
    /// <summary>The colour of each sample number: nine that repeat (a re-sequenced break reads at a glance by its colours).</summary>
    internal static class SampleColours
    {
        static readonly Color[] colours =
        {
            Color.Parse("#F0B429"), Color.Parse("#5AA9FF"), Color.Parse("#FF7B54"), Color.Parse("#6FD08C"), Color.Parse("#C08CFF"),
            Color.Parse("#35C9C1"), Color.Parse("#FF6FA8"), Color.Parse("#9AA7FF"), Color.Parse("#E9E3D0"),
        };

        static readonly IBrush[] brushes = colours.Select(c => (IBrush)new ImmutableSolidColorBrush(c)).ToArray();

        /// <summary>The colour of sample <paramref name="number"/> (1 and up).</summary>
        public static Color Of(int number) => colours[Index(number)];

        public static IBrush BrushOf(int number) => brushes[Index(number)];

        static int Index(int number) => (Math.Max(1, number) - 1) % colours.Length;

        /// <summary>The ink on a chip or a note of the colour: dark, as every one of the nine is light.</summary>
        public static readonly IBrush Ink = new ImmutableSolidColorBrush(Color.Parse("#121417"));

        /// <summary>The chip's own background: dark with the colour of its number as the text.</summary>
        public static readonly IBrush ChipBack = new ImmutableSolidColorBrush(Color.FromArgb(0xD1, 0x12, 0x14, 0x17));

        /// <summary>A chip's text: the number as the roll shows it (two digits at least).</summary>
        public static string Label(int number) => number <= 0 ? "–" : number.ToString("00", CultureInfo.InvariantCulture);

        static readonly Typeface mono = new Typeface(new FontFamily("JetBrains Mono, Cascadia Mono, Menlo, Consolas, monospace"), FontStyle.Normal, FontWeight.Medium);

        public static FormattedText Text(string text, double size, IBrush brush) =>
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, mono, size, brush);
    }

    /// <summary>The samples a machine's notes can name, as the machine says now (refreshed when its version moves).</summary>
    internal sealed class NoteSampleContext
    {
        readonly INoteSampleProvider provider;
        IReadOnlyList<NoteSample> samples = Array.Empty<NoteSample>();
        Dictionary<int, NoteSample> byNumber = new Dictionary<int, NoteSample>();
        int version = int.MinValue;

        public NoteSampleContext(INoteSampleProvider provider)
        {
            this.provider = provider;
            Refresh();
        }

        public INoteSampleProvider Provider => provider;

        public IReadOnlyList<NoteSample> Samples => samples;

        /// <summary>Colour every note by its sample (the toolbar's Colour: by sample).</summary>
        public bool ColourBySample { get; set; } = true;

        public NoteSample Find(int number) => byNumber.TryGetValue(number, out var s) ? s : null;

        /// <summary>The name of a sample number (its own, or "Sample 12" when the machine doesn't have it now).</summary>
        public string NameOf(int number) => Find(number)?.Name ?? $"Sample {number}";

        /// <summary>The sample a new note gets when none was chosen yet: the first the machine has.</summary>
        public int Default => samples.Count > 0 ? samples[0].Number : 0;

        /// <summary>The name as a wide note shows it: "Break · snare" as "snare" (the part after the last dot), at most 14 characters.</summary>
        public string ShortName(int number)
        {
            string name = NameOf(number);
            int dot = name.LastIndexOf('·');
            if (dot >= 0 && dot + 1 < name.Length) name = name[(dot + 1)..].Trim();
            return name.Length > 14 ? name[..14] : name;
        }

        /// <summary>Reads the samples again when the machine's version moved; whether they changed.</summary>
        public bool Refresh()
        {
            int v;
            try { v = provider.NoteSampleVersion; }
            catch (Exception) { return false; }
            if (v == version) return false;
            version = v;
            try { samples = provider.ReadNoteSamples(); }
            catch (Exception) { samples = Array.Empty<NoteSample>(); }
            byNumber = new Dictionary<int, NoteSample>();
            foreach (var s in samples) byNumber[s.Number] = s;
            return true;
        }

        /// <summary>The sample after (<paramref name="delta"/> 1) or before (-1) the number in the list; none (0) steps to the first or last, and the ends stay.</summary>
        public int Step(int number, int delta)
        {
            if (samples.Count == 0) return number;
            int at = -1;
            for (int i = 0; i < samples.Count; i++) if (samples[i].Number == number) { at = i; break; }
            if (at < 0)
            {
                // Not in the list (a wave since removed, or a key-mapped note): the nearest in the direction asked.
                if (number <= 0) return delta > 0 ? samples[0].Number : samples[^1].Number;
                var next = delta > 0 ? samples.FirstOrDefault(s => s.Number > number) : samples.LastOrDefault(s => s.Number < number);
                return next?.Number ?? number;
            }
            return samples[Math.Clamp(at + delta, 0, samples.Count - 1)].Number;
        }

        /// <summary>A length as the picker shows it: "0.21 s", "1.2 s", "12 s".</summary>
        public static string Length(NoteSample s) =>
            s.Seconds < 10 ? s.Seconds.ToString(s.Seconds < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " s" : ((int)Math.Round(s.Seconds)).ToString(CultureInfo.InvariantCulture) + " s";
    }
}
