using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Settings;
using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Actions.GraphActions
{
    internal class ConnectMachinesAction : BuzzAction
    {
        private readonly string src;
        private readonly string dst;
        private readonly IEnumerable<int> srcchn;
        private readonly IEnumerable<int> dstchn;
        private readonly int amp;
        private readonly int pan;
        private readonly ReBuzzCore buzz;
        private IUiDispatcher dispatcher;
        private readonly EngineSettings engineSettings;

        public ConnectMachinesAction(
            ReBuzzCore buzz,
            IMachine src,
            IMachine dst,
            IEnumerable<int> srcchn,
            IEnumerable<int> dstchn,
            int amp,
            int pan,
            IUiDispatcher dispatcher, 
            EngineSettings engineSettings)
        {
            this.src = src.Name;
            this.dst = dst.Name;
            this.srcchn = srcchn.ToArray();
            this.dstchn = dstchn.ToArray();
            this.amp = amp;
            this.pan = pan;
            this.dispatcher = dispatcher;
            this.buzz = buzz;
            this.engineSettings = engineSettings;
        }

        public ConnectMachinesAction(
            ReBuzzCore buzz,
            MachineConnectionCore mc,
            IUiDispatcher dispatcher,
            EngineSettings engineSettings)
        {
            this.src = mc.Source.Name;
            this.dst = mc.Destination.Name;
            this.srcchn = mc.SourceChannels;
            this.dstchn = mc.DestinationChannels;
            this.amp = mc.Amp;
            this.pan = mc.Pan;
            this.buzz = buzz;
            this.dispatcher = dispatcher;
            this.engineSettings = engineSettings;
        }

        protected override void DoAction()
        {
            // Fork (0.4.1): the graph changes under AudioLock; ConnectionAdded (the Machine View
            // builds its controls) is raised after releasing it, so the audio thread isn't held
            // up while a send or cue is connected during a performance.
            MachineConnectionCore added = null;
            lock (ReBuzzCore.AudioLock)
            {
                var mcsrc = buzz.SongCore.MachinesList.FirstOrDefault(m => m.Name == src);
                var mcdst = buzz.SongCore.MachinesList.FirstOrDefault(m => m.Name == dst);
                MachineConnectionCore mcc = new MachineConnectionCore(dispatcher, engineSettings) { Source = mcsrc, Destination = mcdst, Amp = amp, Pan = pan, HasPan = mcdst.HasStereoInput };
                // Fork: start at the given amp. Setting Amp ramps from the default (unity) over
                // four blocks, so a connection made at a low level (a Session View send) started
                // with a short loud burst (SessionMixerTests.NewConnectionsStartAtTheirAmp).
                mcc.SnapAmp();
                foreach (var item in srcchn)
                {
                    mcc.SetSourceChannel(item, true);
                }

                foreach (var item in dstchn)
                {
                    mcc.SetDestinationChannel(item, true);
                }

                mcsrc.AddOutput(mcc);
                mcdst.AddInput(mcc);

                // Don't draw connections for hidden or control machines
                if (!mcsrc.Hidden && !mcdst.Hidden && !mcsrc.DLL.Info.Flags.HasFlag(MachineInfoFlags.CONTROL_MACHINE))
                {
                    ParameterGroup.AddInputs(mcc, mcdst.ParameterGroupsList[0]);
                    added = mcc;
                }
                buzz.UpdateMachineDelayCompensation();
            }
            if (added != null)
                buzz.SongCore.InvokeConnectionAdded(added);
        }

        protected override void UndoAction()
        {
            MachineConnectionCore removed = null;
            lock (ReBuzzCore.AudioLock)
            {
                var mcsrc = buzz.SongCore.MachinesList.FirstOrDefault(m => m.Name == src);
                var mcdst = buzz.SongCore.MachinesList.FirstOrDefault(m => m.Name == dst);
                var connection = mcsrc.Outputs.FirstOrDefault(c => c.Destination.Name == dst) as MachineConnectionCore;
                if (connection != null)
                {
                    mcsrc.RemoveOutput(connection);
                    mcdst.RemoveInput(connection);
                    if (!mcsrc.Hidden && !mcdst.Hidden)
                    {
                        removed = connection;
                    }
                }
                buzz.UpdateMachineDelayCompensation();
            }
            if (removed != null)
                buzz.SongCore.InvokeConnectionRemoved(removed);   // fork: after releasing AudioLock
        }
    }
}
