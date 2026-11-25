using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using WDE.ModernPatternEditor.MPEStructures;

namespace WDE.ModernPatternEditor.MPEStructures
{
    internal class MPEClipDatabase
    {
        private Dictionary<string, MPEClip> ClipToMPEClip = new Dictionary<string, MPEClip>();
        private List<MPEClip> MPEClipList = new List<MPEClip>();
        private IMachine machine;
        private List<XMLClip> ImportedClips = new List<XMLClip>();

        public IMachine Machine
        {
            get { return machine; }
            set
            {
                if (machine != null)
                {
                    machine.PropertyChanged -= Machine_PropertyChanged;
                }
                machine = value;

                if (machine != null)
                {
                    machine.PropertyChanged += Machine_PropertyChanged;

                    foreach (var mpepat in MPEClipList)
                    {
                        mpepat.Clip = machine.Clips.FirstOrDefault(x => x.Name == mpepat.ClipName);
                        if (mpepat.Clip != null)
                            ClipToMPEClip[mpepat.Clip.Name] = mpepat;
                    }
                }
            }
        }


        public ClipEditor Editor { get; }

        public MPEClipDatabase(ClipEditor editor)
        {
            this.Editor = editor;
            ParamterMuted = new Dictionary<ParameterTrack, bool>();
        }

