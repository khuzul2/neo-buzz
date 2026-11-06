using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.ComponentModel;
using System.Collections.ObjectModel;
using Buzz.MachineInterface;
using BuzzGUI.Interfaces;
using BuzzGUI.Common;

namespace WDE.MidiOutput
{
    [MachineDecl(Name = "Midi Output", ShortName = "Midi Out", Author = "WDE", MaxTracks = 32)]
	public class MidiOutputMachine : IBuzzMachine, INotifyPropertyChanged
	{
		IBuzzMachineHost host;

		internal class NoteEvent
		{
			internal int device;
			internal Note note;
            internal int track;
            internal int velocity;
            internal int delayInSamples;
            internal int cutInSamples;
			internal int channel;

            public bool NoteOnHandled { get; internal set; }

			internal NoteEvent Copy()
			{
				NoteEvent e = new NoteEvent();
				e.device = device;
				e.note = note;
				e.velocity = velocity;
				e.channel = channel;
				e.delayInSamples = delayInSamples;
				e.cutInSamples = cutInSamples;
				e.track = track;
				return e;
			}
        }

        internal class MidiCCEvent
        {
            internal int control;
            internal int value;
            internal int channel;
            internal int device;
        }

        Dictionary<int, NoteEvent> pNote = new Dictionary<int, NoteEvent>();
        Dictionary<int, NoteEvent> noteEvents = new Dictionary<int, NoteEvent>();
        Dictionary<int, MidiCCEvent> ccEvents = new Dictionary<int, MidiCCEvent>();
        public MidiOutputMachine(IBuzzMachineHost host)
		{
			this.host = host;
        }

        NoteEvent GetNoteEvent(int track)
		{
			if (!noteEvents.ContainsKey(track))
				noteEvents.Add(track, new NoteEvent());

			return noteEvents[track];
		}

        MidiCCEvent GetCCEvent(int track)
        {
            if (!ccEvents.ContainsKey(track))
                ccEvents.Add(track, new MidiCCEvent());

            return ccEvents[track];
        }

        [ParameterDecl(MinValue = 0, MaxValue = 255, DefValue = 0)]
        public void Device(int value, int track)
        {
		}

        [ParameterDecl(IsStateless = true)]
		public void Note(Note value, int track)
		{
			GetNoteEvent(track).note = value;
        }

        Dictionary<int,int> dictVelocity = new Dictionary<int, int>();
        [ParameterDecl(IsStateless = true, MinValue = 0, MaxValue =127, DefValue = 120)]
        public void Volume(int value, int track)
        {
            dictVelocity[track] = value;
        }

        [ParameterDecl(IsStateless = true, MinValue = 0, MaxValue = 127, DefValue = 0)]
        public void Delay(int value, int track)
        {
            GetNoteEvent(track).delayInSamples = (int)(value / 127.0 * host.MasterInfo.SamplesPerTick);
        }

        [ParameterDecl(IsStateless = true, MinValue = 0, MaxValue = 127, DefValue = 0)]
        public void Cut(int value, int track)
        {
            GetNoteEvent(track).cutInSamples = (int)(value / 127.0 * host.MasterInfo.SamplesPerTick);
        }

        [ParameterDecl(MinValue = 0, MaxValue = 254, DefValue = 0)]
        public void MidiCC(int value, int track)
        {	
        }

        [ParameterDecl(MinValue = 0, MaxValue = 0xfffe, DefValue = 0)]
        public void MidiCCValue(int value, int track)
        {
            GetCCEvent(track).value = value;
        }

        [ParameterDecl(MinValue = 0, MaxValue = 15, DefValue = 0)]
        public void Channel(int value, int track)
        {
        }

        // Fork (docs/specs/mcp-server.md §12.13): the track parameters Work reads, by their place in the
        // group. ReBuzz orders a machine's track parameters as they are declared above (by metadata token):
        // Device, Note, Volume, Delay, Cut, MidiCC, MidiCCValue, Channel. Work read the controller number
        // from 3 (Delay) and the channel from 5 (MidiCC), the places they had before Delay and Cut were
        // declared, and a velocity no Volume had set from 0's default (Device's: 0, which MIDI takes for a
        // note-off); and it encoded every message without its channel, so all went out on channel 1.
        const int DeviceIndex = 0, VolumeIndex = 2, MidiCCIndex = 5, ChannelIndex = 7;

        // MidiCC 0–127 is a controller number; 224 (0xE0, the status byte's) sends MidiCCValue as the pitch
        // wheel, 14 bits; any other number names no message and sends nothing. A value beyond what its
        // message holds (127 for a controller, 16383 for the wheel) is sent as the highest: on Windows the
        // excess went out as a data byte with its top bit set (a status byte to the receiver), on macOS
        // wrapped round (CoreMidi.Sender keeps a data byte's low 7 bits).
        const int MaxControllerValue = 127, MaxPitchWheelValue = 16383;

