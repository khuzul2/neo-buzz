using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BuzzGUI.Interfaces;
using BuzzGUI.Common.Actions;

namespace BuzzGUI.SequenceEditor.Actions
{
	class CreatePatternAction : MachineAction
	{
		string name;
		int length;
		IPattern created;          // fork (0.6.2)

		public CreatePatternAction(IMachine machine, string name, int length)
			: base(machine)
		{
			this.name = name;
			this.length = length;
		}

		protected override void DoAction()
		{
			// Fork (0.6.3): under a free name if its own was taken meanwhile; holds what it made.
			var before = Machine.Patterns.ToList();
			Machine.CreatePattern(PatternNames.Free(Machine, name), length);
			var made = PatternNames.Added(Machine, before);
			if (created != null && made != null)
				Restorations.Report(created, made);
			if (made != null) created = made;
		}

		protected override void UndoAction()
		{
			var pat = created != null && Machine.Patterns.Contains(created) ? created : Machine.Patterns.FirstOrDefault(p => p.Name == name);
			if (pat != null) Machine.DeletePattern(pat);
		}


	}
}
