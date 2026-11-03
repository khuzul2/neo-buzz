using System;
using System.ComponentModel;
using System.IO;
using ReBuzz.Core;
using ClassicBars = BuzzGUI.SequenceEditor.TimeSignatureList;
using ClassicEditor = BuzzGUI.SequenceEditor.SequenceEditor;

namespace ReBuzz.AppViews
{
    /// <summary>
    /// Fork (docs/specs/daw-modes.md S1): the WPF sequence editors' bar list mirrors the engine's
    /// song meter. The classic editor owns the list (ViewSettings.TimeSignatureList, which the
    /// modern editors use too) and replaces it when a song opens or a new one starts; this
    /// follows it, copies the engine's bars into it (a song opened, a new song, the agent) and
    /// its edits and undos into the engine. The engine's list is the one the song means.
    /// </summary>
    internal sealed class SequenceEditorMeter
    {
        static SequenceEditorMeter current;

        readonly SongMeter meter;
        ClassicEditor editor;
        ClassicBars watched;
        bool copying;

        internal SequenceEditorMeter(SongMeter meter)
        {
            this.meter = meter;
            meter.Changed += EngineChanged;
        }

        /// <summary>The list followed now (tests).</summary>
        internal ClassicBars Watched => watched;

        /// <summary>
        /// Mirrors the classic editor's list of the engine's song (MainWindow calls it once the
        /// editor has its song; again when the editor is made anew).
        /// </summary>
        internal static void Attach(ReBuzzCore buzz, ClassicEditor editor)
        {
            if (buzz == null) return;
            if (current == null || current.meter != buzz.SongMeter)
            {
                current?.Detach();
                current = new SequenceEditorMeter(buzz.SongMeter);
            }
            current.Follow(editor);
        }

        void Follow(ClassicEditor e)
        {
            if (editor != null) editor.PropertyChanged -= EditorPropertyChanged;
            editor = e;
            if (editor != null) editor.PropertyChanged += EditorPropertyChanged;
            Watch(ClassicEditor.ViewSettings?.TimeSignatureList);
        }

        void Detach()
        {
            meter.Changed -= EngineChanged;
            if (editor != null) editor.PropertyChanged -= EditorPropertyChanged;
            editor = null;
            Watch(null);
        }

        void EditorPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            // The editor made or read a new list (a song opened, a new song).
            if (e.PropertyName == "TimeSignatureListInit")
                Watch(ClassicEditor.ViewSettings?.TimeSignatureList);
        }

        /// <summary>Follows a list (null: none), which takes the engine's bars.</summary>
        internal void Watch(ClassicBars list)
        {
            if (!ReferenceEquals(list, watched))
            {
                if (watched != null) watched.Changed -= EditorChanged;
                watched = list;
                if (watched != null) watched.Changed += EditorChanged;
            }
            EngineChanged();
        }

        static byte[] Bytes(ClassicBars list)
        {
            var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms)) list.Write(bw);
            return ms.ToArray();
        }

        static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        void EngineChanged()
        {
            if (copying || watched == null) return;
            var bars = meter.ToBytes();
            if (Same(bars, Bytes(watched))) return;
            copying = true;
            try
            {
                using var br = new BinaryReader(new MemoryStream(bars));
                watched.Clone(new ClassicBars(br));
            }
            finally
            {
                copying = false;
            }
        }

        void EditorChanged()
        {
            if (copying || watched == null) return;
            var bars = Bytes(watched);
            if (Same(bars, meter.ToBytes())) return;
            copying = true;
            try
            {
                meter.ReadBytes(bars);
            }
            finally
            {
                copying = false;
            }
        }
    }
}
