using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BuzzGUI.Interfaces;

namespace BuzzGUI.SequenceEditor
{
	class EventRef
	{
		public SequenceEventType Type { get; private set; }
		public string PatternName { get; private set; }
		public int PatternLength { get; private set; }
		// Fork (0.5.1): the part of the pattern the event plays, and its volume.
		public int Offset { get; private set; }
		public int End { get; private set; }
		public double Gain { get; private set; } = 1.0;

		public EventRef(SequenceEvent e)
		{
			Type = e.Type;
			if (e.Type == SequenceEventType.PlayPattern)
			{
				PatternName = e.Pattern.Name;
				PatternLength = e.Pattern.Length;
				Offset = e.Offset;
				End = e.End;
				Gain = e.Gain;
			}

		}

		public void Set(ISequence seq, int time)
		{
			if (Type == SequenceEventType.PlayPattern)
			{
				var pat = seq.Machine.Patterns.FirstOrDefault(p => p.Name == PatternName);
				if (pat == null) return;
				seq.SetEvent(time, new SequenceEvent(Type, pat) { Offset = Offset, End = End, Gain = Gain });
			}
			else
			{
				seq.SetEvent(time, new SequenceEvent(Type, null));
			}
		}

	}
}