        private void Machine_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case "TrackCount":
                    {
                        foreach (var mpepat in MPEClipList)
                        {
                            //mpepat.UpdateData();
                        }
                    }
                    break;
            }
        }

        internal void ClipRenamed(IClip clip)
        {
            foreach (var mpeClip in MPEClipList)
            {
                if (mpeClip.Clip == clip)
                {
                    ClipToMPEClip.Remove(mpeClip.ClipName);
                    mpeClip.ClipName = clip.Name;
                    ClipToMPEClip.Add(mpeClip.ClipName, mpeClip);
                    break;
                }
            }
        }

        public MPEClip GetMPEClip(IClip p)
        {
            if (ClipToMPEClip.ContainsKey(p.Name))
                return ClipToMPEClip[p.Name];
            else
                return new MPEClip(Editor, p.Name);
        }

        public void Release()
        {
            Machine = null;
            foreach (var p in ClipToMPEClip.Values)
            {
                p.Clip = null;
            }
            
            MPEClipList.Clear();
            ClipToMPEClip.Clear();
            ImportedClips.Clear();
        }

        internal void AddClip(IClip clip)
        {
            MPEClip mpeClip = MPEClipList.FirstOrDefault(x => x.ClipName == clip.Name);
            if (mpeClip == null)
            {
                mpeClip = new MPEClip(Editor, clip.Name);
                var lastClip = GetClips().LastOrDefault();
                if (lastClip != null)
                {
                    mpeClip.RowsPerBeat = lastClip.RowsPerBeat;
                }
                mpeClip.Clip = clip;
                MPEClipList.Add(mpeClip);
            }
            else
            {
                mpeClip.Clip = clip;
            }
            ClipToMPEClip.Add(clip.Name, mpeClip);

            // If user imported patterns
            if (ImportedClips.Count > 0)
            {
                var xClip = ImportedClips[0];
                ImportedClips.RemoveAt(0);

                mpeClip.SetBeatCount(xClip.LenghtInBeats);
                mpeClip.RowsPerBeat = xClip.RowsPerBeat;

                foreach (var c in xClip.Columns)
                {
                    var mpeCol = mpeClip.MPEClipColumns.FirstOrDefault(x => x.Parameter.Name == c.Parameter
                        && x.ParamTrack == c.Track && x.Machine.Name == c.Machine);
                    if (mpeCol == null)
                    {
                        IMachine mac = Global.Buzz.Song.Machines.FirstOrDefault(x => x.Name == c.Machine);
                        if (mac != null)
                        {
                            IParameter par = mac.AllParameters().FirstOrDefault(x => x.Name == c.Parameter);
                            mpeCol = mpeClip.CreateNewColumn(par, c.Track);
                        }
                    }

                    mpeCol.SetBeats(c.Beats.ToList());
                    mpeCol.SetEvents(c.Events, true, false);
                }
            }

            if (PrepareToCopyClip)
                DoCopyClip();
        }


        internal void SetClips(List<MPEClip> clips)
        {
            MPEClipList = clips;
            foreach (var mpepat in MPEClipList)
            {
                mpepat.Clip = machine.Clips.FirstOrDefault(x => x.Name == mpepat.ClipName);
                if (mpepat.Clip != null)
                    ClipToMPEClip[mpepat.Clip.Name] = mpepat;
            }
        }

        internal IEnumerable<MPEClip> GetClips()
        {
            return MPEClipList;
        }

        internal void RemoveClip(IClip clip)
        {
            MPEClip mpeClip = MPEClipList.FirstOrDefault(x => x.ClipName == clip.Name);
            if (mpeClip != null)
            {
                MPEClipList.Remove(mpeClip);
                ClipToMPEClip.Remove(clip.Name);
            }
        }

        internal bool IsParameterEnabled(IParameter tPar)
        {
            // Need to check only the first one because all patterns have same columns
            var clip = GetMPEClip(Editor.SelectedMachine.SelectedClip.Clip);
            foreach (var column in clip.MPEClipColumns)
            {
                if (column.Parameter == tPar)
                    return true;
            }
            return false;
        }

        private bool PrepareToCopyClip { get; set; }
        private string CopyClipOldName { get; set; }
        private string CopyClipNewName { get; set; }

        internal void PrepareCopy(string newName, string oldName)
        {
            CopyClipOldName = oldName;
            CopyClipNewName = newName;
            PrepareToCopyClip = true;
        }

        private void DoCopyClip()
        {
            var oldClip = Machine.Clips.FirstOrDefault(x => x.Name == CopyClipOldName);
            var newClip = Machine.Clips.FirstOrDefault(x => x.Name == CopyClipNewName);
            if (oldClip != null && newClip != null)
            {
                var oldMPEClip = GetMPEClip(oldClip);
                var newMPEClip = GetMPEClip(newClip);

                newMPEClip.RowsPerBeat = oldMPEClip.RowsPerBeat;

                foreach (var oldCol in GetMPEClip(oldClip).MPEClipColumns)
                {
                    int index = oldMPEClip.MPEClipColumns.IndexOf(oldCol);
                    var newCol = newMPEClip.MPEClipColumnsDict[index];
                    newCol.SetEvents(oldCol.GetEvents(0, oldClip.Length * ClipEvent.TimeBase).ToArray(), true);
                }
            }
            PrepareToCopyClip = false;
            CopyClipOldName = CopyClipNewName = "";
        }

        internal void ClipImported(XMLClip xClip)
        {
            ImportedClips.Add(xClip);
        }

        // Fork (neo-buzz audio hygiene): equality of its own. The default (ValueType's, by
        // reflection) boxed the fields on every lookup: IsColumnMuted asks for every column on
        // every tick, on the audio thread. The same fields, compared as before.
        public struct ParameterTrack : IEquatable<ParameterTrack>
        {
            public IParameter parameter;
            public int track;

            public bool Equals(ParameterTrack other) => Equals(parameter, other.parameter) && track == other.track;
            public override bool Equals(object obj) => obj is ParameterTrack other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(parameter, track);
        }

        public Dictionary<ParameterTrack, bool> ParamterMuted { get; set; }
        internal void SetColumnsMuteFlag(ParameterColumnSet pcs)
        {
            foreach(var c in pcs.Columns)
            {
                var c2 = (ParameterColumn)c;
                var parameter = c2.ClipColumn.Parameter;
                
                ParameterTrack pt = new ParameterTrack() { parameter = parameter, track = c2.ClipColumn.Track };
                ParamterMuted[pt] = pcs.Muted;
            }
        }

        internal bool IsColumnMuted(IParameter parameter, int paramTrack)
        {
            ParameterTrack pt = new ParameterTrack() { parameter = parameter, track = paramTrack };
            return ParamterMuted.TryGetValue(pt, out bool ret) && ret;
        }
    }
}
