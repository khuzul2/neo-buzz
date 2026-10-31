using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace ReBuzz.AppViews.Session
{
    internal sealed partial class SessionView
    {
        // ------------------------------------------------------------------ audio clips (1.0)

        /// <summary>The audio files the wavetable reads (libsndfile): what a drop or an import takes.</summary>
        internal static readonly string[] AudioExtensions = { ".wav", ".wave", ".aif", ".aiff", ".aifc", ".flac", ".ogg" };

        internal static bool IsAudioFile(string path) =>
            !string.IsNullOrEmpty(path) && AudioExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

        void InitAudioDrop()
        {
            AllowDrop = true;
            // Events added by name would be this view's DragOver method (moving clips).
            AddHandler(DragOverEvent, new DragEventHandler((s, e) =>
            {
                e.Effects = AudioFiles(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            }));
            AddHandler(DropEvent, new DragEventHandler((s, e) =>
            {
                var files = AudioFiles(e.Data);
                if (files.Count == 0) return;
                e.Handled = true;
                // On a slot: there (a new audio track for it if the slot's track isn't one).
                // Anywhere else: a new audio track, from the selected scene.
                var cell = e.OriginalSource is DependencyObject d ? FindParent<SlotCell>(d) : null;
                ImportAudioFiles(cell?.Track ?? -1, cell?.Scene ?? selected?.Scene ?? 0, files);
            }));
        }

        static List<string> AudioFiles(IDataObject data)
        {
            try
            {
                if (data?.GetDataPresent(DataFormats.FileDrop) == true && data.GetData(DataFormats.FileDrop) is string[] paths)
                    return paths.Where(IsAudioFile).ToList();
            }
            catch (Exception)
            {
                // A drag from another program can fail to give its data; then it is nothing for us.
            }
            return new List<string>();
        }

        /// <summary>
        /// Puts audio files in slots: the first in the given one, the rest below it on the same
        /// (audio) track. Shows what couldn't be imported.
        /// </summary>
        internal void ImportAudioFiles(int track, int scene, IReadOnlyList<string> files)
        {
            var problems = new List<string>();
            IPattern last = null;
            for (int i = 0; i < files.Count; i++)
            {
                var clip = layout.ImportAudio(track, scene + i, files[i], out var error);
                if (clip == null)
                {
                    problems.Add(error);
                    continue;
                }
                last = clip;
                track = layout.FirstTrackOf(clip.Machine);
            }
            if (last != null && track >= 0)
            {
                ShowClipPanel();
                Select(track, scene + files.Count - 1);
            }
            if (problems.Count > 0)
                MessageBox.Show(Window.GetWindow(this), string.Join("\n", problems.Distinct()), "Audio clips", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Asks for audio files and imports them into a slot.</summary>
        void ImportAudioInto(int track, int scene)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Import audio into the slot",
                Filter = "Audio files|" + string.Join(";", AudioExtensions.Select(x => "*" + x)) + "|All files|*.*",
                Multiselect = true
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true)
                ImportAudioFiles(track, scene, dialog.FileNames);
        }

        void NewAudioTrack()
        {
            if (layout.NewAudioTrack() is IMachine machine)
            {
                int track = layout.FirstTrackOf(machine);
                if (track >= 0) Select(track, selected?.Scene ?? 0);
            }
        }

        void NewResamplingTrack()
        {
            if (layout.NewResamplingTrack() is IMachine machine)
            {
                int track = layout.FirstTrackOf(machine);
                if (track >= 0) Select(track, selected?.Scene ?? 0);
            }
        }

        /// <summary>Opens the warp marker editor of an audio clip.</summary>
        internal void EditWarpMarkers(ISequence sequence, IPattern pattern)
        {
            if (pattern == null || !layout.IsAudioClip(pattern) || layout.AudioSourceOf(pattern) == null) return;
            var window = new WarpMarkerWindow(layout, session, sequence, pattern) { Owner = Window.GetWindow(this) };
            window.Show();
        }
    }
}
