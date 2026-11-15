// Fork (docs/specs/ux-overhaul.md P5b, the Samples view): the studio's edits of the song's wavetable. Loading is the agent's wave_import
// (WaveTools.Import: one undo step, the same checks); the rest are the same kind of step on the song's history: rename, loop points,
// root note, clear. Slots are 0-based here (the view shows them in hex from 01, as Buzz does). Every method returns null when it worked, else why not.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;
using ReBuzz.Core;

namespace ReBuzz.Agent
{
    internal static class WaveEdits
    {
        public static readonly string[] Extensions = { ".wav", ".aif", ".aiff", ".flac", ".ogg" };

        public static bool IsAudioFile(string path) => Extensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

        static WavetableCore Table(ReBuzzCore core) => core.SongCore.WavetableCore;

        /// <summary>Buzz's slot number, in hex from 01 (the wavetable view's, and the machines').</summary>
        public static string Hex(int index) => (index + 1).ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>Loads a file into a slot: the first empty one (-1), or <paramref name="index"/> (replacing what is there when <paramref name="replace"/>). One undo step.</summary>
        public static string Load(ReBuzzCore core, string path, int index, string name, bool replace, out int placed)
        {
            placed = -1;
            try
            {
                path = Path.GetFullPath((path ?? "").Trim().Trim('"'));
                if (!File.Exists(path)) return $"No file at {path}.";
                if (!IsAudioFile(path)) return $"{Path.GetFileName(path)}: use a WAV, AIFF, FLAC or OGG file.";
                var done = WaveTools.Import(core, path, index < 0 ? null : index + 1, name, null, replace, false, Array.Empty<string>());
                placed = done.Index;
                return null;
            }
            catch (ToolError e) { return e.Message; }
            catch (Exception e) { return e.Message; }
        }

        /// <summary>Renames a wave. One undo step.</summary>
        public static string Rename(ReBuzzCore core, int index, string name)
        {
            var wave = Wave(core, index);
            if (wave == null) return "That slot is empty.";
            name = (name ?? "").Trim();
            if (name.Length == 0) return "A wave needs a name.";
            string was = wave.Name;
            if (was == name) return null;
            void Set(string n)
            {
                var w = Wave(core, index);
                if (w == null) return;
                w.Name = n;
                w.Invalidate();
            }
            core.SongCore.ActionStack.Do(new DelegateAction(() => Set(name), () => Set(was), name: "Rename wave"));
            return null;
        }

        /// <summary>Sets the loop of a wave's first layer: on or off, and the points in samples (start before end, inside the wave). One undo step.</summary>
        public static string SetLoop(ReBuzzCore core, int index, bool loop, int start, int end)
        {
            var wave = Wave(core, index);
            var layer = wave?.LayersList.FirstOrDefault();
            if (layer == null) return "That slot is empty.";
            int count = layer.SampleCount;
            if (loop)
            {
                if (start < 0 || end > count) return $"The loop has to lie inside the wave (0 to {count} samples).";
                if (end - start < 2) return "The loop needs at least two samples: its start must come before its end.";
            }
            else
            {
                start = layer.LoopStart;
                end = layer.LoopEnd;
            }
            var flagsWas = wave.Flags;
            int startWas = layer.LoopStart, endWas = layer.LoopEnd;
            var flagsNow = loop ? flagsWas | WaveFlags.Loop : flagsWas & ~WaveFlags.Loop;
            if (flagsNow == flagsWas && start == startWas && end == endWas) return null;
            void Apply(WaveFlags flags, int s, int e)
            {
                var w = Wave(core, index);
                var l = w?.LayersList.FirstOrDefault();
                if (l == null) return;
                lock (ReBuzzCore.AudioLock)
                {
                    l.LoopStart = s;
                    l.LoopEnd = e;
                }
                w.Flags = flags;
                w.Invalidate();
            }
            core.SongCore.ActionStack.Do(new DelegateAction(() => Apply(flagsNow, start, end), () => Apply(flagsWas, startWas, endWas), name: "Wave loop"));
            return null;
        }

        /// <summary>Sets the root note (a MIDI pitch, 60 = C4) the first layer plays at unchanged. One undo step.</summary>
        public static string SetRoot(ReBuzzCore core, int index, int midi)
        {
            var layer = Wave(core, index)?.LayersList.FirstOrDefault();
            if (layer == null) return "That slot is empty.";
            if (midi < 0 || midi > 127) return "A root note is 0 to 127.";
            int was = layer.RootNote, now = Pitch.ToBuzz(midi);
            if (was == now) return null;
            void Set(int note)
            {
                var w = Wave(core, index);
                var l = w?.LayersList.FirstOrDefault();
                if (l == null) return;
                l.RootNote = note;
                w.Invalidate();
            }
            core.SongCore.ActionStack.Do(new DelegateAction(() => Set(now), () => Set(was), name: "Wave root note"));
            return null;
        }

        /// <summary>Empties a slot. One undo step; undo puts every layer back (from copies kept in the temp folder, as the agent's replace does).</summary>
        public static string Clear(ReBuzzCore core, int index)
        {
            var wave = Wave(core, index);
            if (wave == null) return "That slot is already empty.";
            string name = wave.Name;
            var flags = wave.Flags;
            float volume = wave.Volume;
            var backups = new List<(string Path, int Root, int LoopStart, int LoopEnd)>();
            try
            {
                foreach (var layer in wave.LayersList)
                {
                    string file = Path.Combine(Path.GetTempPath(), "ReBuzzAgent", $"wave-{Guid.NewGuid():N}.wav");
                    WaveTools.Save(layer, file);
                    backups.Add((file, layer.RootNote, layer.LoopStart, layer.LoopEnd));
                }
            }
            catch (Exception e) { return "Couldn't keep a copy for undo: " + e.Message; }
            void Restore()
            {
                var table = core.SongCore.Wavetable;
                for (int i = 0; i < backups.Count; i++)
                {
                    table.LoadWave(index, backups[i].Path, name, i > 0);
                    var w = Wave(core, index);
                    var l = w?.LayersList.ElementAtOrDefault(i);
                    if (l == null) continue;
                    l.RootNote = backups[i].Root;
                    l.LoopStart = backups[i].LoopStart;
                    l.LoopEnd = backups[i].LoopEnd;
                }
                var restored = Wave(core, index);
                if (restored != null)
                {
                    restored.Flags = flags;
                    restored.Volume = volume;
                    restored.Invalidate();
                }
            }
            core.SongCore.ActionStack.Do(new DelegateAction(() => core.SongCore.Wavetable.LoadWave(index, null, null, false), Restore, name: "Clear wave"));
            return null;
        }

        /// <summary>Plays a wave through the master (the Samples view's preview), from the start; Stop ends it.</summary>
        public static void Preview(ReBuzzCore core, int index, bool loop = false)
        {
            var wave = Wave(core, index);
            if (wave == null || wave.LayersList.Count == 0) return;
            Table(core).PlayWave(wave, 0, -1, loop ? LoopType.Unidirectional : LoopType.None, -1);
        }

        public static void StopPreview(ReBuzzCore core) => Table(core)?.StopPlayingWave();

        public static bool IsPreviewing(ReBuzzCore core) => Table(core)?.IsPlayingWave() == true;

        internal static WaveCore Wave(ReBuzzCore core, int index)
        {
            var list = Table(core)?.WavesList;
            return list != null && index >= 0 && index < list.Count ? list[index] : null;
        }
    }
}
