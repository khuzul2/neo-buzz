using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace WDE.AudioBlock
{
    public class WaveData
    {
        public int ChannelCount { get; internal set; }
        public WaveFormat Format { get; internal set; }
        public int LoopEnd { get; internal set; }
        public int LoopStart { get; internal set; }
        public int RootNote { get; internal set; }
        public int SampleCount { get; internal set; }
        public int SampleRate { get; internal set; }
        public float[] SampleDataLeft { get; internal set; }
        public float[] SampleDataRight { get; internal set; }
        public int WaveIndex { get; internal set; }
        public string Name { get; internal set; }
        public float Volume { get; internal set; }
        public string Path { get; internal set; }
    }
    /// <summary>
    /// Fork (docs/specs/history-snapshots.md HS1): an AudioBlock wave's edits are steps of the song's one undo history
    /// (the machine graph's action stack), not a level of their own: as many as the history's memory cap keeps, in step with
    /// every other edit. <see cref="SaveData"/> is called before an edit, as before; the wave's data then is the step's
    /// "before", and the "after" is taken when the step is first undone.
    /// </summary>
    public class WaveUndo
    {
        private AudioBlock audioBlock;

        public bool Working { get; private set; }

        /// <param name="maxSize">Not used: the song's history keeps as many steps as its memory cap allows.</param>
        public WaveUndo(int maxSize, AudioBlock ab)
        {
            this.audioBlock = ab;
        }

        IMachineGraph Graph => audioBlock.host.Machine.Graph;

        public void SaveData(int waveIndex)
        {
            IWavetable wt = Graph.Buzz.Song.Wavetable;
            if (wt.Waves[waveIndex] == null || wt.Waves[waveIndex].Layers.Count == 0)
                return;

            WaveData waveData = GetWaveData(waveIndex);
            if (waveData.SampleDataLeft == null)
                return;

            Graph.Do(new WaveEditAction(this, waveData));
        }

        public WaveData GetWaveData(int waveIndex)
        {
            WaveData waveData = new WaveData();

            //lock (syncLock)
            {
                IWavetable wt = audioBlock.host.Machine.Graph.Buzz.Song.Wavetable;

                if (wt.Waves[waveIndex] != null && wt.Waves[waveIndex].Layers.Count > 0)
                {
                    IWaveLayer waveLayer = wt.Waves[waveIndex].Layers[0];

                    waveData.WaveIndex = waveIndex;
                    waveData.ChannelCount = waveLayer.ChannelCount;
                    waveData.Format = waveLayer.Format;
                    waveData.LoopEnd = waveLayer.LoopEnd;
                    waveData.LoopStart = waveLayer.LoopStart;
                    waveData.RootNote = waveLayer.RootNote;
                    waveData.SampleCount = waveLayer.SampleCount;
                    waveData.SampleRate = waveLayer.SampleRate;
                    waveData.Path = waveLayer.Path;
                    waveData.Name = wt.Waves[waveIndex].Name;
                    waveData.Volume = wt.Waves[waveIndex].Volume;

                    waveData.SampleDataLeft = new float[waveData.SampleCount];

                    waveLayer.GetDataAsFloat(waveData.SampleDataLeft, 0, 1, 0, 0, waveData.SampleCount);
                    if (waveData.ChannelCount == 2)
                    {
                        waveData.SampleDataRight = new float[waveData.SampleCount];
                        waveLayer.GetDataAsFloat(waveData.SampleDataRight, 0, 1, 1, 0, waveData.SampleCount);
                    }
                }
            }
            return waveData;
        }

        /// <summary>Undoes the song's newest step (a wave edit, or whatever was done last).</summary>
        public void Undo()
        {
            if (Working || !Graph.CanUndo)
                return;
            Graph.Undo();
        }

        public void Redo()
        {
            if (Working || !Graph.CanRedo)
                return;
            Graph.Redo();
        }

        /// <summary>Puts a wave's data back (the step's undo and redo).</summary>
        internal void Apply(WaveData waveData)
        {
            if (Working || waveData?.SampleDataLeft == null)
                return;

            Working = true;
            try
            {
                IWavetable wt = Graph.Buzz.Song.Wavetable;
                bool stereo = waveData.ChannelCount != 1;

                wt.AllocateWave(waveData.WaveIndex, waveData.Path, waveData.Name, waveData.SampleCount, WaveFormat.Float32, stereo, waveData.RootNote, false, true);
                var targetLayer = wt.Waves[waveData.WaveIndex].Layers.Last();

                targetLayer.LoopStart = waveData.LoopStart;
                targetLayer.LoopEnd = waveData.LoopEnd;
                targetLayer.SampleRate = waveData.SampleRate;
                wt.Waves[waveData.WaveIndex].Volume = waveData.Volume;

                targetLayer.SetDataAsFloat(waveData.SampleDataLeft, 0, 1, 0, 0, waveData.SampleCount); // Mono or left
                if (stereo && waveData.SampleDataRight != null)
                    targetLayer.SetDataAsFloat(waveData.SampleDataRight, 0, 1, 1, 0, waveData.SampleCount); // Right
                targetLayer.InvalidateData();

                audioBlock.ReCreateEnvPoints();
            }
            finally
            {
                Working = false;
            }
        }

        /// <summary>
        /// One wave edit as a step. The wave is already edited when SaveData is called, so the first Do does nothing; the
        /// edited data is read the first time the step is undone (everything made after it has been undone by then).
        /// </summary>
        sealed class WaveEditAction : IAction, IHistoryAction
        {
            readonly WaveUndo owner;
            readonly WaveData before;
            WaveData after;
            bool skipDo = true;

            public WaveEditAction(WaveUndo owner, WaveData before)
            {
                this.owner = owner;
                this.before = before;
            }

            string IHistoryAction.HistoryName => "Edit wave " + (before.WaveIndex + 1);

            long IHistoryAction.HistoryBytes => 0;

            // Counted twice: the "after" taken on the first undo is about as big.
            IEnumerable<KeyValuePair<object, long>> IHistoryAction.HistoryBlobs
            {
                get
                {
                    yield return new KeyValuePair<object, long>(before.SampleDataLeft, 8L * before.SampleDataLeft.Length);
                    if (before.SampleDataRight != null)
                        yield return new KeyValuePair<object, long>(before.SampleDataRight, 8L * before.SampleDataRight.Length);
                }
            }

            public void Do()
            {
                if (skipDo) { skipDo = false; return; }
                if (after != null) owner.Apply(after);
            }

            public void Undo()
            {
                after ??= owner.GetWaveData(before.WaveIndex);
                owner.Apply(before);
            }
        }
    }
}
