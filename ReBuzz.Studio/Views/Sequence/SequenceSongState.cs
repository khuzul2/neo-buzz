// Fork (docs/specs/studio-mvp.md S4): what the sequence editor keeps with the song, one per
// engine: the bar list (time signatures), the clip colours, the zoom, and the clipboard. On
// Windows the classic sequence editor keeps the first two (ViewSettings) and saves them in the
// song's "SequenceEditor" subsection, the horizontal editor its zoom in
// "ModernSequenceEditorHorizontal"; the studio reads and writes both in the same formats, so
// songs go back and forth. It answers the engine's clip colours (ClipColours.Source, as
// ReBuzz/AppViews/SequenceEditorClipColours.cs does on Windows), which the Session View shares.
// The bar list is the engine's song meter (daw-modes.md S1), which reads it when a song opens;
// this writes it in the subsection, as before.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Sequence
{
    internal sealed class SequenceSongState : IClipColours
    {
        public const string ClassicSection = "SequenceEditor";
        public const string HorizontalSection = "ModernSequenceEditorHorizontal";
        const int ClassicVersion = 2;
        const int HorizontalVersion = 2;

        static SequenceSongState current;

        readonly ReBuzzCore core;
        readonly Dictionary<IClip, int> colours = new Dictionary<IClip, int>();
        double classicZoom = 2;     // the classic editor's own zoom: kept for Windows

        SequenceSongState(ReBuzzCore core)
        {
            this.core = core;
            core.OpenSong += OnOpenSong;
            core.SaveSong += OnSaveSong;
            core.BuzzCommandRaised += OnCommand;
            TimeSignatures.Changed += () => Changed?.Invoke();
            ClipColours.Source = () => current;
        }

        /// <summary>The state of this engine's song (made once; it follows the song from then on).</summary>
        public static SequenceSongState For(ReBuzzCore core)
        {
            if (current == null || current.core != core) current = new SequenceSongState(core);
            return current;
        }

        /// <summary>The bar list: the engine's song meter (daw-modes.md S1), which the session and the agent read too.</summary>
        public SongMeter TimeSignatures => core.SongMeter;

        public SequenceClipboard Clipboard { get; } = new SequenceClipboard();

        /// <summary>Pixels per tick (the zoom slider), saved with the song.</summary>
        public double Zoom { get; set; } = SequenceGeometry.DefaultTickWidth;

        /// <summary>Colours, bar list or zoom changed (a song opened, a colour set): views redraw.</summary>
        public event Action Changed;

        // ------------------------------------------------------------------ clip colours

        /// <summary>The colours a clip can have: the Default theme's ClipColors (SequenceEditor.xaml), as the Session View's.</summary>
        public static IReadOnlyList<(string Name, uint Argb)> Palette => ReBuzz.Core.Session.SessionColourChoices.Choices;

        public IEnumerable<IClip> Clips => colours.Keys;

        public bool TryGet(IClip clip, out int colorIndex)
        {
            if (clip != null && colours.TryGetValue(clip, out colorIndex)) return true;
            colorIndex = -1;
            return false;
        }

        public void Set(IClip clip, int colorIndex)
        {
            if (clip == null) return;
            colours[clip] = colorIndex;
            Changed?.Invoke();
        }

        public void Add(IClip clip, int colorIndex)
        {
            colours.Add(clip, colorIndex);
            Changed?.Invoke();
        }

        public bool Remove(IClip clip)
        {
            bool removed = clip != null && colours.Remove(clip);
            if (removed) Changed?.Invoke();
            return removed;
        }

        /// <summary>
        /// The colour a clip's blocks get (ClipElement): its own, else (ClipBoxColors =
        /// Clip, the default) one by its place among its machine's clips; -1 for the theme's box colour.
        /// </summary>
        public int ColourIndexOf(IClip clip)
        {
            int n = Palette.Count;
            if (clip == null || n == 0) return -1;
            if (colours.TryGetValue(clip, out int ci) && ci >= 0) return ci % n;
            int index = clip.Machine?.Clips.IndexOf(clip) ?? -1;
            return index >= 0 ? index % n : -1;
        }

        // ------------------------------------------------------------------ the song

        void Reset()
        {
            colours.Clear();
            Clipboard.Clear();
            classicZoom = 2;
            // The bars are the engine's: it resets them for a new song and reads them when one opens.
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
            new SongMeter().Read(br);                      // the engine has read the bars (SongMeterStore): skip them
            if (version < 2) return;
            int count = br.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                string machine = br.ReadString();
                string clip = br.ReadString();
                int ci = br.ReadInt32();
                var p = song.Machines.FirstOrDefault(m => m.Name == machine)?.Clips.FirstOrDefault(x => x.Name == clip);
                if (p != null) colours[p] = ci;
            }
        }

        void OnSaveSong(ISaveSong save)
        {
            // The classic editor's subsection: its zoom, the bar list and the colours of clips still in the song.
            var bw = new BinaryWriter(save.CreateSubSection(ClassicSection));
            bw.Write(ClassicVersion);
            bw.Write(classicZoom);
            TimeSignatures.Write(bw);
            var live = colours.Where(c => c.Key.Machine != null && save.Song.Machines.Contains(c.Key.Machine) && c.Key.Machine.Clips.Contains(c.Key)).ToList();
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