        public bool Work(Sample[] output, int n, WorkModes mode)
		{

			for (int t = 0; t < host.Machine.TrackCount; t++)
			{
				var pg = host.Machine.ParameterGroups[2];

				if (noteEvents.ContainsKey(t))
				{
					if (noteEvents[t].note.Value != 0)
					{
                        NoteEvent ne = noteEvents[t];
                        ne.device = pg.Parameters[DeviceIndex].GetValue(t);
                        ne.velocity = dictVelocity.ContainsKey(t) ? dictVelocity[t] : pg.Parameters[VolumeIndex].DefValue;
                        ne.channel = pg.Parameters[ChannelIndex].GetValue(t) & 0x0F;
                    }
					else
					{
						noteEvents.Remove(t);
					}
                }

				if (ccEvents.ContainsKey(t))
				{
					var ce = ccEvents[t];
                    ce.device = pg.Parameters[DeviceIndex].GetValue(t);
                    ce.control = pg.Parameters[MidiCCIndex].GetValue(t);
                    ce.channel = pg.Parameters[ChannelIndex].GetValue(t) & 0x0F;
                }
			}

			foreach (var t in noteEvents.Keys.ToArray())
			{
				var ne = noteEvents[t];
				ne.delayInSamples -= n;
                ne.cutInSamples -= n;

                if (ne.delayInSamples <= 0)
				{
					if (ne.note.Value == BuzzNote.Off)
					{
						MidiNoteOff(t);
                        noteEvents.Remove(t);
                    }
					else if (!ne.NoteOnHandled)
					{
						MidiNoteOff(t);
						int midiNote = BuzzNote.ToMIDINote(ne.note.Value);
						pNote[t] = ne.Copy();
						var data = MIDI.Encode(MIDI.NoteOn, ne.channel, midiNote, ne.velocity);
						Global.Buzz.SendMIDIOutput(ne.device, data);
						ne.NoteOnHandled = true;

						if (ne.delayInSamples >= ne.cutInSamples)
						{
							// Noi need to cut == no need to send note off
                            noteEvents.Remove(t);
                        }
					}
					else if (ne.delayInSamples < ne.cutInSamples && ne.cutInSamples <= 0)
					{
						MidiNoteOff(t);
                        noteEvents.Remove(t);
                    }
				}
			}

			foreach (var t in ccEvents.Keys.ToArray())
			{
				var ce = ccEvents[t];
				int v = ce.value;
				if (ce.control == MIDI.PitchWheel)
				{
                    v = Math.Min(v, MaxPitchWheelValue);
                    var data = MIDI.Encode(MIDI.PitchWheel, ce.channel, v & 127, v >> 7);
					Global.Buzz.SendMIDIOutput(ce.device, data);
                }
				else if (ce.control <= 127)
				{
					var data = MIDI.Encode(MIDI.ControlChange, ce.channel, ce.control, Math.Min(v, MaxControllerValue));
					Global.Buzz.SendMIDIOutput(ce.device, data);
                }
                ccEvents.Remove(t);
            }
			
			return false;
		}

		void MidiNoteOff(int t)
		{
            if (pNote.ContainsKey(t))
            {
                var pn = pNote[t];
                int midiNote = BuzzNote.ToMIDINote(pn.note.Value);
                var data = MIDI.Encode(MIDI.NoteOff, pn.channel, midiNote, pn.velocity);   // the channel the note went out on
                pNote.Remove(t);
                Global.Buzz.SendMIDIOutput(pn.device, data);
            }
        }
		
		public class State : INotifyPropertyChanged
		{
			public State()
			{	
			}	// NOTE: parameterless constructor is required by the xml serializer

            public event PropertyChangedEventHandler PropertyChanged;
		}

		State machineState = new State();
        private bool sendCC;

        public State MachineState			// a property called 'MachineState' gets automatically saved in songs and presets
		{
			get { return machineState; }
			set
			{
				machineState = value;
				if (PropertyChanged != null) PropertyChanged(this, new PropertyChangedEventArgs("MachineState"));
            }
		}		
		
		public IEnumerable<IMenuItem> Commands
		{
			get
			{
				yield return new MenuItemVM() 
				{ 
					Text = "About...", 
					Command = new SimpleCommand()
					{
						CanExecuteDelegate = p => true,
						ExecuteDelegate = p => MessageBox.Show("ReBuzz Midi Output 0.1 (C) 2024 WDE")
					}
				};
			}
		}

        public void ImportFinished(IDictionary<string, string> machineNameMap)
		{
		}

        public event PropertyChangedEventHandler PropertyChanged;
	}

	public class MachineGUIFactory : IMachineGUIFactory { public IMachineGUI CreateGUI(IMachineGUIHost host) { return new MidiOutputGUI(); } }
	public class MidiOutputGUI : UserControl, IMachineGUI
	{
		IMachine machine;
		MidiOutputMachine midiOutputMachine;
		

        public IMachine Machine
		{
			get { return machine; }
			set
			{
				if (machine != null)
				{
                }

				machine = value;

				if (machine != null)
				{
					midiOutputMachine = (MidiOutputMachine)machine.ManagedMachine;
                }
			}
		}

        // view model for machine list box items
        public class MidiOutsVM
        {
            public int DeviceNum { get; private set; }
            public string Name { get; private set; }

            public MidiOutsVM(int n, string name) { DeviceNum = n; Name = name; }
            public override string ToString() { return DeviceNum + ": " + Name; }
        }

        public ObservableCollection<MidiOutsVM> MidiOuts { get; private set; }

        public MidiOutputGUI()
		{
			MidiOuts = new ObservableCollection<MidiOutsVM>();

			StackPanel sp = new StackPanel();
			ListBox lb = new ListBox() { Height = 160, Margin = new Thickness(0, 0, 0, 4) };

			IsVisibleChanged += (sender, e) =>
			{
				if (Visibility == Visibility.Visible)
					UpdateMidiOuts();

            };

            lb.SetBinding(ListBox.ItemsSourceProperty, new Binding("MidiOuts") { Source = this, Mode = BindingMode.OneWay });
			sp.Children.Add(lb);

            this.Content = sp;	
		}

		void UpdateMidiOuts()
		{
            MidiOuts.Clear();

            foreach (var mo in Global.Buzz.GetMidiOuts())
                MidiOuts.Add(new MidiOutsVM(mo.Item1, mo.Item2));
        }
	}
}
