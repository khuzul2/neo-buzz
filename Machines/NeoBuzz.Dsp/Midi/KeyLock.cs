#nullable disable
// Shared by Neo Voice to MIDI and Neo Audio to MIDI: the key a note is locked to. Off (every semitone), the set Root and Scale, the song's key (the
// song's "Key" entry, else the set one) or the key found from the notes played so far; muted notes are taken out and the nearest allowed note wins
// (Scales.Nearest). The same maths as VM1 had inline; allocation-free.

using System;

namespace NeoBuzz.Dsp
{
    internal sealed class KeyLock
    {
        public readonly KeyFinder Finder = new KeyFinder();
        volatile string songKeyText = "";
        int songRoot = -1, songScale;
        string songKeyParsed;
        int lockMode, keyRoot, scale, custom;

        /// <summary>The key actually in force (root and scale index) and its notes after muting, as set by the last <see cref="Compute"/>.</summary>
        public int ActiveRoot { get; private set; }
        public int ActiveScale { get; private set; }

        public void SetSongKey(string text) => songKeyText = text ?? "";

        /// <summary>The notes allowed (absolute 12-bit mask) for the settings; never empty.</summary>
        public int Compute(int lockMode, int keyRoot, int scale, int custom, int muted)
        {
            this.lockMode = lockMode; this.keyRoot = keyRoot; this.scale = scale; this.custom = custom;
            int mask, root = keyRoot, sc = scale;
            switch (lockMode)
            {
                case 1: mask = Scales.KeyMask(keyRoot, scale, custom); break;
                case 2:
                    {
                        string text = songKeyText;
                        if (!ReferenceEquals(text, songKeyParsed))
                        {
                            songKeyParsed = text;
                            songRoot = -1;
                            if (Scales.TryParseKey(text, out int r, out int s)) { songRoot = r; songScale = s; }
                        }
                        if (songRoot >= 0) { root = songRoot; sc = songScale; mask = Scales.KeyMask(root, sc, custom); }
                        else mask = Scales.KeyMask(keyRoot, scale, custom);
                        break;
                    }
                case 3:
                    if (Finder.Known) { root = Finder.Root; sc = Finder.Scale; mask = Scales.KeyMask(root, sc, custom); }
                    else mask = Scales.All;
                    break;
                default: mask = Scales.All; break;
            }
            ActiveRoot = root; ActiveScale = sc;
            int allowed = mask & ~muted & Scales.All;
            if (allowed == 0) allowed = Scales.All & ~muted;
            if (allowed == 0) allowed = Scales.All;
            return allowed;
        }

        /// <summary>The key's notes, before muting (for the editor: the chords, the piano roll's highlights).</summary>
        public int KeyNotes
        {
            get
            {
                switch (lockMode)
                {
                    case 1: return Scales.KeyMask(keyRoot, scale, custom);
                    case 2: return songRoot >= 0 ? Scales.KeyMask(songRoot, songScale, custom) : Scales.KeyMask(keyRoot, scale, custom);
                    case 3: return Finder.Known ? Scales.KeyMask(Finder.Root, Finder.Scale, custom) : Scales.All;
                    default: return Scales.All;
                }
            }
        }
    }
}
