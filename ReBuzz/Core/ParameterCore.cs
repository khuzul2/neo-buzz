using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;

namespace ReBuzz.Core
{
    internal class ParameterCore : IParameter, UiEventQueue.ITarget
    {
        public enum InternalParameter
        {
            SPGlobalTrigger = -10,
            SPGlobalEffect1 = -11,
            SPGlobalEffect1Data = -12,

            SPTrackTrigger = -110,
            SPTrackEffect1 = -111,
            SPTrackEffect1Data = -112,

            MidiNote = -128,
            MidiVelocity = -129,
            MidiNoteDelay = -130,
            MidiNoteCut = -131,
            MidiPitchWheel = -132,
            MidiCC = -133
        };

        public IParameterGroup Group { get; set; }

        public int IndexInGroup { get; set; }
        
        public ParameterType Type { get; set; }

        public string Name { get; set; }

        public string Description { get; set; }

        public int MinValue { get; set; }

        public int MaxValue { get; set; }

        public int NoValue { get; set; }

        //readonly Lock paramLock = new();

        public ParameterFlags Flags { get; set; }

        public int DefValue { get; set; }
        internal ConcurrentDictionary<int, (int, int)> MidiBindings { get => midiBindings; set => midiBindings = value; }

        ConcurrentDictionary<int, (int, int)> midiBindings = new ConcurrentDictionary<int, (int, int)>();
       
