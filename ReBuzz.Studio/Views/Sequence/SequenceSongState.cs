// Fork (docs/specs/studio-mvp.md S4): what the sequence editor keeps with the song, one per
// engine: the bar list (time signatures), the pattern colours, the zoom, and the clipboard. On
// Windows the classic sequence editor keeps the first two (ViewSettings) and saves them in the
// song's "SequenceEditor" subsection, the horizontal editor its zoom in
// "ModernSequenceEditorHorizontal"; the studio reads and writes both in the same formats, so
// songs go back and forth. It answers the engine's pattern colours (PatternColours.Source, as
// ReBuzz/AppViews/SequenceEditorPatternColours.cs does on Windows), which the Session View shares.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Sequence
{
    internal sealed class SequenceSongState : IPatternColours
    {
        public const string ClassicSection = "SequenceEditor";
        public const string HorizontalSection = "ModernSequenceEditorHorizontal";
        const int ClassicVersion = 2;
        const int HorizontalVersion = 2;

        static SequenceSongState current;

        readonly ReBuzzCore core;
        readonly Dictionary<IPattern, int> colours = new Dictionary<IPattern, int>();
        double classicZoom = 2;     // the classic editor's own zoom: kept for Windows

        SequenceSongState(ReBuzzCore core)
        {
            this.core = core;
            core.OpenSong += OnOpenSong;
            core.SaveSong += OnSaveSong;
            core.BuzzCommandRaised += OnCommand;
            TimeSignatures.Changed += () => Changed?.Invoke();
            PatternColours.Source = () => current;
        }

        /// <summary>The state of this engine's song (made once; it follows the song from then on).</summary>
        public static SequenceSongState For(ReBuzzCore core)
        {
            if (current == null || current.core != core) current = new SequenceSongState(core);
            return current;
        }

        public TimeSignatureList TimeSignatures { get; } = new TimeSignatureList();

        public SequenceClipboard Clipboard { get; } = new SequenceClipboard();

        /// <summary>Pixels per tick (the zoom slider), saved with the song.</summary>
        public double Zoom { get; set; } = SequenceGeometry.DefaultTickWidth;

        /// <summary>Colours, bar list or zoom changed (a song opened, a colour set): views redraw.</summary>
        public event Action Changed;

        // ------------------------------------------------------------------ pattern colours

        /// <summary>The colours a pattern can have: the Default theme's PatternColors (SequenceEditor.xaml), as the Session View's.</summary>
        public static IReadOnlyList<(string Name, uint Argb)> Palette => ReBuzz.Core.Session.SessionColourChoices.Choices;

        public IEnumerable<IPattern> Patterns => colours.Keys;

        public bool TryGet(IPattern pattern, out int colorIndex)
        {
            if (pattern != null && colours.TryGetValue(pattern, out colorIndex)) return true;
            colorIndex = -1;
            return false;
        }

        public void Set(IPattern pattern, int colorIndex)
        {
            if (pattern == null) return;
            colours[pattern] = colorIndex;
            Changed?.Invoke();
        }

        public void Add(IPattern pattern, int colorIndex)
        {
            colours.Add(pattern, colorIndex);
            Changed?.Invoke();
        }

        public bool Remove(IPattern pattern)
        {
            bool removed = pattern != null && colours.Remove(pattern);
            if (removed) Changed?.Invoke();
            return removed;
        }

        /// <summary>
        /// The colour a pattern's blocks get (PatternElement): its own, else (PatternBoxColors =
        /// Pattern, the default) one by its place among its machine's patterns; -1 for the theme's box colour.
        /// </summary>
        public int ColourIndexOf(IPattern pattern)
        {
            int n = Palette.Count;
            if (pattern == null || n == 0) return -1;
            if (colours.TryGetValue(pattern, out int ci) && ci >= 0) return ci % n;
            int index = pattern.Machine?.Patterns.IndexOf(pattern) ?? -1;
            return index >= 0 ? index % n : -1;
        }

        // ------------------------------------------------------------------ the song

        void Reset()
        {
            colours.Clear();
            Clipboard.Clear();
            classicZoom = 2;
            TimeSignatures.Reset();
        }

        void OnCommand(BuzzCommand cmd)
        {
            if (cmd == BuzzCommand.NewFile)
            {
                Reset();
                Changed?.Invoke();
            }
        }

        void OnOpenSong(IOpenSong open)
        {
            Reset();
            try
            {
                var classic = open.GetSubSection(ClassicSection);
                if (classic != null) ReadClassic(new BinaryReader(classic), open.Song);
                var horizontal = open.GetSubSection(HorizontalSection);
                if (horizontal != null)
                {
                    var br = new BinaryReader(horizontal);
                    int version = br.ReadInt32();
                    if (version <= HorizontalVersion) Zoom = SequenceGeometry.ClampTickWidth(br.ReadDouble());
                    // The rest (deprecated colours, VU meter targets) isn't used, here or on Windows.
                }
            }
            catch (Exception e) when (e is IOException || e is EndOfStreamException)
            {
                core.DCWriteLine("Sequence editor data: " + e.Message, DCLogLevel.Warning);
            }
            Changed?.Invoke();
        }

        void ReadClassic(BinaryReader br, ISong song)
        {
            int version = br.ReadInt32();
            if (version > ClassicVersion) return;
            classicZoom = br.ReadDouble();
            TimeSignatures.Read(br);
            if (version < 2) return;
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string machine = br.ReadString();
                string pattern = br.ReadString();
                int ci = br.ReadInt32();
                var p = song.Machines.FirstOrDefault(m => m.Name == machine)?.Patterns.FirstOrDefault(x => x.Name == pattern);
                if (p != null) colours[p] = ci;
            }
        }

        void OnSaveSong(ISaveSong save)
        {
            // The classic editor's subsection: its zoom, the bar list and the colours of patterns still in the song.
            var bw = new BinaryWriter(save.CreateSubSection(ClassicSection));
            bw.Write(ClassicVersion);
            bw.Write(classicZoom);
            TimeSignatures.Write(bw);
            var live = colours.Where(c => c.Key.Machine != null && save.Song.Machines.Contains(c.Key.Machine) && c.Key.Machine.Patterns.Contains(c.Key)).ToList();
            bw.Write(live.Count);
            foreach (var c in live)
            {
                bw.Write(c.Key.Machine.Name);
                bw.Write(c.Key.Name);
                bw.Write(c.Value);
            }
            bw.Flush();

            // The horizontal editor's: its zoom, then the two counts it writes as 0 and its VU list (empty).
            var hw = new BinaryWriter(save.CreateSubSection(HorizontalSection));
            hw.Write(HorizontalVersion);
            hw.Write(Zoom);
            hw.Write(0);
            hw.Write(0);
            hw.Write(0);
            hw.Flush();
        }
    }
}
