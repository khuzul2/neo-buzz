// Fork (docs/specs/stem-separation.md): stem separation, layout side. A source is an audio clip of the song or an audio file; a
// separation runs on a worker (the models are in ReBuzz/Core/AudioLibrary/Stems), and its result becomes one new Neo Audio track per stem
// (named after the stem), each with a clip aligned with the source: the same Session scene and the same arrangement positions, with the
// source clip's warp, tempo, gain and region. The stems' audio goes into the song's audio store. The whole application is one undo step.

using BuzzGUI.Common.Actions.SequenceActions;
using BuzzGUI.Common.Actions.SongActions;
using BuzzGUI.SequenceEditor.Actions;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core.AudioLibrary;
using ReBuzz.Core.AudioLibrary.Stems;
using ReBuzz.Core.Clips;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReBuzz.Core.Session
{
    /// <summary>What is separated: an audio clip of the song (its asset, and where it plays), or a file.</summary>
    internal sealed class StemSource
    {
        /// <summary>The clip's name or the file's name without its extension.</summary>
        public string Name { get; init; } = "";
        public IPattern Clip { get; init; }
        /// <summary>The clip's asset; null for a file until it is read.</summary>
        public AudioAsset Asset { get; set; }
        public string Path { get; init; }
        public int Channels => Asset?.Channels ?? 2;
    }

    /// <summary>One stem as it was put into the song.</summary>
    internal sealed class StemOutcome
    {
        public string Stem { get; init; } = "";
        public IMachine Machine { get; init; }
        public IPattern Pattern { get; init; }
        public AudioAsset Asset { get; init; }
        /// <summary>The Session scene (from 0) the clip is in, or -1.</summary>
        public int Scene { get; init; } = -1;
        /// <summary>The arrangement positions (ticks) of the clip.</summary>
        public List<int> Ticks { get; } = new();
    }

    internal sealed partial class SessionLayout
    {
        /// <summary>The source of an audio clip; null and a reason when the pattern isn't an audio clip.</summary>
        public StemSource StemSourceOfClip(IPattern clip, out string error)
        {
            error = null;
            var audio = AudioOf(clip);
            if (audio == null)
            {
                error = $"\"{clip?.Name}\" isn't an audio clip.";
                return null;
            }
            var asset = ClipAudioOf(audio)?.Asset;
            if (asset == null)
            {
                error = $"\"{clip.Name}\" has no audio in the song.";
                return null;
            }
            return new StemSource { Name = clip.Name, Clip = clip, Asset = asset };
        }

        /// <summary>The source of an audio file (read when the separation starts); null and a reason when it isn't one.</summary>
        public StemSource StemSourceOfFile(string path, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                error = $"No file at {path}.";
                return null;
            }
            if (!AudioImporter.IsAudioFile(path))
            {
                error = $"{System.IO.Path.GetFileName(path)} isn't a type {Product.Name} reads: WAV, AIFF, FLAC, OGG or MP3.";
                return null;
            }
            return new StemSource { Name = System.IO.Path.GetFileNameWithoutExtension(path), Path = path };
        }

        /// <summary>
        /// Separates the source on a worker thread. A file is decoded there too (at the song's rate). <paramref name="progress"/> is called
        /// from the worker with 0 to 1. The audio of a file is not added to the song; only the stems are.
        /// </summary>
        public Task<StemResult> SeparateAsync(StemSource source, StemQuality quality, IEnumerable<string> stems, Action<double> progress, CancellationToken cancel)
        {
            string missing = StemModels.Missing(quality);
            if (missing != null) return Task.FromException<StemResult>(new InvalidOperationException(missing));
            var model = StemModels.For(quality);
            int songRate = SampleRate;
            var wanted = StemSeparator.Normalise(stems);
            return Task.Run(() =>
            {
                float[] left, right;
                int rate;
                string key;
                if (source.Asset != null)
                {
                    (left, right) = source.Asset.Source.ToArrays();            // exact, from memory or the cache file
                    if (source.Asset.Channels < 2) right = left;
                    rate = source.Asset.SampleRate;
                    key = source.Asset.Id;
                }
                else
                {
                    var asset = AudioImporter.Import(source.Path, songRate);
                    source.Asset = asset;                                  // not in the store: only a file's stems are added
                    (left, right) = asset.Source.ToArrays();
                    if (asset.Channels < 2) right = left;
                    rate = asset.SampleRate;
                    key = asset.Id;
                }
                return StemSeparator.Separate(model, left, right, rate, wanted, key, progress, cancel);
            }, cancel);
        }

        /// <summary>
        /// Puts the stems in the song: a new Neo Audio track named after each stem with a clip of it. For a clip of the song the clips are in its
        /// Session scene, play where it plays in the arrangement, and share its warp, tempo, gain and region; for a file they are in
        /// <paramref name="scene"/> (from 0; default the first) and at <paramref name="arrangementTicks"/>, warped in Beats at the tempo found
        /// in the audio. One undo step. Null and a reason when it can't.
        /// </summary>
        internal List<StemOutcome> ApplyStems(StemSource source, StemResult result, int? scene, IReadOnlyList<int> arrangementTicks, out string error)
        {
            error = null;
            if (!CanMakeAudioTracks)
            {
                error = $"The {SessionClipHost.MachineName} machine isn't installed (Gear\\Effects), so {Product.Name} can't make audio tracks.";
                return null;
            }
            var asset = source.Asset;
            if (asset == null || result.Stems.Count == 0)
            {
                error = "There is nothing to put in the song.";
                return null;
            }
            var outcomes = new List<StemOutcome>();
            double bpm = 0;
            SessionDocument.ClipSettings sourceSettings = source.Clip != null ? ClipOf(source.Clip).Clone() : null;
            int sourceScene = -1;
            var sourceEvents = new List<(int Time, int Length)>();
            if (source.Clip != null)
            {
                for (int t = 0; t < TrackCount && sourceScene < 0; t++)
                    for (int s = 0; s < SceneCount; s++)
                        if (ReferenceEquals(PatternAt(t, s), source.Clip)) { sourceScene = s; break; }
                foreach (var seq in song.Sequences.Where(q => q.Machine == source.Clip.Machine))
                    foreach (var e in seq.Events)
                        if (e.Value.Type == SequenceEventType.PlayPattern && ReferenceEquals(e.Value.Pattern, source.Clip))
                            sourceEvents.Add((e.Key, e.Value.Span));
            }
            else
            {
                bpm = AudioAnalysis.Tempo(asset.Source, Session.BeatsPerBar).Item1;
            }
            int targetScene = sourceScene >= 0 ? sourceScene : Math.Max(0, scene ?? 0);

            UndoStack.BeginActionGroup();
            try
            {
                foreach (var stem in StemSeparator.AllStems)
                {
                    if (!result.Stems.TryGetValue(stem, out var pcm)) continue;
                    var audioSource = new AudioSource(pcm.Left, asset.Channels > 1 ? pcm.Right : pcm.Left, asset.SampleRate);
                    var stemAsset = Assets.Add(new AudioAsset($"{source.Name} ({stem})", "", null, audioSource, asset.Channels));
                    string pretty = char.ToUpperInvariant(stem[0]) + stem.Substring(1);
                    var machine = NewAudioTrack($"{pretty} - {source.Name}");
                    int track = machine == null ? -1 : FirstTrackOf(machine);
                    if (machine == null || track < 0)
                        throw new InvalidOperationException("Couldn't make an audio track.");
                    var audio = sourceSettings?.Audio?.Clone() ?? new SessionDocument.AudioSettings { Bpm = bpm };
                    audio.Asset = stemAsset.Id;
                    audio.Wave = 0;
                    while (SceneCount <= targetScene) InsertScene(SceneCount);
                    var pattern = AddAudioClip(track, targetScene, machine, $"{pretty} - {source.Name}", audio)
                        ?? throw new InvalidOperationException("Couldn't make a clip.");
                    if (sourceSettings != null)
                    {
                        var settings = sourceSettings.Clone();
                        settings.Audio = audio.Clone();
                        SetClipSettings(pattern, settings);
                        if (pattern.Length != source.Clip.Length) UndoStack.Do(new SessionPatternLengthAction(this, pattern, source.Clip.Length));
                        SetClipColor(pattern, PatternColors.Get(source.Clip));
                    }
                    var outcome = new StemOutcome { Stem = stem, Machine = machine, Pattern = pattern, Asset = stemAsset, Scene = targetScene };
                    var sequence = song.Sequences.FirstOrDefault(q => q.Machine == machine);
                    if (sequence != null)
                    {
                        foreach (var (time, _) in sourceEvents)
                        {
                            UndoStack.Do(new SetEventAction(sequence, time, new SequenceEvent(SequenceEventType.PlayPattern, pattern)));
                            outcome.Ticks.Add(time);
                        }
                        foreach (int tick in arrangementTicks ?? Array.Empty<int>())
                        {
                            UndoStack.Do(new SetEventAction(sequence, tick, new SequenceEvent(SequenceEventType.PlayPattern, pattern)));
                            outcome.Ticks.Add(tick);
                            int end = tick + pattern.Length;
                            if (song.SongEnd < end) UndoStack.Do(new SetMarkerAction(song, SongMarkers.SongEnd, end));
                        }
                    }
                    outcomes.Add(outcome);
                }
            }
            catch (Exception e)
            {
                UndoStack.EndActionGroup();
                try { UndoStack.Undo(); } catch (Exception) { }
                error = e.Message;
                return null;
            }
            UndoStack.EndActionGroup();
            return outcomes;
        }
    }
}
