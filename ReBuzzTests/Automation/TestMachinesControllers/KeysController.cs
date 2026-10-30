using ReBuzzTests.Automation.TestMachines;
using System;

namespace ReBuzzTests.Automation.TestMachinesControllers
{
    /// <summary>Fork addition (0.6.3): controller for the <see cref="Keys"/> test machine.</summary>
    public class KeysController(string instanceName) : DynamicMachineController(MachineName, instanceName)
    {
        private const string MachineName = "Keys";

        public static KeysController NewInstance(string instanceName = MachineName) => new(instanceName);

        public static IDynamicTestMachineInfo Info => KeysInfo.Instance;

        /// <summary>Has the machine call <paramref name="listener"/> (kind, channel, data1, data2) for the MIDI it receives.</summary>
        public ITestMachineInstanceCommand Listen(Action<int, int, int, int> listener)
            => new TestManagedMachineInstanceCommand(this, "Listen", listener);
    }
}