        public (int, int) GetMIDIBinding(int track)
        {
            if (MidiBindings.TryGetValue(track, out var binding))
                return binding;

            return (-1, -1);
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event Action<int> MIDIBindingChanged;

        public void BindToMIDIController(int track, int mcindex)
        {
            var buzz = Group.Machine.Graph.Buzz as ReBuzzCore;
            buzz.MidiControllerAssignments.BindParameter(this, track, mcindex);
        }

        public void BindToMIDIController(int track, int channel, int controller)
        {
            var buzz = Group.Machine.Graph.Buzz as ReBuzzCore;
            buzz.MidiControllerAssignments.BindParameter(this, track, channel, controller);
        }

        int describeValuePrev = -1;
        string descriveValuePrevString = null;
        public string DescribeValue(int value)
        {
            string ret = "";
            // Avoid calling native machines too often
            if (describeValuePrev == value)
            {
                return descriveValuePrevString;
            }

            if (Group.Type == ParameterGroupType.Input)
            {
                if (IndexInGroup == 0)
                {
                    ret = value > 0 ? string.Format("{0:F1}dB", Decibel.FromAmplitude(value * (1.0 / 0x4000))) : "-inf.dB";
                }
                else if (IndexInGroup == 1)
                {
                    string pan = value.ToString();
                    if (value == 0)
                        pan = "L";
                    else if (value == 0x4000)
                        pan = "C";
                    else if (value == 0x8000)
                        pan = "R";

                    ret = pan;
                }
            }
            else
            {
                var machine = Group.Machine as MachineCore;
                var buzz = machine.Graph.Buzz as ReBuzzCore;
                int index = machine.AllNonInputParameters().FindIndex(p => p == this);

                ret = buzz.MachineManager.DescribeValue(machine, index, value);

                if (ret == null)
                {
                    ret = value.ToString();
                }
            }
            describeValuePrev = value;
            descriveValuePrevString = ret;
            return ret;
        }

        public void SetDisplayName(int track, string name)
        {
            displayNames[track] = name;
        }

        public string GetDisplayName(int track)
        {
            if (Group.Type == ParameterGroupType.Input)
            {
                return displayNames.ContainsKey(track) ? displayNames[track] : "";
            }
            else
            {
                return Name;
            }
        }

        // Return values between min & max. No NoValue
        public int GetValue(int track)
        {
            if (values.TryGetValue(track, out int val))
            {
                return val;
            }
            values[track] = DefValue;
            pvalues[track] = DefValue;

            return DefValue;
        }

        // Return current value. Used to send calue to native machines
        public int GetPValue(int track)
        {
            //if (!pvalues.ContainsKey(track))
            //    return NoValue;

            return pvalues[track];
        }

        public void SetPValue(int track, int val)
        {
            pvalues[track] = val;
        }

        public void ClearPVal()
        {
            //pvalues.Clear();
            Array.Fill(pvalues, NoValue, 0, Group.TrackCount);
        }

        public bool IsValidAsciiChar(int ch)
        {
            return true;
        }

        ConcurrentDictionary<int, int> values = new ConcurrentDictionary<int, int>();
        // readonly ConcurrentDictionary<int, int> pvalues = new ConcurrentDictionary<int, int>();
        int[] pvalues = new int[256]; 
        // Fork (engine hardening): the tracks changed since the machine's last tick, a bit a
        // pval, and those its tick in progress took (ParameterChanges).
        internal readonly long[] changedTracks = new long[ParameterChanges.Words];
        internal readonly long[] takenTracks = new long[ParameterChanges.Words];
        // Fork (audio hygiene): the tracks whose value and description events are on their way to
        // the listeners (UiEventQueue), a bit a track.
        readonly long[] valueEventPending = new long[ParameterChanges.Words];
        readonly long[] descriptionEventPending = new long[ParameterChanges.Words];
        UiEventQueue uiEvents;
        const int ValueEvent = 0, DescriptionEvent = 1;
        readonly Dictionary<int, string> displayNames = new Dictionary<int, string>();

        // Is ConcurrentDictionary needed. Adds locks and latency?
        private readonly ConcurrentDictionary<int, EventManager> valueChangedEvent = new ConcurrentDictionary<int, EventManager>();
        private readonly ConcurrentDictionary<int, EventManager> valueDescrtiptionChangedEvent = new ConcurrentDictionary<int, EventManager>();

        public ParameterCore(IUiDispatcher dispatcher)
        {
            this.dispatcher = dispatcher;

            Description = "";
        }
        internal class EventManager
        {
            public event Action<IParameter, int> Event;

            public bool HasListeners => Event != null;

            /// <summary>The listeners, on the UI thread (the UiEventQueue's drain).</summary>
            public void Call(IParameter parameter, int track)
            {
                try
                {
                    Event?.Invoke(parameter, track);
                }
                catch (Exception e)
                {
                    Global.Buzz?.DCWriteLine(e.Message);
                }
            }

            internal void ClearEvents()
            {
                Event = null;
            }
        }

        private readonly IUiDispatcher dispatcher;

        /// <summary>
        /// The machine's tick sent a value of this parameter on <paramref name="track"/>: its value
        /// and description listeners are told on the UI thread. Fork (audio hygiene): queued
        /// (UiEventQueue), not a closure posted for each value (and, for a description listener,
        /// added to a timer's handlers for good, with one pending across all parameters and the
        /// others dropped); allocates nothing.
        /// </summary>
        internal void InvokeEvents(IBuzz buzz, int track)
        {
            if (uiEvents == null)
                return;                                 // nobody ever subscribed
            if (valueChangedEvent.TryGetValue(track, out EventManager em) && em.HasListeners)
                Raise(valueEventPending, track, ValueEvent);
            if (valueDescrtiptionChangedEvent.TryGetValue(track, out EventManager emd) && emd.HasListeners)
                Raise(descriptionEventPending, track, DescriptionEvent);
        }

        void Raise(long[] pending, int track, int kind)
        {
            long bit = 1L << (track & 63);
            if ((Interlocked.Or(ref pending[track >> 6], bit) & bit) == 0)
                uiEvents.Enqueue(this, track, kind);    // else on its way already: the listeners read the value then
        }

        /// <summary>UI thread (the queue's drain): clears the event's flag, then calls its listeners.</summary>
        void UiEventQueue.ITarget.Deliver(int track, int kind)
        {
            var pending = kind == ValueEvent ? valueEventPending : descriptionEventPending;
            Interlocked.And(ref pending[track >> 6], ~(1L << (track & 63)));
            var map = kind == ValueEvent ? valueChangedEvent : valueDescrtiptionChangedEvent;
            if (map.TryGetValue(track, out EventManager em))
                em.Call(this, track);
        }

        internal void DirectSetValue(int track, int value)
        {
            values[track] = value;
        }

        public void SetValue(int track, int value)
        {
            if (Group == null)
            {
                // ParameterCore is used as a temporary storage in BMXFile. FIXME?
                return;
            }

            var machine = Group.Machine as MachineCore;

            // Lock machine here and MachineWorkInstance.cs Tick to avoid potential collision
            lock (machine.workLock)
            {
                track = track == -1 ? 0 : track;

                int do_not_record_flag = 1 << 16;
                bool record = true;
                if ((track & do_not_record_flag) != 0)
                {
                    record = false;
                    track &= ~do_not_record_flag;
                }

                // This is sent to native machines
                pvalues[track] = value;

                // This is the value of GetValue(in track)
                values[track] = value;


                if (Type == ParameterType.Note && value != BuzzNote.Off)
                    value = Math.Max(MinValue, Math.Min(MaxValue, value));

                // Update inputs. Should this be moved to tick?
                // Fork (engine hardening): the lists themselves and the visible input by index; the
                // ParameterGroups, Parameters and Inputs facades built new collections each call.
                if (Group.Type == ParameterGroupType.Input && machine.ParameterGroupsList.Count != 0)
                {
                    var input = IndexInGroup < machine.ParameterGroupsList[0].ParametersList.Count ? machine.VisibleInputAt(track) : null;
                    if (input != null)
                    {
                        if (IndexInGroup == 0)
                        {
                            input.Amp = value;
                        }
                        else
                        {
                            input.Pan = value;
                        }
                    }
                }
                else if (record && Group != null && Group.Machine.Graph != null)
                {
                    var bc = Group.Machine.Graph.Buzz as ReBuzzCore;
                    //bc.RecordParametersDictionary.TryAdd(new Tuple<ParameterCore,int>(this, track), value);
                    bc.RecordControlChange(this, track, value);
                }


                // Save changes to be sent to managed machines and as events to listeners.
                // Fork (engine hardening): preallocated bits, not a dictionary entry (ParameterChanges).
                machine.ParameterChanges.Mark(this, track);
            }
        }

        public void SubscribeEvents(int track, Action<IParameter, int> valueChanged, Action<IParameter, int> valueDescriptionChanged)
        {
            // Fork (audio hygiene): the engine's queue of UI events, with room for this one's.
            var events = uiEvents ??= UiEventQueue.For(dispatcher);
            if (valueChanged != null)
            {
                if (!valueChangedEvent.ContainsKey(track) && valueChangedEvent.TryAdd(track, new EventManager()))
                    events.Reserve(1);

                var em = valueChangedEvent[track];
                em.Event += valueChanged;
            }
            if (valueDescriptionChanged != null)
            {
                if (!valueDescrtiptionChangedEvent.ContainsKey(track) && valueDescrtiptionChangedEvent.TryAdd(track, new EventManager()))
                    events.Reserve(1);

                var em = valueDescrtiptionChangedEvent[track];
                em.Event += valueChanged;
            }
        }

        public void UnsubscribeEvents(int track, Action<IParameter, int> valueChanged, Action<IParameter, int> valueDescriptionChanged)
        {
            if (valueChanged != null)
            {
                if (valueChangedEvent.ContainsKey(track))
                {
                    var em = valueChangedEvent[track];
                    em.Event -= valueChanged;
                }
            }
            if (valueDescriptionChanged != null)
            {
                if (valueDescrtiptionChangedEvent.ContainsKey(track))
                {
                    var em = valueDescrtiptionChangedEvent[track];
                    em.Event -= valueChanged;
                }
            }
        }

        internal int GetTypeSize()
        {
            if (Type == ParameterType.Note ||
                Type == ParameterType.Switch ||
                Type == ParameterType.Byte)
            {
                return 1;
            }
            else if (Type == ParameterType.Word)
            {
                return 2;
            }
            return 0;
        }

        internal ParameterCore Clone()
        {
            ParameterCore p = new ParameterCore(dispatcher)
            {
                Type = Type,
                Name = Name,
                MinValue = MinValue,
                MaxValue = MaxValue,
                DefValue = DefValue,
                NoValue = NoValue,
                Flags = Flags,
                Description = Description,
                Group = Group,
                IndexInGroup = IndexInGroup,
                values = values
            };

            return p;
        }

        internal void ClearEvents()
        {
            //PropertyChanged = null;
            foreach (var em in valueChangedEvent.Values)
            {
                em.ClearEvents();
            }
            foreach (var em in valueDescrtiptionChangedEvent.Values)
            {
                em.ClearEvents();
            }
            valueChangedEvent.Clear();
            valueDescrtiptionChangedEvent.Clear();
        }

        internal static ParameterCore GetMidiParameter(MachineCore machine, IUiDispatcher dispatcher)
        {
            ParameterCore parameter = new ParameterCore(dispatcher);
            parameter.Name = "MIDI Note";
            parameter.Description = "MIDI Note";
            parameter.MinValue = BuzzNote.Min;
            parameter.MaxValue = BuzzNote.Max;
            parameter.DefValue = BuzzNote.Parse("C-4");
            parameter.NoValue = 0;
            parameter.Type = ParameterType.Note;
            parameter.Flags = ParameterFlags.State | ParameterFlags.TickOnEdit;
            parameter.SetValue(0, parameter.NoValue);
            parameter.IndexInGroup = -1;// (int)InternalParameter.MidiNote
            parameter.Group = machine.ParameterGroups[2]; // Put Midi parameters to to Track Group

            return parameter;
        }

        internal void SetMIDIBindingValues(int track, int midiChannel, int midiController)
        {
            MidiBindings[track] = (midiChannel, midiController);
            MIDIBindingChanged?.Invoke(track);
        }

        internal void RemoveMIDIBinding(int track)
        {
            if (MidiBindings.TryRemove(track, out (int, int) _))
            {
                MIDIBindingChanged?.Invoke(track);
            }
        }
    }
}
